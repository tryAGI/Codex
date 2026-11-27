using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeSocketParityTests
{
    private sealed class Socket(string[] incoming, bool holdOpen = false) : WebSocket
    {
        private readonly Queue<string> messages = new(incoming);
        public List<JsonElement> Sent { get; } = [];
        public int Reads { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => WebSocketState.Open;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            Reads++;
            if (!messages.TryDequeue(out var value))
            {
                if (holdOpen) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new(0, WebSocketMessageType.Close, true);
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            bytes.CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(buffer.AsMemory());
            Sent.Add(document.RootElement.Clone());
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("v1", "wss://api.openai.com/v1/realtime?intent=quicksilver&model=snapshot", "wss://api.openai.com/v1/realtime?intent=quicksilver&call_id=rtc_test")]
    [InlineData("v2", "wss://api.openai.com/v1/realtime?model=snapshot", "wss://api.openai.com/v1/realtime?call_id=rtc_test")]
    [InlineData("v3", "wss://api.openai.com/v1/live?model=snapshot", "wss://api.openai.com/v1/live/rtc_test")]
    public void RustDirectAndSidebandUris(string protocol, string direct, string sideband)
    {
        var provider = new Uri("https://api.openai.com/v1");
        Assert.Equal(direct, CodexClient.DirectRealtimeUri(provider, "snapshot", protocol).AbsoluteUri);
        Assert.Equal(sideband, CodexRealtimeProtocol.SidebandUri(provider, "rtc_test", protocol).AbsoluteUri);
        Assert.Throws<CodexProtocolException>(() => CodexRealtimeProtocol.SidebandUri(provider, "../admin", protocol));
    }

    [Theory]
    [InlineData("v1", "https://example.com/proxy/v1/", "/proxy/v1/realtime")]
    [InlineData("v2", "https://example.com/proxy/v1", "/proxy/v1/realtime")]
    [InlineData("v3", "https://example.com/proxy/v1", "/proxy/v1")]
    [InlineData("v3", "https://example.com/proxy/v1/", "/proxy/v1/")]
    [InlineData("v3", "https://example.com/proxy/realtime/", "/proxy/live")]
    [InlineData("v3", "https://example.com/proxy/live/", "/proxy/live")]
    [InlineData("v3", "https://example.com/custom/", "/custom/")]
    public void RustAdapterPathNormalizationIsNotUniform(string protocol, string endpoint, string path)
    {
        Assert.Equal(path, CodexRealtimeProtocol.RealtimeSocketUri(new(endpoint), protocol, null).AbsolutePath);
    }

    [Theory]
    [InlineData("https://chatgpt.com/backend-api/codex")]
    [InlineData("https://chatgpt.com/backend-api/codex/")]
    public void ChatGptDirectRouteRemainsCanonicalWithExplicitQuery(string backend)
    {
        Assert.Equal("wss://chatgpt.com/backend-api/codex?model=snapshot&trace=1",
            CodexClient.DirectRealtimeUri(new Uri(backend), "snapshot", "v3",
                new Dictionary<string, string> { ["trace"] = "1" }).AbsoluteUri);
    }

    [Theory]
    [InlineData("v1", "wss://example.com/v1/realtime?foo=bar&intent=quicksilver&model=snapshot&trace=1")]
    [InlineData("v2", "wss://example.com/v1/realtime?foo=bar&model=snapshot&trace=1")]
    [InlineData("v3", "wss://example.com/v1/live?foo=bar&model=snapshot&trace=1")]
    public void ExplicitProviderQueryFollowsRustPrecedence(string protocol, string expected)
    {
        var query = new Dictionary<string, string> { ["trace"] = "1", ["intent"] = "ignored", ["model"] = "ignored" };
        Assert.Equal(expected, CodexClient.DirectRealtimeUri(new("https://example.com/v1/realtime?foo=bar"), "snapshot", protocol, query).AbsoluteUri);
        Assert.Equal(3, query.Count);
    }

    [Fact]
    public void QueryModelIsUsedOnlyWhenNoExplicitModelAndReservedCallIdentityCannotBeOverridden()
    {
        var query = new Dictionary<string, string> { ["model"] = "snapshot", ["trace"] = "space & equals=1" };
        Assert.Equal("wss://example.com/v1/realtime?model=snapshot&trace=space+%26+equals%3D1&call_id=rtc_test",
            CodexRealtimeProtocol.SidebandUri(new("https://example.com/v1"), "rtc_test", "v2", query).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.SidebandUri(new("https://example.com/v1?%63all_id=rtc_other"), "rtc_test", "v2"));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.SidebandUri(new("https://example.com/v1"), "rtc_test", "v3",
            new Dictionary<string, string> { ["call_id"] = "rtc_other" }));
        Assert.Throws<ArgumentException>(() => CodexClient.DirectRealtimeUri(new("https://example.com/v1"), "snapshot", "v2",
            new Dictionary<string, string> { ["call_id"] = "rtc_other" }));
    }

    [Fact]
    public void RustUriPreservesExistingQuery()
    {
        Assert.Equal("wss://example.com/proxy/v1/live/rtc_test?trace=1",
            CodexRealtimeProtocol.SidebandUri(new("https://example.com/proxy/v1/live?trace=1"), "rtc_test", "v3").AbsoluteUri);
        Assert.Equal("wss://example.com/v1/realtime?foo=bar&model=snapshot",
            CodexClient.DirectRealtimeUri(new("https://example.com/v1/realtime?foo=bar"), "snapshot", "v2").AbsoluteUri);
    }

    [Theory]
    [InlineData("v1", "quicksilver", "input_audio_buffer.append")]
    [InlineData("v2", "realtime", "input_audio_buffer.append")]
    [InlineData("v3", null, "input_audio.append")]
    public async Task DirectSessionUpdatesImmediatelyAndPreservesEvents(string protocol, string? type, string audioType)
    {
        var incoming = protocol == "v3"
            ? new[] { """{"type":"future.event","raw":42}""", """{"type":"session.started","session":{"id":"s"}}""" }
            : new[] { """{"type":"session.created","session":{"id":"s"}}""" };
        var socket = new Socket(incoming);
        await using var connection = new CodexRealtimeConnection(socket, 8192, protocol, chatGptCall: false);
        var ct = TestContext.Current.CancellationToken;
        await connection.InitializeDirectSessionAsync(new("hello") { Protocol = protocol }, TimeSpan.FromSeconds(1), false, ct);
        var update = Assert.Single(socket.Sent);
        Assert.Equal("session.update", update.GetProperty("type").GetString());
        var session = update.GetProperty("session");
        Assert.False(session.TryGetProperty("model", out _));
        if (type is not null) Assert.Equal(type, session.GetProperty("type").GetString());
        else Assert.False(session.TryGetProperty("type", out _));
        Assert.Equal(protocol == "v3" ? 2 : 0, socket.Reads);
        await connection.AppendAudioAsync(new byte[] { 0, 0 }, ct);
        Assert.Equal(audioType, socket.Sent[1].GetProperty("type").GetString());
        var events = new List<CodexRealtimeEvent>();
        await foreach (var item in connection.ReadEventsAsync(ct)) events.Add(item);
        Assert.Equal(incoming.Length, events.Count);
        if (protocol == "v3") Assert.Equal(42, events[0].Payload.GetProperty("raw").GetInt32());
    }

    [Theory]
    [InlineData("session.started")]
    [InlineData("session.updated")]
    public async Task V3RequiresTypedReadinessAndRetainsEarlierMalformedReceipts(string readyType)
    {
        var incoming = new[]
        {
            """{"type":"session.started","session":{"model":"wrong-missing-id"}}""",
            """{"type":"session.updated","session":{"id":42,"model":"wrong-number-id"}}""",
            """{"type":"session.started","session":{"id":" ","model":"wrong-blank-id"}}""",
            """{"type":"session.created","session":{"id":"wrong-adapter","model":"wrong-adapter"}}""",
            "{\"type\":\"" + readyType + "\",\"session\":{\"id\":\"valid\",\"model\":\"snapshot\"}}",
        };
        var socket = new Socket(incoming);
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v3", chatGptCall: false);
        var ct = TestContext.Current.CancellationToken;
        await connection.InitializeDirectSessionAsync(new("test") { Model = "snapshot" }, TimeSpan.FromSeconds(1), false, ct);
        Assert.Equal(incoming.Length, socket.Reads);
        Assert.Equal("snapshot", connection.ModelIdentity.Read("snapshot").ConfirmedModel);
        var events = new List<string>();
        await foreach (var item in connection.ReadEventsAsync(ct)) events.Add(item.Payload.GetRawText());
        Assert.Equal(incoming, events);
        Assert.Single(socket.Sent);
    }

    [Theory]
    [InlineData("""{"type":"session.started"}""")]
    [InlineData("""{"type":"session.started","session":{"id":false,"model":"snapshot"}}""")]
    [InlineData("""{"type":"session.updated","session":{"id":"","model":"snapshot"}}""")]
    [InlineData("""{"type":"session.created","session":{"id":"s","model":"snapshot"}}""")]
    public async Task V3MalformedOrWrongAdapterReceiptCannotEstablishReadinessOrModelIdentity(string frame)
    {
        var socket = new Socket([frame]);
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v3");
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<CodexProtocolException>(() => connection.InitializeDirectSessionAsync(
            new("test") { Model = "snapshot" }, TimeSpan.FromSeconds(1), false, ct));
        Assert.Null(connection.ModelIdentity.Read("snapshot").ProviderReportedModel);
        Assert.Single(socket.Sent);
    }

    [Theory]
    [InlineData("v1", "session.created")]
    [InlineData("v1", "session.started")]
    [InlineData("v2", "session.started")]
    [InlineData("v3", "session.created")]
    public async Task OtherAdapterSessionCannotOverwriteConnectionModelEvidence(string protocol, string eventType)
    {
        var socket = new Socket([
            """{"type":"session.updated","session":{"id":"s","model":"snapshot"}}""",
            "{\"type\":\"" + eventType + "\",\"session\":{\"id\":\"other\",\"model\":\"wrong\"}}",
        ]);
        await using var connection = new CodexRealtimeConnection(socket, 8192, protocol);
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) { }
        Assert.Equal("snapshot", connection.ModelIdentity.Read("snapshot").ConfirmedModel);
        Assert.Equal("session.updated", connection.ModelIdentity.Read("snapshot").SourceEvent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V3RejectsClosureOrProviderErrorBeforeReady(bool providerError)
    {
        var socket = new Socket(providerError ? ["""{"type":"error","error":{"code":"invalid_session"}}"""] : []);
        await using var connection = new CodexRealtimeConnection(socket, 8192);
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => connection.InitializeDirectSessionAsync(new("test"), TimeSpan.FromSeconds(1), false, TestContext.Current.CancellationToken));
        if (providerError) Assert.Equal("invalid_session", error.RealtimeError?.Code);
        Assert.Single(socket.Sent);
    }

    [Fact]
    public async Task V3ReadinessHasBoundedDeadline()
    {
        var socket = new Socket([], holdOpen: true);
        await using var connection = new CodexRealtimeConnection(socket, 8192);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.InitializeDirectSessionAsync(new("test"), TimeSpan.FromMilliseconds(30), false, TestContext.Current.CancellationToken));
        Assert.Single(socket.Sent);
    }
}
