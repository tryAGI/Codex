namespace tryAGI.Codex;

/// <summary>One atomic realtime observation write, with per-input admission results.</summary>
public sealed record CodexRealtimeObservationBatchAdmission(CodexThreadDocument Thread, bool[] Admitted);

public sealed partial class CodexThreadStore
{
    private sealed record RealtimeHistoryEmission(CodexStoredRealtimeHistoryEvent Boundary,
        CodexStoredRealtimeFrame? Frame = null, CodexStoredRealtimeCarryover? Saved = null,
        CodexStoredRealtimeHandoffCapture? Accepted = null, CodexStoredRealtimePromotion? Promotion = null);

    private sealed class RealtimeHistoryReduction(Action<CodexStoredRealtimeHistoryEvent> apply,
        Func<CodexStoredRealtimeHistoryEvent[], CodexStoredRealtimeHistory> snapshot,
        List<RealtimeHistoryEmission> emissions,
        Func<CodexRealtimeReducerState> capture)
    {
        private readonly List<CodexStoredRealtimeHistoryEvent> events = [];
        private readonly List<(CodexStoredRealtimeHistoryEvent Header, string? Payload)> exactPrefix = [];
        internal int Count => events.Count;
        internal long EstimatedBytes { get; private set; }

        internal void Append(CodexStoredRealtimeHistoryEvent item)
        {
            if (item is null) throw new InvalidDataException("Missing realtime observation.");
            var owned = item with { Payload = item.Payload?.Clone() };
            apply(owned);
            events.Add(owned);
            var raw = owned.Payload?.GetRawText();
            exactPrefix.Add((owned with { Payload = null }, raw));
            EstimatedBytes += 512L + 4L * (owned.Id.Length + (raw?.Length ?? 0) +
                (owned.Promotion?.Preview?.Text?.Length ?? 0));
        }

        internal bool IsExactPrefix(CodexStoredRealtimeHistoryEvent[] target)
        {
            if (events.Count > target.Length) return false;
            for (var i = 0; i < events.Count; i++)
            {
                var right = target[i];
                if (right is null) return false;
                if (ReferenceEquals(events[i], right)) continue;
                var (header, raw) = exactPrefix[i];
                // Raw bytes matter: the accumulator's replay receipts hash the raw payload.
                // Cache the immutable comparison side rather than reallocating both sides.
                if (header != (right with { Payload = null }) || raw != right.Payload?.GetRawText()) return false;
            }
            return true;
        }

        internal CodexStoredRealtimeHistory Snapshot() => snapshot(events.ToArray());
        internal CodexRealtimeReducerState CaptureCheckpoint() => capture();

        internal void Emit(Action<CodexStoredRealtimeFrame, CodexStoredRealtimeHistoryEvent>? frame,
            Action<CodexStoredRealtimeCarryover, CodexStoredRealtimeHistoryEvent>? saved,
            Action<CodexStoredRealtimeHandoffCapture, CodexStoredRealtimeHistoryEvent>? accepted,
            Action<CodexStoredRealtimePromotion, CodexStoredRealtimeHistoryEvent>? promoted)
        {
            if (frame is null && saved is null && accepted is null && promoted is null) return;
            foreach (var emission in emissions)
            {
                if (emission.Frame is { } f) frame?.Invoke(f, emission.Boundary);
                if (emission.Saved is { } s) saved?.Invoke(s, emission.Boundary);
                if (emission.Accepted is { } a) accepted?.Invoke(a, emission.Boundary);
                if (emission.Promotion is { } p) promoted?.Invoke(p, emission.Boundary);
            }
        }
    }

    private sealed record RealtimeReductionCacheEntry(string SessionId, string OwnerId, string? CallId,
        string Protocol, bool CanonicalLegacySegments, CodexRealtimeHistoryMode Mode, Guid[] Attachments,
        RealtimeHistoryReduction Reduction)
    {
        internal bool Matches(CodexStoredRealtimeSession session, CodexRealtimeHistoryMode mode)
            => SessionId == session.SessionId && OwnerId == session.OwnerId && CallId == session.CallId &&
                Protocol == session.Protocol && CanonicalLegacySegments == session.CanonicalLegacySegments &&
                Mode == mode && Attachments.SequenceEqual(session.UsedAttachmentIds ?? []);
    }

    // Small account/store-scoped warm cache, not a new durable authority. Cold reads and
    // changed prefixes replay normally. A failed speculative append evicts its reducer;
    // a CAS conflict can never make an uncommitted event part of an authoritative read.
    private readonly Lock realtimeReductionGate = new();
    private readonly List<RealtimeReductionCacheEntry> realtimeReductions = [];
    private long realtimeEventsReduced;
    internal long RealtimeEventsReduced => Interlocked.Read(ref realtimeEventsReduced);
    private const int MaximumCachedReductions = 2;
    private const long MaximumCachedReductionBytes = 4 * 1024 * 1024;

    private static void CheckRealtimeReplayInput(CodexRealtimeHistoryMode mode, CodexStoredRealtimeHistoryEvent[] events)
    {
        if (!Enum.IsDefined(mode) || events is null || events.Length > MaximumItems)
            throw new InvalidDataException("Invalid realtime history enrollment or event count.");
    }

    private CodexStoredRealtimeHistory ReplayRealtimeHistoryIncremental(CodexStoredRealtimeSession session,
        CodexRealtimeHistoryMode mode, CodexStoredRealtimeHistoryEvent[] events,
        Action<CodexStoredRealtimeFrame, CodexStoredRealtimeHistoryEvent>? frameCommitted = null,
        Action<CodexStoredRealtimeCarryover, CodexStoredRealtimeHistoryEvent>? inputSaved = null,
        Action<CodexStoredRealtimeHandoffCapture, CodexStoredRealtimeHistoryEvent>? inputAccepted = null,
        Action<CodexStoredRealtimePromotion, CodexStoredRealtimeHistoryEvent>? promoted = null)
    {
        CheckRealtimeReplayInput(mode, events);
        lock (realtimeReductionGate)
        {
            // An observer can read an older committed revision while the writer has
            // already reduced a newer one. Do not evict the newer exact prefix merely
            // because it is too long for this historical read.
            var entry = realtimeReductions.Where(e => e.Matches(session, mode) &&
                    e.Reduction.Count <= events.Length)
                .OrderByDescending(e => e.Reduction.Count)
                .FirstOrDefault(e => e.Reduction.IsExactPrefix(events));
            if (entry is not null) realtimeReductions.Remove(entry);
            var reducer = entry?.Reduction ?? CreateRealtimeHistoryReduction(session, mode);
            for (var i = reducer.Count; i < events.Length; i++)
            {
                reducer.Append(events[i]);
                Interlocked.Increment(ref realtimeEventsReduced);
            }
            var result = reducer.Snapshot();
            reducer.Emit(frameCommitted, inputSaved, inputAccepted, promoted);
            if (reducer.EstimatedBytes <= MaximumCachedReductionBytes)
            {
                realtimeReductions.Insert(0, new(session.SessionId, session.OwnerId, session.CallId,
                    session.Protocol, session.CanonicalLegacySegments, mode, (session.UsedAttachmentIds ?? []).ToArray(), reducer));
                if (realtimeReductions.Count > MaximumCachedReductions) realtimeReductions.RemoveAt(MaximumCachedReductions);
            }
            return result;
        }
    }
}
