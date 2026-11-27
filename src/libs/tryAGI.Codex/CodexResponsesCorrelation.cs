using System.Text.Json;

namespace tryAGI.Codex;

// A Responses stream is one logical generation. Never attribute a terminal frame
// from another response (including a previous generation on the same socket).
internal sealed class CodexResponsesCorrelation(Func<string, bool>? retired = null, bool requireTerminalId = false)
{
    internal string? ResponseId { get; private set; }

    internal string? Observe(string type, JsonElement payload)
    {
        if (!type.StartsWith("response.", StringComparison.Ordinal) &&
            type is not ("error" or "codex.response.metadata")) return null;

        string? rootId = null, nestedId = null;
        if (!ReadId(payload, "response_id", out rootId)) return "invalid_response_id";
        if (payload.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
            !ReadId(response, "id", out nestedId)) return "invalid_response_id";
        if (rootId is not null && nestedId is not null && rootId != nestedId) return "response_id_mismatch";
        var observed = nestedId ?? rootId;
        if (requireTerminalId && type == "response.completed" && nestedId is null)
            return "missing_response_id";
        if (observed is null) return null; // Legacy SSE may omit IDs; do not invent one.
        if (retired?.Invoke(observed) == true) return "retired_response_id";
        if (ResponseId is not null && ResponseId != observed) return "response_id_mismatch";
        ResponseId = observed;
        return null;
    }

    private static bool ReadId(JsonElement payload, string property, out string? id)
    {
        id = null;
        if (!payload.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 512 } text ||
            text.Any(character => character is < '!' or > '~')) return false;
        id = text;
        return true;
    }
}
