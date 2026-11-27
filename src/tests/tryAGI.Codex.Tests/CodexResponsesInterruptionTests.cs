using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexResponsesInterruptionTests
{
    private const string Created = """{"type":"response.created","response":{"id":"r1"}}""";
    private const string Accepted = """{"type":"response.interrupt.accepted","response_id":"r1"}""";
    private const string Terminal = """{"type":"response.incomplete","response":{"id":"r1","status":"incomplete","incomplete_details":{"reason":"interrupted"},"usage":{"output_tokens":7}}}""";
    private const string Done = """{"type":"response.output_item.done","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"finished"}]}}""";
    private const string Added = """{"type":"response.output_item.added","item":{"type":"message","id":"partial","role":"assistant","status":"in_progress","content":[]}}""";

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("token", "account"));
    }

    private sealed class Observer : ICodexUsageObserver
    {
        internal int Completed, Incomplete, NotDispatched;
        internal JsonElement Receipt;
        public Task ReserveAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Completed++;
            Receipt = response.Clone();
            return Task.CompletedTask;
        }
        public Task IncompleteAsync(string id, string model, CancellationToken ct)
        { Incomplete++; return Task.CompletedTask; }
        public Task NotDispatchedAsync(string id, string model, CancellationToken ct)
        { NotDispatched++; return Task.CompletedTask; }
    }

    private sealed class Http : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Interrupted generation must not fall back to HTTP.");
        }
    }

    private sealed class Socket : WebSocket
    {
        private readonly Channel<string> incoming = Channel.CreateUnbounded<string>();
        private readonly Channel<JsonObject> outgoing = Channel.CreateUnbounded<JsonObject>();
        private WebSocketState state = WebSocketState.Open;
        internal void Receive(string frame) => Assert.True(incoming.Writer.TryWrite(frame));
        internal void End() => incoming.Writer.TryComplete();
        internal ValueTask<JsonObject> Sent(CancellationToken ct) => outgoing.Reader.ReadAsync(ct);
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => Dispose();
        public override void Dispose() { state = WebSocketState.Closed; End(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        { Dispose(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        { Dispose(); return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(outgoing.Writer.TryWrite(JsonNode.Parse(Encoding.UTF8.GetString(buffer.AsSpan()))!.AsObject()));
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (!await incoming.Reader.WaitToReadAsync(ct))
                return new(0, WebSocketMessageType.Close, true);
            var bytes = Encoding.UTF8.GetBytes(await incoming.Reader.ReadAsync(ct));
            bytes.CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
    }

    private static CodexResponseOptions Settings(CodexResponsesWebSocketSession session) =>
        new("model", "instructions") { Identity = new("session", "thread", "turn"), WebSocketSession = session };

    private static async Task<List<CodexResponseEvent>> Read(CodexClient client, CodexResponseOptions settings,
        CancellationToken ct, Action<CodexResponseEvent>? observe = null)
    {
        var result = new List<CodexResponseEvent>();
        await foreach (var item in client.StreamResponseAsync(settings, [], ct))
        {
            result.Add(item);
            observe?.Invoke(item);
        }
        return result;
    }

    private static void AssertInterrupt(JsonObject frame)
    {
        Assert.Equal("response.interrupt", frame["type"]!.GetValue<string>());
        Assert.Equal("r1", frame["response_id"]!.GetValue<string>());
        Assert.Equal("discard_partial_items", frame["mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationWaitsForIdAndBothTerminalAndAck(bool beforeId, bool terminalFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var socket = new Socket();
        var connections = 0;
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
        {
            connections++;
            return Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket));
        });
        var observer = new Observer();
        var http = new Http();
        using var client = new CodexClient(new Credentials(), handler: http, usageObserver: observer);
        var seenCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = Read(client, Settings(session), cancel.Token, item =>
        {
            Assert.Null(item.CompletedOutput);
            if (item.Type == "response.created") seenCreated.TrySetResult();
        });
        Assert.Equal("response.create", (await socket.Sent(timeout.Token))["type"]!.GetValue<string>());
        if (beforeId) cancel.Cancel();
        socket.Receive(Created);
        if (!beforeId) { await seenCreated.Task.WaitAsync(timeout.Token); cancel.Cancel(); }
        AssertInterrupt(await socket.Sent(timeout.Token));
        socket.Receive(terminalFirst ? Terminal : Accepted);
        socket.Receive(terminalFirst ? Accepted : Terminal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        Assert.Equal(1, observer.Completed);
        Assert.Equal(7, observer.Receipt.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        Assert.Equal(0, observer.Incomplete);
        Assert.Equal(WebSocketState.Open, socket.State);
        var next = Read(client, Settings(session), timeout.Token);
        var frame = await socket.Sent(timeout.Token);
        Assert.Equal("response.create", frame["type"]!.GetValue<string>());
        Assert.Null(frame["previous_response_id"]);
        socket.Receive("""{"type":"response.completed","response":{"id":"r2"}}""");
        Assert.Single(await next);
        Assert.Equal(1, connections);
        Assert.Equal(2, observer.Completed);
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData("""{"type":"response.interrupt.failed","response_id":"r1"}""")]
    [InlineData("""{"type":"response.interrupt.accepted","response_id":"other"}""")]
    [InlineData(null)]
    public async Task FailedOrLostAckRetiresSocketButPreservesTerminalUsage(string? acknowledgement)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var socket = new Socket();
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
            Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket)));
        var observer = new Observer();
        var http = new Http();
        using var client = new CodexClient(new Credentials(), handler: http, usageObserver: observer);
        var read = Read(client, Settings(session), cancel.Token, item =>
        {
            if (item.Type == "response.created") cancel.Cancel();
        });
        await socket.Sent(timeout.Token);
        socket.Receive(Created);
        AssertInterrupt(await socket.Sent(timeout.Token));
        socket.Receive(Terminal);
        if (acknowledgement is null) socket.End(); else socket.Receive(acknowledgement);
        await Assert.ThrowsAsync<CodexProtocolException>(async () => await read);
        Assert.Equal(1, observer.Completed);
        Assert.Equal(0, observer.Incomplete);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task CancelBeforeDispatchKeepsUnusedSocketAndReleasesReservation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        using var socket = new Socket();
        var connections = 0;
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
        {
            connections++;
            cancel.Cancel();
            return Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket));
        });
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), handler: new Http(), usageObserver: observer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(client, Settings(session), cancel.Token));
        Assert.Equal(1, observer.NotDispatched);
        Assert.Equal(0, observer.Incomplete);
        Assert.Equal(WebSocketState.Open, socket.State);
        var read = Read(client, Settings(session), timeout.Token);
        Assert.Equal("response.create", (await socket.Sent(timeout.Token))["type"]!.GetValue<string>());
        socket.Receive("""{"type":"response.completed","response":{"id":"r2"}}""");
        await read;
        Assert.Equal(1, connections);
        Assert.Equal(1, observer.Completed);
    }

    [Fact]
    public async Task LateDrainAckCannotSettleNextResponseAndContinuationIsPreserved()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var drain = new CancellationTokenSource();
        using var socket = new Socket();
        var connections = 0;
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
        {
            connections++;
            return Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket));
        });
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), handler: new Http(), usageObserver: observer);
        var read = Read(client, Settings(session) with { WebSocketDrain = drain.Token }, timeout.Token, item =>
        {
            if (item.Type == "response.output_item.done") drain.Cancel();
        });
        await socket.Sent(timeout.Token);
        socket.Receive(Created);
        socket.Receive(Done);
        AssertInterrupt(await socket.Sent(timeout.Token));
        socket.Receive(Terminal);
        var terminal = (await read)[^1];
        Assert.True(terminal.Interrupted);
        var nextInput = terminal.CompletedOutput!;
        var next = Task.Run(async () =>
        {
            var result = new List<CodexResponseEvent>();
            await foreach (var item in client.StreamResponseAsync(Settings(session), nextInput, timeout.Token))
                result.Add(item);
            return result;
        }, timeout.Token);
        var create = await socket.Sent(timeout.Token);
        Assert.Equal("r1", create["previous_response_id"]!.GetValue<string>());
        Assert.Empty(create["input"]!.AsArray());
        socket.Receive(Accepted);
        socket.Receive("""{"type":"response.created","response":{"id":"r2"}}""");
        socket.Receive("""{"type":"response.completed","response":{"id":"r2"}}""");
        var events = await next;
        Assert.Equal(2, events.Count);
        Assert.Equal("r2", events[^1].ResponseId);
        Assert.Equal(2, observer.Completed);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task DrainReturnsCompletedItemsButDiscardsPartialOutput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var drain = new CancellationTokenSource();
        using var socket = new Socket();
        await using var session = new CodexResponsesWebSocketSession((_, _) =>
            Task.FromResult<CodexResponsesWebSocketSession.Connection?>(new(socket)));
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), handler: new Http(), usageObserver: observer);
        var read = Read(client, Settings(session) with { WebSocketDrain = drain.Token }, timeout.Token, item =>
        {
            if (item.Type == "response.output_item.added") drain.Cancel();
        });
        await socket.Sent(timeout.Token);
        socket.Receive(Created);
        socket.Receive(Done);
        socket.Receive(Added);
        AssertInterrupt(await socket.Sent(timeout.Token));
        socket.Receive(Accepted);
        socket.Receive("""{"type":"response.output_item.done","item":{"type":"function_call","status":"incomplete","call_id":"partial-call","name":"danger","arguments":"{"}}""");
        socket.Receive(Terminal);
        var terminal = (await read)[^1];
        Assert.True(terminal.Interrupted);
        Assert.Equal("response.incomplete", terminal.Type);
        Assert.Equal("finished", Assert.Single(terminal.CompletedOutput!).GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, observer.Completed);
        Assert.Equal(0, observer.Incomplete);
        Assert.Equal(WebSocketState.Open, socket.State);
    }
}
