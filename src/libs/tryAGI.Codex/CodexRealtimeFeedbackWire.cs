using System.Text.Json.Nodes;

namespace tryAGI.Codex;

internal enum CodexRealtimeFeedbackKind { Progress, Final, Complete, Item, Speech, SteerAck, Status }

// Pinned Rust v0.172.0: realtime_conversation.rs and realtime_websocket/methods_common.rs.
// V2 progress is a user-role backend item, not a function receipt or a response request.
internal static class CodexRealtimeFeedbackWire
{
    internal const string CompleteAck = "Background agent finished. Use the preceding [BACKEND] messages as the result.";
    internal const string SteerAck = "This was sent to steer the previous background agent task.";
    private const string FinalPrefix = "\"Agent Final Message\":\n\n";

    internal static (IEnumerable<JsonObject> Messages, bool CreateResponse, bool FunctionReceipt) Build(
        string protocol, CodexRealtimeFeedbackKind kind, string text, string? id, string? channel, string? prefix = null)
    {
        if (protocol is not ("v1" or "v2" or "v3")) throw new ArgumentException("Unknown realtime protocol.", nameof(protocol));
        if (kind == CodexRealtimeFeedbackKind.Status && protocol != "v3" ||
            kind is CodexRealtimeFeedbackKind.Complete or CodexRealtimeFeedbackKind.SteerAck && protocol != "v2")
            return ([], false, false);
        if (kind is CodexRealtimeFeedbackKind.Complete or CodexRealtimeFeedbackKind.SteerAck)
            return ([CodexRealtimeGaProtocol.FunctionOutput(id ?? throw new ArgumentNullException(nameof(id)),
                kind == CodexRealtimeFeedbackKind.SteerAck ? SteerAck : text.Length == 0 ? "" : CompleteAck)],
                kind == CodexRealtimeFeedbackKind.SteerAck || text.Length != 0, true);
        if (protocol == "v2" && !text.StartsWith("[BACKEND] ", StringComparison.Ordinal) && text.Length != 0)
            text = "[BACKEND] " + text;
        if (kind == CodexRealtimeFeedbackKind.Item && !string.IsNullOrEmpty(prefix))
            text = prefix + "\n\n" + text;
        if (kind != CodexRealtimeFeedbackKind.Status && protocol != "v3" || kind is CodexRealtimeFeedbackKind.Item or CodexRealtimeFeedbackKind.Speech)
            text = CodexRealtimeStartupContext.Truncate(text, 1000);
        if (protocol == "v3")
            return (CodexRealtimeProtocol.Context(text, kind is CodexRealtimeFeedbackKind.Speech or CodexRealtimeFeedbackKind.Item ? null : id,
                kind == CodexRealtimeFeedbackKind.Speech ? "speakable" : kind == CodexRealtimeFeedbackKind.Status ? "commentary" : channel), false, false);
        if (protocol == "v2")
            return ([CodexRealtimeGaProtocol.Message(kind == CodexRealtimeFeedbackKind.Item ? "developer" : "user", text)],
                kind == CodexRealtimeFeedbackKind.Speech || id is null && kind is CodexRealtimeFeedbackKind.Progress or CodexRealtimeFeedbackKind.Final, false);
        if (kind == CodexRealtimeFeedbackKind.Item)
            return ([CodexRealtimeGaProtocol.Message("developer", text, preserveDeveloperRole: true)], false, false);
        if (kind == CodexRealtimeFeedbackKind.Final) text = FinalPrefix + text;
        return ([new JsonObject { ["type"] = "conversation.handoff.append", ["handoff_id"] = id ?? "codex", ["output_text"] = text }], false, false);
    }
}
