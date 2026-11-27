using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace tryAGI.Codex;

/// <summary>Physical and logical identities of one completed rollout representation.</summary>
/// <param name="LogicalBytes">Uncompressed JSONL byte length.</param>
/// <param name="LogicalSha256">SHA-256 of original logical bytes.</param>
/// <param name="EncodedBytes">Compressed representation byte length.</param>
/// <param name="EncodedSha256">SHA-256 of compressed representation.</param>
public sealed record CodexRolloutRepresentation(long LogicalBytes, string LogicalSha256, long EncodedBytes, string EncodedSha256)
{
    /// <summary>Checks whether two completed representations address identical logical bytes.</summary>
    public bool HasSameLogicalSource(CodexRolloutRepresentation other) =>
        LogicalBytes == other.LogicalBytes && string.Equals(LogicalSha256, other.LogicalSha256, StringComparison.Ordinal);
}

/// <summary>
/// Rust-compatible streaming Zstandard codec for .jsonl.zst rollouts. The host explicitly
/// supplies a trusted absolute path to its installed libzstd (1.4 or later); the SDK never
/// searches for or downloads native libraries. Source streams are read-only, and destination
/// streams must be private staging objects. No rollout is replaced or deleted by this codec.
/// </summary>
public sealed class CodexRolloutZstandard : IAsyncDisposable
{
    private const int BufferBytes = 64 * 1024;
    private const int MaximumWindowLog = 27;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Native native;
    private IntPtr library;
    private bool disposed;

    private CodexRolloutZstandard(IntPtr library, Native native)
    {
        this.library = library;
        this.native = native;
    }

