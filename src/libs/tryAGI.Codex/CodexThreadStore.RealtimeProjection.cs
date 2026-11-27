using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    internal static JsonObject ProjectRealtimeFrame(CodexStoredRealtimeSession session, CodexStoredRealtimeFrame frame)
    {
        var transcript = new JsonObject { ["role"] = frame.Role, ["text"] = frame.Text };
        var item = session.History?.Mode == CodexRealtimeHistoryMode.LegacyFlush ||
            session.History?.Mode == CodexRealtimeHistoryMode.LegacyNoFlush
            ? new JsonObject { ["type"] = "transcriptSegment", ["role"] = frame.Role, ["text"] = frame.Text }
            : new JsonObject { ["type"] = "frame", ["channels"] = new JsonObject { ["voice_transcript"] = transcript } };
        item["id"] = frame.Id;
        item["realtimeSessionId"] = session.SessionId;
        item["observedAtMs"] = session.CanonicalLegacySegments && session.History?.Mode != CodexRealtimeHistoryMode.Canonical
            ? null : JsonValue.Create(frame.ObservedAtMs);
        return item;
    }

    // Filter lightweight metadata first, then materialize only the selected public items.
    internal sealed record RealtimeTimelineRow(long Revision, string Id, string SessionId, long? ObservedAtMs,
        Func<JsonObject> Build)
    {
        internal JsonObject Materialize()
        {
            var item = Build();
            item["id"] = Id;
            item["realtimeSessionId"] = SessionId;
            item["observedAtMs"] = ObservedAtMs;
            return item;
        }
    }

    internal sealed record RealtimeTimelineView(int PrefixCount, RealtimeTimelineRow[] Rows, JsonObject[] ActiveFrames)
    {
        internal int Count => checked(PrefixCount + Rows.Length);
    }

    internal static IReadOnlyList<JsonObject> ProjectRealtimeItems(CodexThreadDocument thread, CodexThreadStore? journal = null)
        => ProjectRealtimeTimeline(thread, journal).Rows.Select(row => row.Materialize()).ToArray();

    // Commit order, not observation time, defines immutable global timeline positions.
    // The notification cursor skips committed payload construction through its last revision.
    internal static RealtimeTimelineView ProjectRealtimeTimeline(CodexThreadDocument thread,
        CodexThreadStore? journal = null, long? afterRevision = null, bool includeActiveFrames = false)
    {
        var result = new List<RealtimeTimelineRow>();
        var activeFrames = new List<JsonObject>();
        var prefixCount = 0;
        foreach (var session in thread.RealtimeSessions ?? [])
        {
            bool Include(long? revision)
            {
                if (afterRevision is { } through && (revision ?? 0) <= through)
                {
                    prefixCount++;
                    return false;
                }
                return true;
            }
            void Add(Func<JsonObject> build, string id, long? observedAt, long? revision, string? attributedSession = null)
                => result.Add(new(revision ?? 0, id, attributedSession ?? session.SessionId, observedAt, build));
            static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            // These stable local boundary identities represent the persisted lifecycle;
            // timestamps that were not retained by the journal are intentionally null.
            if (Include(session.StartedRevision))
                Add(() => new JsonObject { ["type"] = "realtimeSessionStarted" }, "session:" + session.SessionId + ":start", null, session.StartedRevision);
            if (session.History is { } history)
            {
                var admissions = (session.HandoffAdmissions ?? []).ToDictionary(x => x.HandoffId, StringComparer.Ordinal);
                // Only the open session can have unpublished active speech. No global
                // set of all historical item IDs or materialized JSON is needed.
                var published = includeActiveFrames && session.State != "ended"
                    ? new HashSet<string>(StringComparer.Ordinal) : null;
                void Replay(Action<CodexStoredRealtimeFrame, CodexStoredRealtimeHistoryEvent> frame,
                    Action<CodexStoredRealtimeCarryover, CodexStoredRealtimeHistoryEvent> saved,
                    Action<CodexStoredRealtimeHandoffCapture, CodexStoredRealtimeHistoryEvent> accepted,
                    Action<CodexStoredRealtimePromotion, CodexStoredRealtimeHistoryEvent> promoted)
                {
                    _ = journal is null
                        ? ReplayRealtimeHistory(session, history.Mode, history.Events, frame, saved, accepted, promoted)
                        : journal.ReplayRealtimeHistoryIncremental(session, history.Mode, history.Events, frame, saved, accepted, promoted);
                }
                Replay(
                    (frame, boundary) =>
                    {
                        published?.Add(frame.Id);
                        if (Include(boundary.CommitRevision))
                            Add(() => ProjectRealtimeFrame(session, frame), frame.Id,
                                session.CanonicalLegacySegments && history.Mode != CodexRealtimeHistoryMode.Canonical
                                    ? null : frame.ObservedAtMs, boundary.CommitRevision);
                    },
                    (saved, boundary) =>
                    {
                        if (Include(boundary.CommitRevision))
                            Add(() => new JsonObject
                            {
                                ["type"] = "sessionInputSaved", ["input"] = CodexRealtimeProtocol.Text(saved.Input, "content"),
                                ["input_item_id"] = saved.Id, ["source_event_ids"] = Ids(saved.SourceFrameIds),
                                ["reason"] = saved.Reason == "sessionEnd" ? "sessionEnd" : "chunk",
                            }, boundary.Id, boundary.ObservedAtMs, boundary.CommitRevision);
                    },
                    (capture, boundary) =>
                    {
                        if (!admissions.TryGetValue(capture.HandoffId, out var receipt) || receipt.TurnId is not { } turnId ||
                            receipt.InputIndex is not { } index) return;
                        if (!Include(boundary.CommitRevision)) return;
                        Add(() =>
                        {
                            var turn = thread.Turns.Single(t => t.Id == turnId);
                            var prepared = PrepareRealtimeHandoffInput(session, capture.HandoffId);
                            return new JsonObject
                            {
                                ["type"] = "turnInputAccepted", ["handoff_id"] = capture.HandoffId,
                                ["input"] = CodexRealtimeProtocol.Text(turn.Input[index], "content"),
                                ["input_item_id"] = receipt.SubmissionId.ToString(), ["mode"] = receipt.State,
                                ["turn_id"] = turnId.ToString(), ["source_event_ids"] = Ids(prepared?.SourceIds ?? []),
                            };
                        }, boundary.Id, boundary.ObservedAtMs, boundary.CommitRevision);
                    },
                    (promotion, boundary) =>
                    {
                        if (Include(boundary.CommitRevision))
                            Add(() => CodexRealtimePresentation.Json(promotion), boundary.Id,
                                boundary.ObservedAtMs, boundary.CommitRevision, promotion.SessionId);
                    });
                if (published is not null)
                    foreach (var frame in history.Frames)
                        if (!published.Contains(frame.Id)) activeFrames.Add(ProjectRealtimeFrame(session, frame));
            }
            if (session.State == "ended" && Include(session.ClosedRevision))
                Add(() => new JsonObject { ["type"] = "realtimeSessionClosed", ["outcome"] = "ended" },
                    "session:" + session.SessionId + ":end", null, session.ClosedRevision);
        }
        // Legacy unversioned items keep their existing relative positions. Subsequent
        // writes carry the exact CAS revision, including late artifacts of earlier sessions.
        return new(prefixCount, result.OrderBy(x => x.Revision).ToArray(), activeFrames.ToArray());
    }
}
