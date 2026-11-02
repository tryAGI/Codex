using System.Diagnostics;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Metadata only. Request-relative timings share one monotonic clock; no prompts, arguments or credentials.</summary>
/// <param name="Phase">Phase of the request, for example <c>first_byte</c> or <c>completed</c>.</param>
/// <param name="RequestId">Local request ID.</param>
/// <param name="ElapsedMilliseconds">Milliseconds since the request started.</param>
public sealed record CodexResponseDiagnostic(string Phase, string RequestId, double ElapsedMilliseconds)
{
    internal long StartedTimestamp { get; init; }
    /// <summary>Duration of the tool call this phase reports.</summary>
    public double? ToolDurationMilliseconds { get; init; }
    /// <summary>The tool ran concurrently with others.</summary>
    public bool? ParallelDispatch { get; init; }
    /// <summary>Namespace of the tool.</summary>
    public string? ToolNamespace { get; init; }
    /// <summary>1-based request number within the run.</summary>
    public int RequestNumber { get; init; } = 1;
    /// <summary>Input items sent.</summary>
    public int InputItems { get; init; }
    /// <summary>Tools advertised.</summary>
    public int ToolCount { get; init; }
    /// <summary>Request body size.</summary>
    public long? RequestBytes { get; init; }
    /// <summary>Provider response ID.</summary>
    public string? ResponseId { get; init; }
    /// <summary>Type of the output item this phase reports.</summary>
    public string? OutputKind { get; init; }
    /// <summary>Index of that output item.</summary>
    public int? OutputIndex { get; init; }
    /// <summary>Call ID of the tool call.</summary>
    public string? FunctionId { get; init; }
    /// <summary>Name of the tool.</summary>
    public string? ToolName { get; init; }
    /// <summary>Length of the tool arguments.</summary>
    public long? ArgumentCharacters { get; init; }
    /// <summary>Input tokens reported by the provider.</summary>
    public long? InputTokens { get; init; }
    /// <summary>Cached input tokens reported by the provider.</summary>
    public long? CachedInputTokens { get; init; }
    /// <summary>Output tokens reported by the provider.</summary>
    public long? OutputTokens { get; init; }
    /// <summary>Reasoning tokens reported by the provider.</summary>
    public long? ReasoningTokens { get; init; }
    /// <summary>Correlation identity sent with the request.</summary>
    public CodexResponsesIdentity? Identity { get; init; }
}

