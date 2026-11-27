namespace tryAGI.Codex;

/// <summary>Lifecycle of a spawned thread's incoming graph edge, independent of residency.</summary>
public enum CodexThreadSpawnEdgeStatus
{
    /// <summary>The child remains live or resumable.</summary>
    Open,
    /// <summary>The child was closed in the agent graph.</summary>
    Closed,
}

/// <summary>An incoming edge for a spawned thread; ordinary history forks are not spawn edges.</summary>
/// <param name="ParentThreadId">Spawning parent.</param>
/// <param name="ChildThreadId">Spawned child, which may not yet have a journal.</param>
/// <param name="Status">Persisted graph lifecycle.</param>
public sealed record CodexThreadSpawnEdge(Guid ParentThreadId, Guid ChildThreadId, CodexThreadSpawnEdgeStatus Status);

/// <summary>Account-bound snapshot of an entire spawned subtree, including closed edges.</summary>
/// <param name="AccountId">Authenticated storage account.</param>
/// <param name="RootThreadId">Requested subtree root.</param>
/// <param name="Revision">Opaque graph revision, independent of ordinary turn updates.</param>
/// <param name="Edges">All edges reachable below the root, including graph-only children.</param>
public sealed record CodexThreadSpawnGraph(string AccountId, Guid RootThreadId, string Revision,
    IReadOnlyList<CodexThreadSpawnEdge> Edges);

/// <summary>Optional graph capability on the existing thread storage adapter. The adapter must
/// serialize graph mutations with deletion, creation and reference checks, and reject foreign
/// identities, cycles and duplicate incoming edges across the entire account graph.
/// Graph edges may precede child journals; they must survive runtime eviction and restart.</summary>
public interface ICodexThreadSpawnGraphStorage : ICodexThreadStorage
{
    /// <summary>Read all reachable edges under the root and their consistent revision.
    /// An unknown or foreign root must be rejected, not returned as an empty graph.</summary>
    Task<CodexThreadSpawnGraph> ReadSpawnGraphAsync(string accountId, Guid rootThreadId, CancellationToken cancellationToken);

    /// <summary>Atomically replace the child's incoming edge, only at the observed graph revision.
    /// Check the complete account graph, including any incoming edge outside the requested
    /// subtree, and prevent resurrection after deletion. Return false without effects on conflict;
    /// return true only after durable commit. A thrown write may have committed.</summary>
    Task<bool> CompareExchangeSpawnEdgeAsync(string accountId, Guid rootThreadId, string expectedGraphRevision,
        CodexThreadSpawnEdge edge, CancellationToken cancellationToken);
}
