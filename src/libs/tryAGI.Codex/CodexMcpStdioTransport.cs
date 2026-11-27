using System.Buffers;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Newline-delimited JSON-RPC over already authorized stdio streams.
/// This adapter owns the streams but never launches, kills, or authorizes a process.
/// A broken frame or partial write poisons the connection; messages are never replayed.</summary>
public sealed class CodexMcpStdioTransport : ICodexMcpTransport
{
    /// <summary>Default defensive limit on a single UTF-8 message, excluding the newline.</summary>
    public const int DefaultMaximumMessageBytes = 16 * 1024 * 1024;

    private readonly Stream input;
    private readonly Stream output;
    private readonly int maximumMessageBytes;
    private readonly byte[] readBuffer = new byte[8192];
    private readonly object disposeGate = new();
    private int readOffset;
    private int readLength;
    private int broken;
    private int closed;
    private Task? disposeTask;

    /// <summary>Transfers ownership of server stdout and stdin, respectively, to the transport.
    /// Stderr must be handled separately by the host. One reader and one writer may run concurrently;
    /// the MCP client serializes all writes.</summary>
    public CodexMcpStdioTransport(Stream serverOutput, Stream serverInput,
        int maximumMessageBytes = DefaultMaximumMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(serverOutput);
        ArgumentNullException.ThrowIfNull(serverInput);
        if (!serverOutput.CanRead || !serverInput.CanWrite)
            throw new ArgumentException("The MCP stdio streams must be readable and writable, respectively.");
        if (maximumMessageBytes is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        input = serverOutput;
        output = serverInput;
        this.maximumMessageBytes = maximumMessageBytes;
    }

    /// <summary>Sends a compact UTF-8 JSON object followed by a newline and flushes the stream.</summary>
    public async ValueTask SendAsync(JsonElement message, CancellationToken cancellationToken)
    {
        EnsureUsable();
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ValueKind != JsonValueKind.Object)
            throw new CodexProtocolException("An MCP stdio message must be a JSON object.");

        // Re-serialize to avoid embedded formatting newlines in the source JSON.
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            message.WriteTo(writer);
            writer.Flush();
        }
        if (bytes.WrittenCount > maximumMessageBytes)
            throw new CodexProtocolException("An outgoing MCP stdio message exceeds the configured size limit.");
        bytes.GetSpan(1)[0] = (byte)'\n';
        bytes.Advance(1);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await output.WriteAsync(bytes.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The peer may have consumed a partial or complete message. Do not resume writes.
            Interlocked.Exchange(ref broken, 1);
            throw;
        }
    }

    /// <summary>Reads exactly one bounded UTF-8 JSON object, preserving unread bytes for the next message.
    /// Returns null only at a clean frame boundary on EOF.</summary>
    public async ValueTask<JsonElement?> ReceiveAsync(CancellationToken cancellationToken)
    {
        EnsureUsable();
        cancellationToken.ThrowIfCancellationRequested();
        var frame = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                if (readOffset == readLength)
                {
                    readLength = await input.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    readOffset = 0;
                    if (readLength == 0)
                    {
                        if (frame.WrittenCount != 0)
                            throw new CodexProtocolException("MCP stdio ended in the middle of a frame.");
                        return null;
                    }
                }
                var newline = Array.IndexOf(readBuffer, (byte)'\n', readOffset, readLength - readOffset);
                var end = newline < 0 ? readLength : newline;
                var length = end - readOffset;
                if (length > maximumMessageBytes - frame.WrittenCount)
                    throw new CodexProtocolException("An incoming MCP stdio message exceeds the configured size limit.");
                readBuffer.AsSpan(readOffset, length).CopyTo(frame.GetSpan(length));
                frame.Advance(length);
                readOffset = newline < 0 ? end : end + 1;
                if (newline < 0) continue;

                using var json = JsonDocument.Parse(frame.WrittenMemory);
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                    throw new CodexProtocolException("An MCP stdio message must be a JSON object.");
                return json.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            Interlocked.Exchange(ref broken, 1);
            // Do not include arbitrary server output in the exception.
            throw new CodexProtocolException("Malformed MCP stdio JSON.");
        }
        catch
        {
            Interlocked.Exchange(ref broken, 1);
            throw;
        }
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
        if (Volatile.Read(ref broken) != 0)
            throw new IOException("The MCP stdio connection is no longer usable.");
    }

    /// <summary>Closes the owned streams once, including when the connection has failed.</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            return new ValueTask(disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref closed, 1);
        try
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(input, output))
                await input.DisposeAsync().ConfigureAwait(false);
        }
    }
}
