using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

// Public WebSocket boundary: no SDK-internal connection or attachment constructor.
internal sealed class DirectRealtimeTestSocket : WebSocket
{
    private WebSocketState state = WebSocketState.Open;
    internal Channel<string> Incoming { get; } = Channel.CreateUnbounded<string>();
    internal Channel<string> Outgoing { get; } = Channel.CreateUnbounded<string>();
    internal int CloseCommands;
    internal int Disposals;
    internal string? FinalObservation { get; init; }

    public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.CloseReceived ? WebSocketCloseStatus.NormalClosure : null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => state;
    public override void Abort() { state = WebSocketState.Aborted; Incoming.Writer.TryComplete(); }
    public override void Dispose() { Interlocked.Increment(ref Disposals); Abort(); }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        => CloseOutputAsync(status, description, ct);

    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref CloseCommands);
        if (FinalObservation is { } final) Incoming.Writer.TryWrite(final);
        Incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        if (!await Incoming.Reader.WaitToReadAsync(ct))
        {
            state = WebSocketState.CloseReceived;
            return new(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null);
        }
        var bytes = Encoding.UTF8.GetBytes(await Incoming.Reader.ReadAsync(ct));
        bytes.CopyTo(buffer.AsSpan());
        return new(bytes.Length, WebSocketMessageType.Text, true);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = Encoding.UTF8.GetString(buffer.AsSpan());
        await Outgoing.Writer.WriteAsync(text, ct);
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.GetProperty("type").GetString() == "session.close")
            await CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
    }
}
