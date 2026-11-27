using System.Threading.Channels;

namespace tryAGI.Codex;

/// <summary>One independent realtime lane exceeded its configured count or byte capacity.</summary>
public sealed class CodexRealtimeQueueOverflowException(string lane)
    : InvalidOperationException("Realtime " + lane + " queue is full; no input was silently discarded.");

internal sealed class CodexRealtimeBoundedQueue<T>(int count, int maximumBytes, string name)
{
    private readonly Channel<(T Item, int Bytes)> channel = Channel.CreateBounded<(T, int)>(
        new BoundedChannelOptions(count) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Lock gate = new();
    private int bytes;

    internal bool TryWrite(T item, int size)
    {
        if (size < 0 || size > maximumBytes) return false;
        lock (gate)
        {
            if (size > maximumBytes - bytes || !channel.Writer.TryWrite((item, size))) return false;
            bytes += size;
            return true;
        }
    }

    internal void Write(T item, int size)
    {
        if (!TryWrite(item, size)) throw new CodexRealtimeQueueOverflowException(name);
    }

    internal async Task RunAsync(Func<T, CancellationToken, Task> consume, CancellationToken ct)
    {
        await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            lock (gate) bytes -= entry.Bytes;
            await consume(entry.Item, ct).ConfigureAwait(false);
        }
    }
}
