using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Stable native mail occurrence, bound to the original immutable turn and item.</summary>
/// <param name="Sequence">Ordered position in the original native turn history.</param>
/// <param name="TurnId">Original turn that owns the item.</param>
/// <param name="ItemSha256">Canonical JSON digest of the exact original item.</param>
public sealed record CodexStoredMailOccurrence(
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] Guid TurnId,
    [property: JsonRequired] string ItemSha256);

/// <summary>Sampling proof attached to a successful normal inference dispatch, not a compactor.</summary>
/// <param name="ResponseId">Provider response identity of the validated terminal.</param>
/// <param name="Occurrences">Exact previously unproven mail items included in that request.</param>
public sealed record CodexStoredMailSampling(
    [property: JsonRequired] string ResponseId,
    [property: JsonRequired] CodexStoredMailOccurrence[] Occurrences);

// Keep automatic occurrence identities in a separate namespace from imported Rust
// history_sequence values. Sparse proofs must not acknowledge a skipped older mail item.
internal static class CodexNativeMailSampling
{
    internal const string SequenceKey = "native_mail_sequence";
    internal const string SampledKey = "native_mail_sampled";
    private const int MaximumOccurrences = 4096;

    internal static bool IsMail(JsonElement item) =>
        CodexRealtimeProtocol.Text(item, "type") == "agent_message";

    internal static long Sequence(int turnIndex, bool output, int itemIndex) =>
        ((long)(turnIndex + 1) << 32) | (output ? 1L << 31 : 0) | (uint)(itemIndex + 1);

    internal static long? Sequence(JsonElement? metadata) =>
        metadata is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(SequenceKey, out var number) && number.ValueKind == JsonValueKind.Number &&
        number.TryGetInt64(out var sequence) && sequence > 0 ? sequence : null;

    internal static bool IsSampled(CodexRetainedContextEntry entry) =>
        Sequence(entry.Metadata) is not null && CodexRetainedJournalMetadata.Flag(entry.Metadata, SampledKey);

    internal static bool HasReservedKeys(JsonElement metadata) =>
        metadata.TryGetProperty(SequenceKey, out _) || metadata.TryGetProperty(SampledKey, out _);

    internal static bool TryFind(CodexThreadDocument document, long sequence,
        out Guid turnId, out JsonElement item)
    {
        turnId = default;
        item = default;
        if (sequence <= 0 || document.Turns is null) return false;
        var turnIndex = (sequence >> 32) - 1;
        var index = (sequence & 0x7fff_ffffL) - 1;
        if (turnIndex < 0 || turnIndex >= document.Turns.Length || index < 0 ||
            document.Turns[(int)turnIndex] is not { } turn) return false;
        var items = (sequence & (1L << 31)) != 0 ? turn.Output : turn.Input;
        if (items is null || index >= items.Length) return false;
        item = items[(int)index];
        turnId = turn.Id;
        return item.ValueKind == JsonValueKind.Object && IsMail(item);
    }

    internal static HashSet<long> Sampled(CodexThreadDocument document)
    {
        var result = new HashSet<long>();
        foreach (var turn in document.Turns ?? [])
            foreach (var dispatch in turn?.Dispatches ?? [])
                if (dispatch?.MailSampling is { } sampling && dispatch.State == "succeeded")
                    foreach (var occurrence in sampling.Occurrences)
                        result.Add(occurrence.Sequence);
        return result;
    }

    internal static void Validate(CodexThreadDocument document)
    {
        var known = new HashSet<long>();
        for (var index = 0; index < (document.Turns?.Length ?? 0); index++)
        {
            var turn = document.Turns![index];
            foreach (var receipt in turn?.Dispatches ?? [])
            {
                if (receipt?.MailSampling is not { } sampling) continue;
                if (receipt.Kind != "inference" || receipt.State != "succeeded" ||
                    receipt.Result is not { ValueKind: JsonValueKind.Object } result ||
                    string.IsNullOrWhiteSpace(sampling.ResponseId) || sampling.ResponseId.Length > 512 ||
                    CodexRealtimeProtocol.Text(result, "id") != sampling.ResponseId ||
                    sampling.Occurrences is not { Length: > 0 and <= MaximumOccurrences })
                    throw new InvalidDataException("Invalid native mail sampling receipt.");
                foreach (var occurrence in sampling.Occurrences)
                {
                    if (occurrence is null || !known.Add(occurrence.Sequence) ||
                        (occurrence.Sequence >> 32) - 1 > index ||
                        !TryFind(document, occurrence.Sequence, out var source, out var item) ||
                        source != occurrence.TurnId ||
                        occurrence.ItemSha256 != CodexRetainedJournalMetadata.ItemHash(item))
                        throw new InvalidDataException("Native mail sampling does not identify a unique original occurrence.");
                }
            }
        }
    }

