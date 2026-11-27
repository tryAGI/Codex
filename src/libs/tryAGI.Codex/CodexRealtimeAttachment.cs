using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace tryAGI.Codex;

/// <summary>Explicit authorization policy for joining an already-created realtime call.</summary>
public enum CodexRealtimeAttachmentAuthentication
{
    /// <summary>Use SDK credentials only at an official OpenAI origin.</summary>
    ClientCredentials,
    /// <summary>Use only headers supplied by the caller, for an explicit destination.</summary>
    ExternalHeaders,
}

/// <summary>The current SDK OAuth account does not match the persisted logical call account.</summary>
public sealed class CodexRealtimeAccountMismatchException() : InvalidOperationException("Realtime account identity changed.");

/// <summary>Parameters for a transport-only attachment to an existing logical call.</summary>
public sealed class CodexRealtimeAttachmentOptions
{
    /// <summary>Wire protocol: v1, v2 or v3.</summary>
    public string Protocol { get; init; } = "v3";

    /// <summary>Explicit WSS sideband base URI. Required with external headers.</summary>
    public Uri? SidebandBaseUri { get; init; }

    /// <summary>Authorization policy. No implicit credentials are used with external headers.</summary>
    public CodexRealtimeAttachmentAuthentication Authentication { get; init; }

    /// <summary>Caller-owned credentials for the explicitly selected endpoint.</summary>
    [JsonIgnore]
    public Func<CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>>? ExternalHeaderProvider { get; init; }

    /// <summary>Optional account fence for SDK OAuth credentials.</summary>
    public string? ExpectedAccountId { get; init; }

    /// <summary>Absolute activation deadline, including authorization and retries.</summary>
    public DateTimeOffset? StartBefore { get; init; }
}

/// <summary>A caller-owned stream carrying complete JSON wire events and requests.</summary>
/// <param name="Incoming">Provider events. The SDK never completes this channel.</param>
/// <param name="Outgoing">Client requests. The SDK never completes this channel.</param>
public sealed record CodexRealtimeAttachmentStream(ChannelReader<string> Incoming, ChannelWriter<string> Outgoing);

/// <summary>An attachment owns its sideband connection, not the existing media call.</summary>
public sealed class CodexRealtimeAttachedCall : IAsyncDisposable
{
    private int detached;

    internal CodexRealtimeAttachedCall(string callId, CodexRealtimeConnection connection)
        => (CallId, Connection) = (callId, connection);

    /// <summary>Identity of the original, already-created call.</summary>
    public string CallId { get; }

    /// <summary>The control transport. Its lifetime does not determine the logical session lifetime.</summary>
    public CodexRealtimeConnection Connection { get; }

    /// <summary>Close only the control transport. Never send session.close or hang up media.</summary>
    public async Task DetachAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref detached, 1) != 0) return;
        try { await Connection.CloseTransportAsync(cancellationToken).ConfigureAwait(false); }
        finally { await Connection.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Release only this transport, without waiting for a remote close handshake.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref detached, 1) != 0) return ValueTask.CompletedTask;
        return Connection.DisposeAsync();
    }
}

