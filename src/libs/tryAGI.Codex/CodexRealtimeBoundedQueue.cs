using System.Threading.Channels;

namespace tryAGI.Codex;

/// <summary>One independent realtime lane exceeded its configured count or byte capacity.</summary>
public sealed class CodexRealtimeQueueOverflowException(string lane)
    : InvalidOperationException("Realtime " + lane + " queue is full; no input was silently discarded.");

/// <summary>Final accounting of one independent realtime lane. Processing is not a
/// provider or client delivery receipt.</summary>
/// <param name="Lane">Lane name.</param>
/// <param name="Accepted">Items admitted to the queue.</param>
/// <param name="Processed">Consumer callbacks that returned normally.</param>
/// <param name="Abandoned">Failed, cancelled, or unprocessed admitted items.</param>
public sealed record CodexRealtimeQueueReport(string Lane, long Accepted, long Processed, long Abandoned);

internal sealed class CodexRealtimeBoundedQueue<T>(int count, int maximumBytes, string name)
{
    private readonly Channel<(T Item, int Bytes)> channel = Channel.CreateBounded<(T, int)>(
        new BoundedChannelOptions(count) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Lock gate = new();
    private int bytes;
    private long accepted;
    private long processed;
    private long abandoned;

    internal CodexRealtimeQueueReport Report
    {
        get { lock (gate) return new(name, accepted, processed, abandoned); }
    }

    internal bool TryWrite(T item, int size)
    {
        if (size < 0 || size > maximumBytes) return false;
        lock (gate)
        {
            if (size > maximumBytes - bytes || !channel.Writer.TryWrite((item, size))) return false;
            bytes += size;
            accepted++;
            return true;
        }
    }

    internal void Write(T item, int size)
    {
        if (!TryWrite(item, size)) throw new CodexRealtimeQueueOverflowException(name);
    }

    internal async Task RunAsync(Func<T, CancellationToken, Task> consume, CancellationToken ct)
    {
        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                lock (gate) bytes -= entry.Bytes;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await consume(entry.Item, ct).ConfigureAwait(false);
                    lock (gate) processed++;
                }
                catch
                {
                    lock (gate) abandoned++;
                    throw;
                }
            }
        }
        finally
        {
            // Close admission and release queued payloads even when a consumer faults or
            // the attachment is retired. Do not invoke abandoned callbacks on a new run.
            lock (gate)
            {
                channel.Writer.TryComplete();
                while (channel.Reader.TryRead(out var pending))
                {
                    bytes -= pending.Bytes;
                    abandoned++;
                }
            }
        }
    }
}
