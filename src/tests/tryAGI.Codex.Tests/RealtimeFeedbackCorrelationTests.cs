using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeFeedbackCorrelationTests
{
    [Fact]
    public void ExistingAppendContextBinarySignatureRemainsAvailable()
    {
        Assert.NotNull(typeof(CodexRealtimeConnection).GetMethod(nameof(CodexRealtimeConnection.AppendContextAsync),
            [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)]));
    }

    [Fact]
    public async Task ExplicitGaInvitationCarriesItsIdentityOnResponseCreateOnly()
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await connection.AppendCorrelatedContextAsync("Question", "interview-1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, socket.Sent.Count);
        Assert.False(socket.Sent[0].TryGetProperty("response", out _));
        Assert.Equal("interview-1", RequestId(socket.Sent[1]));
    }

    [Fact]
    public async Task PendingResponsePreservesIdentityAcrossUnrelatedToolReceipts()
    {
        var socket = new Socket("""{"type":"response.created"}""",
            """{"type":"response.done","response":{"status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken))
        {
            if (item.Type != "response.created") continue;
            await connection.AppendCorrelatedContextAsync("Question", "interview-1", cancellationToken: TestContext.Current.CancellationToken);
            await connection.AppendContextAsync("Other result", "call-1", "commentary", TestContext.Current.CancellationToken);
            Assert.DoesNotContain(socket.Sent, value => value.GetProperty("type").GetString() == "response.create");
        }
        Assert.Equal("interview-1", RequestId(socket.Sent[^1]));
    }

    [Fact]
    public async Task SleepWakeNeverReplaysCorrelatedPendingSpeech()
    {
        var socket = new Socket("""{"type":"response.created"}""",
            """{"type":"response.done","response":{"status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken))
        {
            if (item.Type != "response.created") continue;
            await connection.AppendCorrelatedContextAsync("Question", "interview-1", cancellationToken: TestContext.Current.CancellationToken);
            connection.SetResponseRequestsAllowed(false);
            connection.SetResponseRequestsAllowed(true);
        }
        Assert.DoesNotContain(socket.Sent, value => value.GetProperty("type").GetString() == "response.create");
    }

    [Fact]
    public async Task DistinctPendingInvitationsCannotShareOrOverwriteAnIdentity()
    {
        var socket = new Socket("""{"type":"response.created"}""",
            """{"type":"response.done","response":{"status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken))
        {
            if (item.Type != "response.created") continue;
            await connection.AppendCorrelatedContextAsync("Question", "interview-1", cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<CodexProtocolException>(() => connection.AppendCorrelatedContextAsync("Other question", "interview-2", cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Equal("interview-1", RequestId(socket.Sent[^1]));
    }

    [Fact]
    public async Task ANewAwakeAdmissionCanReplaceExpiredSuppressedMetadata()
    {
        var socket = new Socket("""{"type":"response.created"}""",
            """{"type":"response.done","response":{"status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken))
        {
            if (item.Type != "response.created") continue;
            await connection.AppendCorrelatedContextAsync("Question", "interview-1", cancellationToken: TestContext.Current.CancellationToken);
            connection.SetResponseRequestsAllowed(false);
            connection.SetResponseRequestsAllowed(true);
            await connection.AppendCorrelatedContextAsync("New question", "interview-2", cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Equal("interview-2", RequestId(socket.Sent[^1]));
    }

    [Fact]
    public async Task V3RejectsUnsupportedMetadataBeforeAnyWireWrite()
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v3");
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.AppendCorrelatedContextAsync("Question", "interview-1", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(socket.Sent);
    }

    private static string? RequestId(JsonElement value) => value.GetProperty("response").GetProperty("metadata")
        .GetProperty(CodexRealtimeConnection.ResponseRequestMetadataKey).GetString();

    private sealed class Socket(params string[] incoming) : WebSocket
    {
        private readonly Queue<string> messages = new(incoming);
        internal List<JsonElement> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => WebSocketState.Open;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (!messages.TryDequeue(out var value)) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var bytes = Encoding.UTF8.GetBytes(value);
            bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(buffer);
            Sent.Add(document.RootElement.Clone());
            return Task.CompletedTask;
        }
    }
}
