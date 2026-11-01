using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexToolExecutionDiagnosticTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("PRIVATE_TOKEN", "PRIVATE_ACCOUNT"));
    }
    private sealed class Handler(string first) : HttpMessageHandler
    {
        private int requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(++requests == 1 ? first : Completed(Message), Encoding.UTF8, "text/event-stream") });
    }
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private const string Message = """{"type":"message","content":[{"type":"output_text","text":"PRIVATE_ANSWER"}]}""";
    private static string Call(string id, string name = "read") => new JsonObject
    { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = "{\"value\":\"PRIVATE_ARGUMENT\"}" }.ToJsonString();
    private static string Completed(params string[] items) => "data: " + new JsonObject
    { ["type"] = "response.completed", ["response"] = new JsonObject { ["id"] = "response1",
        ["output"] = new JsonArray(items.Select(item => JsonNode.Parse(item)).ToArray()),
        ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 10 } } }.ToJsonString() + "\n\n";
    private static CodexResponseOptions Options => new("test", "PRIVATE_PROMPT")
    { ParallelToolCalls = true, Tools =
        [Json("""{"type":"function","name":"read","parameters":{"type":"object"}}"""),
         Json("""{"type":"function","name":"action","parameters":{"type":"object"}}""")] };

    [Fact]
    public async Task ParallelCallsJoinTheirProviderRequestAndActionsRemainSerialBarriers()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        ConcurrentQueue<CodexResponseDiagnostic> points = new();
        using var client = new CodexClient(new Credentials(), handler: new Handler(Completed(Call("1"), Call("2"), Call("3", "action"))));
        var run = new CodexAgent(client).RunWithRichResultsAsync(Options, [], async (call, ct) =>
        {
            if (call.Name == "action") Assert.Equal(0, active);
            else
            {
                if (Interlocked.Increment(ref active) == 2) bothStarted.SetResult();
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref active);
            }
            return new("PRIVATE_RESULT");
        }, observeDiagnostics: points.Enqueue, canExecuteConcurrently: call => call.Name == "read", cancellationToken: TestContext.Current.CancellationToken);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, points.Count(p => p.Phase == "tool.started"));
        Assert.DoesNotContain(points, p => p.Phase == "tool.completed");
        release.SetResult();
        await run;
        var provider = points.First(p => p.Phase == "response.completed");
        foreach (var id in new[] { "1", "2", "3" })
        {
            var start = Assert.Single(points, p => p.Phase == "tool.started" && p.FunctionId == id);
            var end = Assert.Single(points, p => p.Phase == "tool.completed" && p.FunctionId == id);
            Assert.Equal(provider.RequestId, start.RequestId); Assert.Equal(provider.ResponseId, start.ResponseId);
            Assert.Equal(start.RequestId, end.RequestId); Assert.Equal(1, end.RequestNumber);
            Assert.True(start.ElapsedMilliseconds >= provider.ElapsedMilliseconds);
            Assert.True(end.ElapsedMilliseconds >= start.ElapsedMilliseconds);
            Assert.Null(start.ToolDurationMilliseconds); Assert.True(end.ToolDurationMilliseconds >= 0);
            Assert.Equal(id != "3", end.ParallelDispatch); Assert.Equal("function_call", end.OutputKind);
            Assert.Null(end.InputTokens); Assert.Null(end.OutputTokens); Assert.Null(end.CachedInputTokens); Assert.Null(end.ReasoningTokens);
        }
        Assert.True(points.Last(p => p.Phase == "tool.completed" && p.FunctionId != "3").ElapsedMilliseconds <=
            points.Single(p => p.Phase == "tool.started" && p.FunctionId == "3").ElapsedMilliseconds);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(points));
    }

    [Theory]
    [InlineData(false, false, "tool.failed")]
    [InlineData(false, true, "tool.failed")]
    [InlineData(true, true, "tool.cancelled")]
    public async Task FailuresAndCancellationHaveExactlyOneTerminalEventAndDoNotDispatchNextAction(bool cancel, bool throwsCancellation, string phase)
    {
        using var stop = new CancellationTokenSource();
        List<CodexResponseDiagnostic> points = [];
        using var client = new CodexClient(new Credentials(), handler: new Handler(Completed(Call("1"), Call("2", "action"))));
        await Assert.ThrowsAnyAsync<Exception>(() => new CodexAgent(client).RunWithRichResultsAsync(Options, [], (call, _) =>
        {
            Assert.Equal("1", call.Id);
            if (cancel) stop.Cancel();
            throw throwsCancellation ? new OperationCanceledException("PRIVATE_ERROR") : new InvalidOperationException("PRIVATE_ERROR");
        }, cancellationToken: stop.Token, observeDiagnostics: points.Add));
        Assert.Single(points, p => p.Phase == "tool.started");
        Assert.Single(points, p => p.Phase == phase);
        Assert.Equal(2, points.Count(p => p.Phase.StartsWith("tool.", StringComparison.Ordinal)));
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(points));
    }

    [Fact]
    public async Task CustomToolRetainsKindAndNamespaceWithoutFreeformInput()
    {
        const string call = """{"type":"custom_tool_call","call_id":"patch1","name":"patch","namespace":"workspace","input":"PRIVATE_PATCH"}""";
        List<CodexResponseDiagnostic> points = [];
        using var client = new CodexClient(new Credentials(), handler: new Handler(Completed(call)));
        var options = Options with { Tools = [Json("""{"type":"namespace","name":"workspace","tools":[{"type":"custom","name":"patch"}]}""")] };
        await new CodexAgent(client).RunWithToolsAsync(options, [], (_, _) => Task.FromResult(new CodexToolOutput("PRIVATE_RESULT")), observeDiagnostics: points.Add, cancellationToken: TestContext.Current.CancellationToken);
        var end = Assert.Single(points, p => p.Phase == "tool.completed");
        Assert.Equal("workspace", end.ToolNamespace); Assert.Equal("custom_tool_call", end.OutputKind);
        Assert.Equal("patch1", end.FunctionId); Assert.Equal("patch", end.ToolName); Assert.False(end.ParallelDispatch);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(points));
    }

    [Fact]
    public async Task InvalidBatchHasNoExecutionEventsOrEffects()
    {
        List<CodexResponseDiagnostic> points = [];
        var effects = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(Completed(Call("1"), Call("2", "unadvertised"))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) =>
        { effects++; return Task.FromResult(new CodexToolOutput("done")); }, observeDiagnostics: points.Add, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, effects); Assert.DoesNotContain(points, p => p.Phase.StartsWith("tool.", StringComparison.Ordinal));
    }
}
