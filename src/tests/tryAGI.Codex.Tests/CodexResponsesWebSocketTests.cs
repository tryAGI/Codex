using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexResponsesWebSocketTests
{
    private static readonly CodexResponsesIdentity Identity = new("session", "thread", "turn");
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("token", "account"));
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + Completed("sse") + "\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class Observer : ICodexUsageObserver
    {
        internal int Completed, Incomplete, NotDispatched;
        public Task ReserveAsync(string requestId, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string requestId, string model, JsonElement response, CancellationToken ct)
        { Completed++; return Task.CompletedTask; }
        public Task IncompleteAsync(string requestId, string model, CancellationToken ct)
        { Incomplete++; return Task.CompletedTask; }
        public Task NotDispatchedAsync(string requestId, string model, CancellationToken ct)
        { NotDispatched++; return Task.CompletedTask; }
    }

    private sealed class Socket(params string?[] messages) : WebSocket
    {
        private readonly Queue<string?> queue = new(messages);
        private WebSocketState state = WebSocketState.Open;
        internal readonly List<JsonObject> Frames = [];
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        { Dispose(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        { Dispose(); return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Frames.Add(JsonNode.Parse(Encoding.UTF8.GetString(buffer.AsSpan()))!.AsObject());
            return Task.CompletedTask;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!queue.TryDequeue(out var message) || message is null)
            {
                state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var bytes = Encoding.UTF8.GetBytes(message);
            bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
    }

    private static string Completed(string id, string output = "[]") =>
        "{\"type\":\"response.completed\",\"response\":{\"id\":\"" + id + "\",\"output\":" + output + "}}";

    private static JsonElement Item(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static readonly JsonElement User = Item("""{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}""");
    private static readonly JsonElement Reasoning = Item("""{"type":"reasoning","encrypted_content":"opaque","summary":[]}""");
    private static readonly JsonElement Tool = Item("""{"type":"function_call_output","call_id":"call","output":"receipt"}""");

    private static async Task Read(CodexClient client, CodexResponseOptions settings, params JsonElement[] input)
    {
        await foreach (var _ in client.StreamResponseAsync(settings, input, TestContext.Current.CancellationToken)) { }
    }

    [Fact]
    public async Task PrewarmAndContinuationUseExactPrefixAndKeepEncryptedOutput()
    {
        var socket = new Socket(Completed("warm"), Completed("first", "[" + Reasoning.GetRawText() + "]"), Completed("second"));
        var calls = 0;
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
        {
            calls++;
            return Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket, ["sticky"]));
        });
        var observer = new Observer();
        var handler = new Handler();
        using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: handler);
        var settings = new CodexResponseOptions("model", "instructions")
        { Identity = Identity, WebSocketSession = session, PrewarmWebSocket = true };
        await Read(client, settings, User);
        await Read(client, settings, User, Reasoning, Tool);
        Assert.Equal(1, calls);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(2, observer.Completed);
        Assert.Equal(0, observer.Incomplete);
        Assert.Equal(3, socket.Frames.Count);
        Assert.False(socket.Frames[0]["generate"]!.GetValue<bool>());
        Assert.Equal("response.create", socket.Frames[0]["type"]!.GetValue<string>());
        Assert.Single(socket.Frames[0]["input"]!.AsArray());
        Assert.Equal("warm", socket.Frames[1]["previous_response_id"]!.GetValue<string>());
        Assert.Empty(socket.Frames[1]["input"]!.AsArray());
        Assert.Equal("first", socket.Frames[2]["previous_response_id"]!.GetValue<string>());
        Assert.Equal(Tool.GetRawText(), socket.Frames[2]["input"]!.AsArray()[0]!.ToJsonString());
        Assert.All(socket.Frames, frame => Assert.Equal("sticky", frame["client_metadata"]!["x-codex-turn-state"]!.GetValue<string>()));
    }

    [Fact]
    public async Task PrewarmMetadataCapturesStickyStateForSubsequentRequests()
    {
        var socket = new Socket(
            """{"type":"response.metadata","response_id":"warm","headers":{"X-Codex-Turn-State":["sticky-from-stream"]}}""",
            Completed("warm"),
            """{"type":"response.metadata","response_id":"first","headers":{"x-codex-turn-state":"ignored-replacement"}}""",
            Completed("first"), Completed("second"));
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
            Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket)));
        using var client = new CodexClient(new Credentials(), handler: new Handler());
        var settings = new CodexResponseOptions("model", "instructions")
        { Identity = Identity, WebSocketSession = session, PrewarmWebSocket = true };
        await Read(client, settings, User);
        await Read(client, settings, User, Tool);
        Assert.Null(socket.Frames[0]["client_metadata"]!["x-codex-turn-state"]);
        Assert.Equal("sticky-from-stream", socket.Frames[1]["client_metadata"]!["x-codex-turn-state"]!.GetValue<string>());
        Assert.Equal("sticky-from-stream", socket.Frames[2]["client_metadata"]!["x-codex-turn-state"]!.GetValue<string>());
    }

    [Fact]
    public async Task PriorTerminalCannotSettleAnotherReservationOnReusedSocket()
    {
        var socket = new Socket(Completed("first"), Completed("first"), Completed("second"));
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
            Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket)));
        var observer = new Observer();
        var handler = new Handler();
        using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: handler);
        var settings = new CodexResponseOptions("model", "instructions") { Identity = Identity, WebSocketSession = session };
        await Read(client, settings, User);
        var failure = await Assert.ThrowsAsync<CodexProtocolException>(() => Read(client, settings, User, Tool));
        Assert.Contains("retired_response_id", failure.Message);
        Assert.Equal(1, observer.Completed);
        Assert.Equal(1, observer.Incomplete);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task ConflictingResponseIdAndMissingTerminalIdNeverAuthorizeOutput()
    {
        foreach (var terminal in new[] { Completed("different"), """{"type":"response.completed","response":{"output":[]}}""" })
        {
            var socket = new Socket("""{"type":"response.created","response":{"id":"expected"}}""", terminal);
            await using var session = new CodexResponsesWebSocketSession((_, _) =>
                Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket)));
            var observer = new Observer();
            var handler = new Handler();
            using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: handler);
            var settings = new CodexResponseOptions("model", "instructions") { Identity = Identity, WebSocketSession = session };
            await Assert.ThrowsAsync<CodexProtocolException>(() => Read(client, settings, User));
            Assert.Equal(0, observer.Completed);
            Assert.Equal(1, observer.Incomplete);
            Assert.Equal(0, handler.Calls);
        }
    }

    [Fact]
    public async Task ChangedPropertiesOrTurnDoNotUsePriorResponseId()
    {
        var first = new Socket(Completed("first"), Completed("second"));
        var second = new Socket(Completed("third"));
        var calls = 0;
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
            Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(++calls == 1 ? first : second)));
        using var client = new CodexClient(new Credentials(), handler: new Handler());
        var settings = new CodexResponseOptions("model", "instructions") { Identity = Identity, WebSocketSession = session };
        await Read(client, settings, User);
        await Read(client, settings with { ReasoningEffort = "high" }, User, Tool);
        Assert.Null(first.Frames[1]["previous_response_id"]);
        Assert.Equal(2, first.Frames[1]["input"]!.AsArray().Count);
        await Read(client, settings with { Identity = Identity with { TurnId = "new-turn" } }, User);
        Assert.Equal(2, calls);
        Assert.Null(second.Frames[0]["previous_response_id"]);
        Assert.Equal(WebSocketState.Closed, first.State);
    }

    [Fact]
    public async Task FailedHandshakeFallsBackBeforeGenerationOnly()
    {
        var calls = 0;
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
        {
            calls++;
            return Task.FromResult<CodexResponsesWebSocketSession.Connection?>(null);
        });
        var handler = new Handler();
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: handler);
        var settings = new CodexResponseOptions("model", "instructions") { Identity = Identity, WebSocketSession = session };
        await Read(client, settings, User);
        await Read(client, settings, User);
        Assert.Equal(1, calls);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, observer.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectAfterDispatchNeverFallsBack(bool prewarm)
    {
        var socket = new Socket((string?)null);
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
            Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket)));
        var handler = new Handler();
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: handler);
        var settings = new CodexResponseOptions("model", "instructions")
        { Identity = Identity, WebSocketSession = session, PrewarmWebSocket = prewarm };
        await Assert.ThrowsAsync<CodexProtocolException>(() => Read(client, settings, User));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, observer.Completed);
        Assert.Equal(1, observer.Incomplete);
        Assert.Single(socket.Frames);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }
}
