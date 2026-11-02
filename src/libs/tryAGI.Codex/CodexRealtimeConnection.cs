using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>One sideband connection for an already-created WebRTC call. The host owns media and reconnection policy.</summary>
public sealed class CodexRealtimeConnection : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly int maximumBytes;
    private readonly long connectedAt = Stopwatch.GetTimestamp();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<CodexRealtimeEvent> initialEvents = new();
    private readonly string protocol;
    private bool responseActive;
    private bool responsePending;
    private string? pendingResponseRequestId;
    /// <summary>GA response metadata echoed by the provider; not supported by frameless V3.</summary>
    public const string ResponseRequestMetadataKey = "client_request_id";
    private sealed record ResponseAdmission(bool Allowed);
    private readonly Lock responseAdmissionGate = new();
    private ResponseAdmission responseAdmission = new(true);
    private ResponseAdmission? pendingResponseAdmission;
    private readonly HashSet<string> pendingFunctions = new(StringComparer.Ordinal);
    private readonly HashSet<string> toolOutputs = new(StringComparer.Ordinal);
    /// <summary>Protocol of this connection: <c>v3</c>, <c>v2</c> or <c>v1</c>.</summary>
    public string Protocol => protocol;
    /// <summary>Which model the provider reported for this session.</summary>
    public CodexRealtimeModelIdentity ModelIdentity { get; } = new();
    /// <summary>Host speech permission. Re-enabling never replays a request queued before suppression.</summary>
    public void SetResponseRequestsAllowed(bool allowed)
    {
        lock (responseAdmissionGate)
        {
            if (responseAdmission.Allowed != allowed)
                Volatile.Write(ref responseAdmission, new(allowed));
        }
    }
    private int reading;
    private int disposed;

    internal CodexRealtimeConnection(WebSocket socket, int maximumBytes, string protocol = "v3") => (this.socket, this.maximumBytes, this.protocol) = (socket, maximumBytes, protocol);

    internal static async Task<CodexRealtimeConnection> ConnectAsync(Uri uri, CodexCredentials auth, CodexEndpointOptions options, CancellationToken ct, string protocol = "v3")
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + auth.AccessToken);
        socket.Options.SetRequestHeader("ChatGPT-Account-Id", auth.AccountId);
        if (protocol != "v2") socket.Options.SetRequestHeader("openai-alpha", protocol == "v1" ? "quicksilver=v1" : "quicksilver=v2");
        socket.Options.SetRequestHeader("originator", options.Originator);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            using var activity = CodexClient.ActivitySource.StartActivity("codex.realtime.sideband.connect");
            await socket.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
            // V3 session configuration is already in the HTTP call creation. Do not reset it here.
            return new(socket, options.MaximumMessageBytes, protocol);
        }
        catch (WebSocketException failure)
        {
            var status = (int)socket.HttpStatusCode;
            var requestId = socket.HttpResponseHeaders?.FirstOrDefault(x =>
                x.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
            socket.Dispose();
            throw HandshakeFailure(status, requestId, failure);
        }
        catch { socket.Dispose(); throw; }
    }

    internal async Task WaitForSessionStartedAsync(TimeSpan timeout, CancellationToken ct)
        => await WaitForSessionAsync("session.started", timeout, ct).ConfigureAwait(false);

    internal async Task WaitForSessionAsync(string expected, TimeSpan timeout, CancellationToken ct, string? model = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var retained = new List<CodexRealtimeEvent>();
        await foreach (var item in ReadEventsAsync(deadline.Token).ConfigureAwait(false))
        {
            if (item.Error is { } error) throw new CodexProtocolException("Realtime initialization was rejected; no session accepted.") { RealtimeError = error };
            if (retained.Count >= 32) throw new CodexProtocolException("Realtime initialization event bound exceeded.");
            retained.Add(item);
            if (item.Type == expected)
            {
                if (model is not null && (!item.Payload.TryGetProperty("session", out var session) || CodexRealtimeProtocol.Text(session, "model") != model))
                    throw new CodexProtocolException("GA provider model identity did not match the requested model.");
                foreach (var value in retained) initialEvents.Enqueue(value);
                return;
            }
        }
        throw new CodexProtocolException("Realtime closed before " + expected + ".");
    }

    internal static CodexProtocolException HandshakeFailure(int status, string? requestId, Exception? transportFailure = null)
    {
        // Never retain all headers (cookies/auth) or provider bodies in diagnostics.
        var safeId = requestId is { Length: > 0 and <= 128 } && requestId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? requestId : null;
        int? code = status is >= 100 and <= 599 ? status : null;
        return new($"Native sideband handshake failed (HTTP {code?.ToString() ?? "unavailable"}, request {safeId ?? "unavailable"}).", code, safeId)
        {
            TransientSidebandTransport = code is null && CodexSidebandRetry.IsTransientTransport(transportFailure)
        };
    }

    /// <summary>Bounded transport metadata; never retains the peer's arbitrary close description.</summary>
    /// <param name="End">Which side ended the transport.</param>
    /// <param name="CloseCode">WebSocket close code, if any.</param>
    /// <param name="ErrorType">Type of the transport error, if any.</param>
    public sealed record TransportClosure(string End, int? CloseCode, string? ErrorType);
    private TransportClosure? closure;
    /// <summary>How the transport ended, once it has.</summary>
    public TransportClosure? Closure => Volatile.Read(ref closure);

    private async Task<ValueWebSocketReceiveResult> ReceiveObservedAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                Volatile.Write(ref closure, new("remote_close", socket.CloseStatus is { } code ? (int)code : null, null));
            return result;
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref closure, new("cancelled", socket.CloseStatus is { } code ? (int)code : null, null));
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Volatile.Write(ref closure, new("receive_error", socket.CloseStatus is { } code ? (int)code : null, error.GetType().Name));
            throw;
        }
    }

    /// <summary>Consume promptly; execute delegated work separately so tool latency cannot block inbound control events.</summary>
    public async IAsyncEnumerable<CodexRealtimeEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0) throw new InvalidOperationException("Only one sideband reader is supported.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            while (initialEvents.TryDequeue(out var initial)) yield return initial;
            var buffer = new byte[8192];
            while (!linked.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ReceiveObservedAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) yield break;
                    if (result.MessageType != WebSocketMessageType.Text) throw new CodexProtocolException("Unexpected binary sideband message.");
                    if (message.Length + result.Count > maximumBytes) throw new CodexProtocolException("Sideband message exceeded the configured bound.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
                var payload = document.RootElement.Clone();
                var type = CodexRealtimeProtocol.Text(payload, "type") ?? throw new CodexProtocolException("Sideband event omitted its type.");
                ModelIdentity.Observe(payload);
                if (protocol == "v2" && type is "response.created" or "response.done" or "response.function_call_arguments.done")
                {
                    await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        if (type == "response.function_call_arguments.done" && CodexRealtimeProtocol.Text(payload, "call_id") is { } functionId)
                        {
                            if (pendingFunctions.Count >= 128) throw new CodexProtocolException("GA pending function bound exceeded.");
                            pendingFunctions.Add(functionId);
                        }
                        else
                        {
                            responseActive = type == "response.created";
                            if (type == "response.done" && payload.TryGetProperty("response", out var response) && CodexRealtimeProtocol.Text(response, "status") is "cancelled" or "failed" or "incomplete")
                                pendingFunctions.Clear();
                        }
                        await FlushPendingResponseUnderGateAsync(linked.Token).ConfigureAwait(false);
                    }
                    finally { writeGate.Release(); }
                }
                yield return new(type, payload, Stopwatch.GetElapsedTime(connectedAt).TotalMilliseconds);
            }
        }
        finally { Interlocked.Exchange(ref reading, 0); }
    }

    /// <summary>Adds text to the session context, for example a delegation result.</summary>
    /// <param name="text">Context text.</param>
    /// <param name="delegationId">The delegation this text answers, if any.</param>
    /// <param name="channel"><c>speakable</c> to let the model speak about it, or another channel.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task AppendContextAsync(string text, string? delegationId = null, string? channel = "speakable", CancellationToken cancellationToken = default) =>
        AppendContextCoreAsync(text, delegationId, channel, cancellationToken, null);

    /// <summary>Request a GA response whose metadata can be matched to a provider response ID.</summary>
    public Task AppendCorrelatedContextAsync(string text, string responseRequestId, string? delegationId = null,
        string? channel = "speakable", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseRequestId);
        return AppendContextCoreAsync(text, delegationId, channel, cancellationToken, responseRequestId);
    }

    private async Task AppendContextCoreAsync(string text, string? delegationId, string? channel,
        CancellationToken cancellationToken, string? responseRequestId)
    {
        if (channel is not (null or "speakable" or "commentary")) throw new ArgumentException("Unsupported context channel.");
        if (responseRequestId is not null && (responseRequestId.Length is < 1 or > 128 || responseRequestId.Any(char.IsControl)))
            throw new ArgumentException("Response request identity must contain 1..128 printable characters.", nameof(responseRequestId));
        if (responseRequestId is not null && (protocol != "v2" || channel != "speakable"))
            throw new NotSupportedException("Response request metadata requires an explicit GA speakable response.");
        var requestedAdmission = Volatile.Read(ref responseAdmission);
        // Keep a complete multipart context append contiguous when several producers share this connection.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (protocol == "v2")
            {
                if (toolOutputs.Count >= 4096 && delegationId is not null && !toolOutputs.Contains(delegationId))
                    throw new CodexProtocolException("GA tool receipt bound exceeded; rotate the connection.");
                var output = delegationId is not null && toolOutputs.Add(delegationId);
                await SendUnderGateAsync(output ? CodexRealtimeGaProtocol.FunctionOutput((delegationId ?? throw new InvalidDataException("Required data was null.")), text)
                    : CodexRealtimeGaProtocol.Message("developer", channel == "commentary" ? "Background context, not a request to speak: " + text : text), linked.Token).ConfigureAwait(false);
                if (channel == "speakable") await RequestResponseUnderGateAsync(linked.Token, requestedAdmission, responseRequestId).ConfigureAwait(false);
            }
            else foreach (var item in CodexRealtimeProtocol.Context(text, delegationId, channel))
                await SendUnderGateAsync(item, linked.Token, requestedAdmission).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    /// <summary>Send typed input through the pinned Rust V3 frameless adapter. It maps
    /// conversation text to speakable session context; the wire carries no user-item ID
    /// or preserved role. Completion means transport write only, not a provider reply.</summary>
    public Task AppendUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return protocol == "v2" ? AppendGaUserAsync(text, cancellationToken) : AppendContextAsync(text, channel: "speakable", cancellationToken: cancellationToken);
    }

    /// <summary>Asks the provider to end the session gracefully.</summary>
    public async Task CloseSessionAsync(CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (protocol == "v2") await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken).ConfigureAwait(false);
            else await SendUnderGateAsync(new JsonObject { ["type"] = "session.close" }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
    /// <summary>Sends new session settings. Keep reading events to see whether they were accepted.</summary>
    public Task UpdateSessionAsync(CodexRealtimeSessionOptions options, CancellationToken cancellationToken = default) =>
        SendAsync(protocol == "v2" ? CodexRealtimeGaProtocol.SessionUpdate(options) : CodexRealtimeProtocol.SessionUpdate(options), cancellationToken);
    /// <summary>Native V3 PCM audio transport for direct WebSocket sessions. Do not duplicate
    /// audio already sent over WebRTC. Server capability/transport admission belongs to the caller.</summary>
    public Task AppendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) =>
        SendAsync(protocol == "v2" ? CodexRealtimeGaProtocol.AudioAppend(pcm16.Span) : CodexRealtimeProtocol.AudioAppend(pcm16.Span), cancellationToken);
    /// <summary>Bounded raw provider event. Preserves model-specific capabilities without an allowlist or automatic replay.</summary>
    public Task SendEventAsync(JsonObject message, CancellationToken cancellationToken = default) => SendAsync((JsonObject)message.DeepClone(), cancellationToken);
    private async Task AppendGaUserAsync(string text, CancellationToken ct)
    {
        var requestedAdmission = Volatile.Read(ref responseAdmission);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await SendUnderGateAsync(CodexRealtimeGaProtocol.Message("user", text), linked.Token).ConfigureAwait(false);
            await RequestResponseUnderGateAsync(linked.Token, requestedAdmission).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var requestedAdmission = Volatile.Read(ref responseAdmission);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (protocol == "v2" && message["type"]?.GetValue<string>() == "response.create" && message.Count == 1)
                await RequestResponseUnderGateAsync(linked.Token, requestedAdmission).ConfigureAwait(false);
            else await SendUnderGateAsync(message, linked.Token, requestedAdmission).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    private async Task RequestResponseUnderGateAsync(CancellationToken ct, ResponseAdmission? requestedAdmission = null, string? responseRequestId = null)
    {
        var admission = Volatile.Read(ref responseAdmission);
        if (!admission.Allowed || requestedAdmission is not null && !ReferenceEquals(admission, requestedAdmission)) return;
        if (responseActive || pendingFunctions.Count > 0)
        {
            if (!ReferenceEquals(pendingResponseAdmission, admission)) pendingResponseRequestId = null;
            if (pendingResponseRequestId is { } pending && responseRequestId is { } requested && pending != requested)
                throw new CodexProtocolException("A different correlated response is already pending.");
            responsePending = true; pendingResponseAdmission = admission;
            pendingResponseRequestId ??= responseRequestId;
            return;
        }
        var message = new JsonObject { ["type"] = "response.create" };
        if (responseRequestId is not null)
            message["response"] = new JsonObject { ["metadata"] = new JsonObject { [ResponseRequestMetadataKey] = responseRequestId } };
        if (await SendUnderGateAsync(message, ct, admission).ConfigureAwait(false))
            responseActive = true;
    }
    private async Task FlushPendingResponseUnderGateAsync(CancellationToken ct)
    {
        if (responseActive || !responsePending || pendingFunctions.Count != 0) return;
        var admission = pendingResponseAdmission;
        var requestId = pendingResponseRequestId;
        responsePending = false; pendingResponseAdmission = null;
        pendingResponseRequestId = null;
        if (ReferenceEquals(admission, Volatile.Read(ref responseAdmission)))
            await RequestResponseUnderGateAsync(ct, admission, requestId).ConfigureAwait(false);
    }
    private async Task<bool> SendUnderGateAsync(JsonObject message, CancellationToken ct, ResponseAdmission? requestedAdmission = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (bytes.Length > maximumBytes) throw new CodexProtocolException("Outbound sideband message exceeded the configured bound.");
        // Prepare both legal wire forms before the admission lock. Sleep must never wait
        // for serialization or a network write, including when a multipart append is active.
        byte[]? quietBytes = null;
        if (protocol != "v2" && message["type"]?.GetValue<string>() is "session.context.append" or "delegation.context.append" &&
            message["channel"]?.GetValue<string>() != "commentary")
        {
            var quiet = (JsonObject)message.DeepClone();
            quiet["channel"] = "commentary";
            quietBytes = Encoding.UTF8.GetBytes(quiet.ToJsonString());
        }
        // No replay on failure: delivery can be uncertain after a network interruption.
        ValueTask write;
        if (quietBytes is not null)
        {
            lock (responseAdmissionGate)
            {
                var permitted = responseAdmission.Allowed &&
                    (requestedAdmission is null || ReferenceEquals(responseAdmission, requestedAdmission));
                var selected = permitted ? bytes : quietBytes;
                if (selected.Length > maximumBytes) throw new CodexProtocolException("Outbound sideband message exceeded the configured bound.");
                write = socket.SendAsync(selected.AsMemory(), WebSocketMessageType.Text, true, ct);
            }
        }
        else if (protocol == "v2" && message["type"]?.GetValue<string>() == "response.create")
        {
            // Only initiation is serialized with suppression; never hold this lock over network I/O.
            lock (responseAdmissionGate)
            {
                if (!responseAdmission.Allowed || requestedAdmission is not null && !ReferenceEquals(responseAdmission, requestedAdmission)) return false;
                write = socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
            }
        }
        else write = socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
        await write.ConfigureAwait(false);
        if (protocol == "v2" && message["item"] is JsonObject item && item["type"]?.GetValue<string>() == "function_call_output" &&
            item["call_id"]?.GetValue<string>() is { } callId)
        {
            pendingFunctions.Remove(callId);
            await FlushPendingResponseUnderGateAsync(ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        lifetime.Cancel();
        socket.Abort();
        socket.Dispose();
        // A pending reader/writer can still observe the cancelled token/gate. Do not dispose those under it.
        return ValueTask.CompletedTask;
    }
}
