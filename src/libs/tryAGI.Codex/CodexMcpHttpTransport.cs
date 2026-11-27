using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex;

/// <summary>HTTP failure with a known status. A failed MCP operation is never automatically replayed.</summary>
public sealed class CodexMcpHttpException(HttpStatusCode statusCode) : IOException(
    $"MCP HTTP request failed with status {(int)statusCode}.")
{
    /// <summary>Exact response status; for example, 404 may indicate an expired session.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>MCP Streamable HTTP framing. All network exchanges are delegated to an
/// explicitly authorized host sender, including any authentication, proxy and destination policy.
/// The sender must use response-headers-only completion, honor cancellation, and MUST NOT
/// redirect, rewrite the destination or automatically retry POST requests.</summary>
public sealed class CodexMcpHttpTransport : ICodexMcpTransport
{
    private const string SessionHeader = "Mcp-Session-Id";
    private const string ProtocolHeader = "MCP-Protocol-Version";
    private readonly Uri endpoint;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender;
    private readonly int maximumMessageBytes;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken stop;
    private readonly object gate = new();
    private readonly Channel<JsonElement> inbound = Channel.CreateBounded<JsonElement>(16);
    private readonly SemaphoreSlim responseSlots = new(32, 32);
    private readonly List<Task> pumps = [];
    private string? sessionId;
    private string protocolVersion = CodexMcpClient.LegacyProtocolVersion;
    private bool discoverySent;
    private bool initializationSent;
    private bool initialized;
    private bool listening;
    private bool closed;
    private Exception? failure;
    private Task? closeTask;

    /// <summary>Creates a transport pinned to one explicitly selected endpoint. HTTPS is required
    /// except for explicitly opted-in loopback development servers. No connection is opened here.</summary>
    public CodexMcpHttpTransport(Uri endpoint,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sender,
        int maximumMessageBytes = CodexMcpStdioTransport.DefaultMaximumMessageBytes,
        bool allowInsecureLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(allowInsecureLoopback && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("MCP requires an authorized HTTPS endpoint or opted-in HTTP loopback.", nameof(endpoint));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
        if (maximumMessageBytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        this.endpoint = endpoint;
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        this.maximumMessageBytes = maximumMessageBytes;
        stop = lifetime.Token;
    }

    /// <summary>Posts a JSON-RPC message. Request replies may arrive on independent SSE streams;
    /// a notification or server-request response must receive HTTP 202, not another reply.</summary>
    public async ValueTask SendAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.ValueKind != JsonValueKind.Object)
            throw new CodexProtocolException("MCP HTTP message must be a JSON object.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
        var ct = linked.Token;
        var method = CodexRealtimeProtocol.Text(message, "method");
        var request = method is not null && message.TryGetProperty("id", out _);
        var initialize = method == "initialize";
        var discover = method == "server/discover";
        var readyNotification = method == "notifications/initialized";
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes)) { message.WriteTo(writer); writer.Flush(); }
        if (bytes.WrittenCount > maximumMessageBytes)
            throw new CodexProtocolException("Outgoing MCP HTTP message exceeds the configured size limit.");

        ct.ThrowIfCancellationRequested();
        // Limit open response streams without blocking server-request responses or cancellation.
        if (request && !responseSlots.Wait(0))
            throw new IOException("Too many outstanding MCP HTTP response streams.");
        var ownsSlot = request;
        HttpResponseMessage? response = null;
        try
        {
            using var http = CreateRequest(HttpMethod.Post, initialize, method, message);
            http.Content = new ByteArrayContent(bytes.WrittenSpan.ToArray());
            http.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (initialize || discover)
            {
                lock (gate)
                {
                    EnsureUsable();
                    if (initialize)
                    {
                        if (initializationSent || initialized) throw new InvalidOperationException("MCP HTTP initialization cannot be replayed.");
                        initializationSent = true;
                    }
                    else
                    {
                        if (initializationSent || discoverySent) throw new InvalidOperationException("MCP HTTP discovery cannot be replayed.");
                        discoverySent = true;
                    }
                }
            }
            response = await sender(http, ct).ConfigureAwait(false);
            ValidateDestination(response);
            var discoveryRejection = discover && request && response.StatusCode == HttpStatusCode.BadRequest &&
                response.Content.Headers.ContentType?.MediaType == "application/json";
            if (!response.IsSuccessStatusCode && !discoveryRejection)
                throw new CodexMcpHttpException(response.StatusCode);
            CaptureSession(response, initialize);
            if (!request)
            {
                if (response.StatusCode != HttpStatusCode.Accepted)
                    throw new CodexProtocolException("MCP HTTP notification/response requires status 202.");
                if (readyNotification)
                {
                    lock (gate) { EnsureUsable(); initialized = true; }
                }
                return;
            }
            if (response.StatusCode != HttpStatusCode.OK && !discoveryRejection)
                throw new CodexProtocolException("MCP HTTP request requires status 200.");
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not ("application/json" or "text/event-stream"))
                throw new CodexProtocolException("Unsupported MCP HTTP response content type.");
            var expectedId = message.GetProperty("id").Clone();
            lock (gate)
            {
                EnsureUsable();
                pumps.RemoveAll(task => task.IsCompleted);
                var transferred = response;
                response = null;
                ownsSlot = false;
                pumps.Add(PumpAsync(transferred, media == "text/event-stream", expectedId, isListener: false, requireError: discoveryRejection));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation does not imply that the server did not receive the POST.
            throw;
        }
        catch (Exception error)
        {
            Fault(error);
            throw;
        }
        finally
        {
            response?.Dispose();
            if (ownsSlot) responseSlots.Release();
        }
    }

    /// <summary>Explicitly opens the optional server-to-client GET event stream after initialization.
    /// A server may reject GET with 405. No reconnect or event replay is automatic.</summary>
    public async Task<bool> OpenEventStreamAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
        lock (gate)
        {
            EnsureUsable();
            if (!initialized || listening) throw new InvalidOperationException("MCP HTTP event stream is not available.");
            listening = true;
        }
        HttpResponseMessage? response = null;
        var transferred = false;
        try
        {
            using var request = CreateRequest(HttpMethod.Get, initialize: false);
            response = await sender(request, linked.Token).ConfigureAwait(false);
            ValidateDestination(response);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed) return false;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new CodexMcpHttpException(response.StatusCode);
            CaptureSession(response, initialize: false);
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new CodexProtocolException("MCP HTTP GET requires an event stream.");
            lock (gate)
            {
                EnsureUsable();
                pumps.RemoveAll(task => task.IsCompleted);
                var owned = response;
                response = null;
                transferred = true;
                pumps.Add(PumpAsync(owned, isSse: true, expectedId: null, isListener: true));
            }
            return true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception error) { Fault(error); throw; }
        finally
        {
            response?.Dispose();
            if (!transferred) lock (gate) listening = false;
        }
    }

    /// <summary>Explicitly requests deletion of the current server session and closes the transport.
    /// A stateless server or a server that does not support DELETE has no remote termination receipt.</summary>
    public async Task<bool> TerminateSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            lock (gate)
            {
                EnsureUsable();
                if (sessionId is null) return false;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
            using var request = CreateRequest(HttpMethod.Delete, initialize: false);
            using var response = await sender(request, linked.Token).ConfigureAwait(false);
            ValidateDestination(response);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed) return false;
            if (!response.IsSuccessStatusCode) throw new CodexMcpHttpException(response.StatusCode);
            return true;
        }
        finally { await DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Reads the next decoded JSON-RPC message, or null after a local close.</summary>
    public async ValueTask<JsonElement?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (await inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            if (inbound.Reader.TryRead(out var message)) return message;
        return null;
    }

    internal void SetNegotiatedProtocol(string version)
    {
        lock (gate)
        {
            EnsureUsable();
            if (version == CodexMcpClient.ModernProtocolVersion)
            {
                if (!discoverySent || initializationSent || sessionId is not null)
                    throw new CodexProtocolException("Invalid modern MCP HTTP negotiation state.");
                initialized = true;
            }
            else if (!initializationSent || !CodexMcpClient.IsKnownLegacyVersion(version))
                throw new CodexProtocolException("Invalid legacy MCP HTTP negotiation state.");
            protocolVersion = version;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, bool initialize, string? rpcMethod = null,
        JsonElement? message = null)
    {
        lock (gate)
        {
            EnsureUsable();
            var discover = rpcMethod == "server/discover";
            var modern = discover || protocolVersion == CodexMcpClient.ModernProtocolVersion;
            if (!initialize && !discover && !initializationSent && !initialized)
                throw new InvalidOperationException("MCP HTTP initialization is required.");
            if (modern && method != HttpMethod.Post)
                throw new InvalidOperationException("Legacy session event streams and deletion are not available in modern mode.");
            var request = new HttpRequestMessage(method, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (method == HttpMethod.Post)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!initialize)
                request.Headers.Add(ProtocolHeader, discover ? CodexMcpClient.ModernProtocolVersion : protocolVersion);
            if (modern && rpcMethod is not null)
            {
                request.Headers.Add("Mcp-Method", rpcMethod);
                if (message is { } payload && payload.TryGetProperty("params", out var parameters) &&
                    parameters.ValueKind == JsonValueKind.Object && CodexRealtimeProtocol.Text(parameters, "name") is { } name)
                    request.Headers.Add("Mcp-Name", name);
            }
            if (sessionId is not null) request.Headers.Add(SessionHeader, sessionId);
            return request;
        }
    }

    private void ValidateDestination(HttpResponseMessage response)
    {
        // This detects a broken sender contract, but only the host can prevent a redirect
        // before credentials or request bodies are transmitted.
        if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint)
            throw new CodexProtocolException("MCP HTTP response has an unexpected destination.");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new CodexProtocolException("MCP HTTP redirects are not followed.");
    }

    private void CaptureSession(HttpResponseMessage response, bool initialize)
    {
        if (!response.Headers.TryGetValues(SessionHeader, out var values)) return;
        var ids = values.ToArray();
        if (ids.Length != 1 || ids[0].Length is < 1 or > 1024 ||
            ids[0].Any(character => character is < '!' or > '~'))
            throw new CodexProtocolException("Invalid MCP HTTP session identifier.");
        lock (gate)
        {
            EnsureUsable();
            if (initialize && sessionId is null) sessionId = ids[0];
            else if (!string.Equals(sessionId, ids[0], StringComparison.Ordinal))
                throw new CodexProtocolException("MCP HTTP session identifier changed unexpectedly.");
        }
    }

    private async Task PumpAsync(HttpResponseMessage response, bool isSse, JsonElement? expectedId, bool isListener,
        bool requireError = false)
    {
        try
        {
            using (response)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(stop).ConfigureAwait(false);
                if (!isSse)
                {
                    var bytes = new ArrayBufferWriter<byte>();
                    var buffer = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(), stop).ConfigureAwait(false)) != 0)
                    {
                        if (read > maximumMessageBytes - bytes.WrittenCount)
                            throw new CodexProtocolException("Incoming MCP HTTP message exceeds the configured size limit.");
                        bytes.Write(buffer.AsSpan(0, read));
                    }
                    var message = Parse(bytes.WrittenMemory);
                    if (!MatchesResponse(message, expectedId) ||
                        requireError && (!message.TryGetProperty("error", out _) || message.TryGetProperty("result", out _)))
                        throw new CodexProtocolException("MCP HTTP response has an invalid or unexpected request ID/result.");
                    await inbound.Writer.WriteAsync(message, stop).ConfigureAwait(false);
                    return;
                }

                var reader = new SseLineReader(stream, maximumMessageBytes);
                var data = new ArrayBufferWriter<byte>();
                string? eventName = null;
                while (await reader.ReadLineAsync(stop).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (data.WrittenCount != 0)
                        {
                            if (eventName is not (null or "" or "message"))
                                throw new CodexProtocolException("Unsupported MCP SSE event type.");
                            var message = Parse(data.WrittenMemory[..^1]);
                            var isResponse = !message.TryGetProperty("method", out _) && message.TryGetProperty("id", out _);
                            if (isResponse && !MatchesResponse(message, expectedId))
                                throw new CodexProtocolException("MCP SSE response has an unexpected request ID.");
                            await inbound.Writer.WriteAsync(message, stop).ConfigureAwait(false);
                            if (isResponse) return;
                        }
                        data.Clear();
                        eventName = null;
                        continue;
                    }
                    if (line[0] == (byte)':') continue;
                    var colon = Array.IndexOf(line, (byte)':');
                    var field = Encoding.ASCII.GetString(line, 0, colon < 0 ? line.Length : colon);
                    var offset = colon < 0 ? line.Length : colon + 1;
                    if (offset < line.Length && line[offset] == (byte)' ') offset++;
                    if (field == "event") eventName = Encoding.UTF8.GetString(line, offset, line.Length - offset);
                    if (field != "data") continue;
                    if (line.Length - offset >= maximumMessageBytes - data.WrittenCount)
                        throw new CodexProtocolException("Incoming MCP SSE event exceeds the configured size limit.");
                    data.Write(line.AsSpan(offset));
                    data.GetSpan(1)[0] = (byte)'\n';
                    data.Advance(1);
                }
                if (expectedId is not null || data.WrittenCount != 0)
                    throw new CodexProtocolException("MCP SSE ended before the expected complete response.");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Fault(error); }
        finally
        {
            if (isListener) lock (gate) listening = false;
            else responseSlots.Release();
        }
    }

    private static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CodexProtocolException("MCP HTTP message must be a JSON object.");
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new CodexProtocolException("Malformed MCP HTTP JSON."); }
    }

    private static bool MatchesResponse(JsonElement message, JsonElement? expected) =>
        expected is { } id && !message.TryGetProperty("method", out _) &&
        message.TryGetProperty("id", out var actual) && JsonElement.DeepEquals(actual, id);

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (failure is not null) throw new IOException("MCP HTTP connection is no longer usable.", failure);
    }

    private void Fault(Exception error)
    {
        lock (gate)
        {
            if (closed || failure is not null) return;
            failure = error;
            inbound.Writer.TryComplete(error);
        }
        lifetime.Cancel();
    }

    /// <summary>Closes the local transport and outstanding streams without replay or an implicit DELETE.</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closeTask is not null) return new ValueTask(closeTask);
            closed = true;
            inbound.Writer.TryComplete();
            lifetime.Cancel();
            closeTask = Task.WhenAll(pumps.ToArray());
            return new ValueTask(closeTask);
        }
    }

    private sealed class SseLineReader(Stream stream, int limit)
    {
        private readonly byte[] buffer = new byte[8192];
        private int offset;
        private int length;
        private bool skipLf;

        public async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (offset == length)
                {
                    length = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    offset = 0;
                    if (length == 0) return line.WrittenCount == 0 ? null : line.WrittenSpan.ToArray();
                }
                if (skipLf)
                {
                    skipLf = false;
                    if (buffer[offset] == (byte)'\n') { offset++; continue; }
                }
                var value = buffer[offset++];
                if (value is (byte)'\r' or (byte)'\n')
                {
                    skipLf = value == (byte)'\r';
                    return line.WrittenSpan.ToArray();
                }
                if (line.WrittenCount == limit)
                    throw new CodexProtocolException("MCP SSE line exceeds the configured size limit.");
                line.GetSpan(1)[0] = value;
                line.Advance(1);
            }
        }
    }
}
