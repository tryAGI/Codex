using System.Text.Json;

namespace tryAGI.Codex;

// A derived reducer cache, not the authoritative observation journal. Preserve raw JSON
// text explicitly: normal JSON reserialization may change the bytes used by receipts.
internal sealed record CodexRealtimeAccumulatorFragmentState(string? ObservationId, string? EventId,
    string? InputFragmentId, double? InputStartMs, double? InputEndMs, double ReceivedAfterConnectMs,
    string Text, bool Final, string RawPayload);

internal sealed record CodexRealtimeAccumulatorEntryState(string Id, string Role, string Text, bool Final,
    CodexRealtimeAccumulatorFragmentState[] Fragments);

internal sealed record CodexRealtimeAccumulatorHandoffState(string Id, string InputTranscript,
    CodexRealtimeAccumulatorEntryState[] Entries, string RawPayload);

internal sealed record CodexRealtimeAccumulatorState(CodexRealtimeAccumulatorEntryState[] Entries,
    Dictionary<string, string> Receipts, CodexRealtimeAccumulatorHandoffState[] Handoffs,
    string[] FlushedUserInputs, Dictionary<string, string> PresentationPrefixes,
    bool NewInput, bool NewOutput, string? LastFinalRole);

public sealed partial class CodexRealtimeTranscriptAccumulator
{
    internal CodexRealtimeAccumulatorState CaptureCheckpoint()
    {
        lock (gate)
            return new(entries.Select(e => CaptureEntry(e.Snapshot())).ToArray(),
                new(receipts, StringComparer.Ordinal), handoffs.Values.Select(h =>
                    new CodexRealtimeAccumulatorHandoffState(h.Id, h.InputTranscript,
                        h.Entries.Select(CaptureEntry).ToArray(), h.Payload.GetRawText())).ToArray(),
                flushedUserInputs.ToArray(), new(presentationPrefixes, StringComparer.Ordinal),
                newInput, newOutput, lastFinalRole);
    }

