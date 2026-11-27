using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Validate and reconcile all output before completion can authorize tool dispatch or silence.</summary>
internal sealed class CodexResponseOutputValidation(int maximumMessageBytes = 4 * 1024 * 1024)
{
    private const int MaximumItems = 4096;
    private readonly SortedDictionary<int, JsonElement> streamed = [];
    private readonly Dictionary<string, int> streamedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> previewPositions = new(StringComparer.Ordinal);
    private readonly HashSet<int> previewIndices = [];
    private readonly HashSet<string> previewIds = new(StringComparer.Ordinal);
    private string? streamedFailure;
    private long streamedBytes;
    private int nextIndex;
    private int anonymousAdded;
    private bool sawPreview;
    internal IReadOnlyList<JsonElement>? CompletedOutput { get; private set; }

    internal string? Event(string type, JsonElement payload)
    {
        if (type == "response.output_item.done")
        {
            streamedFailure ??= RecordDone(payload);
            return null; // Drain to terminal usage; completed items alone never authorize dispatch.
        }
        if (type == "response.output_item.added" ||
            type.StartsWith("response.", StringComparison.Ordinal) && type.EndsWith(".delta", StringComparison.Ordinal))
        {
            streamedFailure ??= RecordPreview(type, payload);
            return null;
        }
        if (type != "response.completed") return null;
        if (!payload.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return "missing_response";
        if (response.TryGetProperty("status", out var status) &&
            (status.ValueKind != JsonValueKind.String || status.GetString() != "completed")) return "invalid_terminal_status";
        var hasTerminalOutput = response.TryGetProperty("output", out var output);
        if (hasTerminalOutput && output.ValueKind != JsonValueKind.Array) return "invalid_output_array";
        // A Rust Responses completion may contain only a response ID. The separately
        // completed items are authoritative; retain the original sparse receipt unchanged.
        if (!hasTerminalOutput && string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(response, "id")))
            return "invalid_output_array";
        if (streamedFailure is not null) return streamedFailure;
        JsonElement[] items;
        if (hasTerminalOutput && output.GetArrayLength() != 0)
        {
            if (output.GetArrayLength() > MaximumItems) return "too_many_output_items";
            items = output.EnumerateArray().Select(item => item.Clone()).ToArray();
            if (streamed.Any(pair => pair.Key >= items.Length || !JsonElement.DeepEquals(pair.Value, items[pair.Key])))
                return "terminal_output_conflict";
        }
        else
        {
            if (!streamed.Keys.SequenceEqual(Enumerable.Range(0, streamed.Count))) return "missing_output_items";
            items = streamed.Values.ToArray();
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (Item(item) is { } failure) return failure;
            if (CodexRealtimeProtocol.Text(item, "id") is { } id && !ids.Add(id)) return "repeated_output_item_id";
        }
        if (sawPreview && items.Length == 0 || anonymousAdded > items.Length ||
            previewIndices.Any(index => index >= items.Length) || previewIds.Any(id => !ids.Contains(id)))
            return "unfinished_streamed_output";
        if (previewPositions.Any(pair => pair.Value >= items.Length || CodexRealtimeProtocol.Text(items[pair.Value], "id") != pair.Key))
            return "terminal_output_conflict";
        CompletedOutput = Array.AsReadOnly(items);
        return null;
    }

    private string? RecordDone(JsonElement payload)
    {
        if (!payload.TryGetProperty("item", out var item)) return "missing_item";
        if (Item(item) is { } failure) return failure;
        if (!TryIndex(payload, out var explicitIndex)) return "invalid_output_index";
        var id = CodexRealtimeProtocol.Text(item, "id");
        int index;
        if (id is not null && streamedIds.TryGetValue(id, out var priorIndex))
        {
            if (explicitIndex is { } specified && specified != priorIndex) return "repeated_output_item_id";
            index = priorIndex;
        }
        else if (explicitIndex is { } specified) index = specified;
        else if (id is not null && previewPositions.TryGetValue(id, out var previewIndex)) index = previewIndex;
        else index = nextIndex;
        if (index >= MaximumItems) return "too_many_output_items";
        if (streamed.TryGetValue(index, out var previous))
            return JsonElement.DeepEquals(previous, item) ? null : "conflicting_output_items";
        var bytes = Encoding.UTF8.GetByteCount(item.GetRawText());
        if (streamedBytes + bytes > (long)maximumMessageBytes * 4) return "output_budget_exceeded";
        streamedBytes += bytes;
        streamed.Add(index, item.Clone());
        if (id is not null) streamedIds.Add(id, index);
        nextIndex = Math.Max(nextIndex, index + 1);
        return null;
    }

