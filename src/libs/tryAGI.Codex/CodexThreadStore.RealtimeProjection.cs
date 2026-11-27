using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    // Replay in commit order rather than sorting current frames by their first observation.
    // A late sealed frame can have an earlier observation time but must never move a
    // previously returned position in an immutable snapshot.
    internal static IReadOnlyList<JsonObject> ProjectRealtimeItems(CodexThreadDocument thread)
    {
        var result = new List<JsonObject>();
        foreach (var session in thread.RealtimeSessions ?? [])
        {
            void Add(JsonObject value, string id, long? observedAt)
            {
                value["id"] = id;
                value["realtimeSessionId"] = session.SessionId;
                value["observedAtMs"] = observedAt;
                result.Add(value);
            }
            static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            // These stable local boundary identities represent the persisted lifecycle;
            // timestamps that were not retained by the journal are intentionally null.
            Add(new JsonObject { ["type"] = "realtimeSessionStarted" }, "session:" + session.SessionId + ":start", null);
            if (session.History is { } history)
            {
                var admissions = (session.HandoffAdmissions ?? []).ToDictionary(x => x.HandoffId, StringComparer.Ordinal);
                _ = ReplayRealtimeHistory(session, history.Mode, history.Events,
                    (frame, _) =>
                    {
                        var transcript = new JsonObject { ["role"] = frame.Role, ["text"] = frame.Text };
                        var item = history.Mode == CodexRealtimeHistoryMode.Canonical
                            ? new JsonObject { ["type"] = "frame", ["channels"] = new JsonObject { ["voice_transcript"] = transcript } }
                            : new JsonObject { ["type"] = "transcriptSegment", ["role"] = frame.Role, ["text"] = frame.Text };
                        Add(item, frame.Id, frame.ObservedAtMs);
                    },
                    (saved, boundary) => Add(new JsonObject
                    {
                        ["type"] = "sessionInputSaved", ["input"] = CodexRealtimeProtocol.Text(saved.Input, "content"),
                        ["input_item_id"] = saved.Id, ["source_event_ids"] = Ids(saved.SourceFrameIds),
                        ["reason"] = saved.Reason == "sessionEnd" ? "sessionEnd" : "chunk",
                    }, boundary.Id, boundary.ObservedAtMs),
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
                        }, boundary.Id, boundary.ObservedAtMs);
                    });
            }
            if (session.State == "ended")
                Add(new JsonObject { ["type"] = "realtimeSessionClosed", ["outcome"] = "ended" },
                    "session:" + session.SessionId + ":end", null);
        }
        return result;
    }
}
