using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Caller-owned Responses correlation, not proof of provider billing or storage.
/// Mirrors pinned Rust flat client_metadata and HTTP compatibility headers.</summary>
/// <param name="SessionId">Caller session ID.</param>
/// <param name="ThreadId">Caller thread ID.</param>
/// <param name="TurnId">Caller turn ID.</param>
public sealed record CodexResponsesIdentity(string SessionId, string ThreadId, string TurnId)
{
    /// <summary>Thread that spawned this one, for subagents.</summary>
    public string? ParentThreadId { get; init; }
    /// <summary>Turn that spawned this one, for subagents.</summary>
    public string? ParentTurnId { get; init; }
    /// <summary>First turn of the chain.</summary>
    public string? RootTurnId { get; init; }
    /// <summary>Subagent name.</summary>
    public string? Subagent { get; init; }

    /// <summary>Logical voice session, if the backing turn was delegated by realtime. Local correlation only.</summary>
    public string? VoiceSessionId { get; init; }
    /// <summary>Client installation identity, when supplied by the host.</summary>
    public string? InstallationId { get; init; }
    /// <summary>Provider context window routing identity, when known.</summary>
    public string? WindowId { get; init; }
    /// <summary>Logical context window identity, when known.</summary>
    public string? ContextWindowId { get; init; }
    /// <summary>Explicit upstream routing hint. Never derived from an account or credential.</summary>
    public string? RoutingHint { get; init; }
    /// <summary>Explicit comma-separated upstream beta feature keys.</summary>
    public string? BetaFeatures { get; init; }

    internal void Validate()
    {
        foreach (var value in new[] { SessionId, ThreadId, TurnId }) ValidateValue(value);
        foreach (var value in new[] { ParentThreadId, ParentTurnId, RootTurnId, Subagent, VoiceSessionId, InstallationId, WindowId, ContextWindowId, RoutingHint, BetaFeatures })
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
        if (InstallationId is not null) result["x-codex-installation-id"] = InstallationId;
        if (WindowId is not null) result["x-codex-window-id"] = WindowId;
        if (TurnMetadata() is { } metadata) result["x-codex-turn-metadata"] = metadata;
        return result;
    }
    private string? TurnMetadata()
    {
        // Match the upstream compatibility blob without manufacturing absent metadata.
        if (InstallationId is null && WindowId is null && ContextWindowId is null) return null;
        var metadata = new JsonObject { ["session_id"] = SessionId, ["thread_id"] = ThreadId, ["turn_id"] = TurnId };
        if (InstallationId is not null) metadata["installation_id"] = InstallationId;
        if (WindowId is not null) metadata["window_id"] = WindowId;
        if (ContextWindowId is not null) metadata["context_window_id"] = ContextWindowId;
        if (ParentThreadId is not null) metadata["parent_thread_id"] = ParentThreadId;
        if (ParentTurnId is not null) metadata["parent_turn_id"] = ParentTurnId;
        if (RootTurnId is not null) metadata["root_turn_id"] = RootTurnId;
        return metadata.ToJsonString();
    }

    internal void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("session-id", SessionId);
        request.Headers.Add("thread-id", ThreadId);
        // Rust uses thread identity here, not the SDK's per-request accounting ID.
        request.Headers.Add("x-client-request-id", ThreadId);
        if (ParentThreadId is not null) request.Headers.Add("x-codex-parent-thread-id", ParentThreadId);
        if (Subagent is not null) request.Headers.Add("x-openai-subagent", Subagent);
        if (WindowId is not null) request.Headers.Add("x-codex-window-id", WindowId);
        if (TurnMetadata() is { } metadata) request.Headers.Add("x-codex-turn-metadata", metadata);
        if (RoutingHint is not null) request.Headers.Add("x-codex-routing-hint", RoutingHint);
        if (BetaFeatures is not null) request.Headers.Add("x-codex-beta-features", BetaFeatures);
    }
}
