using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Enrollment for realtime transcript frames and reasoning carryover.</summary>
public enum CodexRealtimeHistoryMode
{
    /// <summary>Canonical history always saves speech, even when legacy tail flushing is disabled.</summary>
    Canonical,
    /// <summary>Legacy transcript segments with opt-in reasoning carryover.</summary>
    LegacyFlush,
    /// <summary>Legacy transcript segments without reasoning carryover.</summary>
    LegacyNoFlush,
}

/// <summary>One durable observation or boundary in a logical realtime session.</summary>
public sealed record CodexStoredRealtimeHistoryEvent(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] long ObservedAtMs,
    [property: JsonRequired] int BeforeTurnCount,
    Guid? AttachmentId = null,
    string? EventType = null,
    JsonElement? Payload = null,
    double? ReceivedAfterConnectMs = null,
    string? Reason = null,
    string? HandoffId = null,
    Guid? TurnId = null);

/// <summary>A reconciled transcript frame, ordered by its first observation rather than final arrival.</summary>
public sealed record CodexStoredRealtimeFrame(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Role,
    [property: JsonRequired] string Text,
    [property: JsonRequired] bool Final,
    [property: JsonRequired] bool Sealed,
    [property: JsonRequired] long ObservedAtMs,
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] string[] SourceObservationIds,
    [property: JsonRequired] string Disposition,
    string? HandoffId = null);

/// <summary>Canonical model input saved without starting a reasoning turn. Source frames remain
/// available for display and are never fed into the reasoning history a second time.</summary>
public sealed record CodexStoredRealtimeCarryover(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] int BeforeTurnCount,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] string[] SourceFrameIds,
    [property: JsonRequired] JsonElement Input);

/// <summary>Captured handoff and the exact frames associated with its durable admission.</summary>
public sealed record CodexStoredRealtimeHandoffCapture(
    string HandoffId, string InputTranscript, string[] FrameIds, string State, Guid? TurnId = null);

/// <summary>Append-only history events plus their validated materialized frame projection.</summary>
public sealed record CodexStoredRealtimeHistory(
    [property: JsonRequired] CodexRealtimeHistoryMode Mode,
    [property: JsonRequired] CodexStoredRealtimeHistoryEvent[] Events,
    [property: JsonRequired] CodexStoredRealtimeFrame[] Frames,
    [property: JsonRequired] CodexStoredRealtimeHandoffCapture[] Handoffs,
    [property: JsonRequired] CodexStoredRealtimeCarryover[] Carryover);

public sealed partial class CodexThreadStore
{
    private static CodexStoredRealtimeHistory EmptyRealtimeHistory(CodexRealtimeHistoryMode mode = CodexRealtimeHistoryMode.Canonical)
        => new(mode, [], [], [], []);