    // Stored checkpoint sidecars describe the captured projection. Refresh their
    // sampling state from actual receipts on each subsequent metadata projection.
    internal static JsonElement? Annotate(JsonElement item, JsonElement? metadata,
        long? sequence, HashSet<long> sampled)
    {
        if (!IsMail(item)) return metadata;
        sequence ??= Sequence(metadata);
        if (sequence is not { } identity) return metadata;
        var node = metadata is { ValueKind: JsonValueKind.Object } existing
            ? JsonNode.Parse(existing.GetRawText())!.AsObject() : new JsonObject();
        node[SequenceKey] = identity;
        node.Remove(SampledKey);
        if (sampled.Contains(identity)) node[SampledKey] = true;
        return Element(node);
    }

    internal static bool Matches(JsonElement? projected, JsonElement? original, long sequence, HashSet<long> sampled)
    {
        if (projected is not { ValueKind: JsonValueKind.Object } value || !HasReservedKeys(value))
            return Equal(projected, original);
        if (Sequence(projected) != sequence ||
            CodexRetainedJournalMetadata.Flag(projected, SampledKey) && !sampled.Contains(sequence))
            return false;
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        node.Remove(SequenceKey);
        node.Remove(SampledKey);
        return Equal(node.Count == 0 && original is null ? null : Element(node), original);
    }

    private static bool Equal(JsonElement? left, JsonElement? right) =>
        left is { } item ? right is { } other && JsonElement.DeepEquals(item, other) : right is null;

    private static JsonElement Element(JsonNode value)
    {
        using var json = JsonDocument.Parse(value.ToJsonString());
        return json.RootElement.Clone();
    }

    internal static CodexStoredMailOccurrence[] Capture(CodexThreadDocument document,
        IReadOnlyList<JsonElement> history, IReadOnlyList<JsonElement?> metadata)
    {
        if (history.Count != metadata.Count) throw new ArgumentException("Native sampling metadata is not aligned.");
        var sampled = Sampled(document);
        var captured = new List<CodexStoredMailOccurrence>();
        for (var index = 0; index < history.Count && captured.Count < MaximumOccurrences; index++)
        {
            var item = history[index];
            if (!IsMail(item) || Sequence(metadata[index]) is not { } sequence || sampled.Contains(sequence)) continue;
            if (!TryFind(document, sequence, out var source, out var original) ||
                !JsonElement.DeepEquals(item, original))
                throw new InvalidDataException("Native mail projection does not match the sampled original occurrence.");
            sampled.Add(sequence);
            captured.Add(new(sequence, source, CodexRetainedJournalMetadata.ItemHash(original)));
        }
        return captured.ToArray();
    }

    internal static void WriteReceipt(Utf8JsonWriter writer, CodexStoredDispatch receipt)
    {
        if (receipt.MailSampling is not { } sampling) return;
        writer.WriteStartObject("mail_sampling");
        writer.WriteString("response", sampling.ResponseId);
        writer.WriteStartArray("occurrences");
        foreach (var occurrence in sampling.Occurrences)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", occurrence.Sequence);
            writer.WriteString("turn", occurrence.TurnId);
            writer.WriteString("item_hash", occurrence.ItemSha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

// Internal observer extension, not a new public accounting contract. Only the
// Responses transport can distinguish validated normal output from billed terminals.
internal interface ICodexValidatedResponseObserver
{
    Task CompleteValidatedAsync(string requestId, CodexResponseOptions settings, string responseId,
        JsonElement response, CancellationToken cancellationToken);
}

public sealed partial class CodexThreadStore
{
    // Publish the successful receipt and its sampling evidence in the SAME CAS.
    // No independently committed successful result can lose the associated proof.
    internal Task<CodexThreadDocument> SettleNativeMailSamplingAsync(Guid threadId, long revision,
        Guid turnId, string owner, string requestId, JsonElement response, CodexStoredMailSampling sampling,
        CancellationToken ct)
        => ChangeAsync(threadId, revision, document =>
        {
            var turn = Active(document, turnId, owner);
            var receipt = turn.Dispatches.SingleOrDefault(item => item.CallId == requestId);
            if (receipt is not { Kind: "inference", State: "dispatching" or "unknown", MailSampling: null })
                throw new InvalidOperationException("Native sampling requires an unsettled validated inference receipt.");
            var updated = turn with
            {
                Dispatches = turn.Dispatches.Select(item => item.CallId == requestId
                    ? item with { State = "succeeded", Result = response.Clone(), MailSampling = sampling } : item).ToArray(),
            };
            var next = Replace(document, updated);
            CodexNativeMailSampling.Validate(next);
            return PromoteRealtimeArtifacts(next, turn, updated);
        }, ct);
}
