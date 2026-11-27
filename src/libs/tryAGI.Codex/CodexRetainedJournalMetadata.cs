using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>A trusted host annotation of one original turn input or output item.
/// Position and digest prevent duplicate text from inheriting another item's retention policy.</summary>
/// <param name="Output">True for the original output array; false for the input array.</param>
/// <param name="Index">Zero-based index in the selected array.</param>
/// <param name="ItemSha256">Digest of the exact JSON item, independent of whitespace.</param>
/// <param name="Metadata">Opaque harness metadata, never sent to the model as a new item.</param>
public sealed record CodexStoredRetainedContextMetadata(
    [property: JsonRequired] bool Output,
    [property: JsonRequired] int Index,
    [property: JsonRequired] string ItemSha256,
    [property: JsonRequired] JsonElement Metadata)
{
    /// <summary>Create an annotation bound to an observed immutable item.</summary>
    public static CodexStoredRetainedContextMetadata Create(bool output, int index, JsonElement item, JsonElement metadata)
        => new(output, index, CodexRetainedJournalMetadata.ItemHash(item), metadata.Clone());
}

internal static class CodexRetainedJournalMetadata
{
    private const int MaximumMetadataBytes = 16 * 1024;
    private const int MaximumTurnMetadataBytes = 1024 * 1024;

