namespace tryAGI.Codex;

/// <summary>Storage-side lifecycle operations for a trusted named-agent host. The host remains
/// responsible for model/tool authority and for fencing a genuinely lost execution owner.</summary>
public interface ICodexNamedAgentLifecycleStorage : ICodexNamedAgentSpawnStorage
{
    /// <summary>Durably authorize exactly one new wake claim, turn and owner.</summary>
    Task<bool> AuthorizeWakeAsync(Guid rootThreadId, Guid recipientThreadId, Guid claimId, Guid turnId,
        string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Inspect the exact prior grant without replaying its effects.</summary>
    Task<CodexAgentWakeGrantState?> ReadWakeAuthorizationAsync(Guid rootThreadId, Guid recipientThreadId,
        Guid claimId, Guid turnId, string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Fence an exact unadmitted grant after positive proof that its execution cannot proceed.</summary>
    Task<bool> FenceUnadmittedWakeAsync(Guid rootThreadId, Guid recipientThreadId, Guid claimId, Guid turnId,
        string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Atomically close all edges in the requested subtree, descendants first, at the
    /// exact graph revision. Reject any active turn, open realtime session, unsettled claim,
    /// grant or other execution ownership in that subtree. Retain history and queued mail.
    /// Never partially close the subtree. False means no effects; an exception may have committed.</summary>
    Task<bool> CompareCloseNamedAgentSubtreeAsync(string accountId, Guid rootThreadId, Guid childThreadId,
        string expectedGraphRevision, CancellationToken cancellationToken);
}

/// <summary>Status of an exact local wake invocation or its recorded turn, not the whole agent.</summary>
public enum CodexNamedAgentExecutionStatus
{
    /// <summary>Locally reserved, but no durable turn admission observed yet.</summary>
    PendingAdmission,
    /// <summary>The exact turn is durably in progress.</summary>
    Running,
    /// <summary>The turn completed in its journal.</summary>
    Completed,
    /// <summary>The turn was interrupted in its journal.</summary>
    Interrupted,
    /// <summary>The turn failed in its journal.</summary>
    Failed,
    /// <summary>No exact turn was recorded in the inspected journal.</summary>
    NotAdmitted,
    /// <summary>Invocation ended without a confirmed terminal outcome; inspect/reconcile, never replay.</summary>
    Unconfirmed,
}

/// <summary>Immutable exact-turn status. A nonresident running turn may belong to another
/// process or a lost owner; absence of a local runtime is not proof of owner loss.</summary>
/// <param name="ThreadId">Recipient thread identity.</param>
/// <param name="TurnId">Exact proposed or recorded turn.</param>
/// <param name="OwnerId">Observed execution owner, if known.</param>
/// <param name="Status">Observed status.</param>
/// <param name="IsLocal">Whether the invocation is currently registered with this runtime.</param>
/// <param name="ThreadRevision">Observed journal revision, if available.</param>
public sealed record CodexNamedAgentExecutionSnapshot(Guid ThreadId, Guid TurnId, string? OwnerId,
    CodexNamedAgentExecutionStatus Status, bool IsLocal, long? ThreadRevision = null);

/// <summary>Result of requesting interruption, retaining the exact pre-interruption observation.</summary>
/// <param name="Before">Observed status before dispatching interruption.</param>
/// <param name="Requested">True only if the exact local invocation was signalled.</param>
public sealed record CodexNamedAgentInterruptResult(CodexNamedAgentExecutionSnapshot Before, bool Requested);

/// <summary>Resolved and host-authorized parameters of one explicit idle mailbox wake.</summary>
/// <param name="ThreadId">Named recipient.</param>
/// <param name="ExpectedMailboxRevision">Observed recipient mailbox revision.</param>
/// <param name="ExpectedThreadRevision">Observed recipient journal revision.</param>
/// <param name="ClaimId">Stable proposed claim, retained by the caller.</param>
/// <param name="TurnId">Stable proposed turn, retained by the caller.</param>
/// <param name="OwnerId">Authorized execution owner.</param>
/// <param name="Options">Resolved provider identity, settings and tools.</param>
/// <param name="MaximumRequests">Maximum requests in the existing native runner.</param>
public sealed record CodexNamedAgentWakeRequest(Guid ThreadId, long ExpectedMailboxRevision, long ExpectedThreadRevision,
    Guid ClaimId, Guid TurnId, string OwnerId, CodexResponseOptions Options, int MaximumRequests = 8);
