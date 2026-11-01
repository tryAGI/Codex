using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class SidebandTests
{
    [Theory]
    [InlineData("req_valid-123", "req_valid-123")]
    [InlineData("Bearer secret", null)]
    [InlineData("req\r\nAuthorization: secret", null)]
    public void HandshakeDiagnosticsAllowOnlyBoundedRequestIdentifiers(string supplied, string? retained)
    {
        var error = CodexRealtimeConnection.HandshakeFailure(404, supplied);
        Assert.Equal(404, error.StatusCode);
        Assert.Equal(retained, error.RequestId);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Null(CodexRealtimeConnection.HandshakeFailure(0, new string('a', 129)).RequestId);
        Assert.Null(CodexRealtimeConnection.HandshakeFailure(0, null).StatusCode);
    }

    private sealed class Socket(params string[] fragments) : WebSocket
    {
        private int index;
        public List<string> Sent { get; } = [];
        public Func<CancellationToken, Task>? BeforeSend { get; set; }
        public bool Aborted { get; private set; }
        public WebSocketCloseStatus? ReportedCloseStatus { get; init; }
        public Exception? ReceiveError { get; init; }
        public override WebSocketCloseStatus? CloseStatus => ReportedCloseStatus;
        public override string? CloseStatusDescription => "private close description";
        public override string? SubProtocol => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override void Abort() => Aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ReceiveError is { } error) throw error;
            if (index >= fragments.Length) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var bytes = Encoding.UTF8.GetBytes(fragments[index++]); bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, index == fragments.Length));
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (BeforeSend is { } beforeSend) await beforeSend(ct);
            ct.ThrowIfCancellationRequested();
            Sent.Add(Encoding.UTF8.GetString(buffer));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("speakable")]
    [InlineData("commentary")]
    public async Task V3SleepingContextNeverRequestsSpeechAndPreservesDelegation(string? channel)
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 8192);
        connection.SetResponseRequestsAllowed(false);
        await connection.AppendContextAsync("Synthetic background receipt", "delegation-1", channel, TestContext.Current.CancellationToken);
        using var sent = JsonDocument.Parse(Assert.Single(socket.Sent));
        Assert.Equal("commentary", sent.RootElement.GetProperty("channel").GetString());
        Assert.Equal("delegation-1", sent.RootElement.GetProperty("delegation_item_id").GetString());
        Assert.Equal("Synthetic background receipt", sent.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("response.create", socket.Sent[0]);
    }

    [Fact]
    public async Task V3ContextBoundAppliesToTheSelectedWireForm()
    {
        var wire = (System.Text.Json.Nodes.JsonNode.Parse("""{"type":"session.context.append","channel":"speakable","content":[]}""") ?? throw new InvalidDataException()).AsObject();
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, Encoding.UTF8.GetByteCount(wire.ToJsonString()));
        await connection.SendEventAsync(wire, TestContext.Current.CancellationToken);
        Assert.Single(socket.Sent);
        connection.SetResponseRequestsAllowed(false);
        await Assert.ThrowsAsync<CodexProtocolException>(() => connection.SendEventAsync(wire, TestContext.Current.CancellationToken));
        Assert.Single(socket.Sent); // Never fall back to speech when a quiet frame cannot fit.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V3QueuedContextCannotRegainSpeechPermissionAfterSleepAndWake(bool rawEvent)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var socket = new Socket { BeforeSend = async ct =>
        {
            if (Interlocked.Increment(ref sends) != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        } };
        await using var connection = new CodexRealtimeConnection(socket, 8192);
        var first = connection.AppendContextAsync("Existing background append", channel: "commentary", cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queued = rawEvent
            ? connection.SendEventAsync(new System.Text.Json.Nodes.JsonObject
                { ["type"] = "session.context.append", ["channel"] = "speakable", ["content"] = new System.Text.Json.Nodes.JsonArray() }, TestContext.Current.CancellationToken)
            : connection.AppendContextAsync("Old queued result", cancellationToken: TestContext.Current.CancellationToken);
        connection.SetResponseRequestsAllowed(false);
        connection.SetResponseRequestsAllowed(true);
        release.TrySetResult();
        await Task.WhenAll(first, queued);
        using var old = JsonDocument.Parse(socket.Sent[1]);
        Assert.Equal("commentary", old.RootElement.GetProperty("channel").GetString());
        await connection.AppendContextAsync("Fresh result after wake", cancellationToken: TestContext.Current.CancellationToken);
        using var fresh = JsonDocument.Parse(socket.Sent[2]);
        Assert.Equal("speakable", fresh.RootElement.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task V3SleepDuringMultipartAppendSuppressesRemainingChunksWithoutWaitingForSocket()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var socket = new Socket { BeforeSend = async ct =>
        {
            if (Interlocked.Increment(ref sends) != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        } };
        await using var connection = new CodexRealtimeConnection(socket, 8192);
        var append = connection.AppendContextAsync(new string('x', 1001), cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        connection.SetResponseRequestsAllowed(false);
        release.TrySetResult();
        await append;
        Assert.Equal(3, socket.Sent.Count);
        using var first = JsonDocument.Parse(socket.Sent[0]);
        Assert.Equal("speakable", first.RootElement.GetProperty("channel").GetString());
        foreach (var wire in socket.Sent.Skip(1))
        {
            using var next = JsonDocument.Parse(wire);
            Assert.Equal("commentary", next.RootElement.GetProperty("channel").GetString());
        }
    }

    [Fact]
    public async Task ClosureRetainsRemoteCodeWithoutPrivateDescription()
    {
        await using var connection = new CodexRealtimeConnection(new Socket { ReportedCloseStatus = WebSocketCloseStatus.NormalClosure }, 2048);
        await foreach (var _ in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) { }
        Assert.Equal("remote_close", (connection.Closure ?? throw new InvalidDataException("Required data was null.")).End);
        Assert.Equal(1000, connection.Closure.CloseCode);
        Assert.Null(connection.Closure.ErrorType);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(connection.Closure));
    }

    [Theory]
    [InlineData(true, "cancelled", null)]
    [InlineData(false, "receive_error", "WebSocketException")]
    public async Task ClosureDistinguishesCancellationFromTransportFailure(bool cancellation, string kind, string? errorType)
    {
        var error = cancellation ? (Exception)new OperationCanceledException("private") : new WebSocketException("private");
        await using var connection = new CodexRealtimeConnection(new Socket { ReceiveError = error }, 2048);
        await Assert.ThrowsAnyAsync<Exception>(async () => { await foreach (var _ in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) { } });
        Assert.Equal(kind, (connection.Closure ?? throw new InvalidDataException("Required data was null.")).End);
        Assert.Equal(errorType, connection.Closure.ErrorType);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(connection.Closure));
    }

    [Fact]
    public async Task SessionStartedReceiptIsRetainedAndAudioUsesV3Only()
    {
        var socket = new Socket("{\"type\":\"session.started\",\"session\":{\"id\":\"test\"}}");
        await using var connection = new CodexRealtimeConnection(socket, 2048);
        await connection.WaitForSessionStartedAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var events = new List<CodexRealtimeEvent>();
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal("session.started", Assert.Single(events).Type);
        await connection.UpdateSessionAsync(new("test") { Voice = "sol" }, TestContext.Current.CancellationToken);
        await connection.AppendAudioAsync(new byte[2], TestContext.Current.CancellationToken);
        using var update = JsonDocument.Parse(socket.Sent[0]);
        Assert.False(update.RootElement.GetProperty("session").TryGetProperty("model", out _));
        using var audio = JsonDocument.Parse(socket.Sent[1]);
        Assert.Equal("input_audio.append", audio.RootElement.GetProperty("type").GetString());
        using var inbound = JsonDocument.Parse("{\"type\":\"output_audio.delta\",\"audio\":\"AAA=\"}");
        var parsed = (CodexRealtimeProtocol.ReadAudio(inbound.RootElement) ?? throw new InvalidDataException("Required data was null."));
        Assert.Equal(2, parsed.Pcm16.Length); Assert.Equal(24000, parsed.SampleRate); Assert.Null(parsed.ItemId);
    }
    [Fact]
    public async Task ProviderRejectionNeverCreatesAcceptedSession()
    {
        await using var connection = new CodexRealtimeConnection(new Socket("{\"type\":\"error\",\"error\":{\"code\":\"not_allowed\",\"param\":\"session.delegation\",\"message\":\"private provider text\"}}"), 2048);
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => connection.WaitForSessionStartedAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal("not_allowed", error.RealtimeError?.Code);
        Assert.Equal("session.delegation", error.RealtimeError?.Parameter);
        Assert.DoesNotContain("private provider text", error.Message);
    }

    [Fact]
    public async Task FragmentedEventRetainsUnknownProviderMetadata()
    {
        var socket = new Socket("{\"type\":\"turn.done\",", "\"future_field\":123}");
        await using var connection = new CodexRealtimeConnection(socket, 1024);
        var events = new List<CodexRealtimeEvent>(); await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(123, Assert.Single(events).Payload.GetProperty("future_field").GetInt32());
    }

    [Fact]
    public async Task OutboundContextIsSerializedAndBounded()
    {
        var socket = new Socket(); await using var connection = new CodexRealtimeConnection(socket, 2048);
        await Task.WhenAll(connection.AppendContextAsync(new string('a', 1001), "d1", cancellationToken: TestContext.Current.CancellationToken), connection.AppendContextAsync("second", "d2", cancellationToken: TestContext.Current.CancellationToken));
        var ids = socket.Sent.Select(s => { using var j = JsonDocument.Parse(s); return j.RootElement.GetProperty("delegation_item_id").GetString(); }).ToArray();
        Assert.Equal(new[] { "d1", "d1", "d1", "d2" }, ids);
    }

    [Fact]
    public async Task TypedInputUsesV3ChunksContiguousWithConcurrentWorkerFinding()
    {
        var socket = new Socket { BeforeSend = async _ => await Task.Yield() };
        await using var connection = new CodexRealtimeConnection(socket, 2048);
        var text = string.Concat(Enumerable.Repeat("Ты тут? 👋", 180));
        await Task.WhenAll(connection.AppendUserTextAsync(text, cancellationToken: TestContext.Current.CancellationToken), connection.AppendContextAsync("worker fact", "d1", "commentary", cancellationToken: TestContext.Current.CancellationToken));
        var chunks = new List<string>();
        foreach (var sent in socket.Sent.SkipLast(1))
        {
            using var j = JsonDocument.Parse(sent);
            var item = j.RootElement;
            Assert.Equal("session.context.append", item.GetProperty("type").GetString());
            Assert.Equal("speakable", item.GetProperty("channel").GetString());
            Assert.False(item.TryGetProperty("delegation_item_id", out _));
            var chunk = (item.GetProperty("content")[0].GetProperty("text").GetString() ?? throw new InvalidDataException("Required data was null."));
            Assert.InRange(Encoding.UTF8.GetByteCount(chunk), 1, 500);
            Assert.DoesNotContain('\uFFFD', chunk);
            chunks.Add(chunk);
        }
        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Concat(chunks));
        using var finding = JsonDocument.Parse(socket.Sent.Last());
        Assert.Equal("d1", finding.RootElement.GetProperty("delegation_item_id").GetString());
        Assert.Equal("commentary", finding.RootElement.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task CancellingQueuedTypedInputLeavesWorkerWriteIntact()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new Socket { BeforeSend = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        await using var connection = new CodexRealtimeConnection(socket, 2048);
        var finding = connection.AppendContextAsync("worker fact", "d1", cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var input = connection.AppendUserTextAsync("cancelled request", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input);
        release.TrySetResult();
        await finding;
        Assert.Single(socket.Sent);
        Assert.DoesNotContain("cancelled request", socket.Sent[0]);
    }

    [Fact]
    public async Task PartialTypedWriteFailureDoesNotReplayOrCreateAResponse()
    {
        var writes = 0;
        var socket = new Socket { BeforeSend = _ => ++writes > 1
            ? Task.FromException(new WebSocketException("test interruption")) : Task.CompletedTask };
        await using var connection = new CodexRealtimeConnection(socket, 2048);
        await Assert.ThrowsAsync<WebSocketException>(() => connection.AppendUserTextAsync(new string('a', 1001), TestContext.Current.CancellationToken));
        Assert.Equal(2, writes);
        Assert.Single(socket.Sent);
        Assert.DoesNotContain("response.create", socket.Sent[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n\t")]
    public async Task EmptyTypedInputDoesNotWrite(string text)
    {
        var socket = new Socket();
        await using var connection = new CodexRealtimeConnection(socket, 2048);
        await Assert.ThrowsAsync<ArgumentException>(() => connection.AppendUserTextAsync(text, TestContext.Current.CancellationToken));
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task OversizedEventFailsAndCancellationStopsReading()
    {
        await using var oversized = new CodexRealtimeConnection(new Socket(new string('x', 2000)), 1024);
        await Assert.ThrowsAsync<CodexProtocolException>(async () => { await foreach (var _ in oversized.ReadEventsAsync(TestContext.Current.CancellationToken)) { } });
        var socket = new Socket(); var cancelled = new CodexRealtimeConnection(socket, 1024);
        await cancelled.DisposeAsync(); Assert.True(socket.Aborted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.AppendContextAsync("late", cancellationToken: TestContext.Current.CancellationToken));
    }
}