public sealed partial class CodexClient
{
    private static readonly HttpMessageInvoker AttachmentHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });

    /// <summary>Join an existing call with explicit destination, authorization and deadline.
    /// Does not create a call, reset its session, or acquire its media.</summary>
    public async Task<CodexRealtimeAttachedCall> AttachExistingRealtimeCallAsync(string callId,
        CodexRealtimeAttachmentOptions? attachmentOptions = null, CancellationToken cancellationToken = default)
    {
        var attachment = attachmentOptions ?? new();
        ValidateAttachmentIdentity(callId, attachment.Protocol);
        var configured = attachment.SidebandBaseUri ?? options.SidebandBaseUri;
        ValidateAttachmentDestination(configured, attachment);
        var uri = attachment.Protocol == "v2"
            ? new UriBuilder(configured)
            {
                Path = configured.AbsolutePath.TrimEnd('/').EndsWith("/live", StringComparison.Ordinal)
                    ? configured.AbsolutePath.TrimEnd('/')[..^5] + "/realtime" : configured.AbsolutePath.TrimEnd('/'),
                Query = "call_id=" + Uri.EscapeDataString(callId),
            }.Uri
            : CodexRealtimeProtocol.SidebandUri(configured, callId, attachment.Protocol);
        using var deadline = AttachmentDeadline(attachment.StartBefore, cancellationToken);
        var ct = deadline.Token;
        ct.ThrowIfCancellationRequested();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attachment.Authentication == CodexRealtimeAttachmentAuthentication.ExternalHeaders)
        {
            var supplied = await attachment.ExternalHeaderProvider!(ct).ConfigureAwait(false);
            foreach (var header in supplied)
            {
                if (headers.Count >= 32 || header.Key.Length is 0 or > 128 || header.Value.Length > 16384 ||
                    !header.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                    header.Value.Any(char.IsControl) ||
                    header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                    !headers.TryAdd(header.Key, header.Value))
                    throw new ArgumentException("Invalid or conflicting external sideband header.");
            }
        }
        else
        {
            var auth = await GetRealtimeAuthorizationAsync(ct).ConfigureAwait(false);
            if (attachment.ExpectedAccountId is not null &&
                !string.Equals(auth.AccountId, attachment.ExpectedAccountId, StringComparison.Ordinal))
                throw new CodexRealtimeAccountMismatchException();
            headers.Add("Authorization", "Bearer " + auth.Token);
            if (auth.AccountId is not null) headers.Add("ChatGPT-Account-Id", auth.AccountId);
            if (attachment.Protocol != "v2")
                headers.Add("openai-alpha", attachment.Protocol == "v1" ? "quicksilver=v1" : "quicksilver=v2");
            headers.Add("originator", options.Originator);
        }

        ct.ThrowIfCancellationRequested();
        var connection = await CodexSidebandActivation.ConnectAsync(
            token => ConnectAttachmentSocketAsync(uri, headers, attachment.Protocol, token),
            options.RequestTimeout, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        return new(callId, connection);
    }

    /// <summary>Attach to caller-owned channels without fetching SDK credentials or opening a socket.
    /// Detach never closes either channel. The caller remains responsible for media and external transport.</summary>
    public Task<CodexRealtimeAttachedCall> AttachExistingRealtimeCallAsync(string callId,
        CodexRealtimeAttachmentStream stream, string protocol = "v3", DateTimeOffset? startBefore = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(stream.Incoming);
        ArgumentNullException.ThrowIfNull(stream.Outgoing);
        ValidateAttachmentIdentity(callId, protocol);
        using var deadline = AttachmentDeadline(startBefore, cancellationToken);
        deadline.Token.ThrowIfCancellationRequested();
        var socket = new CodexAttachmentChannelSocket(stream, options.MaximumMessageBytes);
        return Task.FromResult(new CodexRealtimeAttachedCall(callId,
            new CodexRealtimeConnection(socket, options.MaximumMessageBytes, protocol)));
    }

    private static void ValidateAttachmentIdentity(string callId, string protocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        if (callId.Length > 512 || CodexRealtimeProtocol.CallId(callId) != callId)
            throw new ArgumentException("Invalid call ID.", nameof(callId));
        if (protocol is not ("v1" or "v2" or "v3"))
            throw new ArgumentException("Unsupported realtime protocol.", nameof(protocol));
    }

    private void ValidateAttachmentDestination(Uri uri, CodexRealtimeAttachmentOptions attachment)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "wss" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Sideband requires an explicit WSS base URI without credentials, query or fragment.");
        if (attachment.Authentication == CodexRealtimeAttachmentAuthentication.ExternalHeaders)
        {
            if (attachment.SidebandBaseUri is null || attachment.ExternalHeaderProvider is null ||
                attachment.ExpectedAccountId is not null)
                throw new ArgumentException("External authorization requires an explicit URI and header provider, without an SDK account fence.");
            return;
        }
        if (attachment.Authentication != CodexRealtimeAttachmentAuthentication.ClientCredentials ||
            attachment.ExternalHeaderProvider is not null)
            throw new ArgumentException("Invalid realtime attachment authorization policy.");
        if (uri.Port != 443 || !(uri.IdnHost.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) ||
            realtimeApiKeyProvider is null && uri.IdnHost.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("SDK credentials cannot be sent to this sideband origin.");
    }

    private CancellationTokenSource AttachmentDeadline(DateTimeOffset? startBefore, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var timeout = options.RequestTimeout;
        if (startBefore is { } end)
        {
            var remaining = end - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("Realtime attachment deadline elapsed.");
            if (remaining < timeout) timeout = remaining;
        }
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private async Task<CodexRealtimeConnection> ConnectAttachmentSocketAsync(Uri uri,
        IReadOnlyDictionary<string, string> headers, string protocol, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            foreach (var header in headers) socket.Options.SetRequestHeader(header.Key, header.Value);
            await socket.ConnectAsync(uri, AttachmentHttp, ct).ConfigureAwait(false);
            return new(socket, options.MaximumMessageBytes, protocol, realtimeApiKeyProvider is null);
        }
        catch (WebSocketException failure)
        {
            var status = (int)socket.HttpStatusCode;
            var requestId = socket.HttpResponseHeaders?.FirstOrDefault(x =>
                x.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
            socket.Dispose();
            throw CodexRealtimeConnection.HandshakeFailure(status, requestId, failure);
        }
        catch { socket.Dispose(); throw; }
    }
}

