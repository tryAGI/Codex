namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Whether the existing graph adapter supports consistent named identity reads.</summary>
    public bool SupportsAgentIdentities => storage is ICodexAgentIdentityStorage;

    /// <summary>Read and validate the agent tree directory without loading any child history,
    /// registering runtimes or executing a model. Re-read after an observed topology change.</summary>
    public async Task<CodexAgentIdentityDirectory> ReadAgentIdentitiesAsync(Guid rootThreadId,
        CancellationToken cancellationToken = default)
    {
        Id(rootThreadId);
        if (storage is not ICodexAgentIdentityStorage identities)
            throw new InvalidOperationException("Durable agent identity storage is unavailable.");
        var snapshot = await identities.ReadAgentIdentitySnapshotAsync(accountId, rootThreadId, cancellationToken)
            .ConfigureAwait(false);
        return ValidateAgentIdentitySnapshot(snapshot, rootThreadId);
    }

    internal CodexAgentIdentityDirectory ValidateAgentIdentitySnapshot(CodexAgentIdentitySnapshot snapshot, Guid rootThreadId)
    {
        if (snapshot is null || snapshot.Identities is null || snapshot.Identities.Count > MaximumSpawnEdges)
            throw new InvalidDataException("Invalid agent identity snapshot.");
        var graph = ValidateSpawnGraph(snapshot.Graph, rootThreadId);
        var recorded = snapshot.Identities.ToArray();
        if (recorded.Length > graph.Edges.Count)
            throw new InvalidDataException("Identity snapshot contains more identities than graph edges.");
        var order = TraverseSpawnEdges(rootThreadId, graph.Edges, null);
        var open = TraverseSpawnEdges(rootThreadId, graph.Edges, CodexThreadSpawnEdgeStatus.Open).ToHashSet();
        return new CodexAgentIdentityDirectory(graph, recorded, order, open);
    }
}
