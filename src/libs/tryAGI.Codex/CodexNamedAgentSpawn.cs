using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Stable provenance of a named child's initial creation and accepted follow-up.
/// These are historical facts, not live execution authority.</summary>
/// <param name="ParentTurnId">Spawning turn.</param>
/// <param name="RootTurnId">Root reasoning turn inherited from the spawning turn.</param>
/// <param name="ParentOwnerId">Owner of the spawning turn at admission.</param>
/// <param name="SourceDispatchId">Admitted parent tool dispatch.</param>
/// <param name="SourceDispatchSha256">Exact digest of that dispatch request.</param>
/// <param name="ParentRevision">Observed parent journal revision at creation.</param>
/// <param name="GraphRevision">Observed graph revision before creation.</param>
/// <param name="InitialMessageId">Stable accepted initial delivery identity.</param>
/// <param name="InitialMessageFingerprint">Exact fingerprint of the initial message.</param>
/// <param name="InitialSettingsSha256">Digest of the resolved initial child settings.</param>
public sealed record CodexAgentSpawnOrigin(Guid ParentTurnId, Guid RootTurnId, string ParentOwnerId,
    string SourceDispatchId, string SourceDispatchSha256, long ParentRevision, string GraphRevision,
    Guid InitialMessageId, string InitialMessageFingerprint, string InitialSettingsSha256)
{
    /// <summary>Resolved initial child model, when known.</summary>
    public string? InitialModel { get; init; }

    /// <summary>Resolved initial reasoning effort, when known.</summary>
    public string? InitialReasoningEffort { get; init; }

    internal bool IsValid => (InitialModel is null || ValidIdentity(InitialModel)) &&
        (InitialReasoningEffort is null || ValidIdentity(InitialReasoningEffort)) && ParentTurnId != Guid.Empty && RootTurnId != Guid.Empty && ParentRevision > 0 &&
        ValidIdentity(ParentOwnerId) && ValidIdentity(SourceDispatchId) &&
        GraphRevision is { Length: > 0 and <= 1024 } && !string.IsNullOrWhiteSpace(GraphRevision) &&
        InitialMessageId != Guid.Empty && Digest(SourceDispatchSha256) &&
        Digest(InitialMessageFingerprint) && Digest(InitialSettingsSha256);

    private static bool ValidIdentity(string? value) => value is { Length: > 0 and <= 512 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

/// <summary>Host-authorized fresh named child creation. The host resolves and authorizes
/// settings, role and permissions before constructing the request. This API does not grant
/// authority to model-supplied settings or start model execution.</summary>
/// <param name="RootThreadId">Agent tree root.</param>
/// <param name="ParentThreadId">Immediate spawning parent.</param>
/// <param name="ExpectedParentRevision">Exact observed parent journal revision.</param>
/// <param name="ExpectedGraphRevision">Exact observed graph and identity revision.</param>
/// <param name="ParentTurnId">Current spawning turn.</param>
/// <param name="ParentOwnerId">Authorized owner of that turn.</param>
/// <param name="SourceDispatchId">Already admitted spawn tool dispatch.</param>
/// <param name="ChildThreadId">Stable child ID, chosen before dispatching the storage operation.</param>
/// <param name="TaskName">Single Rust-compatible child name.</param>
/// <param name="EffectiveSettings">Host-resolved child settings, with no new permissions granted by this API.</param>
/// <param name="InitialMessage">Stable initial follow-up from the immediate parent.</param>
/// <param name="MaximumDepth">Host-approved maximum named spawn depth, with root at zero.</param>
/// <param name="Role">Optional resolved role name.</param>
/// <param name="Nickname">Optional resolved display nickname.</param>
public sealed record CodexNamedAgentSpawnRequest(Guid RootThreadId, Guid ParentThreadId, long ExpectedParentRevision,
    string ExpectedGraphRevision, Guid ParentTurnId, string ParentOwnerId, string SourceDispatchId,
    Guid ChildThreadId, string TaskName, JsonElement EffectiveSettings, CodexAgentMail InitialMessage,
    int MaximumDepth, string? Role = null, string? Nickname = null);

/// <summary>One immutable creation candidate for the existing host thread storage transaction.</summary>
/// <param name="AccountId">Authenticated account.</param>
/// <param name="Identity">New child's named identity and immutable spawn origin.</param>
/// <param name="Document">Exact initial thread journal, revision one.</param>
/// <param name="Mailbox">Initial accepted follow-up mailbox, revision one.</param>
/// <param name="MaximumDepth">Host-approved spawn-depth bound to recheck at commit.</param>
public sealed record CodexNamedAgentSpawnPlan(string AccountId, CodexNamedAgentIdentity Identity,
    JsonElement Document, CodexAgentMailbox Mailbox, int MaximumDepth);

/// <summary>Durable fresh-child acceptance on the existing host thread storage adapter.
/// A successful transaction must make the open graph edge, named identity, initial thread
/// and initial follow-up visible together, even after process restart. It is not a turn
/// execution or provider receipt.</summary>
public interface ICodexNamedAgentSpawnStorage : ICodexAgentMailboxExecutionStorage
{
    /// <summary>Atomically compare the parent journal and graph revisions in the spawn origin,
    /// verify parent/owner/dispatch and host authority, graph depth, active path uniqueness,
    /// permanent child-ID uniqueness and uniqueness of the parent spawn dispatch (including
    /// historical closed children). Reject existing/tombstoned children and foreign identities.
    /// Validate the candidate's immutable origin, journal, initial mail and account binding.
    /// Serialize with graph mutation, archive, deletion, fork and mailbox admission; no partial
    /// edge, metadata, journal or initial mail publication is permitted. False means no effects;
    /// true means all four are committed. Exceptions may follow commit, so inspect the exact
    /// child/dispatch identity instead of automatically retrying or creating another child.</summary>
    Task<bool> CompareCreateNamedAgentAsync(CodexNamedAgentSpawnPlan plan, CancellationToken cancellationToken);
}