    internal static string ItemHash(JsonElement item)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) item.WriteTo(writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static bool Flag(JsonElement? metadata, string key) =>
        metadata is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(key, out var flag) &&
        flag.ValueKind == JsonValueKind.True;

    internal static bool ValidMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(metadata.GetRawText()) > MaximumMetadataBytes)
            return false;
        foreach (var name in new[] { "retain_across_windows", "client_authored", CodexCompactionProvenance.ClearFileSources })
            if (metadata.TryGetProperty(name, out var flag) &&
                flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                return false;
        foreach (var name in new[] { "history_sequence", CodexCompactionProvenance.SampledSequence })
            if (metadata.TryGetProperty(name, out var sequence) && sequence.ValueKind != JsonValueKind.Null &&
                (sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out var number) ||
                 number < (name == "history_sequence" ? 1 : 0)))
                return false;
        return true;
    }

    internal static bool IsCall(JsonElement item) => CodexRealtimeProtocol.Text(item, "type") is "function_call" or "custom_tool_call";
    internal static bool IsOutput(JsonElement item) => CodexRealtimeProtocol.Text(item, "type") is "function_call_output" or "custom_tool_call_output";
    internal static bool IsStandalone(JsonElement item) => CodexRealtimeProtocol.Text(item, "type") == "function_call_output" &&
        (!item.TryGetProperty("call_id", out var callId) || callId.ValueKind == JsonValueKind.Null) &&
        !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(item, "name"));

    internal static bool Pair(JsonElement call, JsonElement output) =>
        (CodexRealtimeProtocol.Text(call, "type"), CodexRealtimeProtocol.Text(output, "type")) is
            ("function_call", "function_call_output") or ("custom_tool_call", "custom_tool_call_output") &&
        CodexRealtimeProtocol.Text(call, "call_id") is { Length: > 0 } id &&
        CodexRealtimeProtocol.Text(output, "call_id") == id;

    internal static bool SettledPair(CodexStoredTurn turn, JsonElement call, JsonElement output) =>
        Pair(call, output) && turn.Dispatches.Any(receipt => receipt is not null && receipt.Kind == "tool" && receipt.State == "succeeded" &&
            receipt.CallId == CodexRealtimeProtocol.Text(call, "call_id") &&
            receipt.Result is { } result && JsonElement.DeepEquals(output, result));

    internal static bool IsMessage(JsonElement item) =>
        CodexRealtimeProtocol.Text(item, "type") is null or "message" &&
        CodexRealtimeProtocol.Text(item, "role") is "user" or "developer";

    internal static void Validate(CodexStoredTurn turn)
    {
        if (turn.RetainedContextMetadata is not { } entries) return;
        if (turn.Status != "completed" || entries.Length > 4096 ||
            turn.Input is null || turn.Output is null || turn.Dispatches is null)
            throw new InvalidDataException("Retention annotations require a bounded, completed native turn.");
        var positions = new HashSet<(bool, int)>();
        long bytes = 0;
        foreach (var entry in entries)
        {
            if (entry is null || entry.Index < 0 || !positions.Add((entry.Output, entry.Index)) ||
                entry.Index >= (entry.Output ? turn.Output.Length : turn.Input.Length) ||
                !entry.Output && (turn.RealtimeSteeringInputIndices ?? []).Contains(entry.Index) ||
                !ValidMetadata(entry.Metadata) ||
                entry.Metadata.TryGetProperty(CodexCompactionProvenance.ClearFileSources, out _))
                throw new InvalidDataException("Invalid native retention annotation position or metadata.");
            bytes += Encoding.UTF8.GetByteCount(entry.Metadata.GetRawText());
            if (bytes > MaximumTurnMetadataBytes)
                throw new InvalidDataException("Native turn retention metadata exceeds its bound.");
            var item = (entry.Output ? turn.Output : turn.Input)[entry.Index];
            if (item.ValueKind != JsonValueKind.Object || entry.ItemSha256 != ItemHash(item))
                throw new InvalidDataException("Retention annotation does not match its original history item.");
        }

        var annotatedOutputs = entries.Where(entry => entry.Output && Flag(entry.Metadata, "retain_across_windows"))
            .Select(entry => entry.Index).ToHashSet();
        foreach (var entry in entries.Where(entry => Flag(entry.Metadata, "retain_across_windows")))
        {
            var item = (entry.Output ? turn.Output : turn.Input)[entry.Index];
            if (IsMessage(item) || IsStandalone(item) || CodexRealtimeProtocol.Text(item, "type") == "agent_message") continue;
            // A model-generated call is never made retained independently of its
            // adjacent, known successful receipt, nor on the authority of raw wire metadata.
            if (entry.Output && (IsCall(item) || IsOutput(item)))
            {
                var callIndex = IsCall(item) ? entry.Index : entry.Index - 1;
                if (callIndex >= 0 && callIndex + 1 < turn.Output.Length &&
                    annotatedOutputs.Contains(callIndex) && annotatedOutputs.Contains(callIndex + 1) &&
                    SettledPair(turn, turn.Output[callIndex], turn.Output[callIndex + 1])) continue;
            }
            throw new InvalidDataException("Explicit retention requires a supported message or a settled adjacent tool exchange.");
        }
    }

    internal static void WritePrefix(Utf8JsonWriter writer, CodexStoredTurn turn, int inputCount, int outputCount)
    {
        var entries = (turn.RetainedContextMetadata ?? []).Where(entry =>
            entry.Index < (entry.Output ? outputCount : inputCount)).OrderBy(entry => entry.Output).ThenBy(entry => entry.Index).ToArray();
        if (entries.Length == 0) return; // Preserve the hash of legacy unannotated checkpoints.
        writer.WriteStartArray("retained_context_metadata");
        foreach (var entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("output", entry.Output);
            writer.WriteNumber("index", entry.Index);
            writer.WriteString("item_hash", entry.ItemSha256);
            writer.WritePropertyName("metadata");
            entry.Metadata.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static Dictionary<(bool Output, int Index), JsonElement> ForTurn(CodexStoredTurn turn)
    {
        Validate(turn);
        return (turn.RetainedContextMetadata ?? []).ToDictionary(entry => (entry.Output, entry.Index), entry => entry.Metadata);
    }

    internal static JsonElement? At(Dictionary<(bool Output, int Index), JsonElement>? annotations, bool output, int index)
        => annotations is not null && annotations.TryGetValue((output, index), out var metadata) ? metadata.Clone() : null;

    internal static bool ValidProjection(CodexThreadDocument document, CodexStoredCompactionCheckpoint checkpoint)
    {
        var projection = checkpoint.Projection;
        var annotations = checkpoint.ProjectionMetadata;
        if (annotations is not null && (annotations.Length != projection.Length ||
            annotations.Where((metadata, index) => metadata is { } value &&
                (!ValidMetadata(value) ||
                 value.TryGetProperty(CodexCompactionProvenance.ClearFileSources, out _) &&
                 !CodexThreadCompaction.IsCheckpoint(projection[index]))).Any()))
            return false;

        // Only inspect original history for structured retained items. Ordinary message-only
        // checkpoints preserve the legacy fast path.
        var structured = projection.Any(item => !IsMessage(item) && !CodexThreadCompaction.IsCheckpoint(item));
        if (!structured) return true;

        var pairs = new Dictionary<string, (JsonElement Call, JsonElement Output, JsonElement CallMetadata, JsonElement OutputMetadata)>(StringComparer.Ordinal);
        var exact = new Dictionary<string, List<(JsonElement Item, JsonElement? Metadata)>>(StringComparer.Ordinal);
        var coveredCount = checkpoint.ThroughTurnCount + (checkpoint.ActiveBoundary is null ? 0 : 1);
        for (var turnIndex = 0; turnIndex < coveredCount; turnIndex++)
        {
            var turn = document.Turns[turnIndex];
            var boundary = turnIndex == checkpoint.ThroughTurnCount ? checkpoint.ActiveBoundary : null;
            if (turn.Status != "completed" && boundary is null) continue;
            var inputCount = boundary?.InputCount ?? turn.Input.Length;
            var outputCount = boundary?.OutputCount ?? turn.Output.Length;
            var metadata = ForTurn(turn);
            void AddExact(JsonElement item, JsonElement? sidecar)
            {
                if (CodexRealtimeProtocol.Text(item, "type") != "agent_message" &&
                    !(IsStandalone(item) && (Flag(sidecar, "retain_across_windows") || Flag(sidecar, "client_authored")))) return;
                var hash = ItemHash(item);
                if (!exact.TryGetValue(hash, out var candidates)) exact[hash] = candidates = [];
                candidates.Add((item, sidecar));
            }

            for (var inputIndex = 0; inputIndex < inputCount; inputIndex++)
                AddExact(turn.Input[inputIndex], At(metadata, false, inputIndex));
            for (var outputIndex = 0; outputIndex < outputCount; outputIndex++)
            {
                var item = turn.Output[outputIndex];
                var sidecar = At(metadata, true, outputIndex);
                AddExact(item, sidecar);
                if (outputIndex + 1 >= outputCount || !IsCall(item) ||
                    sidecar is not { } callMeta || At(metadata, true, outputIndex + 1) is not { } outputMeta ||
                    !Flag(callMeta, "retain_across_windows") || !Flag(outputMeta, "retain_across_windows") ||
                    !SettledPair(turn, item, turn.Output[outputIndex + 1])) continue;
                pairs[CodexRealtimeProtocol.Text(item, "call_id")!] =
                    (item, turn.Output[outputIndex + 1], callMeta, outputMeta);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < projection.Length; index++)
        {
            var item = projection[index];
            if (IsMessage(item) || CodexThreadCompaction.IsCheckpoint(item)) continue;
            var metadata = annotations?[index];
            if (IsCall(item))
            {
                var id = CodexRealtimeProtocol.Text(item, "call_id");
                if (id is null || !seen.Add(id) || index + 1 >= projection.Length ||
                    !pairs.TryGetValue(id, out var original) ||
                    !JsonElement.DeepEquals(original.Call, item) ||
                    !JsonElement.DeepEquals(original.Output, projection[index + 1]) ||
                    !Equal(original.CallMetadata, metadata) || !Equal(original.OutputMetadata, annotations?[index + 1]))
                    return false;
                index++; // The adjacent output is historical, never a new invocation.
            }
            else if (!exact.TryGetValue(ItemHash(item), out var candidates) ||
                !candidates.Any(candidate => JsonElement.DeepEquals(item, candidate.Item) && Equal(metadata, candidate.Metadata)))
                return false;
        }
        return true;
    }

    private static bool Equal(JsonElement? left, JsonElement? right) =>
        left is { } value ? right is { } other && JsonElement.DeepEquals(value, other) : right is null;

    internal static bool Same(IReadOnlyList<JsonElement?> left, IReadOnlyList<JsonElement?> right) =>
        left.Count == right.Count && !left.Where((item, index) => item is { } value
            ? right[index] is not { } other || !JsonElement.DeepEquals(value, other)
            : right[index] is not null).Any();
}

public sealed partial class CodexThreadStore
{
    /// <summary>Replace the trusted host's retention annotations for a completed turn at an exact
    /// revision. A stale candidate or covered checkpoint is invalidated; the original items and
    /// dispatch receipts never change. This API grants no tool authority and is not a provider RPC.</summary>
    public Task<CodexThreadDocument> SetRetainedContextMetadataAsync(Guid threadId, long expectedRevision, Guid turnId,
        IReadOnlyList<CodexStoredRetainedContextMetadata> annotations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (annotations.Count > 4096 || annotations.Any(entry => entry is null || !CodexRetainedJournalMetadata.ValidMetadata(entry.Metadata)))
            throw new ArgumentException("Invalid or oversized retention metadata.", nameof(annotations));
        var owned = annotations.OrderBy(entry => entry.Output).ThenBy(entry => entry.Index)
            .Select(entry => entry with { Metadata = entry.Metadata.Clone() }).ToArray();
        return ChangeAsync(threadId, expectedRevision, document =>
        {
            // Annotation updates are explicit host writes, never races with an active runner.
            if (document.Turns.Any(turn => turn.Status == "inProgress"))
                throw new InvalidOperationException("Retention annotation requires an idle thread.");
            var turn = document.Turns.SingleOrDefault(item => item.Id == turnId)
                ?? throw new KeyNotFoundException("Native retention annotation turn not found.");
            if (turn.Status != "completed") throw new InvalidOperationException("Only completed native turns may be annotated.");
            var updated = turn with { RetainedContextMetadata = owned.Length == 0 ? null : owned };
            CodexRetainedJournalMetadata.Validate(updated);
            return Replace(document, updated);
        }, cancellationToken);
    }
}
