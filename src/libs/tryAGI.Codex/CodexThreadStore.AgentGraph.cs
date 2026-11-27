namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    private const int MaximumSpawnEdges = 4096;

    /// <summary>Whether this account's existing storage adapter supports durable spawn topology.</summary>
    public bool SupportsSpawnGraph => storage is ICodexThreadSpawnGraphStorage;

    /// <summary>Read and validate an immutable graph snapshot, without loading or executing child threads.</summary>
    public async Task<CodexThreadSpawnGraph> ReadSpawnGraphAsync(Guid rootThreadId, CancellationToken cancellationToken = default)
    {
        Id(rootThreadId);
        var graphStorage = RequireSpawnGraphStorage();
        var graph = await graphStorage.ReadSpawnGraphAsync(accountId, rootThreadId, cancellationToken).ConfigureAwait(false);
        if (graph is null || graph.AccountId != accountId || graph.RootThreadId != rootThreadId ||
            string.IsNullOrWhiteSpace(graph.Revision) || graph.Revision.Length > 1024 ||
            graph.Edges is null || graph.Edges.Count > MaximumSpawnEdges)
            throw new InvalidDataException("Invalid native spawn graph snapshot.");

        var edges = graph.Edges.ToArray();
        if (edges.Length > MaximumSpawnEdges)
            throw new InvalidDataException("Native spawn graph exceeds the supported bound.");
        var incoming = new HashSet<Guid>();
        foreach (var edge in edges)
            if (edge is null || edge.ParentThreadId == Guid.Empty || edge.ChildThreadId == Guid.Empty ||
                edge.ChildThreadId == rootThreadId || edge.ParentThreadId == edge.ChildThreadId ||
                !Enum.IsDefined(edge.Status) || !incoming.Add(edge.ChildThreadId))
                throw new InvalidDataException("Invalid or duplicate incoming spawn edge.");

        if (TraverseSpawnEdges(rootThreadId, edges, null).Count != edges.Length)
            throw new InvalidDataException("Native spawn graph contains a cycle or unreachable edges.");
        return graph with { Edges = Array.AsReadOnly(edges) };
    }

    /// <summary>List direct children in thread ID order; null includes open and closed edges.</summary>
    public async Task<IReadOnlyList<Guid>> ListSpawnChildrenAsync(Guid parentThreadId,
        CodexThreadSpawnEdgeStatus? status = null, CancellationToken cancellationToken = default)
    {
        ValidateSpawnStatus(status);
        var graph = await ReadSpawnGraphAsync(parentThreadId, cancellationToken).ConfigureAwait(false);
        return graph.Edges.Where(edge => edge.ParentThreadId == parentThreadId && (status is null || edge.Status == status))
            .Select(edge => edge.ChildThreadId).Order().ToArray();
    }

    /// <summary>List descendants breadth-first by depth, then thread ID. A status filter prunes
    /// traversal through every nonmatching edge, not just the returned children.</summary>
    public async Task<IReadOnlyList<Guid>> ListSpawnDescendantsAsync(Guid rootThreadId,
        CodexThreadSpawnEdgeStatus? status = null, CancellationToken cancellationToken = default)
    {
        ValidateSpawnStatus(status);
        var graph = await ReadSpawnGraphAsync(rootThreadId, cancellationToken).ConfigureAwait(false);
        return TraverseSpawnEdges(rootThreadId, graph.Edges, status);
    }

    /// <summary>Publish or replace an incoming edge using the exact previously observed revision.
    /// This operation never creates a journal or starts a model turn.</summary>
    public async Task UpsertSpawnEdgeAsync(Guid rootThreadId, string expectedGraphRevision, CodexThreadSpawnEdge edge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        Id(edge.ParentThreadId);
        Id(edge.ChildThreadId);
        ValidateSpawnStatus(edge.Status);
        var graph = await ReadExpectedSpawnGraphAsync(rootThreadId, expectedGraphRevision, cancellationToken).ConfigureAwait(false);
        var incoming = graph.Edges.ToDictionary(item => item.ChildThreadId);
        if (edge.ChildThreadId == rootThreadId || edge.ParentThreadId == edge.ChildThreadId ||
            edge.ParentThreadId != rootThreadId && !incoming.ContainsKey(edge.ParentThreadId))
            throw new ArgumentException("Spawn edge must connect a child to a parent in the requested graph.", nameof(edge));
        for (var ancestor = edge.ParentThreadId; ancestor != rootThreadId; ancestor = incoming[ancestor].ParentThreadId)
            if (ancestor == edge.ChildThreadId)
                throw new ArgumentException("Spawn edge would introduce a cycle.", nameof(edge));

        if (incoming.TryGetValue(edge.ChildThreadId, out var previous) && previous == edge) return;
        if (previous is null && graph.Edges.Count == MaximumSpawnEdges)
            throw new InvalidOperationException("Native spawn graph capacity reached.");
        await CompareExchangeSpawnEdgeAsync(graph, edge, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Update the incoming edge status; a missing child or unchanged status is a no-op.
    /// A stale graph revision is still a conflict.</summary>
    public async Task SetSpawnEdgeStatusAsync(Guid rootThreadId, string expectedGraphRevision, Guid childThreadId,
        CodexThreadSpawnEdgeStatus status, CancellationToken cancellationToken = default)
    {
        Id(childThreadId);
        ValidateSpawnStatus(status);
        var graph = await ReadExpectedSpawnGraphAsync(rootThreadId, expectedGraphRevision, cancellationToken).ConfigureAwait(false);
        var edge = graph.Edges.SingleOrDefault(item => item.ChildThreadId == childThreadId);
        if (edge is null || edge.Status == status) return;
        await CompareExchangeSpawnEdgeAsync(graph, edge with { Status = status }, cancellationToken).ConfigureAwait(false);
    }

    private ICodexThreadSpawnGraphStorage RequireSpawnGraphStorage()
        => storage as ICodexThreadSpawnGraphStorage
            ?? throw new InvalidOperationException("Durable spawn graph storage is unavailable.");

    private async Task<CodexThreadSpawnGraph> ReadExpectedSpawnGraphAsync(Guid rootThreadId, string expectedGraphRevision,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGraphRevision);
        var graph = await ReadSpawnGraphAsync(rootThreadId, ct).ConfigureAwait(false);
        if (!string.Equals(graph.Revision, expectedGraphRevision, StringComparison.Ordinal))
            throw new CodexThreadConflictException();
        return graph;
    }

    private async Task CompareExchangeSpawnEdgeAsync(CodexThreadSpawnGraph graph, CodexThreadSpawnEdge edge, CancellationToken ct)
    {
        var graphStorage = RequireSpawnGraphStorage();
        ct.ThrowIfCancellationRequested();
        bool committed;
        try
        {
            committed = await graphStorage.CompareExchangeSpawnEdgeAsync(accountId, graph.RootThreadId, graph.Revision,
                edge, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A lost acknowledgement is not permission to reload a revision and replay a write.
            throw new IOException("Spawn edge commit is unconfirmed.", error);
        }
        if (!committed) throw new CodexThreadConflictException();
    }

    private static void ValidateSpawnStatus(CodexThreadSpawnEdgeStatus? status)
    {
        if (status is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(status));
    }

    private static IReadOnlyList<Guid> TraverseSpawnEdges(Guid root, IReadOnlyList<CodexThreadSpawnEdge> edges,
        CodexThreadSpawnEdgeStatus? status)
    {
        var children = edges.ToLookup(edge => edge.ParentThreadId);
        var visited = new HashSet<Guid> { root };
        var result = new List<Guid>();
        Guid[] level = [root];
        while (level.Length != 0)
        {
            level = level.SelectMany(parent => children[parent])
                .Where(edge => status is null || edge.Status == status)
                .Select(edge => edge.ChildThreadId).Where(visited.Add).Order().ToArray();
            result.AddRange(level);
        }
        return result.AsReadOnly();
    }
}
