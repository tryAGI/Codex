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
    /// <summary>Service tier, for example <c>priority</c>.</summary>
    public string? ServiceTier { get; init; }
    /// <summary>Correlation identity sent as client metadata.</summary>
    public CodexResponsesIdentity? Identity { get; init; }
}
/// <summary>One server-sent event of a Responses stream.</summary>
/// <param name="Type">Event type, for example <c>response.output_text.delta</c>.</param>
/// <param name="Payload">Raw event JSON.</param>
/// <param name="ReceivedAfterRequestMs">Milliseconds between sending the request and receiving this event.</param>
public sealed record CodexResponseEvent(string Type, JsonElement Payload, double ReceivedAfterRequestMs);

public sealed partial class CodexClient
{
    /// <summary>Native Codex Responses SSE stream, using ChatGPT OAuth. No hosted connector or CLI agent.</summary>
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
        var dispatchStarted = false;
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
        if (identity is not null) body["client_metadata"] = identity.ClientMetadata();
        if (settings.ReasoningSummary is { } summary && body["reasoning"] is JsonObject reasoning)
            reasoning["summary"] = summary;
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        requestCancellation = deadline.Token;
        using var request = await RequestAsync("responses", body, deadline.Token).ConfigureAwait(false);
        identity?.ApplyHeaders(request);
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
        // Entering SendAsync is the conservative boundary: even an immediate
        // transport exception cannot prove that the provider received no bytes.
        dispatchStarted = true;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        diagnostics.Mark("response.headers");
        await EnsureCallSuccessAsync(response, deadline.Token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        var completed = false;
        var outputValidation = new CodexResponseOutputValidation();
        await foreach (var data in ReadSseAsync(stream, options.MaximumMessageBytes, deadline.Token).ConfigureAwait(false))
        {
            if (data == "[DONE]") break;
            using var document = JsonDocument.Parse(data);
            var payload = document.RootElement.Clone();
            var type = CodexRealtimeProtocol.Text(payload, "type") ?? throw new CodexProtocolException("Responses event omitted its type.");
            if (type is "error" or "response.failed" or "response.incomplete")
            {
                var category = diagnostics.Failure(type, payload);
                throw new CodexProtocolException($"Codex response terminated ({type}; {category}); usage is unknown and output must not execute.");
            }
            diagnostics.Event(type, payload);
            if (type == "response.completed" && usageObserver is not null &&
                payload.TryGetProperty("response", out var accountingResponse) && accountingResponse.ValueKind == JsonValueKind.Object)
            {
                await usageObserver.CompleteAsync(accountingId, settings.Model, accountingResponse, deadline.Token).ConfigureAwait(false);
                accountingComplete = true;
                diagnostics.Mark("usage.persisted");
            }
            if (outputValidation.Event(type, payload) is { } outputFailure)
            {
                diagnostics.Mark("response.protocol_rejected." + outputFailure);
                throw new CodexProtocolException("Codex completed output rejected (" + outputFailure + "); not a silent success.");
            }
            completed |= type == "response.completed";
            yield return new(type, payload, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
                if (dispatchStarted)
                {
                    try { diagnostics.Interrupted(cancellationToken.IsCancellationRequested, requestCancellation.IsCancellationRequested); }
                    finally
                    {
                        // Diagnostic callbacks must not prevent conservative accounting.
                        await usageObserver.IncompleteAsync(accountingId, settings.Model, save.Token).ConfigureAwait(false);
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
