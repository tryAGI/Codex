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
    private readonly bool chatGptCall;
    private bool responseActive;
    private bool responsePending;
    private string? pendingResponseRequestId;
    private JsonObject? pendingResponsePayload;
    private string? activeResponseId;
    private bool cancelRequested;
    private JsonObject? unacknowledgedResponse;
    private ResponseAdmission? activeResponseAdmission;
    private string? outputAudioItemId;
    private uint outputAudioEndMs;
    private readonly HashSet<string> retiredResponses = new(StringComparer.Ordinal);
    private readonly Queue<string> retiredResponseOrder = new();
    private readonly HashSet<string> retiredFunctions = new(StringComparer.Ordinal);
    private bool feedbackRetired;
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
            if (!feedbackRetired && responseAdmission.Allowed != allowed)
                Volatile.Write(ref responseAdmission, new(allowed));
        }
    }

    /// <summary>Opaque, connection-bound feedback generation. Capture before starting asynchronous work.</summary>
    public sealed class FeedbackAdmission
    {
        internal FeedbackAdmission(CodexRealtimeConnection owner, object stamp) => (Owner, Stamp) = (owner, stamp);
        internal CodexRealtimeConnection Owner { get; }
        internal object Stamp { get; }
    }

    /// <summary>Captures the current generation, without granting permission to speak.</summary>
    public FeedbackAdmission CaptureFeedbackAdmission() => new(this, Volatile.Read(ref responseAdmission));

    /// <summary>Whether this token still refers to the same connection and current speech-permitted generation.</summary>
    public bool IsFeedbackAdmissionCurrent(FeedbackAdmission admission) =>
        ReferenceEquals(admission.Owner, this) && ReferenceEquals(admission.Stamp, Volatile.Read(ref responseAdmission)) &&
        ((ResponseAdmission)admission.Stamp).Allowed && Volatile.Read(ref disposed) == 0;

    /// <summary>Fences pending speech from the previous turn, including work not yet submitted to the socket.</summary>
    public void InvalidatePendingFeedback()
    {
        lock (responseAdmissionGate)
            Volatile.Write(ref responseAdmission, new(responseAdmission.Allowed && !feedbackRetired));
    }

    /// <summary>Permanently retires this connection's feedback admission after a terminal policy/session failure.</summary>
    public void RetireFeedbackAdmission()
    {
        lock (responseAdmissionGate)
        {
            feedbackRetired = true;
            Volatile.Write(ref responseAdmission, new(false));
        }
    }

    /// <summary>Appends feedback only for a still-current generation. False means the feedback was stale or suppressed.</summary>
    public Task<bool> AppendFencedContextAsync(FeedbackAdmission admission, string text, string? delegationId = null,
        string? channel = "speakable", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!IsFeedbackAdmissionCurrent(admission)) return Task.FromResult(false);
        return AppendContextCoreAsync(text, delegationId, channel, cancellationToken, null, (ResponseAdmission)admission.Stamp, rejectStale: true);
    }

    private int reading;
    private int disposed;

    internal CodexRealtimeConnection(WebSocket socket, int maximumBytes, string protocol = "v3", bool chatGptCall = true) =>
        (this.socket, this.maximumBytes, this.protocol, this.chatGptCall) = (socket, maximumBytes, protocol, chatGptCall);

    internal static Task<CodexRealtimeConnection> ConnectAsync(Uri uri, CodexCredentials auth, CodexEndpointOptions options, CancellationToken ct, string protocol = "v3", bool chatGptCall = true)
        => ConnectAsync(uri, auth.AccessToken, auth.AccountId, options, ct, protocol, chatGptCall);

    internal static async Task<CodexRealtimeConnection> ConnectAsync(Uri uri, string accessToken, string? accountId, CodexEndpointOptions options,
        CancellationToken ct, string protocol = "v3", bool chatGptCall = true, CodexRealtimeSessionOptions? session = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
        if (accountId is not null) socket.Options.SetRequestHeader("ChatGPT-Account-Id", accountId);
        if (session?.SessionId is { } id) socket.Options.SetRequestHeader("x-session-id", id);
        if (session?.VoiceSessionId is { } voiceId) socket.Options.SetRequestHeader("voice-session-id", voiceId);
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
            return new(socket, options.MaximumMessageBytes, protocol, chatGptCall);
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

    internal async Task InitializeDirectSessionAsync(CodexRealtimeSessionOptions session, TimeSpan timeout, bool legacyOAuthGa, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        // The official API adapter updates immediately; only frameless V3 waits for session.started.
        // Retain the older GA OAuth route's explicit model verification for compatibility.
        if (legacyOAuthGa) await WaitForSessionAsync("session.created", timeout, deadline.Token, session.Model).ConfigureAwait(false);
        await UpdateSessionAsync(session, deadline.Token).ConfigureAwait(false);
        if (protocol == "v3") await WaitForSessionStartedAsync(timeout, deadline.Token).ConfigureAwait(false);
        if (legacyOAuthGa) await WaitForSessionAsync("session.updated", timeout, deadline.Token, session.Model).ConfigureAwait(false);
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
                var item = new CodexRealtimeEvent(type, payload, Stopwatch.GetElapsedTime(connectedAt).TotalMilliseconds) { Protocol = protocol };
                if (protocol == "v2" && type is "response.created" or "response.done" or "response.cancelled" or
                    "response.function_call_arguments.done" or "conversation.item.done" or "input_audio_buffer.speech_started" or
                    "response.output_audio.delta" or "response.audio.delta" or "error")
                {
                    await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        item = item with { StaleForFeedback = IsStaleGaReceipt(item) };
                        if (!item.StaleForFeedback) await ObserveGaLifecycleUnderGateAsync(item, linked.Token).ConfigureAwait(false);
                    }
                    finally { writeGate.Release(); }
                }
                else if (protocol == "v3" && item.Turn is { Role: "user" }) InvalidatePendingFeedback();
                yield return item with { Admission = item.StaleForFeedback ? null : CaptureFeedbackAdmission() };
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

    private async Task<bool> AppendContextCoreAsync(string text, string? delegationId, string? channel,
        CancellationToken cancellationToken, string? responseRequestId, ResponseAdmission? capturedAdmission = null, bool rejectStale = false)
    {
        if (channel is not (null or "speakable" or "commentary")) throw new ArgumentException("Unsupported context channel.");
        if (responseRequestId is not null && (responseRequestId.Length is < 1 or > 128 || responseRequestId.Any(char.IsControl)))
            throw new ArgumentException("Response request identity must contain 1..128 printable characters.", nameof(responseRequestId));
        if (responseRequestId is not null && (protocol != "v2" || channel != "speakable"))
            throw new NotSupportedException("Response request metadata requires an explicit GA speakable response.");
        var requestedAdmission = capturedAdmission ?? Volatile.Read(ref responseAdmission);
        // Keep a complete multipart context append contiguous when several producers share this connection.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (rejectStale && (!requestedAdmission.Allowed || !ReferenceEquals(requestedAdmission, Volatile.Read(ref responseAdmission)))) return false;
            if (protocol == "v2")
            {
                if (delegationId is not null && retiredFunctions.Contains(delegationId)) return false;
                if (toolOutputs.Count >= 4096 && delegationId is not null && !toolOutputs.Contains(delegationId))
                    throw new CodexProtocolException("GA tool receipt bound exceeded; rotate the connection.");
                var output = delegationId is not null && toolOutputs.Add(delegationId);
                await SendUnderGateAsync(output ? CodexRealtimeGaProtocol.FunctionOutput((delegationId ?? throw new InvalidDataException("Required data was null.")), text)
                    : CodexRealtimeGaProtocol.Message("developer", channel == "commentary" ? "Background context, not a request to speak: " + text : text), linked.Token).ConfigureAwait(false);
                if (channel == "speakable") await RequestResponseUnderGateAsync(linked.Token, requestedAdmission, responseRequestId).ConfigureAwait(false);
            }
            else if (protocol == "v1")
            {
                if (!requestedAdmission.Allowed || !ReferenceEquals(requestedAdmission, Volatile.Read(ref responseAdmission))) return false;
                var legacy = delegationId is null ? CodexRealtimeGaProtocol.Message("developer", text, preserveDeveloperRole: true)
                    : new JsonObject { ["type"] = "conversation.handoff.append", ["handoff_id"] = delegationId, ["output_text"] = text };
                if (!await SendUnderGateAsync(legacy, linked.Token, requestedAdmission).ConfigureAwait(false)) return false;
            }
            else foreach (var item in CodexRealtimeProtocol.Context(text, delegationId, channel))
                await SendUnderGateAsync(item, linked.Token, requestedAdmission).ConfigureAwait(false);
            return !rejectStale || ReferenceEquals(requestedAdmission, Volatile.Read(ref responseAdmission));
        }
        finally { writeGate.Release(); }
    }

    /// <summary>Send typed input through the pinned Rust V3 frameless adapter. It maps
    /// conversation text to speakable session context; the wire carries no user-item ID
    /// or preserved role. Completion means transport write only, not a provider reply.</summary>
    public Task AppendUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return protocol == "v2" ? AppendGaUserAsync(text, cancellationToken)
            : protocol == "v1" ? SendAsync(CodexRealtimeGaProtocol.Message("user", text, preserveDeveloperRole: true), cancellationToken)
            : AppendContextAsync(text, channel: "speakable", cancellationToken: cancellationToken);
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

    /// <summary>Detaches this connection with a normal WebSocket close and no <c>session.close</c>,
    /// so a frontend-owned (WebRTC) call keeps running. Dispose the connection afterwards.</summary>
    public async Task CloseTransportAsync(CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
    /// <summary>Sends new session settings. Keep reading events to see whether they were accepted.</summary>
    public Task UpdateSessionAsync(CodexRealtimeSessionOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Protocol != protocol) throw new ArgumentException("Cannot change the wire protocol of an attached session.", nameof(options));
        return SendAsync(protocol == "v2" ? CodexRealtimeGaProtocol.SessionUpdate(options) : CodexRealtimeProtocol.SessionUpdate(options, chatGptCall), cancellationToken);
    }
    /// <summary>Native V3 PCM audio transport for direct WebSocket sessions. Do not duplicate
    /// audio already sent over WebRTC. Server capability/transport admission belongs to the caller.</summary>
    public Task AppendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) =>
        SendAsync(protocol == "v3" ? CodexRealtimeProtocol.AudioAppend(pcm16.Span) : CodexRealtimeGaProtocol.AudioAppend(pcm16.Span), cancellationToken);
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
            if (protocol == "v2" && message["type"]?.GetValue<string>() == "response.create")
                await RequestResponseUnderGateAsync(linked.Token, requestedAdmission, rawResponse: message).ConfigureAwait(false);
            else if (protocol == "v2" && message["type"]?.GetValue<string>() == "response.cancel")
            {
                var id = message["response_id"]?.GetValue<string>();
                if (responseActive && !cancelRequested && (id is null || activeResponseId == id))
                {
                    InterruptFeedbackUnderGate(); cancelRequested = true;
                    await SendUnderGateAsync(message, linked.Token).ConfigureAwait(false);
                }
            }
            else await SendUnderGateAsync(message, linked.Token, requestedAdmission).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    private static string? ResponseRequestId(JsonObject message) =>
        (message["response"] as JsonObject)?["metadata"] is JsonObject metadata &&
        metadata[ResponseRequestMetadataKey] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;

    private async Task RequestResponseUnderGateAsync(CancellationToken ct, ResponseAdmission? requestedAdmission = null,
        string? responseRequestId = null, JsonObject? rawResponse = null)
    {
        var admission = Volatile.Read(ref responseAdmission);
        if (!admission.Allowed || requestedAdmission is not null && !ReferenceEquals(admission, requestedAdmission)) return;
        var message = rawResponse ?? new JsonObject { ["type"] = "response.create" };
        if (rawResponse is null && responseRequestId is not null)
            message["response"] = new JsonObject { ["metadata"] = new JsonObject { [ResponseRequestMetadataKey] = responseRequestId } };
        responseRequestId ??= ResponseRequestId(message);
        if (responseActive || pendingFunctions.Count > 0)
        {
            QueueResponseUnderGate(message, admission, responseRequestId);
            return;
        }
        if (await SendUnderGateAsync(message, ct, admission).ConfigureAwait(false))
        {
            responseActive = true; activeResponseId = null; cancelRequested = false;
            activeResponseAdmission = admission; unacknowledgedResponse = message;
        }
    }

    private void QueueResponseUnderGate(JsonObject message, ResponseAdmission admission, string? requestId)
    {
        if (!ReferenceEquals(pendingResponseAdmission, admission)) ClearPendingResponse();
        if (pendingResponseRequestId is { } pending && requestId is { } requested && pending != requested)
            throw new CodexProtocolException("A different correlated response is already pending.");
        if (pendingResponsePayload is { Count: > 1 } existing && message.Count > 1 && !JsonNode.DeepEquals(existing, message))
            throw new CodexProtocolException("A different configured response is already pending.");
        responsePending = true; pendingResponseAdmission = admission;
        pendingResponseRequestId ??= requestId;
        if (pendingResponsePayload is null || pendingResponsePayload.Count == 1) pendingResponsePayload = message;
    }

    private void ClearPendingResponse()
    {
        responsePending = false; pendingResponseAdmission = null; pendingResponseRequestId = null; pendingResponsePayload = null;
    }

    private async Task FlushPendingResponseUnderGateAsync(CancellationToken ct)
    {
        if (responseActive || !responsePending || pendingFunctions.Count != 0) return;
        var admission = pendingResponseAdmission;
        var message = pendingResponsePayload;
        ClearPendingResponse();
        if (message is not null && ReferenceEquals(admission, Volatile.Read(ref responseAdmission)))
            await RequestResponseUnderGateAsync(ct, admission, rawResponse: message).ConfigureAwait(false);
    }

    private void RetireResponseId(string? id)
    {
        if (id is null || !retiredResponses.Add(id)) return;
        retiredResponseOrder.Enqueue(id);
        if (retiredResponseOrder.Count > 512) retiredResponses.Remove(retiredResponseOrder.Dequeue());
    }

    private void RetirePendingFunctionsUnderGate()
    {
        foreach (var id in pendingFunctions) retiredFunctions.Add(id);
        if (retiredFunctions.Count > 4096) throw new CodexProtocolException("Retired tool receipt bound exceeded; rotate the connection.");
        pendingFunctions.Clear();
    }

    private void InterruptFeedbackUnderGate()
    {
        InvalidatePendingFeedback();
        ClearPendingResponse();
        RetirePendingFunctionsUnderGate();
        unacknowledgedResponse = null;
    }

    private static string? ErrorMessage(JsonElement payload)
    {
        if (CodexRealtimeProtocol.Text(payload, "message") is { } message) return message;
        if (!payload.TryGetProperty("error", out var error)) return null;
        return CodexRealtimeProtocol.Text(error, "message") ?? (error.ValueKind == JsonValueKind.String ? error.GetString() : null);
    }

    private bool IsStaleGaReceipt(CodexRealtimeEvent item)
    {
        var id = item.Response?.ResponseId ?? CodexRealtimeProtocol.Text(item.Payload, "response_id");
        if (id is not null && retiredResponses.Contains(id)) return true;
        if (item.Type is "response.done" or "response.cancelled") return activeResponseId is not null && id != activeResponseId;
        if (item.Type is "response.function_call_arguments.done" or "conversation.item.done" or "response.audio.delta" or "response.output_audio.delta")
            return activeResponseId is not null && id is not null && activeResponseId != id ||
                activeResponseAdmission is not null && !ReferenceEquals(activeResponseAdmission, Volatile.Read(ref responseAdmission));
        return false;
    }

    private async Task ObserveGaLifecycleUnderGateAsync(CodexRealtimeEvent item, CancellationToken ct)
    {
        var payload = item.Payload;
        var eventType = item.Type;
        var id = item.Response?.ResponseId ?? CodexRealtimeProtocol.Text(payload, "response_id");
        if (id is not null && retiredResponses.Contains(id)) return;
        if (eventType == "response.created")
        {
            if (activeResponseId is not null && id is not null && activeResponseId != id)
            {
                RetireResponseId(activeResponseId);
                if (ReferenceEquals(activeResponseAdmission, Volatile.Read(ref responseAdmission))) InterruptFeedbackUnderGate();
                else RetirePendingFunctionsUnderGate();
                outputAudioItemId = null; outputAudioEndMs = 0;
                activeResponseAdmission = null; cancelRequested = false;
            }
            responseActive = true; activeResponseId = id ?? activeResponseId;
            activeResponseAdmission ??= Volatile.Read(ref responseAdmission);
            unacknowledgedResponse = null;
        }
        else if (eventType is "response.done" or "response.cancelled")
        {
            // A terminal for a superseded response cannot release the current response queue.
            if (activeResponseId is not null && id != activeResponseId) return;
            var abnormal = eventType == "response.cancelled" || item.Response?.Status is "cancelled" or "failed" or "incomplete";
            if (abnormal)
            {
                // A queued explicit request survives a normal provider terminal only if its generation is still current.
                var previous = Volatile.Read(ref responseAdmission);
                var preservePending = pendingFunctions.Count == 0 && ReferenceEquals(pendingResponseAdmission, previous);
                if (ReferenceEquals(activeResponseAdmission, previous))
                {
                    InvalidatePendingFeedback();
                    if (preservePending) pendingResponseAdmission = Volatile.Read(ref responseAdmission);
                }
                RetirePendingFunctionsUnderGate();
            }
            RetireResponseId(id ?? activeResponseId);
            responseActive = false; activeResponseId = null; activeResponseAdmission = null; unacknowledgedResponse = null; cancelRequested = false;
            outputAudioItemId = null; outputAudioEndMs = 0;
            await FlushPendingResponseUnderGateAsync(ct).ConfigureAwait(false);
        }
        else if (eventType == "error")
        {
            var attempted = unacknowledgedResponse;
            var admission = activeResponseAdmission;
            if (attempted is null || admission is null) return;
            var eventId = item.Error?.EventId;
            var sentId = attempted["event_id"] is JsonValue value && value.TryGetValue<string>(out var sent) ? sent : null;
            if (eventId is not null && eventId != sentId) return;
            if (ErrorMessage(payload)?.StartsWith("Conversation already has an active response in progress:", StringComparison.Ordinal) == true)
            {
                // Exactly the pinned Rust retry condition. Never replay an ambiguous transport failure.
                unacknowledgedResponse = null; responseActive = true;
                if (ReferenceEquals(admission, Volatile.Read(ref responseAdmission)) && admission.Allowed &&
                    (!responsePending || !ReferenceEquals(pendingResponseAdmission, admission)))
                    QueueResponseUnderGate(attempted, admission, ResponseRequestId(attempted));
            }
            else if (eventId is not null && eventId == sentId)
            {
                unacknowledgedResponse = null; responseActive = false; activeResponseId = null; activeResponseAdmission = null;
                ClearPendingResponse();
            }
        }
        else if (eventType == "input_audio_buffer.speech_started")
        {
            InterruptFeedbackUnderGate();
            var outputItem = outputAudioItemId;
            var end = outputAudioEndMs;
            outputAudioItemId = null; outputAudioEndMs = 0;
            if (outputItem is not null && end > 0 && (item.SpeechStarted?.ItemId is null || item.SpeechStarted.ItemId == outputItem))
                await SendUnderGateAsync(new JsonObject
                {
                    ["type"] = "conversation.item.truncate", ["item_id"] = outputItem, ["content_index"] = 0, ["audio_end_ms"] = end,
                }, ct).ConfigureAwait(false);
        }
        else if (eventType is "response.audio.delta" or "response.output_audio.delta")
        {
            if (activeResponseId is not null && id is not null && activeResponseId != id ||
                activeResponseAdmission is not null && !ReferenceEquals(activeResponseAdmission, Volatile.Read(ref responseAdmission))) return;
            CodexRealtimeAudio? frame;
            try { frame = item.Audio; }
            catch (CodexProtocolException) { return; } // Preserve the raw malformed event for the caller.
            if (frame?.ItemId is not { } outputItem) return;
            var samples = frame.SamplesPerChannel ?? (uint)(frame.Pcm16.Length / 2 / frame.Channels);
            var duration = (uint)Math.Min(uint.MaxValue, (ulong)samples * 1000 / (uint)frame.SampleRate);
            if (duration == 0) return;
            if (outputAudioItemId == outputItem) outputAudioEndMs = (uint)Math.Min(uint.MaxValue, (ulong)outputAudioEndMs + duration);
            else { outputAudioItemId = outputItem; outputAudioEndMs = duration; }
        }
        else
        {
            if (activeResponseId is not null && id is not null && activeResponseId != id ||
                activeResponseAdmission is not null && !ReferenceEquals(activeResponseAdmission, Volatile.Read(ref responseAdmission))) return;
            var functionId = eventType == "response.function_call_arguments.done" ? CodexRealtimeProtocol.Text(payload, "call_id") : null;
            if (eventType == "conversation.item.done" && item.ConversationItem?.Item is { } function &&
                CodexRealtimeProtocol.Text(function, "type") == "function_call")
                functionId = CodexRealtimeProtocol.Text(function, "call_id") ?? CodexRealtimeProtocol.Text(function, "id");
            if (functionId is null || toolOutputs.Contains(functionId) || retiredFunctions.Contains(functionId)) return;
            if (pendingFunctions.Count >= 128 && !pendingFunctions.Contains(functionId))
                throw new CodexProtocolException("GA pending function bound exceeded.");
            pendingFunctions.Add(functionId);
        }
    }

    /// <summary>Cancels the current GA response if its optional provider identity still matches.
    /// V3 has no pinned response.cancel wire command; fence feedback and stop local playback instead.</summary>
    public async Task<bool> CancelResponseAsync(string? responseId = null, CancellationToken cancellationToken = default)
    {
        if (protocol != "v2") throw new NotSupportedException("The pinned adapter only exposes explicit cancellation for GA V2.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!responseActive || cancelRequested || responseId is not null && responseId != activeResponseId) return false;
            InterruptFeedbackUnderGate(); cancelRequested = true;
            var message = new JsonObject { ["type"] = "response.cancel" };
            if (activeResponseId is { } id) message["response_id"] = id;
            await SendUnderGateAsync(message, linked.Token).ConfigureAwait(false);
            return true;
        }
        finally { writeGate.Release(); }
    }
    private async Task<bool> SendUnderGateAsync(JsonObject message, CancellationToken ct, ResponseAdmission? requestedAdmission = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (protocol == "v2" && message["item"] is JsonObject output && output["type"]?.GetValue<string>() == "function_call_output" &&
            output["call_id"]?.GetValue<string>() is { } staleCall && retiredFunctions.Contains(staleCall)) return false;
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
        else if (message["type"]?.GetValue<string>() == "response.create" ||
            protocol == "v1" && requestedAdmission is not null && message["type"]?.GetValue<string>() == "conversation.handoff.append")
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
        RetireFeedbackAdmission();
        lifetime.Cancel();
        socket.Abort();
        socket.Dispose();
        // A pending reader/writer can still observe the cancelled token/gate. Do not dispose those under it.
        return ValueTask.CompletedTask;
    }
}
