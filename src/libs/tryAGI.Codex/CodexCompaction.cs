using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>A model-context checkpoint, not a replacement for the host's original journal.
/// Retain this result with its model identity and executed call IDs before using its projection.</summary>
public sealed record CodexCompactionResult(string Model, string ResponseId, JsonElement Item,
    JsonElement? Usage, IReadOnlyList<JsonElement> OriginalHistory, IReadOnlyList<string> ExecutedCallIds)
{
    /// <summary>The host chooses retained user/hook and explicitly client-authored developer messages.
    /// This does not implement Rust's metadata, image or token retention policy. OriginalHistory remains intact.</summary>
    public IReadOnlyList<JsonElement> CreatePrompt(IReadOnlyList<JsonElement> retainedMessages)
    {
        if (retainedMessages.Any(item => item.ValueKind != JsonValueKind.Object ||
            CodexRealtimeProtocol.Text(item, "role") is not ("user" or "developer") ||
            CodexRealtimeProtocol.Text(item, "type") is not (null or "message")))
            throw new ArgumentException("Retain only user or explicitly client-authored developer messages.", nameof(retainedMessages));
        return retainedMessages.Select(item => item.Clone()).Append(Item.Clone()).ToArray();
    }
}

public sealed partial class CodexClient
{
    /// <summary>Rust remote compaction V2: ordinary Responses SSE with a trailing compaction_trigger.
    /// No tools execute, no automatic retry or history mutation occurs, and normal usage admission applies.
    /// Provider acceptance and installation/retention policy remain separate host gates.</summary>
    public async Task<CodexCompactionResult> CompactAsync(CodexResponseOptions settings,
        IReadOnlyList<JsonElement> history, CancellationToken cancellationToken = default,
        Action<CodexResponseDiagnostic>? observeDiagnostics = null,
        IReadOnlyCollection<string>? previouslyExecutedCallIds = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Any(item => item.ValueKind != JsonValueKind.Object ||
            CodexRealtimeProtocol.Text(item, "type") == "compaction_trigger"))
            throw new ArgumentException("Compaction requires history objects without a request trigger.", nameof(history));
        var seen = CodexAgent.ExecutedIds(history, previouslyExecutedCallIds);
        var original = history.Select(item => item.Clone()).ToArray();
        using var trigger = JsonDocument.Parse("{\"type\":\"compaction_trigger\"}");
        var input = original.Append(trigger.RootElement.Clone()).ToArray();
        var completedItems = new SortedDictionary<int, JsonElement>();
        JsonElement? terminal = null;
        // Rust retains the current model/tools/instructions, enables parallel tool calls,
        // and disables the ordinary final-answer schema for this request.
        var options = settings with { ParallelToolCalls = true, OutputSchema = null };
        using var activity = ActivitySource.StartActivity("codex.responses.compact");
        await foreach (var response in StreamResponseAsync(options, input, cancellationToken, observeDiagnostics).ConfigureAwait(false))
        {
            if (response.Type == "response.output_item.done")
            {
                if (!response.Payload.TryGetProperty("output_index", out var indexValue) || !indexValue.TryGetInt32(out var index) ||
                    index is < 0 or >= 4096 || !response.Payload.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                    throw new CodexProtocolException("Compaction output item has an invalid index or body.");
                if (completedItems.TryGetValue(index, out var previous) && !JsonElement.DeepEquals(previous, item))
                    throw new CodexProtocolException("Conflicting compaction output items.");
                completedItems[index] = item.Clone();
            }
            if (response.Type == "response.completed") terminal = response.Payload.GetProperty("response").Clone();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal is not { } result || !result.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array ||
            string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(result, "id")) ||
            CodexRealtimeProtocol.Text(result, "status") is not (null or "completed"))
            throw new CodexProtocolException("Compaction requires a completed response with an ID and output array.");
        var items = output.EnumerateArray().Select(item => item.Clone()).ToArray();
        if (items.Length == 0)
        {
            if (!completedItems.Keys.SequenceEqual(Enumerable.Range(0, completedItems.Count)))
                throw new CodexProtocolException("Compaction response has missing output items.");
            items = completedItems.Values.ToArray();
        }
        else if (completedItems.Any(pair => pair.Key >= items.Length || !JsonElement.DeepEquals(pair.Value, items[pair.Key])))
            throw new CodexProtocolException("Compaction response conflicts with completed output items.");
        var checkpoints = items.Where(item => CodexRealtimeProtocol.Text(item, "type") is "compaction" or "compaction_summary").ToArray();
        if (checkpoints.Length != 1 || string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(checkpoints[0], "encrypted_content")))
            throw new CodexProtocolException("Compaction requires exactly one nonempty encrypted compaction item.");
        // Rust accepts other output items but installs only the compaction checkpoint.
        // In particular an accompanying function call never reaches any executor.
        var checkpoint = checkpoints[0];
        if (CodexRealtimeProtocol.Text(checkpoint, "type") == "compaction_summary")
        {
            var canonical = (JsonNode.Parse(checkpoint.GetRawText()) ?? throw new InvalidDataException("Required data was null.")).AsObject();
            canonical["type"] = "compaction";
            using var parsed = JsonDocument.Parse(canonical.ToJsonString());
            checkpoint = parsed.RootElement.Clone();
        }
        return new(settings.Model, (CodexRealtimeProtocol.Text(result, "id") ?? throw new InvalidDataException("Required data was null.")), checkpoint.Clone(),
            result.TryGetProperty("usage", out var usage) ? usage.Clone() : null, original, seen.Order(StringComparer.Ordinal).ToArray());
    }
}
