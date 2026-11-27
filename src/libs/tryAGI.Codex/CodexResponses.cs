using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Settings of a Codex Responses request.</summary>
/// <param name="Model">Model name.</param>
/// <param name="Instructions">System instructions.</param>
public sealed record CodexResponseOptions(string Model, string Instructions)
{
    /// <summary>Reasoning effort, for example <c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    public string ReasoningEffort { get; init; } = "low";
    /// <summary>Advertised tools in Responses format (function, custom or namespace).</summary>
    public IReadOnlyList<JsonElement> Tools { get; init; } = [];
    /// <summary>Require one advertised flat function on the first agent request only.
    /// This selects a function, never its arguments or permission to perform effects.</summary>
    public string? RequiredFirstFunction { get; init; }
    /// <summary>Prompt cache key; reuse it across turns of one conversation.</summary>
    public string? PromptCacheKey { get; init; }
    /// <summary>Let the model request several tool calls at once.</summary>
    public bool ParallelToolCalls { get; init; }
    /// <summary>Most tool calls executed at the same time.</summary>
    public int MaximumConcurrentTools { get; init; } = 4;
    /// <summary>JSON schema the final answer must follow.</summary>
    public JsonElement? OutputSchema { get; init; }
    /// <summary>Enforce <see cref="OutputSchema"/> strictly.</summary>
    public bool OutputSchemaStrict { get; init; } = true;
    /// <summary>Answer verbosity, for example <c>low</c>.</summary>
    public string? Verbosity { get; init; }
    /// <summary>Reasoning summary mode, for example <c>auto</c>.</summary>
    public string? ReasoningSummary { get; init; }
    /// <summary>Opt into OpenAI sequential-cutoff delivery when the selected model supports reasoning summaries.
    /// Ignored if no reasoning summary is requested. The host must know provider/model feature support.</summary>
    public bool ConcurrentReasoningSummaries { get; init; }
    internal string RequestKind { get; init; } = "turn";
    /// <summary>Service tier, for example <c>priority</c>.</summary>
    public string? ServiceTier { get; init; }
    /// <summary>Correlation identity sent as client metadata.</summary>
    public CodexResponsesIdentity? Identity { get; init; }
    /// <summary>Optional caller-owned native WebSocket transport. The default remains SSE.</summary>
    public CodexResponsesWebSocketSession? WebSocketSession { get; init; }
    /// <summary>Prime a new WebSocket using a non-generating response.create before inference.</summary>
    public bool PrewarmWebSocket { get; init; }
    /// <summary>Per-request signal to interrupt generation and drain completed items on Responses WebSocket.
    /// Unlike caller cancellation, a drain can return validated output. Not used by SSE.</summary>
    public CancellationToken WebSocketDrain { get; init; }
    /// <summary>Opt-in automatic compaction at a native thread's closed-turn boundary.</summary>
    public CodexThreadCompactionOptions? AutomaticCompaction { get; init; }
}
/// <summary>One server-sent event of a Responses stream.</summary>
/// <param name="Type">Event type, for example <c>response.output_text.delta</c>.</param>
/// <param name="Payload">Raw event JSON.</param>
/// <param name="ReceivedAfterRequestMs">Milliseconds between sending the request and receiving this event.</param>
public sealed record CodexResponseEvent(string Type, JsonElement Payload, double ReceivedAfterRequestMs)
{
    /// <summary>Caller-owned request correlation.</summary>
    public CodexResponsesIdentity? Identity { get; init; }
    /// <summary>Local reservation identifier.</summary>
    public string? RequestId { get; init; }
    /// <summary>Observed provider accounting data, if this event carries it.</summary>
    public CodexObservedUsage? Usage => CodexObservedUsage.Read(Payload, Identity, RequestId, ObservedAt) is { } usage
        ? usage with { ResponseId = usage.ResponseId ?? ResponseId } : null;
    /// <summary>Provider response identifier established by this event or an earlier frame of the same stream.</summary>
    public string? ResponseId { get; init; }
    /// <summary>Validated output in provider order, available only for a successful completion.
    /// A sparse provider terminal remains unchanged in <see cref="Payload"/>.</summary>
    public IReadOnlyList<JsonElement>? CompletedOutput { get; init; }
    /// <summary>The provider explicitly completed an interrupted generation; a new turn may be needed.</summary>
    public bool Interrupted { get; init; }
    /// <summary>Local receipt time.</summary>
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed partial class CodexClient
{
    private readonly CodexResponsesRouting responsesRouting = new();

    /// <summary>Native Codex Responses SSE or optional WebSocket stream, using ChatGPT OAuth.</summary>
    public async IAsyncEnumerable<CodexResponseEvent> StreamResponseAsync(CodexResponseOptions settings,
        IReadOnlyList<JsonElement> input, [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<CodexResponseDiagnostic>? observeDiagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Instructions);
        settings.Identity?.Validate();
        if (settings.RequiredFirstFunction is { } requiredFunction &&
            (string.IsNullOrWhiteSpace(requiredFunction) || settings.Tools.Count(tool =>
                CodexRealtimeProtocol.Text(tool, "type") == "function" &&
                CodexRealtimeProtocol.Text(tool, "name") == requiredFunction) != 1))
            throw new ArgumentException("The required first function must be advertised exactly once.", nameof(settings));
        using var activity = ActivitySource.StartActivity("codex.responses.stream");
        var diagnostics = new CodexResponseDiagnostics(input.Count, settings.Tools.Count, observeDiagnostics);
        diagnostics.Mark("request.started");
        var accountingId = diagnostics.RequestId;
        var reservedIdentity = usageObserver is null ? null :
            await usageObserver.ReserveWithIdentityAsync(accountingId, settings.Model, cancellationToken).ConfigureAwait(false);
        var accountingComplete = false;
        var accountingAttempted = false;
        var dispatchStarted = false;
        CodexResponsesInterruption? interruption = null;
        CodexObservedUsage? incompleteUsage = null;
        JsonElement? incompleteEvidence = null;
        CancellationToken requestCancellation = default;
        try
        {
        if (settings.Identity is not null && reservedIdentity is not null && settings.Identity != reservedIdentity)
            throw new CodexProtocolException("Requested wire identity differs from the durable reservation; dispatch refused.");
        var identity = reservedIdentity ?? settings.Identity;
        identity?.Validate();
        diagnostics.Identity = identity;
        if (usageObserver is not null) diagnostics.Mark("usage.reserved");
        var body = new JsonObject
        {
            ["model"] = settings.Model, ["instructions"] = settings.Instructions,
            ["input"] = new JsonArray(input.Select(x => JsonNode.Parse(x.GetRawText())).ToArray()),
            ["tools"] = new JsonArray(settings.Tools.Select(x => JsonNode.Parse(x.GetRawText())).ToArray()),
            ["tool_choice"] = settings.RequiredFirstFunction is { } function
                ? new JsonObject { ["type"] = "function", ["name"] = function } : JsonValue.Create("auto"),
            ["parallel_tool_calls"] = settings.RequiredFirstFunction is null && settings.ParallelToolCalls,
            ["reasoning"] = new JsonObject { ["effort"] = settings.ReasoningEffort },
            ["store"] = false, ["stream"] = true,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
        };
        if (identity is not null) body["client_metadata"] = identity.ClientMetadata(settings.RequestKind);
        if (settings.ReasoningSummary is { } summary && body["reasoning"] is JsonObject reasoning)
        {
            reasoning["summary"] = summary;
            if (settings.ConcurrentReasoningSummaries && summary != "none")
                body["stream_options"] = new JsonObject { ["reasoning_summary_delivery"] = "sequential_cutoff" };
        }
        if (settings.ServiceTier is { } tier) body["service_tier"] = tier;
        if (settings.OutputSchema is not null || settings.Verbosity is not null)
        {
            var text = new JsonObject();
            if (settings.Verbosity is { } verbosity)
            {
                if (verbosity is not ("low" or "medium" or "high")) throw new ArgumentException("Invalid response verbosity.");
                text["verbosity"] = verbosity;
            }
            if (settings.OutputSchema is { } schema)
            {
                if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException("Output schema must be an object.");
                text["format"] = new JsonObject { ["type"] = "json_schema", ["strict"] = settings.OutputSchemaStrict,
                    ["name"] = "codex_output_schema", ["schema"] = JsonNode.Parse(schema.GetRawText()) };
            }
            body["text"] = text;
        }
        if (settings.PromptCacheKey is not null) body["prompt_cache_key"] = settings.PromptCacheKey;
        using var transportDeadline = new CancellationTokenSource(options.RequestTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transportDeadline.Token);
        requestCancellation = deadline.Token;
        using var request = await RequestAsync("responses", body, deadline.Token).ConfigureAwait(false);
        identity?.ApplyHeaders(request, settings.RequestKind);
        var routingGeneration = responsesRouting.Prepare(request, identity);
        request.Headers.Accept.ParseAdd("text/event-stream");
        diagnostics.RequestBytes = request.Content?.Headers.ContentLength;
        diagnostics.Mark("request.prepared");
        if (usageObserver is { RequiresPreparedRequest: true })
        {
            using var prepared = JsonDocument.Parse(body.ToJsonString());
            await usageObserver.PreparedAsync(accountingId, settings.Model, prepared.RootElement, deadline.Token).ConfigureAwait(false);
        }
        var started = Stopwatch.GetTimestamp();
        deadline.Token.ThrowIfCancellationRequested();
        using var ws = settings.WebSocketSession is { } session
            ? await session.OpenAsync(request, responsesRouting, routingGeneration, options.MaximumMessageBytes, deadline.Token, settings.PrewarmWebSocket).ConfigureAwait(false)
            : null;
        diagnostics.ObserveQuotas(ws?.Quotas);
        if (settings.WebSocketSession is not null && ws is null) diagnostics.Mark("transport.websocket_fallback_sse");
        if (deadline.IsCancellationRequested)
        {
            ws?.KeepUnused();
            deadline.Token.ThrowIfCancellationRequested();
        }
        // Entering SendAsync is the conservative boundary: no replay after ambiguous dispatch.
        // Even unexpected generation during a prewarm must not be replayed automatically.
        if (ws is not null && settings.PrewarmWebSocket && ws.NeedsPrewarm)
        {
            dispatchStarted = true;
            diagnostics.Mark("transport.websocket_prewarm");
            await ws.PrewarmAsync(body, deadline.Token).ConfigureAwait(false);
            dispatchStarted = false;
        }
        if (deadline.IsCancellationRequested)
        {
            ws?.KeepUnused();
            deadline.Token.ThrowIfCancellationRequested();
        }
        dispatchStarted = true;
        using var response = ws is null
            ? await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false)
            : null;
        if (response is not null)
        {
            diagnostics.Mark("response.headers");
            diagnostics.ObserveQuotaHeaders(response.Headers);
            await EnsureCallSuccessAsync(response, deadline.Token).ConfigureAwait(false);
            responsesRouting.Capture(response, routingGeneration);
        }
        else
        {
            diagnostics.Mark("transport.websocket");
            await ws!.SendAsync(body, false, deadline.Token).ConfigureAwait(false);
        }
        await using var stream = response is not null
            ? await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false) : null;
        var completed = false;
        var outputValidation = new CodexResponseOutputValidation(options.MaximumMessageBytes);
        var correlation = ws?.NewCorrelation() ?? new CodexResponsesCorrelation();
        if (ws is not null)
            interruption = new(correlation, cancellationToken, settings.WebSocketDrain, diagnostics.Mark);
        var events = interruption is not null
            ? interruption.ReadAsync(ws!.ReadAsync, ws!.SendFrameAsync, ws!.ConsumeLateDrainAcknowledgement, transportDeadline.Token)
            : ReadSseAsync(stream!, options.MaximumMessageBytes, deadline.Token);
        await foreach (var data in events.ConfigureAwait(false))
        {
            if (data == "[DONE]") break;
            using var document = JsonDocument.Parse(data);
            var payload = document.RootElement.Clone();
            var type = CodexRealtimeProtocol.Text(payload, "type") ?? throw new CodexProtocolException("Responses event omitted its type.");
            if (correlation.Observe(type, payload) is { } correlationFailure)
            {
                diagnostics.Mark("response.protocol_rejected." + correlationFailure);
                throw new CodexProtocolException("Responses stream identity rejected (" + correlationFailure + "); usage is unknown and output must not execute.");
            }
            diagnostics.BindResponseId(correlation.ResponseId);
            responsesRouting.CaptureEvent(payload, type, routingGeneration);
            var interruptedTerminal = interruption?.InterruptSent == true &&
                CodexResponsesInterruption.IsInterruptedTerminal(type, payload);
            var terminal = type == "response.completed" || interruptedTerminal;
            if (type is "error" or "response.failed" || type == "response.incomplete" && !interruptedTerminal)
            {
                incompleteUsage = CodexObservedUsage.Read(payload, identity, accountingId) is { } observed
                    ? observed with { ResponseId = observed.ResponseId ?? correlation.ResponseId } : null;
                if (type is "response.failed" or "response.incomplete" && incompleteUsage is not null &&
                    correlation.ResponseId is { } incompleteId &&
                    payload.TryGetProperty("response", out var incompleteResponse) &&
                    incompleteResponse.ValueKind == JsonValueKind.Object &&
                    CodexRealtimeProtocol.Text(incompleteResponse, "id") == incompleteId)
                    incompleteEvidence = payload;
                var category = diagnostics.Failure(type, payload);
                var error = payload.TryGetProperty("response", out var failed) && failed.ValueKind == JsonValueKind.Object &&
                    failed.TryGetProperty("error", out var nested) ? nested :
                    payload.TryGetProperty("error", out var direct) ? direct : default;
                if (error.ValueKind == JsonValueKind.Object && CodexUsageLimitException.TryRead(429, error, null) is { } quota)
                    throw quota;
                throw new CodexProtocolException($"Codex response terminated ({type}; {category}); usage is unknown and output must not execute.");
            }
            diagnostics.Event(interruptedTerminal ? "response.completed" : type, payload);
            string? outputFailure = null;
            var validatedBeforeAccounting = false;
            if (terminal && usageObserver is not null &&
                payload.TryGetProperty("response", out var accountingResponse) && accountingResponse.ValueKind == JsonValueKind.Object)
            {
                accountingAttempted = true;
                // A confirmed terminal receipt must survive cancellation of the caller.
                using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var validated = false;
                var nativeObserver = usageObserver as ICodexValidatedResponseObserver;
                try
                {
                    if (nativeObserver is not null && settings.RequestKind is ("turn" or "compaction") &&
                        type == "response.completed" && !interruptedTerminal &&
                        interruption?.InterruptSent != true && !cancellationToken.IsCancellationRequested)
                    {
                        validatedBeforeAccounting = true;
                        outputFailure = outputValidation.Event(type, payload);
                        validated = outputFailure is null && outputValidation.CompletedOutput is not null;
                    }
                }
                finally
                {
                    // Even rejected output or an unexpected validation exception must
                    // account for the known terminal. Only validated normal inference
                    // may settle model-visible sampling evidence with its receipt.
                    if (validated && correlation.ResponseId is { } responseId && nativeObserver is not null)
                        await nativeObserver.CompleteValidatedAsync(accountingId, settings, responseId,
                            accountingResponse, outputValidation.CompletedOutput!, save.Token).ConfigureAwait(false);
                    else if (usageObserver is ICodexAbnormalResponseObserver abnormal &&
                        correlation.ResponseId is { } abnormalId &&
                        CodexRealtimeProtocol.Text(accountingResponse, "id") == abnormalId)
                        await abnormal.CompleteUnvalidatedAsync(accountingId, settings, type, abnormalId,
                            accountingResponse, save.Token).ConfigureAwait(false);
                    else
                        await usageObserver.CompleteAsync(accountingId, settings.Model, accountingResponse, save.Token).ConfigureAwait(false);
                    accountingComplete = true;
                    diagnostics.Mark("usage.persisted");
                }
            }
            if (terminal && interruption is not null && cancellationToken.IsCancellationRequested)
            {
                ws!.CompleteInterrupted(correlation.ResponseId!, interruption.HandshakeComplete &&
                    (!interruption.InterruptSent || interruption.Acknowledged));
                diagnostics.Mark("transport.websocket_cancelled");
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!validatedBeforeAccounting) outputFailure = outputValidation.Event(type, payload, interruptedTerminal);
            if (outputFailure is not null)
            {
                diagnostics.Mark("response.protocol_rejected." + outputFailure);
                throw new CodexProtocolException("Codex completed output rejected (" + outputFailure + "); not a silent success.");
            }
            completed |= terminal;
            if (completed && ws is not null)
            {
                ws.Complete(body, correlation.ResponseId!, outputValidation.CompletedOutput!,
                    interruption!.InterruptSent && !interruption.Acknowledged);
            }
            yield return new(type, payload, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
            {
                Identity = identity, RequestId = accountingId, ResponseId = correlation.ResponseId,
                CompletedOutput = completed ? outputValidation.CompletedOutput : null, Interrupted = interruptedTerminal,
            };
            if (completed) yield break;
        }
        if (!completed)
        {
            diagnostics.Mark("response.stream_ended");
            throw new CodexProtocolException("Responses stream ended before response.completed; do not replay consequential calls.");
        }
        }
        finally
        {
            if (!accountingComplete && usageObserver is not null)
            {
                using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (!accountingAttempted && interruption?.TerminalReceipt is { } receipt &&
                    receipt.TryGetProperty("response", out var terminalResponse) && terminalResponse.ValueKind == JsonValueKind.Object)
                {
                    // The terminal may have arrived before a rejected or lost interrupt acknowledgement.
                    // Do not regenerate or discard its provider usage when retiring the socket.
                    if (usageObserver is ICodexAbnormalResponseObserver abnormal &&
                        CodexRealtimeProtocol.Text(receipt, "type") is { } receiptType &&
                        CodexRealtimeProtocol.Text(terminalResponse, "id") is { } receiptId)
                        await abnormal.CompleteUnvalidatedAsync(accountingId, settings, receiptType, receiptId,
                            terminalResponse, save.Token).ConfigureAwait(false);
                    else
                        await usageObserver.CompleteAsync(accountingId, settings.Model, terminalResponse, save.Token).ConfigureAwait(false);
                    diagnostics.Mark("usage.persisted_after_interrupt");
                }
                else if (dispatchStarted)
                {
                    try { diagnostics.Interrupted(cancellationToken.IsCancellationRequested, requestCancellation.IsCancellationRequested); }
                    finally
                    {
                        // Diagnostic callbacks must not prevent conservative accounting.
                        if (!accountingAttempted && usageObserver is ICodexAbnormalResponseObserver abnormal &&
                            incompleteEvidence is { } evidence && incompleteUsage is { } usage)
                            await abnormal.ObserveIncompleteAsync(accountingId, settings, evidence, usage, save.Token).ConfigureAwait(false);
                        else
                            await usageObserver.IncompleteWithUsageAsync(accountingId, settings.Model, incompleteUsage, save.Token).ConfigureAwait(false);
                    }
                }
                else
                    await usageObserver.NotDispatchedAsync(accountingId, settings.Model, save.Token).ConfigureAwait(false);
            }
        }
    }

    internal static async IAsyncEnumerable<string> ReadSseAsync(Stream stream, int maximumBytes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Bound bytes before decoding or accumulating lines; StreamReader.ReadLine alone is unbounded.
        var buffer = new byte[8192]; using var line = new MemoryStream(); var data = new StringBuilder(); var eventBytes = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                if (++eventBytes > maximumBytes) throw new CodexProtocolException("Responses event exceeded the configured bound.");
                if (buffer[i] != '\n') { line.WriteByte(buffer[i]); continue; }
                var text = new UTF8Encoding(false, true).GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r'); line.SetLength(0);
                if (text.Length == 0)
                {
                    if (data.Length > 0) { yield return data.ToString().TrimEnd('\n'); data.Clear(); }
                    eventBytes = 0;
                }
                else if (text.StartsWith("data:", StringComparison.Ordinal))
                {
                    var value = text.AsSpan(5); if (value.StartsWith(" ")) value = value[1..];
                    data.Append(value); data.Append('\n');
                }
            }
        }
        // EOF does not dispatch a partial SSE event; the caller requires an explicit terminal event.
    }
}