    /// <summary>Choose legacy compatibility before observing speech. New sessions default to
    /// canonical frames; the legacy flush option cannot disable canonical carryover.</summary>
    public Task<CodexThreadDocument> ConfigureRealtimeHistoryAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, CodexRealtimeHistoryMode mode, CancellationToken cancellationToken = default)
        => ChangeRealtimeAsync(threadId, expectedRevision, sessionId, ownerId, session =>
        {
            if (!Enum.IsDefined(mode) || session.State != "detached" || session.History is { Events.Length: > 0 } ||
                session.UsedAttachmentIds is { Length: > 0 })
                throw new InvalidOperationException("History enrollment must be resolved before attachment.");
            return session with { History = EmptyRealtimeHistory(mode) };
        }, cancellationToken);

    /// <summary>Only transcript, handoff and transcript-boundary events belong in this journal.
    /// Audio bytes, unrelated control events and credentials are not retained here.</summary>
    public static bool IsRealtimeHistoryEvent(CodexRealtimeEvent source)
        => source.Transcript is not null || source.Delegation is not null ||
            source.Type is "input_audio_buffer.speech_started" or "response.created" or
            "conversation.input_transcript.delta" or "conversation.input_transcript.turn_marked" or
            "conversation.item.input_audio_transcription.delta" or "conversation.output_transcript.delta";

    /// <summary>CAS-commit an observation before delivering it to downstream observers. Reuse the
    /// same observation ID after a lost write receipt; duplicate IDs with changed payloads fail closed.
    /// A failed/uncertain write does not consume the event from caller-owned pending storage.</summary>
    public Task<CodexThreadAdmission> RecordRealtimeObservationAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, string observationId, CodexRealtimeEvent source,
        CancellationToken cancellationToken = default)
        => RecordRealtimeObservationCoreAsync(threadId, expectedRevision, sessionId, ownerId, attachmentId,
            observationId, source, false, cancellationToken);

    /// <summary>Reconcile an exact retained observation after the last transport has been fenced
    /// for recovery. Requires proof from the host that the original attachment is gone.</summary>
    public Task<CodexThreadAdmission> RecoverRealtimeObservationAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid lostAttachmentId, string observationId, CodexRealtimeEvent source,
        CancellationToken cancellationToken = default)
        => RecordRealtimeObservationCoreAsync(threadId, expectedRevision, sessionId, ownerId, lostAttachmentId,
            observationId, source, true, cancellationToken);

    private async Task<CodexThreadAdmission> RecordRealtimeObservationCoreAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, string observationId, CodexRealtimeEvent source,
        bool recovery, CancellationToken cancellationToken)
    {
        Token(observationId);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsRealtimeHistoryEvent(source) || source.Payload.ValueKind != JsonValueKind.Object ||
            CodexRealtimeProtocol.Text(source.Payload, "type") != source.Type ||
            !double.IsFinite(source.ReceivedAfterConnectMs) || source.ReceivedAfterConnectMs < 0)
            throw new ArgumentException("Invalid or unrelated realtime history observation.", nameof(source));
        var before = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var session = FindRealtimeSession(before, sessionId, ownerId);
        if (recovery)
        {
            Id(attachmentId);
            if (session.State != "recoveryRequired" || session.EndOperationId is not null ||
                session.UsedAttachmentIds?.LastOrDefault() != attachmentId)
                throw new InvalidOperationException("Recovered observation does not belong to the last fenced attachment.");
        }
        else RequireRealtimeAttachment(session, attachmentId);
        var history = session.History ?? EmptyRealtimeHistory();
        if (history.Events.FirstOrDefault(e => e.Id == observationId) is { } prior)
        {
            if (prior.Kind != "observation" || prior.EventType != source.Type ||
                prior.Payload is not { } payload || payload.GetRawText() != source.Payload.GetRawText())
                throw new InvalidOperationException("Realtime observation ID was reused for different content.");
            return new(false, before);
        }
        var item = new CodexStoredRealtimeHistoryEvent(observationId, "observation",
            NextRealtimeObservationTime(history), before.Turns.Length, attachmentId,
            source.Type, source.Payload.Clone(), source.ReceivedAfterConnectMs);
        var next = ReplaceRealtimeSession(before, session with
        {
            History = AppendRealtimeHistory(session, history, item),
        });
        return new(true, await PublishAsync(before.Revision, next with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Seal the pending tail before a new reasoning input or end. A lost receipt can be
    /// retried using the same seal identity, and pending handoff admission must settle first.</summary>
    public async Task<CodexThreadAdmission> SealRealtimeHistoryAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid? expectedAttachmentId, string sealId,
        string reason = "nextTurn", CancellationToken cancellationToken = default)
    {
        Token(sealId);
        if (reason is not ("nextTurn" or "chunk" or "sessionEnd"))
            throw new ArgumentException("Unsupported transcript seal reason.", nameof(reason));
        var before = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var session = FindRealtimeSession(before, sessionId, ownerId);
        if (session.State is not ("attached" or "detached") || session.AttachmentId != expectedAttachmentId)
            throw new InvalidOperationException("Realtime transcript seal does not own this lifecycle state.");
        var next = SealRealtimeHistory(before, session, sealId, reason);
        if (ReferenceEquals(before, next)) return new(false, before);
        return new(true, await PublishAsync(before.Revision, next with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Recover and validate the materialized transcript from the exact durable observation
    /// log. No transport, inference or external action is executed.</summary>
    public static CodexStoredRealtimeHistory RecoverRealtimeHistory(CodexStoredRealtimeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var stored = session.History ?? EmptyRealtimeHistory();
        var recovered = ReplayRealtimeHistory(session, stored.Mode, stored.Events);
        if (!SameRealtimeHistory(stored, recovered))
            throw new InvalidDataException("Realtime frame projection does not match its durable journal.");
        return recovered;
    }

    // Handoff admission/rejection must call this helper inside the same document CAS as the
    // admission receipt and reasoning input. A separate acceptance write could lose speech.
    internal CodexThreadDocument ResolveRealtimeHandoffHistory(CodexThreadDocument document,
        string sessionId, string handoffId, Guid? acceptedTurnId)
    {
        var session = document.RealtimeSessions?.SingleOrDefault(s => s.SessionId == sessionId)
            ?? throw new InvalidOperationException("Unknown realtime logical session.");
        var history = session.History;
        if (history is null || history.Events.Length == 0) return document;
        var capture = history.Handoffs.SingleOrDefault(h => h.HandoffId == handoffId)
            ?? throw new InvalidOperationException("Realtime handoff has no persisted observation.");
        var state = acceptedTurnId is null ? "rejected" : "accepted";
        if (capture.State != "pending")
        {
            if (capture.State == state && capture.TurnId == acceptedTurnId) return document;
            throw new InvalidOperationException("Realtime handoff outcome changed.");
        }
        if (acceptedTurnId is { } turnId && !document.Turns.Any(t => t.Id == turnId))
            throw new InvalidOperationException("Accepted handoff must bind to a durable reasoning turn.");
        var item = new CodexStoredRealtimeHistoryEvent("handoff:" + Guid.NewGuid().ToString("N"),
            state, NextRealtimeObservationTime(history), document.Turns.Length,
            HandoffId: handoffId, TurnId: acceptedTurnId);
        return ReplaceRealtimeSession(document, session with { History = AppendRealtimeHistory(session, history, item) });
    }

    // The transport has been drained or authoritatively fenced. Never dispatch an unadmitted
    // handoff from that generation. Its source frames remain pending for ordinary carryover.
    private CodexThreadDocument RetireLostRealtimeHandoffs(CodexThreadDocument document, CodexStoredRealtimeSession session)
    {
        if (session.History is not { } history) return document;
        var pending = history.Handoffs.Where(h => h.State == "pending").ToArray();
        if (pending.Length == 0) return document;
        if (pending.Length > MaximumItems - history.Events.Length)
            throw new InvalidOperationException("Realtime journal capacity exhausted before handoff retirement.");
        var observedAt = NextRealtimeObservationTime(history);
        var rejected = pending.Select(h => new CodexStoredRealtimeHistoryEvent(
            "handoff:" + Guid.NewGuid().ToString("N"), "rejected", observedAt, document.Turns.Length,
            Reason: "attachmentRetired", HandoffId: h.HandoffId));
        return ReplaceRealtimeSession(document, session with
        {
            History = ReplayRealtimeHistory(session, history.Mode, [.. history.Events, .. rejected]),
        });
    }

    internal CodexThreadDocument SealRealtimeBeforeTurn(CodexThreadDocument document, Guid turnId)
    {
        foreach (var session in document.RealtimeSessions ?? [])
        {
            if (session.State is "recoveryRequired" or "ending")
                throw new InvalidOperationException("Realtime history recovery required before starting a reasoning turn.");
            if (session.State is "attached" or "detached")
                document = SealRealtimeHistory(document, session, "turn:" + turnId.ToString("N"), "nextTurn");
        }
        return document;
    }

    private CodexThreadDocument SealRealtimeHistory(CodexThreadDocument document, CodexStoredRealtimeSession session,
        string sealId, string reason)
    {
        var history = session.History;
        if (history is null || history.Events.Length == 0) return document;
        if (history.Events.FirstOrDefault(e => e.Id == sealId) is { } prior)
        {
            if (prior.Kind != "seal" || prior.Reason != reason)
                throw new InvalidOperationException("Realtime seal ID was reused for a different operation.");
            return document;
        }
        if (history.Handoffs.Any(h => h.State == "pending"))
            throw new InvalidOperationException("Realtime handoff admission must settle before sealing carryover.");
        if (!history.Frames.Any(f => f.Disposition == "pending")) return document;
        var item = new CodexStoredRealtimeHistoryEvent(sealId, "seal", NextRealtimeObservationTime(history),
            document.Turns.Length, Reason: reason);
        return ReplaceRealtimeSession(document, session with { History = AppendRealtimeHistory(session, history, item) });
    }

    /// <summary>Get carryover saved at a position in the backing reasoning history. The index is the
    /// number of turns already present when the input was saved (zero means before the first turn).</summary>
    public static IReadOnlyList<JsonElement> RealtimeCarryoverBeforeTurn(CodexThreadDocument document, int precedingTurns)
        => AllRealtimeCarryover(document).Where(c => c.BeforeTurnCount == precedingTurns)
            .Select(c => c.Input.Clone()).ToArray();

    private static IEnumerable<CodexStoredRealtimeCarryover> AllRealtimeCarryover(CodexThreadDocument document)
        => (document.InheritedRealtimeCarryover ?? []).Concat((document.RealtimeSessions ?? [])
            .SelectMany(s => s.History?.Carryover ?? []));

    private static CodexStoredRealtimeCarryover[]? ForkRealtimeCarryover(CodexThreadDocument source, int count, bool full)
    {
        var history = AllRealtimeCarryover(source).Where(c => c.BeforeTurnCount < count || full && c.BeforeTurnCount == count).ToArray();
        return history.Length == 0 ? null : history;
    }

    private static void ValidateRealtimeHistoryProjection(CodexThreadDocument document)
    {
        foreach (var session in document.RealtimeSessions ?? [])
        {
            if (session.History is null) continue;
            var history = RecoverRealtimeHistory(session);
            if (history.Events.Any(e => e.BeforeTurnCount > document.Turns.Length) ||
                history.Handoffs.Any(h => h.TurnId is { } turn && !document.Turns.Any(t => t.Id == turn)) ||
                session.State is "ended" or "ending" && (history.Frames.Any(f => f.Disposition == "pending") ||
                    history.Handoffs.Any(h => h.State == "pending")))
                throw new InvalidDataException("Realtime history contradicts the backing reasoning or lifecycle state.");
        }
        if (document.InheritedRealtimeCarryover is { } inherited && (inherited.Length > MaximumItems ||
            inherited.Any(c => c is null || !ValidToken(c.Id) || !ValidToken(c.SessionId) ||
                c.BeforeTurnCount < 0 || c.BeforeTurnCount > document.Turns.Length || c.SourceFrameIds is null ||
                c.SourceFrameIds.Length == 0 || c.SourceFrameIds.Any(id => !ValidToken(id)) || c.Input.ValueKind != JsonValueKind.Object)))
            throw new InvalidDataException("Invalid inherited realtime carryover.");
    }

    private long NextRealtimeObservationTime(CodexStoredRealtimeHistory history)
        => Math.Max(time.GetUtcNow().ToUnixTimeMilliseconds(), history.Events.LastOrDefault()?.ObservedAtMs ?? 0);

    private static CodexStoredRealtimeHistory AppendRealtimeHistory(CodexStoredRealtimeSession session,
        CodexStoredRealtimeHistory history, CodexStoredRealtimeHistoryEvent item)
    {
        if (history.Events.Length >= MaximumItems || history.Events.Any(e => e.Id == item.Id))
            throw new InvalidOperationException("Realtime journal capacity or identity conflict.");
        return ReplayRealtimeHistory(session, history.Mode, [.. history.Events, item]);
    }

    private static CodexStoredRealtimeHistory ReplayRealtimeHistory(CodexStoredRealtimeSession session,
        CodexRealtimeHistoryMode mode, CodexStoredRealtimeHistoryEvent[] events,
        Action<CodexStoredRealtimeFrame, CodexStoredRealtimeHistoryEvent>? frameCommitted = null,
        Action<CodexStoredRealtimeCarryover, CodexStoredRealtimeHistoryEvent>? inputSaved = null,
        Action<CodexStoredRealtimeHandoffCapture, CodexStoredRealtimeHistoryEvent>? inputAccepted = null)
    {
        if (!Enum.IsDefined(mode) || events is null || events.Length > MaximumItems)
            throw new InvalidDataException("Invalid realtime history enrollment or event count.");
        var accumulator = new CodexRealtimeTranscriptAccumulator(session.Protocol);
        var frames = new Dictionary<string, CodexStoredRealtimeFrame>(StringComparer.Ordinal);
        var handoffs = new Dictionary<string, CodexStoredRealtimeHandoffCapture>(StringComparer.Ordinal);
        var carryover = new List<CodexStoredRealtimeCarryover>();
        var observed = new Dictionary<string, (long Sequence, long Timestamp)>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long previousTimestamp = 0;
        int previousTurnCount = 0;
        long sequence = 0;

        CodexStoredRealtimeFrame Frame(CodexRealtimeTranscriptEntry entry, bool sealedFrame,
            CodexStoredRealtimeHistoryEvent boundary, string? handoffId = null)
        {
            var sources = entry.Fragments.Select(f => f.ObservationId ??
                throw new InvalidDataException("Missing durable transcript observation identity.")).Distinct(StringComparer.Ordinal).ToArray();
            if (sources.Length == 0) sources = [boundary.Id];
            var first = sources.Select(id => observed.TryGetValue(id, out var position) ? position :
                throw new InvalidDataException("Transcript references an unknown observation.")).MinBy(p => p.Sequence);
            return new(entry.Id, entry.Role, entry.Text, entry.Final, sealedFrame, first.Timestamp,
                first.Sequence, sources, "pending", handoffId);
        }

        void AddFrames(IEnumerable<CodexRealtimeTranscriptEntry> entries, CodexStoredRealtimeHistoryEvent boundary,
            string? handoffId = null)
        {
            foreach (var entry in entries)
            {
                var frame = Frame(entry, true, boundary, handoffId);
                if (!frames.TryAdd(frame.Id, frame))
                    throw new InvalidDataException("Realtime transcript frame identity collided.");
                frameCommitted?.Invoke(frame, boundary);
            }
        }

        void Carryover(CodexStoredRealtimeHistoryEvent boundary, string reason)
        {
            if (handoffs.Values.Any(h => h.State == "pending")) return;
            var selected = frames.Values.Where(f => f.Disposition == "pending").OrderBy(f => f.Sequence).ToArray();
            if (selected.Length == 0) return;
            var save = mode != CodexRealtimeHistoryMode.LegacyNoFlush;
            if (save)
            {
                var saved = new CodexStoredRealtimeCarryover(boundary.Id, session.SessionId, boundary.BeforeTurnCount, reason,
                    selected.Select(f => f.Id).ToArray(), CarryoverInput(selected, reason, mode == CodexRealtimeHistoryMode.Canonical || session.Protocol == "v3"));
                carryover.Add(saved);
                inputSaved?.Invoke(saved, boundary);
            }
            foreach (var frame in selected) frames[frame.Id] = frame with { Disposition = save ? "carried" : "legacyOnly" };
        }

        foreach (var item in events)
        {
            sequence++;
            if (item is null || !ValidToken(item.Id) || !ids.Add(item.Id) || item.ObservedAtMs < previousTimestamp ||
                item.BeforeTurnCount < previousTurnCount)
                throw new InvalidDataException("Invalid realtime history event order or identity.");
            previousTimestamp = item.ObservedAtMs;
            previousTurnCount = item.BeforeTurnCount;
            switch (item.Kind)
            {
                case "observation":
                {
                    if (item.AttachmentId is not { } attachment || !(session.UsedAttachmentIds ?? []).Contains(attachment) ||
                        item.EventType is not { } type || item.Payload is not { ValueKind: JsonValueKind.Object } payload ||
                        CodexRealtimeProtocol.Text(payload, "type") != type ||
                        item.ReceivedAfterConnectMs is not { } elapsed || !double.IsFinite(elapsed) || elapsed < 0)
                        throw new InvalidDataException("Invalid persisted realtime observation.");
                    var source = new CodexRealtimeEvent(type, payload, elapsed);
                    if (!IsRealtimeHistoryEvent(source)) throw new InvalidDataException("Unexpected realtime history event type.");
                    observed.Add(item.Id, (sequence, item.ObservedAtMs));
                    var handoff = accumulator.Observe(source, item.Id);
                    if (handoff is { IsNew: true })
                    {
                        AddFrames(handoff.Entries, item, handoff.Id);
                        if (!handoffs.TryAdd(handoff.Id, new(handoff.Id, handoff.InputTranscript,
                            handoff.Entries.Select(e => e.Id).ToArray(), "pending")))
                            throw new InvalidDataException("Realtime handoff capture identity collided.");
                    }
                    var completed = accumulator.TakeCompleted();
                    if (completed.Length > 0)
                    {
                        AddFrames(completed, item);
                        Carryover(item, "chunk");
                    }
                    break;
                }
                case "seal":
                    if (item.Reason is not ("nextTurn" or "chunk" or "sessionEnd") || handoffs.Values.Any(h => h.State == "pending"))
                        throw new InvalidDataException("Realtime carryover seal preceded handoff settlement.");
                    AddFrames(accumulator.TakeTail(), item);
                    Carryover(item, item.Reason);
                    break;
                case "accepted":
                case "rejected":
                {
                    if (item.HandoffId is not { } handoffId || !handoffs.TryGetValue(handoffId, out var capture) ||
                        capture.State != "pending" || (item.Kind == "accepted") != (item.TurnId is not null))
                        throw new InvalidDataException("Invalid realtime handoff history settlement.");
                    handoffs[handoffId] = capture with { State = item.Kind, TurnId = item.TurnId };
                    if (item.Kind == "accepted")
                    {
                        foreach (var frameId in capture.FrameIds)
                            frames[frameId] = frames[frameId] with { Disposition = "accepted" };
                        inputAccepted?.Invoke(capture, item);
                    }
                    break;
                }
                default: throw new InvalidDataException("Unknown realtime history event kind.");
            }
        }
        if (events.LastOrDefault() is { } last)
            foreach (var entry in accumulator.Snapshot())
            {
                var frame = Frame(entry, entry.Final, last);
                if (!frames.TryAdd(frame.Id, frame))
                    throw new InvalidDataException("Active realtime transcript frame identity collided.");
            }
        return new(mode, events, frames.Values.OrderBy(f => f.Sequence).ToArray(), handoffs.Values.ToArray(), carryover.ToArray());
    }

    private const string RealtimeEndInstruction = "The user just ended their realtime session. Here is the remaining handoff/transcript tail. You probably do not have to do anything; acknowledge the handoff unless the transcript itself asks for something.";

    private static JsonElement CarryoverInput(CodexStoredRealtimeFrame[] frames, string reason, bool preserve)
    {
        static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        static string Bound(string escaped, bool tail)
        {
            var bytes = Encoding.UTF8.GetBytes(escaped);
            const int maximum = 4096;
            if (bytes.Length <= maximum) return escaped;
            const int retained = maximum - 3;
            if (tail)
            {
                var start = bytes.Length - retained;
                while ((bytes[start] & 0xC0) == 0x80) start++;
                return "…" + Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
            }
            var end = retained;
            while ((bytes[end] & 0xC0) == 0x80) end--;
            return Encoding.UTF8.GetString(bytes, 0, end) + "…";
        }
        var transcript = Escape(string.Join("\n", frames.Select(f => f.Role + ": " + f.Text)));
        var input = reason == "sessionEnd" ? Escape(RealtimeEndInstruction) : transcript;
        var source = reason == "sessionEnd" ? "  <source>transcript_tail_flush</source>\n" : "";
        var text = "<realtime_delegation>\n" + source + "  <input>" + Bound(input, false) +
            "</input>\n  <transcript_delta>" + (preserve ? transcript : Bound(transcript, true)) +
            "</transcript_delta>\n</realtime_delegation>";
        using var parsed = JsonDocument.Parse(new JsonObject { ["role"] = "user", ["content"] = text }.ToJsonString());
        return parsed.RootElement.Clone();
    }

    private static bool SameRealtimeHistory(CodexStoredRealtimeHistory first, CodexStoredRealtimeHistory second)
    {
        // Compare the source-generated JSON projection, including source IDs and exact saved input.
        return JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(first, CodexThreadJson.Default.CodexStoredRealtimeHistory),
            JsonSerializer.SerializeToElement(second, CodexThreadJson.Default.CodexStoredRealtimeHistory));
    }
}
