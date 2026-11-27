using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Host-authorized backing reasoning mode instructions. Null selects the pinned Rust default;
/// an explicitly empty string remains an empty contextual fragment.</summary>
public sealed record CodexRealtimeModeInstructions(string? Start = null, string? End = null);

/// <summary>Durable identity of an injected realtime mode fragment in a turn's output timeline.</summary>
public sealed record CodexStoredRealtimeModeTransition(int OutputIndex, bool Active, string SessionId,
    string MessageSha256, string? EndInstructions);

internal sealed record CodexRealtimeModeCapture(CodexStoredRealtimeModeTransition Transition, JsonElement Message);

// Pinned Rust v0.172.0: context/world_state/realtime.rs and prompts/templates/realtime.
// The model-visible state changes only at a sampling boundary, not at every socket event.
internal static class CodexRealtimeModeContext
{
    internal const string StartDefault = """
        Realtime conversation started.

        You are operating as a backend executor behind an intermediary. The user does not talk to you directly. Any response you produce will be consumed by the intermediary and may be summarized before the user sees it.

        When invoked, you receive the latest conversation transcript and any relevant mode or metadata. The intermediary may invoke you even when backend help is not actually needed. Use the transcript to decide whether you should do work. If backend help is unnecessary, avoid verbose responses that add user-visible latency.

        When user text is routed from realtime, treat it as a transcript. It may be unpunctuated or contain recognition errors.

        - Keep responses concise and action-oriented. Your updates should help the intermediary respond to the user.
        """;

    internal const string EndDefault = """
        Realtime conversation ended.

        Subsequent user input will return to typed text rather than transcript-style text. Do not assume recognition errors or missing punctuation once realtime has ended. Resume normal chat behavior.
        """;

    internal static void Validate(CodexRealtimeModeInstructions? instructions)
    {
        CodexRealtimeSessionOptions.ValidateModeInstructions(instructions?.Start);
        CodexRealtimeSessionOptions.ValidateModeInstructions(instructions?.End);
    }

    internal static CodexRealtimeModeCapture? Capture(CodexThreadDocument document,
        IReadOnlyList<JsonElement> retained, int seedCount)
    {
        CodexStoredRealtimeModeTransition? previous = null;
        foreach (var turn in document.Turns)
            foreach (var transition in turn.RealtimeModeTransitions ?? [])
                if (retained.Any(item => JsonElement.DeepEquals(item, turn.Output[transition.OutputIndex])))
                    previous = transition;

        // A detached transport does not end its logical call. An uncertain lifecycle is not
        // proof of an end, and cannot introduce a new mode transition before reconciliation.
        var session = document.RealtimeSessions?.LastOrDefault(s => s.UsedAttachmentIds is { Length: > 0 });
        if (session?.State is "recoveryRequired" or "ending") return null;
        var active = session?.State is "attached" or "detached";
        if (previous?.Active == active || previous is null && !active) return null;
        var sessionId = session?.SessionId ?? previous?.SessionId
            ?? throw new InvalidOperationException("Realtime mode transition has no session identity.");
        var end = session is not null ? session.ModeInstructions?.End : previous?.EndInstructions;
        var instructions = active ? session?.ModeInstructions?.Start ?? StartDefault : end ?? EndDefault;
        using var parsed = JsonDocument.Parse(new JsonObject
        {
            ["role"] = "developer",
            ["content"] = "<realtime_conversation>\n" + instructions + "\n</realtime_conversation>",
        }.ToJsonString());
        var message = parsed.RootElement.Clone();
        return new(new(retained.Count - seedCount, active, sessionId, Digest(message), end), message);
    }

    internal static string Digest(JsonElement message)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message.GetRawText())));

    internal static bool IsModeFragment(JsonElement message)
        => CodexRealtimeProtocol.Text(message, "role") == "developer" &&
            CodexRealtimeProtocol.Text(message, "content") is { } text &&
            text.StartsWith("<realtime_conversation>\n", StringComparison.Ordinal) &&
            text.EndsWith("\n</realtime_conversation>", StringComparison.Ordinal);
}

public sealed partial class CodexThreadStore
{
    /// <summary>Configure the backing model's start/end instructions before the first attachment.
    /// The instructions remain immutable for the logical call, including reconnects.</summary>
    public Task<CodexThreadDocument> ConfigureRealtimeModeInstructionsAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, CodexRealtimeModeInstructions instructions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        CodexRealtimeModeContext.Validate(instructions);
        return ChangeRealtimeAsync(threadId, expectedRevision, sessionId, ownerId, session =>
        {
            if (session.State != "detached" || session.UsedAttachmentIds is { Length: > 0 } ||
                session.ModeInstructions is not null && session.ModeInstructions != instructions)
                throw new InvalidOperationException("Realtime mode instructions cannot change after logical admission.");
            return session with { ModeInstructions = instructions };
        }, cancellationToken);
    }

    internal static void ValidateRealtimeModeTransitions(CodexStoredTurn turn)
    {
        var prior = -1;
        foreach (var transition in turn.RealtimeModeTransitions ?? [])
        {
            if (transition is null || transition.OutputIndex <= prior || transition.OutputIndex >= turn.Output.Length ||
                !ValidToken(transition.SessionId) ||
                !CodexRealtimeModeContext.IsModeFragment(turn.Output[transition.OutputIndex]) ||
                CodexRealtimeModeContext.Digest(turn.Output[transition.OutputIndex]) != transition.MessageSha256 ||
                transition.EndInstructions is { } end && CodexRealtimeStartupContext.ApproximateTokens(end) > 8192)
                throw new InvalidDataException("Invalid realtime mode transition history.");
            prior = transition.OutputIndex;
        }
    }
}
