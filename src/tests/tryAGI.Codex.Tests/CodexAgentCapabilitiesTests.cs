using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using tryAGI.Codex;

namespace tryAGI.Codex.Tests;

public sealed class CodexAgentCapabilitiesTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("test", "test"));
    }
    private sealed class Handler(Func<int, JsonElement, string> response) : HttpMessageHandler
    {
        private int requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(response(++requests, body.RootElement), Encoding.UTF8, "text/event-stream") };
        }
    }
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private static string Completed(params string[] items) => "data: " + new JsonObject
    { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(items.Select(item => JsonNode.Parse(item)).ToArray()) } }.ToJsonString() + "\n\n";
    private static string Call(string id, string name = "read") => new JsonObject
    { ["id"] = "f" + id, ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = "{}" }.ToJsonString();
    private const string Message = """{"type":"message","content":[{"type":"output_text","text":"observed"}]}""";
    private static CodexResponseOptions Options => new("test", "test") { Tools =
        [Json("""{"type":"function","name":"read","parameters":{"type":"object"}}"""),
         Json("""{"type":"function","name":"action","parameters":{"type":"object"}}""")] };

    [Fact]
    public async Task ImageToolResultReachesTheModelAsPixelsAndCannotFinishWithoutVision()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, body) =>
        {
            if (number == 1) return Completed(Call("image"));
            var output = body.GetProperty("input")[1].GetProperty("output");
            Assert.Equal(JsonValueKind.Array, output.ValueKind);
            Assert.Equal("source and age", output[0].GetProperty("text").GetString());
            Assert.Equal("input_image", output[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,AQID", output[1].GetProperty("image_url").GetString());
            return Completed(Message);
        }));
        var result = await new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) =>
            Task.FromResult(new CodexToolOutput("source and age") { Images = [new(new byte[] { 1, 2, 3 }, "image/png")] }), completeAfterSingleTool: (_, _) => "do not skip image reasoning", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Requests);
        Assert.False(result.CompletedDirectly);
    }

    [Fact]
    public async Task HostAuthorizedLargerImageReachesContinuationWithoutTruncation()
    {
        var pixels = new byte[524289]; pixels[^1] = 47;
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, body) =>
        {
            if (number == 1) return Completed(Call("image"));
            var url = (body.GetProperty("input")[1].GetProperty("output")[1].GetProperty("image_url").GetString() ?? throw new InvalidDataException("Required data was null."));
            Assert.Equal(pixels, Convert.FromBase64String(url["data:image/png;base64,".Length..]));
            return Completed(Message);
        }));
        var result = await new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) =>
            Task.FromResult(new CodexToolOutput("exact authorized original")
            { Images = [new(pixels, "image/png")], ImageLimits = new() { MaximumTotalBytes = 1048576 } }), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Requests);
    }

    [Theory]
    [InlineData(524288, 4, 524289, 1)]
    [InlineData(10, 4, 6, 2)]
    [InlineData(1024, 1, 1, 2)]
    [InlineData(16777217, 4, 1, 1)]
    [InlineData(1024, 17, 1, 1)]
    public async Task ImageHostLimitsRejectBeforeVisionContinuation(int maximumBytes, int maximumCount, int imageBytes, int count)
    {
        var requests = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
        { requests++; return Completed(Call("image")); }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) =>
            Task.FromResult(new CodexToolOutput("source")
            { Images = Enumerable.Range(0, count).Select(_ => new CodexToolImage(new byte[imageBytes], "image/png")).ToArray(),
                ImageLimits = new() { MaximumImages = maximumCount, MaximumTotalBytes = maximumBytes } }), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task IndependentCallsOverlapWithinBoundAndActionsRemainBarriers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0; var finished = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, body) =>
        {
            Assert.True(body.GetProperty("parallel_tool_calls").GetBoolean());
            if (number == 1) return Completed(Call("1"), Call("2"), Call("3"), Call("4", "action"));
            Assert.Equal(new[] { "1", "2", "3", "4" }, body.GetProperty("input").EnumerateArray()
                .Where(x => x.GetProperty("type").GetString() == "function_call_output")
                .Select(x => x.GetProperty("call_id").GetString()));
            return Completed(Message);
        }));
        var run = new CodexAgent(client).RunWithRichResultsAsync(Options with { ParallelToolCalls = true, MaximumConcurrentTools = 2 }, [], async (call, ct) =>
        {
            if (call.Name == "action") { Assert.Equal(3, finished); Assert.Equal(0, active); return new("done"); }
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            if (count == 2) started.TrySetResult();
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref active); Interlocked.Increment(ref finished);
            return new(call.Id);
        }, canExecuteConcurrently: call => call.Name == "read", cancellationToken: TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        release.SetResult();
        var result = await run;
        Assert.Equal(2, maximum);
        Assert.Equal(4, result.ToolCalls);
    }

    [Fact]
    public async Task MissingHostClassificationKeepsExecutionSequential()
    {
        var active = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, _) =>
            number == 1 ? Completed(Call("1"), Call("2")) : Completed(Message)));
        await new CodexAgent(client).RunWithRichResultsAsync(Options with { ParallelToolCalls = true }, [], async (_, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Yield(); Interlocked.Decrement(ref active); return new("done");
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailureWaitsForStartedCallsAndNeverDispatchesFollowingAction()
    {
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
            Completed(Call("1"), Call("2"), Call("3", "action"))));
        var run = new CodexAgent(client).RunWithRichResultsAsync(Options with { ParallelToolCalls = true }, [], async (call, _) =>
        {
            Assert.NotEqual("action", call.Name);
            if (call.Id == "1") { await otherStarted.Task; throw new InvalidOperationException("failed read"); }
            otherStarted.SetResult(); await release.Task; return new("done");
        }, canExecuteConcurrently: call => call.Name == "read", cancellationToken: TestContext.Current.CancellationToken);
        await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }
}
