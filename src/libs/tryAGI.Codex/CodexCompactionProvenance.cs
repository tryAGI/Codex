using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

// Pinned Rust: compact_pending_input.rs, context_manager/sampling.rs and
// context_manager/history_rejected_audio.rs. All evidence is host metadata.
internal static class CodexCompactionProvenance
{
    internal const string ClearFileSources = "compaction_without_file_error_sources";
    internal const string SampledSequence = "sampled_through_sequence";

    internal static long SampledThrough(IReadOnlyList<CodexRetainedContextEntry> history)
    {
        long sampled = 0;
        foreach (var entry in history)
            if (entry.Metadata is { ValueKind: JsonValueKind.Object } metadata &&
                metadata.TryGetProperty(SampledSequence, out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var sequence))
                sampled = Math.Max(sampled, sequence);
        return sampled;
    }

    internal static bool InputWithoutFileErrorSources(IReadOnlyList<CodexRetainedContextEntry> history)
        => history.All(WithoutFileErrorSources);

    private static bool WithoutFileErrorSources(CodexRetainedContextEntry entry)
    {
        var item = entry.Item;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var type = CodexRealtimeProtocol.Text(item, "type");
        if (type is "compaction" or "context_compaction" or "compaction_summary")
        {
            // The provider's raw output is not evidence. Only the marker from
            // a prior trusted compaction of this ciphertext can establish provenance.
            return type != "compaction_summary" &&
                !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(item, "encrypted_content")) &&
                CodexRetainedJournalMetadata.Flag(entry.Metadata, ClearFileSources);
        }
        var message = type == "message" || type is null && CodexRealtimeProtocol.Text(item, "role") is not null;
        var toolOutput = type is "function_call_output" or "custom_tool_call_output";
        if (!message && !toolOutput) return true;
        if (!item.TryGetProperty(message ? "content" : "output", out var content)) return false;
        if (content.ValueKind == JsonValueKind.String) return true;
        if (content.ValueKind != JsonValueKind.Array) return false;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) return false;
            switch (CodexRealtimeProtocol.Text(part, "type"))
            {
                case "input_text":
                case "output_text":
                case "encrypted_content" when toolOutput:
                    continue;
                case "input_audio":
                    // File-backed and inline audio compete for rejection attribution.
                    return false;
                case "input_image":
                    // Rust allows ordinary inline images; file references compete.
                    if (!IsInlineImage(part)) return false;
                    continue;
                default:
                    // Unknown content shapes cannot prove the absence of files.
                    return false;
            }
        }
        return true;
    }

    private static bool IsInlineImage(JsonElement part)
    {
        if (part.TryGetProperty("file_id", out var file) && file.ValueKind != JsonValueKind.Null)
            return false;
        if (part.TryGetProperty("image", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.Object ||
                reference.TryGetProperty("file_id", out file) && file.ValueKind != JsonValueKind.Null)
                return false;
            return !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(reference, "image_url"));
        }
        return !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(part, "image_url"));
    }

    internal static JsonElement? CheckpointMetadata(CodexRetainedContextEntry checkpoint, long sampled, bool clear)
    {
        var metadata = checkpoint.Metadata is { ValueKind: JsonValueKind.Object } existing
            ? JsonNode.Parse(existing.GetRawText())!.AsObject() : new JsonObject();
        // Do not copy a caller-supplied assertion of delivery or file provenance
        // onto a new ciphertext. Both are derived from the selected source history.
        metadata.Remove(ClearFileSources);
        metadata.Remove(SampledSequence);
        if (clear && !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(checkpoint.Item, "encrypted_content")))
            metadata[ClearFileSources] = true;
        if (sampled > 0) metadata[SampledSequence] = sampled;
        if (metadata.Count == 0) return null;
        using var json = JsonDocument.Parse(metadata.ToJsonString());
        return json.RootElement.Clone();
    }
}
