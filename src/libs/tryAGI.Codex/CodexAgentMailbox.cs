namespace tryAGI.Codex;

/// <summary>Delivery intent, independent of whether the recipient runtime is currently resident.</summary>
public enum CodexAgentMailMode
{
    /// <summary>Deliver without starting an idle turn.</summary>
    QueueOnly,
    /// <summary>Request a new task or steer an active agent.</summary>
    Followup,
    /// <summary>Queue a terminal result for the child's actual parent without waking it.</summary>
    Result,
}

/// <summary>One attributed, stable inter-agent delivery.</summary>
/// <param name="Id">Caller-retained delivery identity, chosen before any write.</param>
/// <param name="SenderThreadId">Authenticated sender within the recipient's agent tree.</param>
/// <param name="Mode">Delivery intent.</param>
/// <param name="Content">Plaintext or opaque encrypted message content.</param>
/// <param name="IsEncrypted">Whether content must be preserved without a plaintext wrapper.</param>
public sealed record CodexAgentMail(Guid Id, Guid SenderThreadId, CodexAgentMailMode Mode,
    string Content, bool IsEncrypted = false)
{
    /// <summary>Exact durable child terminal response, when this is a native bound result.</summary>
    public CodexAgentCompletedResult? CompletedResult { get; init; }
}

/// <summary>Deduplication receipt for consumed mail, without retaining its content.</summary>
/// <param name="Id">Original delivery identity.</param>
/// <param name="Fingerprint">Canonical SHA-256 of sender, intent, encryption flag and content.</param>
public sealed record CodexAgentMailReceipt(Guid Id, string Fingerprint);

/// <summary>A durable ordered-prefix reservation; it is not evidence of turn admission.</summary>
/// <param name="Id">Caller-retained reservation identity.</param>
/// <param name="OwnerId">Host-authorized execution owner.</param>
/// <param name="TurnId">Exact target turn identity, including a proposed wake turn.</param>
/// <param name="MessageCount">Number of pending messages reserved from the head.</param>
/// <param name="IsWake">Whether this reservation requests an idle follow-up turn.</param>
public sealed record CodexAgentMailClaim(Guid Id, string OwnerId, Guid TurnId, int MessageCount, bool IsWake)
{
    /// <summary>Exact model-input and graph binding for a native runner wake, if present.</summary>
    public CodexAgentMailWakeAdmission? WakeAdmission { get; init; }
}

/// <summary>Recipient-scoped durable mailbox. Its revision is independent of turn journal writes.</summary>
/// <param name="AccountId">Authenticated storage account.</param>
/// <param name="RootThreadId">Authoritative agent-tree root.</param>
/// <param name="RecipientThreadId">Recipient identity.</param>
/// <param name="Revision">Monotonic mailbox revision; zero denotes an empty mailbox.</param>
/// <param name="Pending">Unconsumed messages in acceptance order, including any reserved prefix.</param>
/// <param name="Receipts">Retained consumed-message identities and fingerprints.</param>
/// <param name="Claim">Current reservation, if any.</param>
public sealed record CodexAgentMailbox(string AccountId, Guid RootThreadId, Guid RecipientThreadId, long Revision,
    IReadOnlyList<CodexAgentMail> Pending, IReadOnlyList<CodexAgentMailReceipt> Receipts, CodexAgentMailClaim? Claim = null)
{
    /// <summary>Whether an unreserved follow-up could request a wake. This is not authorization or execution.</summary>
    public bool HasWakeRequest => Claim is null && Pending.Any(mail => mail.Mode == CodexAgentMailMode.Followup);
}

/// <summary>Storage mutation intent used to atomically enforce graph and execution fences.</summary>
public enum CodexAgentMailboxOperation
{
    /// <summary>Append one authenticated message.</summary>
    Enqueue,
    /// <summary>Reserve an ordered prefix for an authorized execution owner.</summary>
    Claim,
    /// <summary>Consume a prefix after verifying its exact durable turn admission.</summary>
    Acknowledge,
    /// <summary>Release only after proving the reservation did not admit its messages.</summary>
    Release,
}

/// <summary>Optional capability on the existing host thread storage adapter, not a second thread store.
/// Implementations must share the account/agent graph, deletion, archive and execution ownership
/// fences with the thread journal. Mailbox state must survive evictions and process restarts.
/// An adapter without these guarantees must not advertise this capability.</summary>
public interface ICodexAgentMailboxStorage : ICodexThreadStorage
{
    /// <summary>Read the recipient's mailbox at a consistent revision. Reject unknown, foreign or
    /// mismatched tree/recipient identities. An existing eligible recipient without mail has revision zero.</summary>
    Task<CodexAgentMailbox> ReadAgentMailboxAsync(string accountId, Guid rootThreadId, Guid recipientThreadId,
        CancellationToken cancellationToken);

    /// <summary>Atomically compare the exact observed revision and persist the proposed mailbox transition.
    /// Check the recipient's current root membership and lifecycle; an enqueue also checks authenticated
    /// sender membership and direct parent routing for a Result. A bound CompletedResult must match
    /// the original child terminal journal using CodexAgentCompletedResult.Matches in the same
    /// transaction. Validate claim ownership against
    /// the host's execution authority, and acknowledge only if the exact reserved messages were
    /// durably admitted to its target turn. Release requires a positive proof of non-admission
    /// and fencing of the previous owner (expiry or a missing runtime alone is not proof).
    /// Deletion must remove mailbox state in its transaction; archive/close must fence new work.
    /// False means no effects; true means durably committed. An exception may have committed,
    /// so inspect the same mailbox and delivery/claim identities rather than replaying blindly.</summary>
    Task<bool> CompareExchangeAgentMailboxAsync(long expectedRevision, CodexAgentMailbox next,
        CodexAgentMailboxOperation operation, CancellationToken cancellationToken);
}
