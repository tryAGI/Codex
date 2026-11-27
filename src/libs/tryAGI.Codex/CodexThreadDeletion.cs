namespace tryAGI.Codex;

/// <summary>A member of the host's authoritative spawned-thread subtree, in child-first deletion order.</summary>
/// <param name="ThreadId">Thread or durable graph-only identity to delete.</param>
/// <param name="ParentThreadId">Parent within the requested subtree; null only for its root.</param>
/// <param name="Revision">Expected journal revision, null only for graph-only state without a document.</param>
public sealed record CodexThreadDeletionTarget(Guid ThreadId, Guid? ParentThreadId, long? Revision);

/// <summary>An account-bound, revision-fenced permanent deletion scope. Ordinary fork descendants
/// are not spawn descendants and cannot be silently included in this scope.</summary>
/// <param name="AccountId">Authenticated storage account.</param>
/// <param name="RootThreadId">Explicitly requested thread.</param>
/// <param name="GraphRevision">Opaque revision of the authoritative subtree and reference index.</param>
/// <param name="Targets">Complete child-first deletion order, ending with the requested root.</param>
public sealed record CodexThreadDeletionPlan(string AccountId, Guid RootThreadId, string GraphRevision,
    IReadOnlyList<CodexThreadDeletionTarget> Targets);

/// <summary>Optional transactional hard-delete extension of the existing host-scoped journal.
/// The host must fence deletion against all other processes, including idle writer leases, and
/// include spawned graph edges, external attached/fork references and auxiliary thread-owned data.
/// A host that cannot provide these guarantees must not implement this capability.</summary>
public interface ICodexThreadDeletionStorage : ICodexThreadStorage
{
    /// <summary>Returns the complete authoritative subtree and current journal revisions without
    /// changing state. Reject an unknown root, external attachment/history references, foreign
    /// writer leases and unavailable required cleanup stores before returning any plan.
    /// The graph revision must remain stable across ordinary turn journal updates.</summary>
    Task<CodexThreadDeletionPlan> ReadDeletionPlanAsync(string accountId, Guid rootThreadId, CancellationToken cancellationToken);

    /// <summary>Atomically revalidate the account, complete subtree, graph revision, all expected
    /// document revisions, external references and writer ownership; then permanently remove all
    /// target documents, graph state, and owned auxiliary records. False means no deletion occurred.
    /// True is permitted only after durable commit. A thrown operation may have committed; do not
    /// retry it blindly. Coordinate compare-exchange creation, fork/spawn and lifecycle ownership
    /// with this transaction so no stale writer or concurrent fork can resurrect a deleted identity
    /// or add a reference after preflight.</summary>
    Task<bool> CompareDeleteAsync(CodexThreadDeletionPlan plan, CancellationToken cancellationToken);
}
