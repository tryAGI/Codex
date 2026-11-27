using System.Net;

namespace tryAGI.Codex;

// Owns the original response body. Revocation invalidates future reads and cancels active reads,
// including callers that obtained a stream using ResponseHeadersRead before the policy changed.
internal sealed class CodexRevocableHttpContent : HttpContent
{
    private readonly HttpContent inner;
    private readonly CancellationToken revoked;
    private readonly CancellationTokenRegistration registration;
    private readonly long? contentLength;

    internal CodexRevocableHttpContent(HttpContent inner, CancellationToken revoked)
    {
        this.inner = inner;
        this.revoked = revoked;
        contentLength = inner.Headers.ContentLength;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        registration = revoked.Register(static state =>
        {
            try { ((HttpContent)state!).Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { /* Revocation is still authoritative. */ }
        }, inner);
    }

    private static CodexProviderRouteException Denied() => new(CodexProviderRouteFailure.NetworkPolicyRevoked);
    private void Check() { if (revoked.IsCancellationRequested) throw Denied(); }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked);
        try
        {
            await inner.CopyToAsync(stream, context, linked.Token).ConfigureAwait(false);
            Check();
        }
        catch (Exception error) when (revoked.IsCancellationRequested && error is not OutOfMemoryException)
        { throw Denied(); }
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked);
        try
        {
            inner.CopyTo(stream, context, linked.Token);
            Check();
        }
        catch (Exception error) when (revoked.IsCancellationRequested && error is not OutOfMemoryException)
        { throw Denied(); }
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
        => CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked);
        try
        {
            var stream = await inner.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            Check();
            return new RevocableStream(stream, revoked);
        }
        catch (Exception error) when (revoked.IsCancellationRequested && error is not OutOfMemoryException)
        { throw Denied(); }
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked);
        try
        {
            var stream = inner.ReadAsStream(linked.Token);
            Check();
            return new RevocableStream(stream, revoked);
        }
        catch (Exception error) when (revoked.IsCancellationRequested && error is not OutOfMemoryException)
        { throw Denied(); }
    }

    protected override bool TryComputeLength(out long length)
    {
        Check();
        if (contentLength is { } known) { length = known; return true; }
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            registration.Dispose();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class RevocableStream(Stream inner, CancellationToken revoked) : Stream
    {
        private void Check() { if (revoked.IsCancellationRequested) throw Denied(); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length { get { Check(); return inner.Length; } }
        public override long Position { get { Check(); return inner.Position; } set { Check(); inner.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Check();
            try
            {
                var count = inner.Read(buffer);
                Check();
                return count;
            }
            catch (Exception error) when (revoked.IsCancellationRequested && error is not OutOfMemoryException)
            { throw Denied(); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked);
            try
            {
                var count = await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                Check();
                return count;
            }
            catch (Exception error) when (revoked.IsCancellationRequested && error is not OutOfMemoryException)
            { throw Denied(); }
        }

        public override long Seek(long offset, SeekOrigin origin) { Check(); return inner.Seek(offset, origin); }
        public override void Flush() { Check(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