    private string? RecordPreview(string type, JsonElement payload)
    {
        sawPreview = true;
        if (!TryIndex(payload, out var index)) return "invalid_output_index";
        var id = CodexRealtimeProtocol.Text(payload, "item_id");
        if (type == "response.output_item.added")
        {
            if (!payload.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return "missing_item";
            var nestedId = CodexRealtimeProtocol.Text(item, "id");
            if (id is not null && nestedId is not null && id != nestedId) return "output_item_id_mismatch";
            id ??= nestedId;
            if (id is null && index is null && ++anonymousAdded > MaximumItems) return "too_many_output_items";
        }
        if (id is not null)
        {
            if (previewIds.Count >= MaximumItems && !previewIds.Contains(id)) return "too_many_output_items";
            previewIds.Add(id);
            if (index is { } position)
            {
                if (previewPositions.TryGetValue(id, out var previous) && previous != position) return "output_item_id_mismatch";
                previewPositions[id] = position;
            }
        }
        if (index is { } knownIndex) previewIndices.Add(knownIndex);
        return null;
    }

    private static bool TryIndex(JsonElement payload, out int? index)
    {
        index = null;
        if (!payload.TryGetProperty("output_index", out var property)) return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value) ||
            value is < 0 or >= MaximumItems) return false;
        index = value;
        return true;
    }

    private static string? Item(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return "invalid_item";
        var type = CodexRealtimeProtocol.Text(item, "type");
        // Rust custom-tool status is optional metadata, not the stream completion authority.
        // Agent validates tool identity/schema/status representation/replay as a whole batch.
        if (type is "function_call" or "custom_tool_call") return null;
        if (item.TryGetProperty("status", out var status) &&
            (status.ValueKind != JsonValueKind.String || status.GetString() != "completed")) return "unfinished_item";
        if (type is "reasoning" or "compaction" or "compaction_summary")
        {
            if (item.TryGetProperty("encrypted_content", out var encrypted) &&
                encrypted.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return "invalid_encrypted_content";
            if (type == "reasoning")
            {
                if (Parts(item, "summary", "summary_text", false) is { } summaryFailure) return summaryFailure;
                if (Parts(item, "content", "reasoning_text", true) is { } contentFailure) return contentFailure;
            }
            return null;
        }
        if (type != "message") return "unsupported_item";
        if (item.TryGetProperty("role", out var role) &&
            (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant")) return "invalid_message_role";
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "invalid_message_content";
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) return "invalid_message_part";
            var kind = CodexRealtimeProtocol.Text(part, "type");
            if (kind == "refusal") return "refusal"; // Refusal is never a successful silent answer.
            if (kind != "output_text") return "unsupported_message_part";
            if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                return "invalid_output_text";
        }
        return null; // Empty content and whitespace output_text are valid completed model silence.
    }

    private static string? Parts(JsonElement item, string field, string kind, bool nullable)
    {
        if (!item.TryGetProperty(field, out var parts) || nullable && parts.ValueKind == JsonValueKind.Null) return null;
        if (parts.ValueKind != JsonValueKind.Array) return "invalid_reasoning_" + field;
        foreach (var part in parts.EnumerateArray())
            if (part.ValueKind != JsonValueKind.Object || CodexRealtimeProtocol.Text(part, "type") != kind ||
                !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                return "invalid_reasoning_" + field;
        return null;
    }
}
