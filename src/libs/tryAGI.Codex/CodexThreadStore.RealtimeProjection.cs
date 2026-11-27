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

    // Replay in commit order rather than sorting current frames by their first observation.
    // A late sealed frame can have an earlier observation time but must never move a
    // previously returned position in an immutable snapshot.
    internal static IReadOnlyList<JsonObject> ProjectRealtimeItems(CodexThreadDocument thread, CodexThreadStore? journal = null)
    {
        var result = new List<(long Revision, JsonObject Item)>();
        foreach (var session in thread.RealtimeSessions ?? [])
        {
            void Add(JsonObject value, string id, long? observedAt, long? revision, string? attributedSession = null)
            {
                value["id"] = id;
                value["realtimeSessionId"] = attributedSession ?? session.SessionId;
                value["observedAtMs"] = observedAt;
                result.Add((revision ?? 0, value));
            }
            static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            // These stable local boundary identities represent the persisted lifecycle;
            // timestamps that were not retained by the journal are intentionally null.
            Add(new JsonObject { ["type"] = "realtimeSessionStarted" }, "session:" + session.SessionId + ":start", null, session.StartedRevision);
            if (session.History is { } history)
            {
                var admissions = (session.HandoffAdmissions ?? []).ToDictionary(x => x.HandoffId, StringComparer.Ordinal);
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
                        Add(ProjectRealtimeFrame(session, frame), frame.Id,
                            session.CanonicalLegacySegments && history.Mode != CodexRealtimeHistoryMode.Canonical
                                ? null : frame.ObservedAtMs, boundary.CommitRevision);
                    },
                    (saved, boundary) => Add(new JsonObject
                    {
                        ["type"] = "sessionInputSaved", ["input"] = CodexRealtimeProtocol.Text(saved.Input, "content"),
                        ["input_item_id"] = saved.Id, ["source_event_ids"] = Ids(saved.SourceFrameIds),
                        ["reason"] = saved.Reason == "sessionEnd" ? "sessionEnd" : "chunk",
                    }, boundary.Id, boundary.ObservedAtMs, boundary.CommitRevision),
                    (capture, boundary) =>
                    {
                        if (!admissions.TryGetValue(capture.HandoffId, out var receipt) || receipt.TurnId is not { } turnId ||
                            receipt.InputIndex is not { } index) return;
                        var turn = thread.Turns.Single(t => t.Id == turnId);
                        var prepared = PrepareRealtimeHandoffInput(session, capture.HandoffId);
                        Add(new JsonObject
                        {
                            ["type"] = "turnInputAccepted", ["handoff_id"] = capture.HandoffId,
                            ["input"] = CodexRealtimeProtocol.Text(turn.Input[index], "content"),
                            ["input_item_id"] = receipt.SubmissionId.ToString(), ["mode"] = receipt.State,
                            ["turn_id"] = turnId.ToString(), ["source_event_ids"] = Ids(prepared?.SourceIds ?? []),
                        }, boundary.Id, boundary.ObservedAtMs, boundary.CommitRevision);
                    },
                    (promotion, boundary) => Add(CodexRealtimePresentation.Json(promotion), boundary.Id,
                        boundary.ObservedAtMs, boundary.CommitRevision, promotion.SessionId));
            }
            if (session.State == "ended")
                Add(new JsonObject { ["type"] = "realtimeSessionClosed", ["outcome"] = "ended" },
                    "session:" + session.SessionId + ":end", null, session.ClosedRevision);
        }
        // Legacy unversioned items keep their existing relative positions. Subsequent
        // writes carry the exact CAS revision, including late artifacts of earlier sessions.
        return result.OrderBy(x => x.Revision).Select(x => x.Item).ToArray();
    }
}
