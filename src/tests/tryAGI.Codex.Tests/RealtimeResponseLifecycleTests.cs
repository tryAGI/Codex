using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeResponseLifecycleTests
{
    private sealed class Socket(params string[] incoming) : WebSocket
    {
        private readonly Queue<string> messages = new(incoming);
        public List<JsonElement> Sent { get; } = [];
        public int Creates => Sent.Count(x => x.GetProperty("type").GetString() == "response.create");
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
            if (!messages.TryDequeue(out var value))
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var bytes = Encoding.UTF8.GetBytes(value);
            bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(buffer.AsMemory());
            Sent.Add(document.RootElement.Clone());
            return Task.CompletedTask;
        }
    }

    private static JsonObject Create() => new() { ["type"] = "response.create" };

    [Theory]
    [InlineData("Conversation already has an active response in progress: response_older", true)]
    [InlineData("Rate limit reached", false)]
    public async Task OnlyPinnedActiveResponseConflictIsRetried(string message, bool retry)
    {
        var error = JsonSerializer.Serialize(new { type = "error", error = new { message } });
        var socket = new Socket(error, """{"type":"response.done","response":{"id":"response_older","status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        await connection.SendEventAsync(Create(), ct);
        await foreach (var item in connection.ReadEventsAsync(ct))
            if (item.Type == "error") Assert.Equal(1, socket.Creates);
        Assert.Equal(retry ? 2 : 1, socket.Creates);
    }

    [Theory]
    [InlineData("response.cancelled", null)]
    [InlineData("response.done", "incomplete")]
    [InlineData("response.done", "completed")]
    public async Task MatchingTerminalReleasesOnePendingResponseAndMismatchedTerminalCannot(string terminal, string? status)
    {
        var socket = new Socket("""{"type":"response.created","response":{"id":"active"}}""",
            """{"type":"response.done","response":{"id":"old","status":"completed"}}""",
            JsonSerializer.Serialize(new { type = terminal, response = new { id = "active", status } }));
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Type == "response.created")
            {
                await connection.SendEventAsync(Create(), ct);
                await connection.SendEventAsync(Create(), ct);
                Assert.Equal(0, socket.Creates);
            }
            else if (item.Response?.ResponseId == "old")
            {
                Assert.True(item.StaleForFeedback);
                Assert.Null(item.Admission);
                Assert.Equal(0, socket.Creates);
            }
        }
        Assert.Equal(1, socket.Creates);
    }

    [Fact]
    public async Task ConfiguredResponseQueueRetainsRawPayloadAndCorrelation()
    {
        var socket = new Socket("""{"type":"response.created","response":{"id":"active"}}""",
            """{"type":"response.done","response":{"id":"active"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        var request = JsonNode.Parse("""{"type":"response.create","event_id":"request","response":{"metadata":{"client_request_id":"client"},"future_field":42}}""")!.AsObject();
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Type != "response.created") continue;
            await connection.SendEventAsync(request, ct);
            request["event_id"] = "mutated";
            Assert.Equal(0, socket.Creates);
        }
        var sent = Assert.Single(socket.Sent);
        Assert.Equal("request", sent.GetProperty("event_id").GetString());
        Assert.Equal(42, sent.GetProperty("response").GetProperty("future_field").GetInt32());
    }

    [Fact]
    public async Task UnrelatedErrorIdentityCannotRetryAndRevokedAdmissionCannotReplayConflict()
    {
        var socket = new Socket(
            """{"type":"error","error":{"event_id":"another_request","message":"Conversation already has an active response in progress: old"}}""",
            """{"type":"error","error":{"event_id":"request","message":"Conversation already has an active response in progress: old"}}""",
            """{"type":"response.done","response":{"id":"old"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        await connection.SendEventAsync(new JsonObject { ["type"] = "response.create", ["event_id"] = "request" }, ct);
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Error?.EventId == "another_request") Assert.Equal(1, socket.Creates);
            if (item.Error?.EventId == "request")
            {
                connection.SetResponseRequestsAllowed(false);
                connection.SetResponseRequestsAllowed(true);
            }
        }
        Assert.Equal(1, socket.Creates);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("audio_item", true)]
    [InlineData("different_item", false)]
    public async Task SpeechInterruptTruncatesOnlyMatchingOutputAndFencesLateFeedback(string? speechItem, bool truncate)
    {
        var speech = JsonSerializer.Serialize(new { type = "input_audio_buffer.speech_started", item_id = speechItem });
        var socket = new Socket("""{"type":"response.created","response":{"id":"active"}}""",
            """{"type":"response.output_audio.delta","response_id":"active","item_id":"audio_item","delta":"AAAAAA==","samples_per_channel":480}""",
            """{"type":"response.audio.delta","response_id":"active","item_id":"audio_item","delta":"AAAAAA==","samples_per_channel":480}""",
            """{"type":"response.function_call_arguments.done","response_id":"active","call_id":"tool"}""",
            speech,
            """{"type":"response.output_audio.delta","response_id":"active","item_id":"audio_item","delta":"AAAAAA==","samples_per_channel":480}""",
            """{"type":"response.done","response":{"id":"active","status":"cancelled"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        CodexRealtimeConnection.FeedbackAdmission? old = null;
        var audioCount = 0;
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Type == "response.function_call_arguments.done")
            {
                old = item.Admission;
                await connection.SendEventAsync(Create(), ct);
            }
            if (item.Type is "response.output_audio.delta" or "response.audio.delta")
            {
                audioCount++;
                Assert.Equal(audioCount == 3, item.StaleForFeedback);
            }
            if (item.Type == "input_audio_buffer.speech_started")
            {
                Assert.NotNull(old);
                Assert.False(await connection.AppendFencedContextAsync(old, "stale", "tool", cancellationToken: ct));
                await connection.AppendContextAsync("also stale", "tool", cancellationToken: ct);
            }
        }
        Assert.Equal(0, socket.Creates);
        if (truncate)
        {
            var sent = Assert.Single(socket.Sent);
            Assert.Equal("conversation.item.truncate", sent.GetProperty("type").GetString());
            Assert.Equal("audio_item", sent.GetProperty("item_id").GetString());
            Assert.Equal(40, sent.GetProperty("audio_end_ms").GetInt32());
        }
        else Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task ExplicitCancellationIsIdBoundAndNotRepeated()
    {
        var socket = new Socket("""{"type":"response.created","response":{"id":"active"}}""",
            """{"type":"response.cancelled","response_id":"active"}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Type != "response.created") continue;
            var admission = connection.CaptureFeedbackAdmission();
            Assert.False(await connection.CancelResponseAsync("old", ct));
            Assert.True(connection.IsFeedbackAdmissionCurrent(admission));
            Assert.True(await connection.CancelResponseAsync("active", ct));
            Assert.False(await connection.CancelResponseAsync("active", ct));
            Assert.False(connection.IsFeedbackAdmissionCurrent(admission));
        }
        var sent = Assert.Single(socket.Sent);
        Assert.Equal("response.cancel", sent.GetProperty("type").GetString());
        Assert.Equal("active", sent.GetProperty("response_id").GetString());
    }

    [Fact]
    public async Task FramelessFeedbackTokenIsBoundToTurnConnectionAndSpeechAdmission()
    {
        var socket = new Socket("""{"type":"delegation.created","item":{"type":"delegation","target":"client","id":"old","content":[{"type":"input_text","text":"first"}]}}""",
            """{"type":"turn.done","turn":{"role":"user","transcript":"new"}}""",
            """{"type":"delegation.created","item":{"type":"delegation","target":"client","id":"new","content":[{"type":"input_text","text":"second"}]}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v3");
        var otherSocket = new Socket();
        await using var replacement = new CodexRealtimeConnection(otherSocket, 8192, "v3");
        var ct = TestContext.Current.CancellationToken;
        CodexRealtimeConnection.FeedbackAdmission? old = null;
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Delegation?.Id == "old") old = item.Admission;
            if (item.Delegation?.Id != "new") continue;
            Assert.NotNull(old);
            var fresh = Assert.IsType<CodexRealtimeConnection.FeedbackAdmission>(item.Admission);
            Assert.False(await connection.AppendFencedContextAsync(old, "old result", "old", cancellationToken: ct));
            Assert.False(await replacement.AppendFencedContextAsync(fresh, "wrong session", "new", cancellationToken: ct));
            Assert.True(await connection.AppendFencedContextAsync(fresh, "new result", "new", cancellationToken: ct));
            connection.SetResponseRequestsAllowed(false);
            connection.SetResponseRequestsAllowed(true);
            Assert.False(await connection.AppendFencedContextAsync(fresh, "revoked", "new", cancellationToken: ct));
            connection.RetireFeedbackAdmission();
            connection.SetResponseRequestsAllowed(true);
            Assert.False(await connection.AppendFencedContextAsync(connection.CaptureFeedbackAdmission(), "retired", cancellationToken: ct));
        }
        Assert.Equal("delegation.context.append", Assert.Single(socket.Sent).GetProperty("type").GetString());
        Assert.Empty(otherSocket.Sent);
    }

    [Fact]
    public async Task V1UsesConversationItemAndHandoffAppendRatherThanV3Commands()
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v1");
        var ct = TestContext.Current.CancellationToken;
        await connection.AppendUserTextAsync("user text", ct);
        await connection.AppendContextAsync("result", "handoff", cancellationToken: ct);
        Assert.Equal("conversation.item.create", socket.Sent[0].GetProperty("type").GetString());
        Assert.Equal("user", socket.Sent[0].GetProperty("item").GetProperty("role").GetString());
        Assert.Equal("conversation.handoff.append", socket.Sent[1].GetProperty("type").GetString());
        Assert.Equal("handoff", socket.Sent[1].GetProperty("handoff_id").GetString());
        Assert.Equal("result", socket.Sent[1].GetProperty("output_text").GetString());
    }
}