    /// <summary>
    /// Loads only the explicitly supplied host-trusted library. The host owns binary
    /// provenance, architecture compatibility, patching, and directory permissions.
    /// </summary>
    public static CodexRolloutZstandard Open(string trustedLibraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedLibraryPath);
        if (!Path.IsPathFullyQualified(trustedLibraryPath) || trustedLibraryPath != Path.GetFullPath(trustedLibraryPath))
            throw new ArgumentException("Zstandard requires a normalized absolute library path.", nameof(trustedLibraryPath));
        var handle = NativeLibrary.Load(trustedLibraryPath);
        try
        {
            var api = new Native(handle);
            if (api.Version() < 10400) throw new PlatformNotSupportedException("Zstandard 1.4 or later is required.");
            return new(handle, api);
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    /// <summary>
    /// Streams the original JSONL bytes into an empty private staging stream, with the same
    /// compression level as the pinned Rust rollout worker. Input and output limits are hard
    /// byte bounds. A failed or cancelled staging stream must not be published.
    /// </summary>
    public Task<CodexRolloutRepresentation> EncodeAsync(Stream logical, Stream encoded,
        long maximumLogicalBytes, long maximumEncodedBytes, CancellationToken cancellationToken = default) =>
        TranscodeAsync(logical, encoded, maximumLogicalBytes, maximumEncodedBytes, encode: true, cancellationToken);

    /// <summary>
    /// Decodes concatenated Zstandard frames into an empty private staging stream. For
    /// seekable migration, use a host-owned private temporary file as the destination,
    /// then rewind it after success and pass it to CodexRolloutReader. The host must compare
    /// the returned logical digest with its trusted revision before restoring a cursor.
    /// No source-file materialization or durable mutation is performed.
    /// </summary>
    public Task<CodexRolloutRepresentation> DecodeAsync(Stream encoded, Stream logical,
        long maximumEncodedBytes, long maximumLogicalBytes, CancellationToken cancellationToken = default) =>
        TranscodeAsync(encoded, logical, maximumEncodedBytes, maximumLogicalBytes, encode: false, cancellationToken);

    private async Task<CodexRolloutRepresentation> TranscodeAsync(Stream source, Stream destination,
        long maximumSourceBytes, long maximumDestinationBytes, bool encode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSourceBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDestinationBytes, 1);
        if (ReferenceEquals(source, destination) || !source.CanRead || !destination.CanWrite ||
            (source.CanSeek && source.Position != 0) ||
            (destination.CanSeek && (destination.Position != 0 || destination.Length != 0)))
            throw new ArgumentException("Rollout codec requires distinct complete source and empty private destination streams.");

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var destinationHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var context = native.Create(encode);
            try
            {
                native.Configure(context, encode);
                var input = new byte[BufferBytes];
                var output = new byte[BufferBytes];
                long sourceBytes = 0, destinationBytes = 0;
                var frameEnded = false;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // Read no more than the remaining budget plus a one-byte overflow probe.
                    var remaining = maximumSourceBytes - sourceBytes;
                    var request = remaining >= BufferBytes ? BufferBytes : (int)remaining + 1;
                    var count = await source.ReadAsync(input.AsMemory(0, request), ct).ConfigureAwait(false);
                    if (count > remaining) throw new InvalidDataException("Rollout source exceeded its byte bound.");
                    sourceBytes += count;
                    sourceHash.AppendData(input, 0, count);

                    if (!encode && count == 0)
                    {
                        if (!frameEnded) throw new InvalidDataException("Incomplete Zstandard frame at end of rollout.");
                        break;
                    }

                    var finish = count == 0;
                    var position = 0;
                    Step step;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        step = native.Process(context, encode, input, count, position, output, finish);
                        if (step.InputPosition < position || step.InputPosition > count || step.OutputBytes < 0 ||
                            step.OutputBytes > output.Length)
                            throw new InvalidDataException("Invalid Zstandard buffer progress.");
                        if (step.InputPosition == position && step.OutputBytes == 0 &&
                            (position < count || (finish && !step.Ended)))
                            throw new InvalidDataException("Zstandard failed to advance its stream.");
                        position = step.InputPosition;
                        frameEnded = step.Ended;
                        if (step.OutputBytes > maximumDestinationBytes - destinationBytes)
                            throw new InvalidDataException("Rollout output exceeded its byte bound.");
                        if (step.OutputBytes != 0)
                        {
                            await destination.WriteAsync(output.AsMemory(0, step.OutputBytes), ct).ConfigureAwait(false);
                            destinationHash.AppendData(output, 0, step.OutputBytes);
                            destinationBytes += step.OutputBytes;
                        }
                    }
                    while (position < count || (finish && !step.Ended) ||
                        (!encode && !step.Ended && step.OutputBytes == output.Length));

                    if (finish) break;
                }
                await destination.FlushAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var sourceDigest = Convert.ToHexString(sourceHash.GetHashAndReset());
                var destinationDigest = Convert.ToHexString(destinationHash.GetHashAndReset());
                return encode
                    ? new(sourceBytes, sourceDigest, destinationBytes, destinationDigest)
                    : new(destinationBytes, destinationDigest, sourceBytes, sourceDigest);
            }
            finally { native.Release(context, encode); }
        }
        finally { gate.Release(); }
    }

    /// <summary>Waits for the current operation and releases the trusted native library.</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
        }
        finally { gate.Release(); }
    }

    private readonly record struct Step(int InputPosition, int OutputBytes, bool Ended);

    private sealed unsafe class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct InputBuffer { internal void* Data; internal nuint Size; internal nuint Position; }
        [StructLayout(LayoutKind.Sequential)]
        private struct OutputBuffer { internal void* Data; internal nuint Size; internal nuint Position; }

        private readonly delegate* unmanaged[Cdecl]<uint> version;
        private readonly delegate* unmanaged[Cdecl]<IntPtr> createEncoder;
        private readonly delegate* unmanaged[Cdecl]<IntPtr> createDecoder;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, nuint> freeEncoder;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, nuint> freeDecoder;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, nuint> configureEncoder;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, int, int, nuint> configureDecoder;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, OutputBuffer*, InputBuffer*, int, nuint> compress;
        private readonly delegate* unmanaged[Cdecl]<IntPtr, OutputBuffer*, InputBuffer*, nuint> decompress;
        private readonly delegate* unmanaged[Cdecl]<nuint, uint> isError;

        internal Native(IntPtr library)
        {
            version = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, "ZSTD_versionNumber");
            createEncoder = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(library, "ZSTD_createCCtx");
            createDecoder = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(library, "ZSTD_createDCtx");
            freeEncoder = (delegate* unmanaged[Cdecl]<IntPtr, nuint>)NativeLibrary.GetExport(library, "ZSTD_freeCCtx");
            freeDecoder = (delegate* unmanaged[Cdecl]<IntPtr, nuint>)NativeLibrary.GetExport(library, "ZSTD_freeDCtx");
            configureEncoder = (delegate* unmanaged[Cdecl]<IntPtr, int, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_CCtx_setParameter");
            configureDecoder = (delegate* unmanaged[Cdecl]<IntPtr, int, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_DCtx_setParameter");
            compress = (delegate* unmanaged[Cdecl]<IntPtr, OutputBuffer*, InputBuffer*, int, nuint>)NativeLibrary.GetExport(library, "ZSTD_compressStream2");
            decompress = (delegate* unmanaged[Cdecl]<IntPtr, OutputBuffer*, InputBuffer*, nuint>)NativeLibrary.GetExport(library, "ZSTD_decompressStream");
            isError = (delegate* unmanaged[Cdecl]<nuint, uint>)NativeLibrary.GetExport(library, "ZSTD_isError");
        }

        internal uint Version() => version();

        internal IntPtr Create(bool encode)
        {
            var context = encode ? createEncoder() : createDecoder();
            return context != IntPtr.Zero ? context : throw new IOException("Could not allocate Zstandard context.");
        }

        internal void Release(IntPtr context, bool encode) => Check(encode ? freeEncoder(context) : freeDecoder(context));

        internal void Configure(IntPtr context, bool encode)
        {
            // ZSTD_c_compressionLevel = 100; ZSTD_d_windowLogMax = 100.
            // Bound native decoder memory before accepting even the first frame.
            Check(encode ? configureEncoder(context, 100, 3) : configureDecoder(context, 100, MaximumWindowLog));
        }

        internal Step Process(IntPtr context, bool encode, byte[] input, int count, int position, byte[] output, bool finish)
        {
            fixed (byte* src = input)
            fixed (byte* dst = output)
            {
                var inBuffer = new InputBuffer { Data = src, Size = (nuint)count, Position = (nuint)position };
                var outBuffer = new OutputBuffer { Data = dst, Size = (nuint)output.Length, Position = 0 };
                // ZSTD_e_continue = 0; ZSTD_e_end = 2.
                var result = encode
                    ? compress(context, &outBuffer, &inBuffer, finish ? 2 : 0)
                    : decompress(context, &outBuffer, &inBuffer);
                Check(result);
                return new(checked((int)inBuffer.Position), checked((int)outBuffer.Position), result == 0);
            }
        }

        private void Check(nuint result)
        {
            if (isError(result) != 0) throw new InvalidDataException("Zstandard rejected the rollout stream or codec parameters.");
        }
    }
}
