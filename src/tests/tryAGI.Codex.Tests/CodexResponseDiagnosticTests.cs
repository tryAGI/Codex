using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexResponseDiagnosticTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("PRIVATE_TOKEN", "PRIVATE_ACCOUNT"));
    }
    private sealed class Handler(Func<int, string> stream) : HttpMessageHandler
    {
        private int requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(stream(++requests), Encoding.UTF8, "text/event-stream") });
    }
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static string Sse(string value) => "data: " + value + "\n\n";

    [Fact]
    public async Task DiagnosticsSeparateTransportGenerationAndDispatchWithoutLeakingContent()
    {
        const string function = """{"id":"f1","type":"function_call","call_id":"c1","name":"act","arguments":"{\"text\":\"PRIVATE_ARGUMENT\"}"}""";
        const string message = """{"id":"m1","type":"message","content":[{"type":"output_text","text":"PRIVATE_ANSWER"}]}""";
        using var client = new CodexClient(new Credentials(), handler: new Handler(number => number == 1
            ? Sse("""{"type":"response.created","response":{"id":"response1"}}""") +
              Sse("{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":" + function + "}") +
              Sse("{\"type\":\"response.completed\",\"response\":{\"output\":[" + function + "],\"usage\":{\"input_tokens\":1200,\"input_tokens_details\":{\"cached_tokens\":1000},\"output_tokens\":200,\"output_tokens_details\":{\"reasoning_tokens\":180}}}}")
            : Sse("{\"type\":\"response.completed\",\"response\":{\"output\":[" + message + "]}}")));
        List<CodexResponseDiagnostic> points = []; List<string> order = [];
        var options = new CodexResponseOptions("model", "PRIVATE_PROMPT")
        { Tools = [Json("""{"type":"function","name":"act","parameters":{"type":"object"}}""")] };
        var result = await new CodexAgent(client).RunAsync(options, [], (_, _) =>
        { order.Add("tool"); return Task.FromResult("PRIVATE_RESULT"); }, observeDiagnostics: point =>
        { points.Add(point); order.Add(point.Phase); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Requests);
        Assert.True(order.IndexOf("response.function_item_completed") < order.IndexOf("response.completed"));
        Assert.True(order.IndexOf("response.completed") < order.IndexOf("tool"));
        var first = points.Where(p => p.RequestNumber == 1).ToArray();
        Assert.Equal(new[] { "request.started", "request.prepared", "response.headers", "response.created", "response.first_output", "response.function_item_completed", "response.completed", "response.output_shape.tool", "tool.started", "tool.completed" }, first.Select(p => p.Phase));
        Assert.Single(first.Select(p => p.RequestId).Distinct());
        Assert.NotEqual(first[0].RequestId, points.Last().RequestId);
        Assert.True(first.Zip(first.Skip(1)).All(pair => pair.First.ElapsedMilliseconds <= pair.Second.ElapsedMilliseconds));
        Assert.True(first[1].RequestBytes > 0);
        Assert.Equal(1200, first.Single(p => p.Phase == "response.completed").InputTokens); Assert.Equal(1000, first.Single(p => p.Phase == "response.completed").CachedInputTokens);
        Assert.Equal(200, first.Single(p => p.Phase == "response.completed").OutputTokens); Assert.Equal(180, first.Single(p => p.Phase == "response.completed").ReasoningTokens);
        Assert.Equal("c1", first.Single(p => p.Phase == "response.function_item_completed").FunctionId);
        Assert.Null(points.Last().InputTokens);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(points));
    }

    [Fact]
    public void MissingOrInvalidUsageIsUnavailableNotZero()
    {
        List<CodexResponseDiagnostic> points = [];
        var diagnostics = new CodexResponseDiagnostics(2, 3, points.Add);
        diagnostics.Event("response.completed", Json("""{"response":{"usage":{"input_tokens":-1,"output_tokens":"5","input_tokens_details":{"cached_tokens":null}}}}"""));
        var point = Assert.Single(points, p => p.Phase == "response.completed");
        Assert.Null(point.InputTokens); Assert.Null(point.CachedInputTokens); Assert.Null(point.OutputTokens); Assert.Null(point.ReasoningTokens);
        Assert.Equal(2, point.InputItems); Assert.Equal(3, point.ToolCount);
        Assert.Single(points, p => p.Phase == "response.output_shape.invalid_output");
    }

    private sealed class Usage : ICodexUsageObserver
    {
        internal List<string> Events { get; } = [];
        public Task ReserveAsync(string id, string model, CancellationToken ct) { Events.Add("reserve"); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) { Events.Add("complete"); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Events.Add("unknown"); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData("response.failed", "rate_limit_exceeded", "rate_limit_exceeded")]
    [InlineData("response.failed", "server_error", "server_error")]
    [InlineData("response.failed", "context_length_exceeded", "context_length_exceeded")]
    [InlineData("response.incomplete", "max_output_tokens", "max_output_tokens")]
    [InlineData("response.incomplete", "content_filter", "content_filter")]
    [InlineData("error", "insufficient_quota", "insufficient_quota")]
    [InlineData("error", "invalid_request_error", "invalid_request_error")]
    [InlineData("response.failed", "PRIVATE_ARBITRARY_CODE", "unclassified")]
    [InlineData("response.incomplete", "PRIVATE_UNKNOWN_REASON", "unclassified")]
    public async Task TerminalFailureIsJoinedBeforeThrowingAndNeverDispatchesOrClaimsZeroUsage(string type, string code, string category)
    {
        const string function = """{"id":"f1","type":"function_call","call_id":"c1","name":"act","arguments":"{}"}""";
        var error = new JsonObject { ["code"] = code, ["message"] = "PRIVATE_PROVIDER_MESSAGE", ["param"] = "PRIVATE_PARAMETER" };
        var terminal = type == "error"
            ? new JsonObject { ["type"] = type, ["error"] = error }.ToJsonString()
            : new JsonObject { ["type"] = type, ["response"] = new JsonObject { ["id"] = "response_failure", ["error"] = error,
                ["incomplete_details"] = new JsonObject { ["reason"] = code },
                ["usage"] = new JsonObject { ["input_tokens"] = 42, ["output_tokens"] = 7 } } }.ToJsonString();
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ =>
            Sse("""{"type":"response.created","response":{"id":"response_created"}}""") +
            Sse("{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":" + function + "}") + Sse(terminal)), usageObserver: usage);
        List<CodexResponseDiagnostic> points = [];
        var effects = 0;
        var options = new CodexResponseOptions("model", "PRIVATE_PROMPT")
        { Tools = [Json("""{"type":"function","name":"act","parameters":{"type":"object"}}""")] };
        var exception = await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(options, [], (_, _) =>
        { effects++; return Task.FromResult("PRIVATE_RESULT"); }, observeDiagnostics: points.Add, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, effects);
        Assert.Equal(new[] { "reserve", "unknown" }, usage.Events);
        var terminalPoint = Assert.Single(points, p => p.Phase == type + "." + category);
        Assert.Equal(points[0].RequestId, terminalPoint.RequestId);
        Assert.Equal(type == "error" ? "response_created" : "response_failure", terminalPoint.ResponseId);
        Assert.Equal(1, terminalPoint.RequestNumber);
        Assert.True(terminalPoint.ElapsedMilliseconds >= points[0].ElapsedMilliseconds);
        Assert.Null(terminalPoint.InputTokens); Assert.Null(terminalPoint.OutputTokens);
        Assert.DoesNotContain(points, p => p.Phase is "response.completed" or "tool.started");
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(points) + exception.Message);
        Assert.Contains(category, exception.Message);
    }

    [Theory]
    [InlineData("{\"type\":\"response.failed\",\"response\":{\"id\":\"failed\",\"error\":[]}}", "response.failed.unclassified")]
    [InlineData("{\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":42}}}", "response.incomplete.unclassified")]
    [InlineData("{\"type\":\"error\",\"code\":\"rate_limit_exceeded\"}", "error.rate_limit_exceeded")]
    public async Task MalformedOrRootFailureMetadataRemainsBounded(string payload, string expected)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Sse(payload)));
        List<CodexResponseDiagnostic> points = [];
        await Assert.ThrowsAsync<CodexProtocolException>(async () =>
        { await foreach (var _ in client.StreamResponseAsync(new("model", "instructions"), [], observeDiagnostics: points.Add, cancellationToken: TestContext.Current.CancellationToken)) { } });
        Assert.Single(points, p => p.Phase == expected);
    }

    [Fact]
    public async Task PrematureStreamEndRecordsTerminalMetadataWithUnknownUsage()
    {
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ =>
            Sse("""{"type":"response.created","response":{"id":"unfinished"}}""") + Sse("[DONE]")), usageObserver: usage);
        List<CodexResponseDiagnostic> points = [];
        await Assert.ThrowsAsync<CodexProtocolException>(async () =>
        { await foreach (var _ in client.StreamResponseAsync(new("model", "instructions"), [], observeDiagnostics: points.Add, cancellationToken: TestContext.Current.CancellationToken)) { } });
        var terminal = Assert.Single(points, p => p.Phase == "response.stream_ended");
        Assert.Equal("unfinished", terminal.ResponseId);
        Assert.Equal(points[0].RequestId, terminal.RequestId);
        Assert.Equal(new[] { "reserve", "unknown" }, usage.Events);
        Assert.Equal("response.stream_ended", points[^1].Phase);
    }

    [Theory]
    [InlineData(true, true, "caller_cancelled")]
    [InlineData(true, false, "caller_cancelled")]
    [InlineData(false, true, "timeout")]
    [InlineData(false, false, "interrupted")]
    public void UnfinishedRequestKeepsItsLastObservedPhaseAndResponseIdentity(bool callerCancelled,
        bool requestCancelled, string cause)
    {
        List<CodexResponseDiagnostic> points = [];
        var diagnostics = new CodexResponseDiagnostics(1, 0, points.Add);
        diagnostics.Event("response.created", Json("""{"response":{"id":"unfinished"}}"""));
        diagnostics.Mark("response.first_output");
        diagnostics.Interrupted(callerCancelled, requestCancelled);
        Assert.Equal("request." + cause + ".after.response.first_output", points[^1].Phase);
        Assert.Equal("unfinished", points[^1].ResponseId);
        Assert.Equal(points[0].RequestId, points[^1].RequestId);
        Assert.Null(points[^1].InputTokens);
        Assert.Null(points[^1].OutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationAfterOutputRetainsUnknownUsageEvenWhenDiagnosticSinkFails(bool failDiagnostic)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ =>
            Sse("""{"type":"response.created","response":{"id":"unfinished"}}""") +
            Sse("""{"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":"PRIVATE_ANSWER"}]}}""")), usageObserver: usage);
        List<CodexResponseDiagnostic> points = [];
        async Task Read()
        {
            await foreach (var _ in client.StreamResponseAsync(new("model", "PRIVATE_PROMPT"), [], cancellation.Token,
                point =>
                {
                    points.Add(point);
                    if (point.Phase == "response.first_output") cancellation.Cancel();
                    if (failDiagnostic && point.Phase.StartsWith("request.caller_cancelled.", StringComparison.Ordinal))
                        throw new InvalidOperationException("Diagnostic sink failed.");
                })) { }
        }
        if (failDiagnostic) await Assert.ThrowsAsync<InvalidOperationException>(Read);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(Read);
        var terminal = Assert.Single(points, p => p.Phase == "request.caller_cancelled.after.response.first_output");
        Assert.Equal("unfinished", terminal.ResponseId);
        Assert.Equal(new[] { "reserve", "unknown" }, usage.Events);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(points));
    }

    [Theory]
    [InlineData("[]", "empty")]
    [InlineData("[{\"type\":\"reasoning\",\"encrypted_content\":\"PRIVATE_REASONING\"}]", "reasoning")]
    [InlineData("[{\"type\":\"message\",\"content\":[]}]", "empty_message")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\" \\n\"}]}]", "empty_message")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"PRIVATE_ANSWER\"}]}]", "text")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"PRIVATE_REFUSAL\"}]}]", "unsupported")]
    [InlineData("[{\"type\":\"message\",\"content\":null}]", "invalid")]
    [InlineData("[42]", "invalid")]
    public void CompletedOutputShapeDistinguishesSilenceFromMalformedOrUnsupportedContent(string output, string shape)
    {
        List<CodexResponseDiagnostic> points = [];
        var diagnostics = new CodexResponseDiagnostics(2, 3, points.Add);
        diagnostics.Event("response.completed", Json("{\"response\":{\"id\":\"response1\",\"output\":" + output + ",\"usage\":{\"output_tokens\":4}}}"));
        var point = Assert.Single(points, p => p.Phase == "response.output_shape." + shape);
        Assert.Equal("response1", point.ResponseId);
        Assert.Equal(points[0].RequestId, point.RequestId);
        Assert.Equal(4, points[0].OutputTokens);
        Assert.Null(point.OutputTokens); // Never duplicate provider usage on a structural marker.
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(points));
    }

    [Fact]
    public void EmptyTerminalArrayKeepsStreamedMessageShapeAndCorrelation()
    {
        List<CodexResponseDiagnostic> points = [];
        var diagnostics = new CodexResponseDiagnostics(2, 3, points.Add);
        diagnostics.Event("response.output_item.done", Json("""{"output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":""}]}}"""));
        diagnostics.Event("response.completed", Json("""{"response":{"id":"silent1","output":[]}}"""));
        var point = Assert.Single(points, p => p.Phase == "response.output_shape.empty_message");
        Assert.Equal("silent1", point.ResponseId);
        Assert.Equal(points[0].RequestId, point.RequestId);
    }
}