    // Only restore into a fresh reducer. The enclosing checkpoint binds this derived
    // state to the exact source prefix and reducer configuration; never use it for
    // admission, provider dispatch or validation of authoritative history.
    internal void RestoreCheckpoint(CodexRealtimeAccumulatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            if (entries.Count != 0 || receipts.Count != 0 || handoffs.Count != 0 ||
                state.Entries is null || state.Entries.Length > MaximumReceipts ||
                state.Receipts is null || state.Receipts.Count > MaximumReceipts ||
                state.Handoffs is null || state.Handoffs.Length > MaximumHandoffs ||
                state.FlushedUserInputs is null || state.FlushedUserInputs.Length > MaximumReceipts ||
                state.PresentationPrefixes is null || state.PresentationPrefixes.Count > 2 ||
                state.LastFinalRole is not (null or "user" or "assistant"))
                throw new InvalidDataException("Invalid realtime accumulator checkpoint.");
            var restoredEntries = state.Entries.Select(RestoreEntry).ToArray();
            var restoredHandoffs = state.Handoffs.Select(h =>
            {
                if (h is null || !CheckpointIdentity(h.Id) || h.InputTranscript is null || h.Entries is null ||
                    h.Entries.Length > MaximumReceipts)
                    throw new InvalidDataException("Invalid realtime handoff checkpoint.");
                return new CodexRealtimeHandoffSnapshot(h.Id, h.InputTranscript,
                    h.Entries.Select(RestoreEntry).ToArray(), ParseRaw(h.RawPayload), true);
            }).ToArray();
            var active = restoredEntries.Sum(e => (long)EntryBytes(e));
            var captured = restoredHandoffs.Sum(h => (long)Utf8(h.Payload.GetRawText()) + h.Entries.Sum(e => (long)EntryBytes(e)));
            if (restoredEntries.Any(e => e.Fragments.Length == 0) ||
                restoredEntries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != restoredEntries.Length ||
                restoredHandoffs.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() != restoredHandoffs.Length ||
                active > maximumActiveBytes || captured > MaximumHandoffBytes ||
                state.Receipts.Any(p => string.IsNullOrWhiteSpace(p.Key) || !CheckpointDigest(p.Value)) ||
                state.FlushedUserInputs.Any(h => !CheckpointDigest(h)) ||
                state.FlushedUserInputs.Distinct(StringComparer.Ordinal).Count() != state.FlushedUserInputs.Length ||
                state.PresentationPrefixes.Any(p => p.Key is not ("user" or "assistant") ||
                    p.Value is null || Utf8(p.Value) > maximumActiveBytes))
                throw new InvalidDataException("Realtime accumulator checkpoint exceeds its bound or has invalid receipts.");

            foreach (var entry in restoredEntries)
            {
                var restored = new Entry(entry.Id, entry.Role)
                {
                    Text = entry.Text, Final = entry.Final, Bytes = EntryBytes(entry),
                };
                restored.Fragments.AddRange(entry.Fragments);
                entries.Add(restored);
            }
            foreach (var p in state.Receipts) receipts.Add(p.Key, p.Value);
            foreach (var h in restoredHandoffs) handoffs.Add(h.Id, h);
            foreach (var h in state.FlushedUserInputs) flushedUserInputs.Add(h);
            foreach (var p in state.PresentationPrefixes) presentationPrefixes.Add(p.Key, p.Value);
            activeBytes = checked((int)active);
            handoffBytes = checked((int)captured);
            newInput = state.NewInput;
            newOutput = state.NewOutput;
            lastFinalRole = state.LastFinalRole;
        }
    }

    private static CodexRealtimeAccumulatorEntryState CaptureEntry(CodexRealtimeTranscriptEntry entry)
        => new(entry.Id, entry.Role, entry.Text, entry.Final, entry.Fragments.Select(f =>
            new CodexRealtimeAccumulatorFragmentState(f.ObservationId, f.EventId, f.InputFragmentId,
                f.InputStartMs, f.InputEndMs, f.ReceivedAfterConnectMs, f.Text, f.Final, f.Payload.GetRawText())).ToArray());

    private static CodexRealtimeTranscriptEntry RestoreEntry(CodexRealtimeAccumulatorEntryState state)
    {
        if (state is null || !CheckpointIdentity(state.Id) || state.Role is not ("user" or "assistant") ||
            state.Text is null || state.Fragments is null || state.Fragments.Length > MaximumReceipts)
            throw new InvalidDataException("Invalid realtime transcript entry checkpoint.");
        return new(state.Id, state.Role, state.Text, state.Final, state.Fragments.Select(f =>
        {
            if (f is null || f.Text is null || !double.IsFinite(f.ReceivedAfterConnectMs) || f.ReceivedAfterConnectMs < 0)
                throw new InvalidDataException("Invalid realtime transcript fragment checkpoint.");
            return new CodexRealtimeTranscriptFragment(f.ObservationId, f.EventId, f.InputFragmentId,
                f.InputStartMs, f.InputEndMs, f.ReceivedAfterConnectMs, f.Text, f.Final, ParseRaw(f.RawPayload));
        }).ToArray());
    }

    private static int EntryBytes(CodexRealtimeTranscriptEntry entry)
        => checked(Utf8(entry.Text) + 64 + (entry.Fragments.Length == 0 ? 0 :
            Utf8(entry.Role) + entry.Fragments.Sum(f => Utf8(f.Payload.GetRawText()))));

    private static JsonElement ParseRaw(string raw)
    {
        if (raw is null) throw new InvalidDataException("Missing realtime checkpoint payload.");
        using var parsed = JsonDocument.Parse(raw);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid realtime checkpoint payload.");
        return parsed.RootElement.Clone();
    }

    private static bool CheckpointIdentity(string? value)
        => value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool CheckpointDigest(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
