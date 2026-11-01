using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Caller-owned Responses correlation, not proof of provider billing or storage.
/// Mirrors pinned Rust flat client_metadata and HTTP compatibility headers.</summary>
public sealed record CodexResponsesIdentity(string SessionId, string ThreadId, string TurnId)
{
    public string? ParentThreadId { get; init; }
    public string? ParentTurnId { get; init; }
    public string? RootTurnId { get; init; }
    public string? Subagent { get; init; }

    internal void Validate()
    {
        foreach (var value in new[] { SessionId, ThreadId, TurnId }) ValidateValue(value);
        foreach (var value in new[] { ParentThreadId, ParentTurnId, RootTurnId, Subagent })
            if (value is not null) ValidateValue(value);
    }
    private static void ValidateValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("Responses identity must be bounded visible ASCII without whitespace.");
    }
    internal JsonObject ClientMetadata()
    {
        var result = new JsonObject { ["session_id"] = SessionId, ["thread_id"] = ThreadId, ["turn_id"] = TurnId };
        if (ParentThreadId is not null) result["x-codex-parent-thread-id"] = ParentThreadId;
        if (ParentTurnId is not null) result["parent_turn_id"] = ParentTurnId;
        if (RootTurnId is not null) result["root_turn_id"] = RootTurnId;
        if (Subagent is not null) result["x-openai-subagent"] = Subagent;
        return result;
    }
    internal void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("session-id", SessionId);
        request.Headers.Add("thread-id", ThreadId);
        // Rust uses thread identity here, not the SDK's per-request accounting ID.
        request.Headers.Add("x-client-request-id", ThreadId);
        if (ParentThreadId is not null) request.Headers.Add("x-codex-parent-thread-id", ParentThreadId);
        if (Subagent is not null) request.Headers.Add("x-openai-subagent", Subagent);
    }
}
