using System.Net;
using System.Text;
using System.Text.Json;
using tryAGI.Codex;

namespace tryAGI.Codex.Tests;

public sealed class CodexAgentTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("test", "test"));
    }
    private sealed class Handler(Func<int, string> response, Action<int, JsonElement>? capture = null) : HttpMessageHandler
    {
        private int requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = ++requests;
            if (capture is not null && request.Content is { } content)
            {
                using var body = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
                capture(number, body.RootElement);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response(number), Encoding.UTF8, "text/event-stream") };
        }
    }
    private const string Message = """{"id":"m1","type":"message","content":[{"type":"output_text","text":"готов"}]}""";
    private const string Function = """{"id":"f1","type":"function_call","call_id":"c1","name":"act","arguments":"{}"}""";
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static string Done(string item, int index = 0) => $"data: {{\"type\":\"response.output_item.done\",\"output_index\":{index},\"item\":{item}}}\n\n";
    private static string Completed(string output = "") => $"data: {{\"type\":\"response.completed\",\"response\":{{\"output\":[{output}]}}}}\n\n";
    private static CodexResponseOptions Options => new("test", "test")
    { Tools = [Json("""{"type":"function","name":"act","parameters":{"type":"object"}}""")] };

    [Fact]
    public async Task RequiredFirstFunctionIsExclusiveThenReleasesTheLoopToAuto()
    {
        var bodies = new List<JsonElement>();
        var saved = false;
        using var client = new CodexClient(new Credentials(), handler: new Handler(number => number == 1
            ? Done(Function) + Completed(Function) : Done(Message) + Completed(Message),
            (_, body) => bodies.Add(body.Clone())));
        var result = await new CodexAgent(client).RunAsync(Options with
        { RequiredFirstFunction = "act", ParallelToolCalls = true }, [], (_, _) =>
        {
            saved = true;
            return Task.FromResult("{\"saved\":true}");
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(saved);
        Assert.Equal("готов", result.Text);
        Assert.Equal(2, result.Requests);
        Assert.Equal("function", bodies[0].GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("act", bodies[0].GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.False(bodies[0].GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("auto", bodies[1].GetProperty("tool_choice").GetString());
        Assert.True(bodies[1].GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Contains(bodies[1].GetProperty("input").EnumerateArray(), item =>
            item.TryGetProperty("output", out var output) && output.GetString() == "{\"saved\":true}");
    }

    [Theory]
    [InlineData(Message)]
    [InlineData("")]
    [InlineData("""{"id":"f2","type":"function_call","call_id":"c2","name":"other","arguments":"{}"}""")]
    [InlineData(Function + "," + Message)]
    [InlineData(Function + "," + """{"id":"f2","type":"function_call","call_id":"c2","name":"act","arguments":"{}"}""")]
    public async Task ViolatingRequiredFirstFunctionCannotCompleteOrExecute(string output)
    {
        var executed = 0;
        var completed = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Completed(output)));
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithRichResultsAsync(
            Options with { RequiredFirstFunction = "act" }, [], (_, _) =>
            { executed++; return Task.FromResult(new CodexToolOutput("unexpected")); },
            cancellationToken: TestContext.Current.CancellationToken, allowSilentCompletion: true,
            observeValidatedItem: (_, _) => { completed++; return Task.CompletedTask; }));
        Assert.Equal(0, executed);
        Assert.Equal(0, completed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public async Task UnknownRequiredFunctionIsRejectedBeforeDispatch(string name)
    {
        var dispatched = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ =>
        { dispatched++; return Completed(Message); }));
        await Assert.ThrowsAsync<ArgumentException>(() => new CodexAgent(client).RunAsync(
            Options with { RequiredFirstFunction = name }, [], (_, _) => Task.FromResult("unused"),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, dispatched);
    }

    [Fact]
    public async Task FailedRequiredControlCannotRequestTheAnswer()
    {
        var requests = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ =>
        { requests++; return Done(Function) + Completed(Function); }));
        await Assert.ThrowsAsync<IOException>(() => new CodexAgent(client).RunAsync(
            Options with { RequiredFirstFunction = "act" }, [],
            (_, _) => throw new IOException("Synthetic persistence failure"),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LocalRequestLimitReportsExecutedCallsAndNeverDispatchesTheLastBatch(int maximumRequests)
    {
        var executed = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(request =>
        {
            var function = Function.Replace("c1", $"c{request}", StringComparison.Ordinal)
                .Replace("f1", $"f{request}", StringComparison.Ordinal);
            return Done(function) + Completed(function);
        }));
        var error = await Assert.ThrowsAsync<CodexAgentRequestLimitException>(() => new CodexAgent(client).RunAsync(Options, [],
            (_, _) => { executed++; return Task.FromResult("ok"); }, maximumRequests: maximumRequests,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(maximumRequests, error.MaximumRequests);
        Assert.Equal(maximumRequests - 1, error.ExecutedToolCalls);
        Assert.Equal(error.ExecutedToolCalls, executed);
        Assert.Contains("further tools were not executed", error.Message);
    }

    [Fact]
    public async Task FinalAnswerOnLastAllowedRequestStillCompletes()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Done(Message) + Completed(Message)));
        var result = await new CodexAgent(client).RunAsync(Options, [], (_, _) => throw new InvalidOperationException("No tool expected."),
            maximumRequests: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("готов", result.Text);
        Assert.Equal(1, result.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamedItemsArePreservedAndTerminalCopiesAreDeduplicated(bool terminalCopy)
    {
        const string reasoning = """{"id":"r1","type":"reasoning","encrypted_content":"opaque"}""";
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ =>
            Done(Message, 1) + Done(reasoning) + Done(Message, 1) + Completed(terminalCopy ? reasoning + "," + Message : "")));
        var result = await new CodexAgent(client).RunAsync(Options, [], (_, _) => throw new InvalidOperationException(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("готов", result.Text);
        Assert.Equal(2, result.History.Count);
        Assert.Equal("opaque", result.History[0].GetProperty("encrypted_content").GetString());
    }

    [Fact]
    public async Task CompletedStreamedFunctionRunsOnceAndContinuesToText()
    {
        var calls = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(request => request == 1
            ? Done(Function) + Completed(Function) : Done(Message) + Completed()));
        var result = await new CodexAgent(client).RunAsync(Options, [], (_, _) => { calls++; return Task.FromResult("ok"); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal("готов", result.Text);
        Assert.Equal(3, result.History.Count);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("conflict")]
    [InlineData("gap")]
    [InlineData("duplicate-id")]
    [InlineData("empty")]
    [InlineData("delta-only")]
    public async Task IncompleteOrAmbiguousOutputNeverExecutesFunction(string kind)
    {
        var response = kind switch
        {
            "truncated" => Done(Function),
            "conflict" => Done(Function) + Completed(Function.Replace("{}", "{\\\"other\\\":true}", StringComparison.Ordinal)),
            "gap" => Done(Function, 1) + Completed(),
            "duplicate-id" => Done(Function) + Done(Function, 1) + Completed(),
            "delta-only" => "data: {\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{}\"}\n\n" + Completed(),
            _ => Completed(),
        };
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => response));
        var executed = false;
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(Options, [], (_, _) =>
        { executed = true; return Task.FromResult("ok"); }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(executed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveHostAcceptsConfirmedModelSilenceAndRetainsToolHistory(bool afterTool)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(request =>
            afterTool && request == 1 ? Done(Function) + Completed(Function) : Completed()));
        var calls = 0;
        var result = await new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) =>
        {
            calls++;
            return Task.FromResult(new CodexToolOutput("ok"));
        }, allowSilentCompletion: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(result.Text);
        Assert.Equal(afterTool ? 1 : 0, calls);
        Assert.Equal(afterTool ? 2 : 1, result.Requests);
        Assert.Equal(afterTool ? 2 : 0, result.History.Count);
        Assert.Equal(afterTool ? new[] { "c1" } : [], result.ExecutedCallIds);
    }

    [Fact]
    public async Task SilentCompletionStillRejectsAnUnfinishedStream()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Done(Function)));
        var executed = false;
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) =>
        {
            executed = true;
            return Task.FromResult(new CodexToolOutput("ok"));
        }, allowSilentCompletion: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(executed);
    }
}
