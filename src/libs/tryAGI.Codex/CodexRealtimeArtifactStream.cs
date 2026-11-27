using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Evidence of a public assistant preview observed during an admitted inference.
/// This is presentation-only history, not a completed response item or a continuation input.</summary>
public sealed record CodexStoredRealtimeArtifactPreview(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Text,
    string? Phase = null);

internal sealed record CodexRealtimeArtifactCandidate(string ItemId, CodexStoredRealtimeArtifactPreview Preview);

// Only retain bounded, explicitly public assistant text. A new presentation is emitted once,
// not on every token. Completed messages are still validated and journaled by the native agent.
internal sealed class CodexRealtimeArtifactStream
{
    internal const int MaximumPreviewBytes = 32 * 1024;
    private const int MaximumItems = 64;
    private const int MaximumParts = 32;
    private const int MaximumPresentations = 64;
    private string? request;
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private int presentations;

    private sealed class Item(string? phase)
    {
        internal string? Phase { get; } = phase;
        internal SortedDictionary<int, string> Parts { get; } = [];
        internal HashSet<(string, int?)> Presented { get; } = [];
        internal int Bytes;
        internal bool Retired;
    }

    internal CodexRealtimeArtifactCandidate? Observe(CodexResponseEvent source)
    {
        if (!ValidId(source.RequestId)) return null;
        if (source.RequestId != request)
        {
            request = source.RequestId;
            items.Clear();
            presentations = 0;
        }
        var payload = source.Payload;
        string id;
        Item item;
        if (source.Type == "response.output_item.added")
        {
            if (!payload.TryGetProperty("item", out var message) || message.ValueKind != JsonValueKind.Object ||
                CodexRealtimeProtocol.Text(message, "role") != "assistant" ||
                CodexRealtimeProtocol.Text(message, "type") != "message" ||
                CodexRealtimeProtocol.Text(message, "id") is not { } identity || !ValidId(identity) ||
                items.ContainsKey(identity) || items.Count >= MaximumItems)
                return null;
            id = identity;
            var phase = CodexRealtimeProtocol.Text(message, "phase");
            item = new(phase is "commentary" or "partial_answer" or "final_answer" ? phase : null);
            items.Add(id, item);
            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() > MaximumParts)
                { item.Retired = true; return null; }
                var partIndex = 0;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || CodexRealtimeProtocol.Text(part, "type") != "output_text" ||
                        CodexRealtimeProtocol.Text(part, "text") is not { } text || !Append(item, partIndex++, text))
                    { item.Retired = true; return null; }
                }
            }
        }
        else if (source.Type == "response.output_text.delta")
        {
            if (CodexRealtimeProtocol.Text(payload, "item_id") is not { } identity ||
                !items.TryGetValue(identity, out var known) || known.Retired ||
                CodexRealtimeProtocol.Text(payload, "delta") is not { } delta)
                return null;
            id = identity;
            item = known;
            var partIndex = 0;
            if (payload.TryGetProperty("content_index", out var index) &&
                (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out partIndex) || partIndex is < 0 or >= MaximumParts))
            { item.Retired = true; return null; }
            if (!Append(item, partIndex, delta)) { item.Retired = true; return null; }
        }
        else return null;

        // A gap in multipart content is not an observed contiguous assistant message.
        if (!item.Parts.Keys.SequenceEqual(Enumerable.Range(0, item.Parts.Count))) return null;
        var textSnapshot = string.Concat(item.Parts.Values);
        var candidates = CodexRealtimePresentation.AssistantPresentations(textSnapshot).ToArray();
        var fresh = candidates.Count(p => !item.Presented.Contains(p));
        if (fresh == 0 || fresh > MaximumPresentations - presentations) return null;
        foreach (var candidate in candidates) item.Presented.Add(candidate);
        presentations += fresh;
        return new(id, new(request!, textSnapshot, item.Phase));
    }

    private static bool Append(Item item, int index, string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaximumPreviewBytes - item.Bytes) return false;
        item.Parts[index] = item.Parts.GetValueOrDefault(index, "") + text;
        item.Bytes += bytes;
        return true;
    }

    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 512 } &&
        !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl);
}