// Adapt complete caller-owned channel messages to the same bounded JSON parser and writer.
internal sealed class CodexAttachmentChannelSocket(CodexRealtimeAttachmentStream stream, int maximumBytes) : WebSocket
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly CancellationTokenSource lifetime = new();
    private byte[]? current;
    private int offset;
    private int state = (int)WebSocketState.Open;

    public override WebSocketCloseStatus? CloseStatus => State is WebSocketState.CloseReceived or WebSocketState.Closed
        ? WebSocketCloseStatus.NormalClosure : null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => (WebSocketState)Volatile.Read(ref state);
    public override string? SubProtocol => null;

    public override void Abort()
    {
        Interlocked.Exchange(ref state, (int)WebSocketState.Aborted);
        lifetime.Cancel();
    }
    public override void Dispose()
    {
        Interlocked.Exchange(ref state, (int)WebSocketState.Closed);
        lifetime.Cancel();
    }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref state, (int)WebSocketState.Closed);
        lifetime.Cancel();
        return Task.CompletedTask;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (current is null)
        {
            string value;
            try { value = await stream.Incoming.ReadAsync(linked.Token).ConfigureAwait(false); }
            catch (ChannelClosedException e) when (e.InnerException is null)
            {
                Interlocked.Exchange(ref state, (int)WebSocketState.CloseReceived);
                return new(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null);
            }
            if (Utf8.GetByteCount(value) > maximumBytes)
                throw new CodexProtocolException("Inbound sideband message exceeded the configured bound.");
            current = Utf8.GetBytes(value);
            offset = 0;
        }
        var count = Math.Min(buffer.Count, current.Length - offset);
        current.AsSpan(offset, count).CopyTo(buffer.AsSpan());
        offset += count;
        var complete = offset == current.Length;
        if (complete) current = null;
        return new(count, WebSocketMessageType.Text, complete);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        if (messageType != WebSocketMessageType.Text || !endOfMessage || buffer.Count > maximumBytes)
            throw new CodexProtocolException("Invalid outbound attachment message.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        await stream.Outgoing.WriteAsync(Utf8.GetString(buffer.AsSpan()), linked.Token).ConfigureAwait(false);
    }
}
