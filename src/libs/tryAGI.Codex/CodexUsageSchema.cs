using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>A bounded diagnostic observation, not a billing or quota interpretation.</summary>
public sealed record CodexUsageSchemaSnapshot(string EventType, DateTimeOffset ObservedAt,
    CodexUsageField[] Fields, bool Truncated);

/// <summary>String content and array elements are deliberately not retained.</summary>
public sealed record CodexUsageField(string Path, string Kind, decimal? Number = null,
    bool? Boolean = null, int? StringLength = null, int? ArrayLength = null);

/// <summary>Retains usage schema changes without persisting raw provider events or private text.</summary>
public static class CodexUsageSchema
{
    public const int MaximumFields = 64;
    public const int MaximumPathCharacters = 4096;
    private const int MaximumVisitedProperties = 256;
    private const int MaximumDepth = 4;

    public static CodexUsageSchemaSnapshot? ObserveSession(JsonElement payload, DateTimeOffset observedAt)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() is not ("session.usage.updated" or "session.closed"))
            return null;
        var eventType = type.GetString() ?? string.Empty;

        var fields = new List<CodexUsageField>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var visited = 0;
        var pathCharacters = 0;

        void Visit(JsonElement value, string path, int depth)
        {
            if (fields.Count >= MaximumFields || pathCharacters + path.Length > MaximumPathCharacters || !paths.Add(path))
            { truncated = true; return; }
            pathCharacters += path.Length;
            fields.Add(value.ValueKind switch
            {
                JsonValueKind.Number => new(path, "number", Number: value.TryGetDecimal(out var number) ? number : null),
                JsonValueKind.True => new(path, "boolean", Boolean: true),
                JsonValueKind.False => new(path, "boolean", Boolean: false),
                JsonValueKind.String => new(path, "string", StringLength: value.GetString()?.Length),
                JsonValueKind.Array => new(path, "array", ArrayLength: value.GetArrayLength()),
                JsonValueKind.Null => new(path, "null"),
                _ => new(path, "object"),
            });
            if (value.ValueKind != JsonValueKind.Object) return;
            foreach (var property in value.EnumerateObject())
            {
                if (++visited > MaximumVisitedProperties || fields.Count >= MaximumFields || depth >= MaximumDepth)
                { truncated = true; break; }
                if (property.Name.Length is 0 or > 80 || property.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                { truncated = true; continue; }
                Visit(property.Value, path + "." + property.Name, depth + 1);
            }
        }

        // These are accounting envelopes observed on the Codex V3 connection. Do not
        // recursively traverse unrelated session, transcript, reasoning or audio data.
        foreach (var property in payload.EnumerateObject())
        {
            if (++visited > MaximumVisitedProperties) { truncated = true; break; }
            if (property.Name is "usage" or "usage_limit" or "context_window") Visit(property.Value, property.Name, 0);
        }
        return new(eventType, observedAt, fields.ToArray(), truncated);
    }
}
