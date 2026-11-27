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
        public Func<CancellationToken, Task>? AfterFirstSend { get; init; }
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
            return Sent.Count == 1 && AfterFirstSend is { } after ? after(ct) : Task.CompletedTask;
        }
    }

    private static JsonObject Create() => new() { ["type"] = "response.create" };

    private const string SilentCall = """{"type":"conversation.item.done","response_id":"active","item":{"type":"function_call","name":"remain_silent","call_id":"silent-call","id":"silent-item","arguments":"{}"}}""";
    private const string Active = """{"type":"response.created","response":{"id":"active"}}""";
    private const string Finished = """{"type":"response.done","response":{"id":"active","status":"completed"}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemainSilentSendsExactlyOneEmptyReceiptWithoutCreatingSpeech(bool suppressed)
    {
        var socket = new Socket(Active, SilentCall, SilentCall, Finished);
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        if (suppressed) connection.SetResponseRequestsAllowed(false);
        var noops = 0;
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Noop is null) continue;
            noops++;
            Assert.Null(item.Delegation);
            Assert.Equal("silent-call", item.Noop.CallId);
            Assert.Equal(SilentCall, item.Payload.GetRawText());
        }
        Assert.Equal(2, noops);
        var receipt = Assert.Single(socket.Sent);
        Assert.Equal("conversation.item.create", receipt.GetProperty("type").GetString());
        var output = receipt.GetProperty("item");
        Assert.Equal("function_call_output", output.GetProperty("type").GetString());
        Assert.Equal("silent-call", output.GetProperty("call_id").GetString());
        Assert.Equal("", output.GetProperty("output").GetString());
        Assert.Equal(0, socket.Creates);
    }

    [Fact]
    public async Task SilentReceiptClearsAudioCursorAndUnblocksOnlyExplicitQueuedResponse()
    {
        var socket = new Socket(Active,
            """{"type":"response.output_audio.delta","response_id":"active","item_id":"audio","delta":"AQACAA==","samples_per_channel":2400}""",
            """{"type":"response.function_call_arguments.done","response_id":"active","call_id":"silent-call"}""",
            SilentCall, Finished,
            """{"type":"input_audio_buffer.speech_started"}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        await foreach (var item in connection.ReadEventsAsync(ct))
        {
            if (item.Noop is not null)
            {
                Assert.Equal(0, socket.Creates);
                await connection.SendEventAsync(Create(), ct);
                Assert.Equal(0, socket.Creates);
            }
        }
        Assert.Equal(1, socket.Creates);
        Assert.Equal(2, socket.Sent.Count);
        Assert.Equal("conversation.item.create", socket.Sent[0].GetProperty("type").GetString());
        Assert.Equal("response.create", socket.Sent[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task NoopClearsAudioCursorBeforeSpeechStarts()
    {
        var socket = new Socket(Active,
            """{"type":"response.output_audio.delta","response_id":"active","item_id":"audio","delta":"AQACAA==","samples_per_channel":2400}""",
            SilentCall, """{"type":"input_audio_buffer.speech_started"}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) { }
        Assert.Equal("conversation.item.create", Assert.Single(socket.Sent).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v3")]
    [InlineData("v2")]
    public async Task WrongAdapterStaleOrUnrelatedFunctionDoesNotEmitSilentReceipt(string protocol)
    {
        var socket = new Socket(Active,
            protocol == "v2" ? SilentCall.Replace("active", "stale", StringComparison.Ordinal) : SilentCall,
            """{"type":"conversation.item.done","response_id":"active","item":{"type":"function_call","name":"unrelated","id":"other"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, protocol);
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken))
            if (protocol == "v2" && item.Noop is not null) Assert.True(item.StaleForFeedback);
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task AmbiguousSilentReceiptIsNeverAutomaticallyReplayed()
    {
        var socket = new Socket(Active, SilentCall, SilentCall)
        {
            AfterFirstSend = _ => Task.FromException(new WebSocketException("ambiguous")),
        };
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            await foreach (var item in connection.ReadEventsAsync(ct)) { }
        });
        await foreach (var item in connection.ReadEventsAsync(ct)) Assert.NotNull(item.Noop);
        Assert.Single(socket.Sent);
        Assert.Equal(0, socket.Creates);
    }

    [Theory]
    [InlineData("v1", "user", "input_text")]
    [InlineData("v1", "developer", "input_text")]
    [InlineData("v1", "assistant", "output_text")]
    [InlineData("v2", "user", "input_text")]
    [InlineData("v2", "developer", "input_text")]
    [InlineData("v2", "assistant", "output_text")]
    public async Task NativeLegacyTextDispatchPreservesPinnedRoleAndContentType(string protocol, string role, string contentType)
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 8192, protocol);
        var ct = TestContext.Current.CancellationToken;
        await connection.AppendRoleTextAsync("text", role, ct);
        var message = socket.Sent[0];
        Assert.Equal("conversation.item.create", message.GetProperty("type").GetString());
        var item = message.GetProperty("item");
        Assert.Equal(role, item.GetProperty("role").GetString());
        var content = item.GetProperty("content")[0];
        Assert.Equal(contentType, content.GetProperty("type").GetString());
        Assert.Equal("text", content.GetProperty("text").GetString());
        Assert.Equal(protocol == "v2" && role == "user" ? 1 : 0, socket.Creates);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task NativeBackingItemFeedbackPreservesDeveloperRole(string protocol)
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 8192, protocol);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await connection.SendReasoningFeedbackAsync(connection.CaptureFeedbackAdmission(),
            CodexRealtimeFeedbackKind.Item, "artifact", "handoff", null, null, ct));
        var item = Assert.Single(socket.Sent).GetProperty("item");
        Assert.Equal("developer", item.GetProperty("role").GetString());
        Assert.Equal("input_text", item.GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("speakable", false)]
    [InlineData("commentary", false)]
    [InlineData("speakable", true)]
    [InlineData("commentary", true)]
    public async Task RevokingFencedMultipartFeedbackStopsEveryRemainingFrame(string channel, bool reasoning)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new Socket
        {
            AfterFirstSend = async ct =>
            {
                started.SetResult();
                await release.Task.WaitAsync(ct);
            },
        };
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v3");
        var ct = TestContext.Current.CancellationToken;
        var admission = connection.CaptureFeedbackAdmission();
        var text = new string('x', 1400);
        var send = reasoning
            ? connection.SendReasoningFeedbackAsync(admission, CodexRealtimeFeedbackKind.Progress, text, "handoff", channel, null, ct)
            : connection.AppendFencedContextAsync(admission, text, "handoff", channel, ct);
        try
        {
            await started.Task.WaitAsync(ct);
            connection.InvalidatePendingFeedback();
        }
        finally { release.TrySetResult(); }
        Assert.False(await send);
        var first = Assert.Single(socket.Sent);
        Assert.Equal(channel, first.GetProperty("channel").GetString());
        Assert.False(connection.IsFeedbackAdmissionCurrent(admission));
        Assert.True(await connection.AppendFencedContextAsync(connection.CaptureFeedbackAdmission(), "new turn", channel: channel, cancellationToken: ct));
        Assert.Equal(2, socket.Sent.Count);
    }

    [Fact]
    public async Task FencedReceiptRejectedBeforeDispatchCanBeRetriedByCurrentGeneration()
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 1024, "v2");
        var ct = TestContext.Current.CancellationToken;
        var admission = connection.CaptureFeedbackAdmission();
        await Assert.ThrowsAsync<CodexProtocolException>(() =>
            connection.AppendFencedContextAsync(admission, new string('x', 2048), "call", "commentary", ct));
        Assert.Empty(socket.Sent);
        connection.InvalidatePendingFeedback();
        Assert.True(await connection.AppendFencedContextAsync(connection.CaptureFeedbackAdmission(), "valid", "call", "commentary", ct));
        var message = Assert.Single(socket.Sent);
        Assert.Equal("function_call_output", message.GetProperty("item").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AmbiguousFencedReceiptIsNeverResent()
    {
        var socket = new Socket { AfterFirstSend = _ => Task.FromException(new WebSocketException("ambiguous")) };
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var ct = TestContext.Current.CancellationToken;
        var admission = connection.CaptureFeedbackAdmission();
        await Assert.ThrowsAsync<WebSocketException>(() =>
            connection.SendReasoningFeedbackAsync(admission, CodexRealtimeFeedbackKind.Complete, "done", "call", "speakable", null, ct));
        connection.InvalidatePendingFeedback();
        Assert.False(await connection.SendReasoningFeedbackAsync(connection.CaptureFeedbackAdmission(),
            CodexRealtimeFeedbackKind.Complete, "done", "call", "speakable", null, ct));
        Assert.Single(socket.Sent);
    }

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
