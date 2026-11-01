using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Validate output bodies before a completed response can authorize tool dispatch or silence.</summary>
internal sealed class CodexResponseOutputValidation
{
    private string? streamedFailure;

    internal string? Event(string type, JsonElement payload)
    {
        if (type == "response.output_item.done")
        {
            streamedFailure ??= payload.TryGetProperty("item", out var item) ? Item(item) : "missing_item";
            return null; // Drain to terminal usage; completed items alone never authorize dispatch.
        }
        if (type != "response.completed") return null;
        if (!payload.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return "missing_response";
        if (response.TryGetProperty("status", out var status) &&
            (status.ValueKind != JsonValueKind.String || status.GetString() != "completed")) return "invalid_terminal_status";
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return "invalid_output_array";
        if (streamedFailure is not null) return streamedFailure;
        foreach (var item in output.EnumerateArray())
            if (Item(item) is { } failure) return failure;
        return null;
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
