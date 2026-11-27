using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace tryAGI.Codex;

/// <summary>Remote gRPC status. Messages and trailers may contain sensitive server data;
/// diagnostics expose only the status code.</summary>
public sealed class CodexCodeModeGrpcException(int statusCode) : IOException($"Code Mode gRPC status {statusCode}.")
{
    /// <summary>The exact nonzero gRPC status.</summary>
    public int StatusCode { get; } = statusCode;
}

/// <summary>Native uncompressed gRPC message framing for the pinned CodeModeHost service.
/// The sender is an explicitly authorized HTTP/2, response-headers-only host adapter. It must
/// enforce credentials, endpoint and network policy before sending, without redirects or
/// automatic POST retries. This transport never launches a process or opens an implicit connection.</summary>
public sealed class CodexCodeModeGrpcTransport
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "OpenSession", "CloseSession", "SubscribeToToolCalls", "CompleteToolCall",
        "AcknowledgeNotification", "Execute", "Wait", "YieldObservation", "CancelWait",
        "Terminate", "GetProcessMetrics",
    };

    private readonly Uri endpoint;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender;
    private readonly int maximumMessageBytes;

    /// <summary>Creates a transport bound to one explicitly selected HTTPS authority.
    /// Plaintext HTTP/2 is permitted only for explicitly opted-in loopback development hosts.</summary>
    public CodexCodeModeGrpcTransport(Uri endpoint,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender,
        int maximumMessageBytes = 16 * 1024 * 1024, bool allowInsecureLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
                !(allowInsecureLoopback && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/")
            throw new ArgumentException("Code Mode requires an authorized service authority.", nameof(endpoint));
        if (maximumMessageBytes is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        this.endpoint = endpoint;
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        this.maximumMessageBytes = maximumMessageBytes;
    }

    /// <summary>Sends a single protobuf request and streams complete response messages.
    /// Consumers must enumerate through EOF to observe final gRPC status. Disposal or cancellation
    /// closes the response stream without replaying the request. No status is inferred from HTTP 200.</summary>
    public async IAsyncEnumerable<byte[]> StreamAsync(string method, ReadOnlyMemory<byte> protobuf,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Methods.Contains(method)) throw new ArgumentException("Unknown Code Mode RPC.", nameof(method));
        if (protobuf.Length > maximumMessageBytes) throw new CodexProtocolException("Code Mode request is too large.");
        cancellationToken.ThrowIfCancellationRequested();
        var destination = new Uri(endpoint, "/codex.code_mode.v1.CodeModeHost/" + method);
        var framed = new byte[protobuf.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), (uint)protobuf.Length);
        protobuf.CopyTo(framed.AsMemory(5));

        using var request = new HttpRequestMessage(HttpMethod.Post, destination)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(framed),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));
        request.Headers.Add("grpc-accept-encoding", "identity");
        using var response = await sender(request, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } actual && actual != destination)
            throw new CodexProtocolException("Code Mode response has unexpected destination.");
        if (response.Version.Major != 2 || response.StatusCode != HttpStatusCode.OK)
            throw new CodexProtocolException("Code Mode requires successful HTTP/2 response.");
        if (response.Content.Headers.ContentType?.MediaType is not ("application/grpc" or "application/grpc+proto"))
            throw new CodexProtocolException("Invalid Code Mode gRPC content type.");
        if (response.Headers.TryGetValues("grpc-encoding", out var encodings) &&
            !encodings.SequenceEqual(new[] { "identity" }, StringComparer.OrdinalIgnoreCase))
            throw new CodexProtocolException("Unsupported Code Mode gRPC compression.");

        var initialStatus = Status(response.Headers);
        if (initialStatus is > 0) throw new CodexCodeModeGrpcException(initialStatus.Value);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var header = new byte[5];
        var sawMessage = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (first == 0) break;
            await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            if (initialStatus is not null)
                throw new CodexProtocolException("Code Mode trailers-only response contained messages.");
            if (header[0] != 0) throw new CodexProtocolException("Compressed Code Mode gRPC frames are unsupported.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
            if (length > maximumMessageBytes)
                throw new CodexProtocolException("Code Mode response message is too large.");
            var body = new byte[(int)length];
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            sawMessage = true;
            yield return body;
        }

        var trailingStatus = Status(response.TrailingHeaders);
        if (initialStatus is not null && trailingStatus is not null)
            throw new CodexProtocolException("Code Mode gRPC status was duplicated.");
        var status = trailingStatus ?? initialStatus ??
            throw new CodexProtocolException("Code Mode gRPC final status is missing.");
        if (status != 0) throw new CodexCodeModeGrpcException(status);
        if (initialStatus is not null && sawMessage)
            throw new CodexProtocolException("Invalid trailers-only Code Mode response.");
    }

    /// <summary>Completes a unary RPC only after receiving exactly one message and a successful
    /// final status. A lost or ambiguous response is never automatically retried.</summary>
    public async Task<byte[]> UnaryAsync(string method, ReadOnlyMemory<byte> protobuf,
        CancellationToken cancellationToken = default)
    {
        byte[]? result = null;
        await foreach (var message in StreamAsync(method, protobuf, cancellationToken).ConfigureAwait(false))
        {
            if (result is not null) throw new CodexProtocolException("Code Mode unary response contained multiple messages.");
            result = message;
        }
        return result ?? throw new CodexProtocolException("Code Mode unary response omitted its message.");
    }

    private static int? Status(HttpHeaders headers)
    {
        if (!headers.TryGetValues("grpc-status", out var values)) return null;
        var entries = values.ToArray();
        if (entries.Length != 1 || !int.TryParse(entries[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var code) || code is < 0 or > 16)
            throw new CodexProtocolException("Invalid Code Mode gRPC status.");
        return code;
    }
}