internal sealed class CodexResponseDiagnostics(int inputItems, int toolCount, Action<CodexResponseDiagnostic>? observe)
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly string requestId = Guid.NewGuid().ToString("N");
    internal string RequestId => requestId;
    private bool firstOutput;
    private string? responseId;
    private string? completedOutputShape;
    private string lastTransportPhase = "request.started";
    private bool terminalFailure;
    internal long? RequestBytes { get; set; }
    internal CodexResponsesIdentity? Identity { get; set; }

    private CodexResponseDiagnostic Point(string phase) => new(phase, requestId, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
    { StartedTimestamp = started, InputItems = inputItems, ToolCount = toolCount, RequestBytes = RequestBytes, ResponseId = responseId, Identity = Identity };

    internal void Mark(string phase)
    {
        lastTransportPhase = phase;
        terminalFailure |= phase == "response.stream_ended" || phase.StartsWith("response.protocol_rejected.", StringComparison.Ordinal);
        observe?.Invoke(Point(phase));
    }

    // Keep explicit failures and the latest transport milestone without retaining payloads.
    internal void Interrupted(bool callerCancelled, bool requestCancelled)
    {
        if (terminalFailure) return;
        var cause = callerCancelled ? "caller_cancelled" : requestCancelled ? "timeout" : "interrupted";
        Mark("request." + cause + ".after." + lastTransportPhase);
    }

    // Terminal events never reach the caller's successful output stream. Persist a bounded
    // classification before throwing; never copy a provider message, parameter or raw code.
    // Encoding the category in Phase also preserves it through existing host metadata mappers.
    internal string Failure(string type, JsonElement payload)
    {
        var response = payload.TryGetProperty("response", out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;
        responseId = Text(response, "id") ?? responseId;
        var error = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("error", out var nestedError)
            ? nestedError : payload.TryGetProperty("error", out var rootError) ? rootError : default;
        var reason = type == "response.incomplete" && response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("incomplete_details", out var incomplete)
                ? Text(incomplete, "reason") : Text(error, "code") ?? Text(payload, "code");
        var category = reason switch
        {
            "rate_limit_exceeded" or "server_error" or "context_length_exceeded" or "max_output_tokens" or
            "content_filter" or "insufficient_quota" or "invalid_request_error" => reason,
            _ => "unclassified",
        };
        terminalFailure = true;
        Mark(type + "." + category);
        return category;
    }

    internal void Event(string type, JsonElement payload)
    {
        if (observe is null) return;
        if (payload.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
            responseId = Text(response, "id") ?? responseId;
        var item = payload.TryGetProperty("item", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        var kind = Text(item, "type");
        if (type == "response.output_item.done")
            completedOutputShape = MergeShape(completedOutputShape, OutputShape(item));
        if (!firstOutput && (type == "response.output_item.added" || type == "response.output_item.done" ||
            type is "response.function_call_arguments.delta" or "response.output_text.delta"))
        {
            firstOutput = true;
            lastTransportPhase = "response.first_output";
            observe(Point("response.first_output") with { OutputKind = Kind(kind) });
        }
        if (type == "response.output_item.done" && kind == "function_call")
            observe(Point("response.function_item_completed") with
            {
                OutputKind = "function_call", FunctionId = Text(item, "call_id"), ToolName = Text(item, "name"),
                OutputIndex = Number(payload, "output_index") is { } index && index <= int.MaxValue ? (int)index : null,
                ArgumentCharacters = item.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String
                    ? arguments.GetString()?.Length : null,
            });
        if (type == "response.created") Mark(type);
        if (type == "response.completed")
        {
            lastTransportPhase = type;
            observe(Point(type) with
            {
                InputTokens = Number(response, "usage", "input_tokens"),
                CachedInputTokens = Number(response, "usage", "input_tokens_details", "cached_tokens"),
                OutputTokens = Number(response, "usage", "output_tokens"),
                ReasoningTokens = Number(response, "usage", "output_tokens_details", "reasoning_tokens"),
            });
            // Structural evidence, not a claim that the host accepted this response. A later
            // protocol rejection remains distinct from provider completion and paid usage.
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Array)
            {
                string? shape = null;
                foreach (var entry in output.EnumerateArray()) shape = MergeShape(shape, OutputShape(entry));
                if (output.GetArrayLength() == 0) shape = completedOutputShape;
                observe(Point("response.output_shape." + (shape ?? "empty")));
            }
            else observe(Point("response.output_shape.invalid_output"));
        }
    }

    private static string MergeShape(string? previous, string current)
    {
        // Keep the most consequential kind without retaining provider content.
        static int Rank(string shape) => shape switch
        { "invalid" => 0, "unsupported" => 1, "tool" => 2, "text" => 3, "empty_message" => 4, _ => 5 };
        return previous is null || Rank(current) < Rank(previous) ? current : previous;
    }

    private static string OutputShape(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return "invalid";
        var kind = Text(item, "type");
        if (kind is "function_call" or "custom_tool_call") return "tool";
        if (kind == "reasoning") return "reasoning";
        if (kind != "message") return "unsupported";
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "invalid";
        var shape = "empty_message";
        foreach (var part in content.EnumerateArray())
        {
            if (Text(part, "type") != "output_text") return "unsupported";
            if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return "invalid";
            if (!string.IsNullOrWhiteSpace(text.GetString())) shape = "text";
        }
        return shape;
    }

    private static string? Kind(string? value) => value is "reasoning" or "message" or "function_call" ? value : null;
    private static string? Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 256 } text && !text.Any(char.IsControl) ? text : null;
    private static long? Number(JsonElement value, params string[] path)
    {
        foreach (var key in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var next)) return null;
            value = next;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;
    }
}
