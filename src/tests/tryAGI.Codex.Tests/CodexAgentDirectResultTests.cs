using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexAgentDirectResultTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("test", "test"));
    }
    private sealed class Handler(Func<int, string> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response(++Requests), Encoding.UTF8, "text/event-stream") });
    }
    private const string Function = """{"id":"f1","type":"function_call","call_id":"c1","name":"act","arguments":"{}"}""";
    private const string Message = """{"id":"m1","type":"message","content":[{"type":"output_text","text":"continued"}]}""";
    private static string Done(string output) => "data: {\"type\":\"response.completed\",\"response\":{\"output\":[" + output + "]}}\n\n";
    private static CodexResponseOptions Options => new("test", "test")
    { Tools = [JsonSerializer.Deserialize<JsonElement>("""{"type":"function","name":"act","parameters":{"type":"object"}}""")] };

    [Fact]
    public async Task ConfirmedHostResultSkipsNextRequestAndPreservesHistoryForNextTurn()
    {
        var handler = new Handler(n => Done(n == 1 ? Function : Message));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var result = await new CodexAgent(client).RunAsync(Options, [], (_, _) => Task.FromResult("confirmed"), completeAfterSingleTool: (_, output) => output == "confirmed" ? "Отправлено." : null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.CompletedDirectly);
        Assert.Equal(1, handler.Requests); Assert.Equal(1, result.Requests); Assert.Equal(1, result.ToolCalls);
        Assert.Equal("confirmed", result.History[1].GetProperty("output").GetString());
        Assert.Equal("assistant", result.History[2].GetProperty("role").GetString());
        Assert.Equal(result.Text, result.History[2].GetProperty("content").GetString());
        var next = await new CodexAgent(client).RunAsync(Options, result.History, (_, _) => throw new InvalidOperationException("Must not repeat the action."), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(next.CompletedDirectly); Assert.Equal("continued", next.Text);
    }

    [Fact]
    public async Task ExplicitSilentHostCompletionUsesOneRequestAndKeepsAValidContinuation()
    {
        var handler = new Handler(n => Done(n == 1 ? Function : Message));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var result = await new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) => Task.FromResult(new CodexToolOutput("confirmed")),
            completeAfterSingleTool: (_, _) => "", allowSilentCompletion: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.CompletedDirectly);
        Assert.Empty(result.Text);
        Assert.Equal(1, result.Requests);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(2, result.History.Count);
        Assert.Equal("function_call_output", result.History[1].GetProperty("type").GetString());
        var next = await new CodexAgent(client).RunAsync(Options, result.History, (_, _) => throw new InvalidOperationException("Must not repeat."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("continued", next.Text);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, " ")]
    public async Task SilentCompletionNeedsExplicitOptInAndWhitespaceIsNotASilentResult(bool allow, string direct)
    {
        var handler = new Handler(n => Done(n == 1 ? Function : Message));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var result = await new CodexAgent(client).RunWithRichResultsAsync(Options, [], (_, _) => Task.FromResult(new CodexToolOutput("confirmed")),
            completeAfterSingleTool: (_, _) => direct, allowSilentCompletion: allow, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.CompletedDirectly);
        Assert.Equal("continued", result.Text);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("batch")]
    [InlineData("message")]
    public async Task NonterminalOrCompoundResultsKeepNormalReasoning(string kind)
    {
        var output = kind switch
        {
            "batch" => Function + "," + Function.Replace("f1", "f2").Replace("c1", "c2"),
            "message" => Function + "," + Message,
            _ => Function,
        };
        var handler = new Handler(n => Done(n == 1 ? output : Message));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var callbacks = 0; var executions = 0;
        var result = await new CodexAgent(client).RunAsync(Options, [], (_, _) =>
        { executions++; return Task.FromResult("pending"); }, completeAfterSingleTool: (_, _) =>
        { callbacks++; return kind == "fallback" ? null : "should not finish"; }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.CompletedDirectly); Assert.Equal(2, handler.Requests);
        Assert.Equal(kind == "batch" ? 2 : 1, executions);
        Assert.Equal(kind == "fallback" ? 1 : 0, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrExecutionFailureNeverReturnsSuccess(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var handler = new Handler(_ => Done(Function));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var callbacks = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => new CodexAgent(client).RunAsync(Options, [], (_, _) =>
        {
            if (!cancel) throw new InvalidOperationException("tool failure");
            cts.Cancel(); return Task.FromResult("confirmed");
        }, cancellationToken: cts.Token, completeAfterSingleTool: (_, _) => { callbacks++; return "success"; }));
        Assert.Equal(0, callbacks); Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task TruncatedResponseCannotReachDirectCompletionOrExecute()
    {
        var handler = new Handler(_ => "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":" + Function + "}\n\n");
        using var client = new CodexClient(new Credentials(), handler: handler);
        var calls = 0;
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(Options, [], (_, _) => { calls++; return Task.FromResult("confirmed"); }, completeAfterSingleTool: (_, _) => "success", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }
}
