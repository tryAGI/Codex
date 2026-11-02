using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Retain text, identities and provenance without duplicating inference image pixels.</summary>
internal static class CodexThreadImageProjection
{
    // One-to-one history projection: preserve ordering, provenance and tool receipts,
    // while pixel payloads live only in the active inference request.
    public static JsonElement Redact(JsonElement item)
    {
        JsonNode? Project(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "input_image")
                    return new JsonObject { ["type"] = "input_text", ["text"] = "[Historical image pixels omitted. Use retained source metadata; this is not current visual evidence.]" };
                var copy = new JsonObject();
                foreach (var field in obj) copy[field.Key] = Project(field.Value);
                return copy;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(Project).ToArray());
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.StartsWith("data:image/", StringComparison.Ordinal))
                return JsonValue.Create("[Historical image payload omitted]");
            return node?.DeepClone();
        }
        using var document = JsonDocument.Parse((Project(JsonNode.Parse(item.GetRawText())) ?? throw new InvalidDataException("Missing worker history item.")).ToJsonString());
        return document.RootElement.Clone();
    }
}
