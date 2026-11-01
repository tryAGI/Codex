using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexSilentResponseValidationTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("PRIVATE_TOKEN", "PRIVATE_ACCOUNT"));
    }
    private sealed class Handler(string stream) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") });
        }
    }
    private sealed class Usage : ICodexUsageObserver
    {
        internal List<string> Events = [];
        public Task ReserveAsync(string id, string model, CancellationToken ct) { Events.Add("reserve"); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) { Events.Add("complete"); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Events.Add("unknown"); return Task.CompletedTask; }
    }
    private static string Sse(string payload) => "data: " + payload + "\n\n";
    private static string Completed(string output) => Sse("{\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"output\":" + output + ",\"usage\":{\"output_tokens\":4}}}");

    [Theory]
    [InlineData("[{\"type\":\"message\"}]", "invalid_message_content")]
    [InlineData("[{\"type\":\"message\",\"content\":null}]", "invalid_message_content")]
    [InlineData("[{\"type\":\"message\",\"content\":[42]}]", "invalid_message_part")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\"}]}]", "invalid_output_text")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":42}]}]", "invalid_output_text")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"PRIVATE_REFUSAL\"}]}]", "refusal")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"PRIVATE_KIND\"}]}]", "unsupported_message_part")]
    [InlineData("[{\"type\":\"PRIVATE_KIND\"}]", "unsupported_item")]
    [InlineData("[42]", "invalid_item")]
    [InlineData("[{\"type\":\"reasoning\",\"encrypted_content\":42}]", "invalid_encrypted_content")]
    [InlineData("[{\"type\":\"reasoning\",\"summary\":null}]", "invalid_reasoning_summary")]
    [InlineData("[{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":42}]}]", "invalid_reasoning_summary")]
    [InlineData("[{\"type\":\"reasoning\",\"content\":[{\"type\":\"reasoning_text\"}]}]", "invalid_reasoning_content")]
    [InlineData("[{\"type\":\"message\",\"role\":\"user\",\"content\":[]}]", "invalid_message_role")]
    [InlineData("[{\"type\":\"message\",\"status\":\"incomplete\",\"content\":[]}]", "unfinished_item")]
    public async Task MalformedOrRefusedOutputCannotBecomeSilentSuccessOrDispatchTools(string output, string code)
    {
        // A valid action alongside malformed output must also remain undispatched.
        var mixed = output.Insert(1, "{\"type\":\"function_call\",\"call_id\":\"c1\",\"name\":\"act\",\"arguments\":\"{}\"},");
        var handler = new Handler(Completed(mixed));
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(), handler: handler, usageObserver: usage);
        List<CodexResponseDiagnostic> points = [];
        var dispatched = 0;
        var options = new CodexResponseOptions("model", "PRIVATE_PROMPT")
        { Tools = [JsonSerializer.Deserialize<JsonElement>("""{"type":"function","name":"act","parameters":{"type":"object"}}""")] };
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithRichResultsAsync(options, [], (_, _) =>
        { dispatched++; return Task.FromResult(new CodexToolOutput("ok")); }, allowSilentCompletion: true, observeDiagnostics: points.Add, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, dispatched);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(new[] { "reserve", "complete" }, usage.Events); // Confirmed provider usage survives host rejection.
        var rejection = Assert.Single(points, p => p.Phase == "response.protocol_rejected." + code);
        Assert.Equal("r1", rejection.ResponseId);
        Assert.Equal(points[0].RequestId, rejection.RequestId);
        Assert.Null(rejection.OutputTokens);
        Assert.DoesNotContain("PRIVATE_", error.Message + JsonSerializer.Serialize(points));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}]")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\" \\n\"}]}]")]
    [InlineData("[{\"type\":\"reasoning\",\"encrypted_content\":\"opaque\"}]")]
    [InlineData("[{\"type\":\"reasoning\",\"encrypted_content\":null,\"summary\":[],\"content\":null}]")]
    public async Task ValidCompletedSilenceRemainsSuccessful(string output)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(Completed(output)));
        var result = await new CodexAgent(client).RunWithRichResultsAsync(new("model", "instructions"), [], (_, _) => throw new InvalidOperationException("No tools expected."), allowSilentCompletion: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(string.IsNullOrWhiteSpace(result.Text));
        Assert.Equal(0, result.ToolCalls);
        Assert.Equal(1, result.Requests);
    }

    [Fact]
    public async Task MalformedStreamedItemCannotHideBehindAnEmptyTerminalArray()
    {
        var stream = Sse("""{"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":null}]}}""") + Completed("[]");
        using var client = new CodexClient(new Credentials(), handler: new Handler(stream));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithRichResultsAsync(new("model", "instructions"), [], (_, _) => throw new InvalidOperationException(), allowSilentCompletion: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("invalid_output_text", error.Message);
    }

    [Theory]
    [InlineData("{\"type\":\"response.completed\",\"response\":null}", "missing_response")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":null}}", "invalid_output_array")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\",\"output\":[]}}", "invalid_terminal_status")]
    public async Task InvalidTerminalEnvelopeHasABoundedProtocolFailure(string payload, string code)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(Sse(payload)));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(async () =>
        { await foreach (var _ in client.StreamResponseAsync(new("model", "instructions"), [], TestContext.Current.CancellationToken)) { } });
        Assert.Contains(code, error.Message);
    }
}
