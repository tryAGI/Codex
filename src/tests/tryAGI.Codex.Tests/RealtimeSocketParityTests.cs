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
