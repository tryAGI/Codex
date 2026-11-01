using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexUsageObserverTests
{
    private sealed class Credentials : ICodexCredentialProvider
    { public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new CodexCredentials("test", "test")); }
    private sealed class Observer(List<string> order, bool deny = false, bool failComplete = false) : ICodexUsageObserver
    {
        public Task ReserveAsync(string id, string model, CancellationToken ct)
        { order.Add("reserve"); if (deny) throw new InvalidOperationException("budget"); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        { order.Add("complete"); if (failComplete) throw new InvalidOperationException("storage"); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { order.Add("unknown"); Assert.False(ct.IsCancellationRequested); return Task.CompletedTask; }
    }
    private sealed class Handler(List<string> order, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { order.Add("http"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") }); }
    }
    [Fact]
    public async Task BudgetRejectionPreventsNetwork()
    {
        var order = new List<string>(); using var client = new CodexClient(new Credentials(), handler: new Handler(order, ""), usageObserver: new Observer(order, deny: true));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in client.StreamResponseAsync(new("test", "test"), [], TestContext.Current.CancellationToken)) { } });
        Assert.Equal(new[] { "reserve" }, order);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionPersistedBeforeTerminalEventAndFailedAccountingIsUnknown(bool fail)
    {
        var order = new List<string>();
        using var client = new CodexClient(new Credentials(), handler: new Handler(order, "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n"), usageObserver: new Observer(order, failComplete: fail));
        async Task Read() { await foreach (var _ in client.StreamResponseAsync(new("test", "test"), [])) order.Add("yield"); }
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(Read); else await Read();
        Assert.Equal(fail ? new[] { "reserve", "http", "complete", "unknown" } : new[] { "reserve", "http", "complete", "yield" }, order);
    }
    [Fact]
    public async Task AbruptStreamKeepsUnknownExpense()
    {
        var order = new List<string>(); using var client = new CodexClient(new Credentials(), handler: new Handler(order, ""), usageObserver: new Observer(order));
        await Assert.ThrowsAsync<CodexProtocolException>(async () => { await foreach (var _ in client.StreamResponseAsync(new("test", "test"), [], TestContext.Current.CancellationToken)) { } });
        Assert.Equal(new[] { "reserve", "http", "unknown" }, order);
    }
    [Fact]
    public void MalformedQuotaDoesNotInventZeroOrThrowNumericConversion()
    {
        using var doc = JsonDocument.Parse("""{"rate_limit":{"allowed":true,"primary_window":{"used_percent":"bad","limit_window_seconds":10,"reset_at":1}},"credits":{"balance":"bad"}}""");
        var quota = CodexClient.ParseAccountUsage(doc.RootElement, DateTimeOffset.UtcNow);
        Assert.Empty(quota.Windows); Assert.Null(quota.Credits); Assert.True(quota.Allowed);
    }
    [Fact]
    public void V3UpdatesAndTranscriptsRespectPinnedShapes()
    {
        var update = CodexRealtimeProtocol.SessionUpdate(new("test") { Voice = "sol" });
        Assert.Null((update["session"] ?? throw new InvalidDataException("Required data was null."))["model"]);
        Assert.Equal("input_audio.append", (CodexRealtimeProtocol.AudioAppend(new byte[2])["type"] ?? throw new InvalidDataException("Required data was null.")).GetValue<string>());
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.AudioAppend(new byte[3]));
        using var doc = JsonDocument.Parse("""{"type":"turn.done","turn":{"role":"user","transcript":"Ты тут?"}}""");
        Assert.Equal(new("user", "Ты тут?", true) { ReplaceFinalText = true }, CodexRealtimeProtocol.ReadTranscript(doc.RootElement));
    }
    [Fact]
    public async Task StructuredOutputUsesReviewedRustTextControls()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var order = new List<string>();
        var handler = new InspectingHandler(async request =>
        {
            using var body = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync());
            var format = body.RootElement.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.Equal("codex_output_schema", format.GetProperty("name").GetString());
            Assert.True(format.GetProperty("strict").GetBoolean());
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        });
        using var client = new CodexClient(new Credentials(), handler: handler);
        await foreach (var _ in client.StreamResponseAsync(new("test", "test") { OutputSchema = schema.RootElement }, [], TestContext.Current.CancellationToken)) { }
        Assert.Equal("wss://api.openai.com/v1/live?model=gpt-live-1-codex",
            CodexClient.DirectRealtimeUri(new CodexEndpointOptions().SidebandBaseUri, "gpt-live-1-codex").AbsoluteUri);
        Assert.Equal("wss://chatgpt.com/backend-api/codex?model=gpt-live-1-codex",
            CodexClient.DirectRealtimeUri(new CodexEndpointOptions().BackendBaseUri, "gpt-live-1-codex").AbsoluteUri);
    }
    private sealed class InspectingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> inspect) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => inspect(request); }

}
