namespace tryAGI.Codex;

/// <summary>Stored metadata of one named child. Root metadata is derived from the authenticated graph root.</summary>
/// <param name="ThreadId">Child journal or reserved spawn identity.</param>
/// <param name="ParentThreadId">Immediate spawning parent, not the original history fork source.</param>
/// <param name="RootThreadId">Root of this agent tree.</param>
/// <param name="Path">Canonical absolute agent path.</param>
/// <param name="Role">Optional resolved role name.</param>
/// <param name="Nickname">Optional display nickname.</param>
public sealed record CodexNamedAgentIdentity(Guid ThreadId, Guid? ParentThreadId, Guid RootThreadId,
    string Path, string? Role = null, string? Nickname = null);

/// <summary>A consistent storage snapshot of topology and persisted child identity metadata.
/// Graph-only children whose creation has not settled can be present without metadata.</summary>
/// <param name="Graph">Complete account-bound graph.</param>
/// <param name="Identities">Recorded child identities, including closed children.</param>
public sealed record CodexAgentIdentitySnapshot(CodexThreadSpawnGraph Graph, IReadOnlyList<CodexNamedAgentIdentity> Identities);

/// <summary>Optional capability on the same durable graph/thread storage adapter. The snapshot
/// must use one consistent revision covering topology and metadata; no child history is loaded.
/// Identity creation/changes must be fenced against graph mutations, deletion and foreign ownership.</summary>
public interface ICodexAgentIdentityStorage : ICodexThreadSpawnGraphStorage
{
    /// <summary>Read a consistent account/root identity snapshot.</summary>
    Task<CodexAgentIdentitySnapshot> ReadAgentIdentitySnapshotAsync(string accountId, Guid rootThreadId,
        CancellationToken cancellationToken);
}

/// <summary>Validated immutable named tree directory. Open paths are resolvable; historical
/// identities are accessible only by explicit ID, never by accidentally reused names.</summary>
public sealed class CodexAgentIdentityDirectory
{
    private readonly Dictionary<Guid, CodexNamedAgentIdentity> recorded;
    private readonly Dictionary<string, CodexNamedAgentIdentity> activePaths;
    private readonly HashSet<Guid> activeIds;

    internal CodexAgentIdentityDirectory(CodexThreadSpawnGraph graph, CodexNamedAgentIdentity[] identities,
        IReadOnlyList<Guid> graphOrder, IReadOnlySet<Guid> openIds)
    {
        Graph = graph;
        var root = new CodexNamedAgentIdentity(graph.RootThreadId, null, graph.RootThreadId, CodexAgentPath.Root);
        recorded = new() { [root.ThreadId] = root };
        activePaths = new(StringComparer.Ordinal) { [root.Path] = root };
        activeIds = [root.ThreadId];
        var edges = graph.Edges.ToDictionary(edge => edge.ChildThreadId);
        var supplied = new Dictionary<Guid, CodexNamedAgentIdentity>();
        foreach (var identity in identities)
        {
            if (identity is null || identity.RootThreadId != graph.RootThreadId ||
                !edges.TryGetValue(identity.ThreadId, out var edge) || identity.ParentThreadId != edge.ParentThreadId ||
                !ValidMetadata(identity.Role) || !ValidMetadata(identity.Nickname) ||
                !supplied.TryAdd(identity.ThreadId, identity))
                throw new InvalidDataException("Invalid or duplicate recorded agent identity.");
            try
            {
                if (CodexAgentPath.Parse(identity.Path) is CodexAgentPath.Root or CodexAgentPath.Morpheus)
                    throw new ArgumentException("Reserved root path.");
            }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid recorded agent path.", error); }
        }

        var restorable = new List<CodexNamedAgentIdentity>();
        foreach (var id in graphOrder)
        {
            if (!supplied.TryGetValue(id, out var identity)) continue;
            var parentId = edges[id].ParentThreadId;
            if (!recorded.TryGetValue(parentId, out var parent))
                throw new InvalidDataException("Recorded child identity has no recorded parent.");
            if (CodexAgentPath.Join(parent.Path, CodexAgentPath.Name(identity.Path)) != identity.Path)
                throw new InvalidDataException("Recorded path disagrees with its immediate parent.");
            recorded.Add(id, identity);
            if (!openIds.Contains(id) || !activeIds.Contains(parentId)) continue;
            if (!activePaths.TryAdd(identity.Path, identity))
                throw new InvalidDataException("Duplicate active agent path.");
            activeIds.Add(id);
            restorable.Add(identity);
        }
        RestorableDescendants = restorable.AsReadOnly();
    }

    /// <summary>Validated graph and revision used to construct this directory.</summary>
    public CodexThreadSpawnGraph Graph { get; }

    /// <summary>Open, fully identified descendants in ancestor-first breadth-first order.</summary>
    public IReadOnlyList<CodexNamedAgentIdentity> RestorableDescendants { get; }

    /// <summary>Look up a currently open, identified thread without loading its runtime.</summary>
    public CodexNamedAgentIdentity GetActive(Guid threadId)
        => activeIds.Contains(threadId) ? recorded[threadId] :
            throw new KeyNotFoundException("Active agent identity not found.");

    /// <summary>Look up explicit historical identity for recovery or result attribution.
    /// This does not authorize resuming a closed agent or delivering mail.</summary>
    public CodexNamedAgentIdentity GetRecorded(Guid threadId)
        => recorded.TryGetValue(threadId, out var identity) ? identity :
            throw new KeyNotFoundException("Recorded agent identity not found.");

    /// <summary>Resolve an active absolute or descendant-relative name using the caller's recorded
    /// identity in this exact account/tree. No runtime is loaded.</summary>
    public CodexNamedAgentIdentity Resolve(Guid callerThreadId, string reference)
    {
        var caller = GetActive(callerThreadId);
        var path = CodexAgentPath.Resolve(caller.Path, reference);
        return activePaths.TryGetValue(path, out var identity) ? identity :
            throw new KeyNotFoundException("Active agent path not found.");
    }

    /// <summary>Return the exact active ancestor chain to the target, excluding root, in load order.</summary>
    public IReadOnlyList<CodexNamedAgentIdentity> RestoreChain(Guid threadId)
    {
        var chain = new List<CodexNamedAgentIdentity>();
        var current = GetActive(threadId);
        while (current.ParentThreadId is { } parent)
        {
            chain.Add(current);
            current = GetActive(parent);
        }
        chain.Reverse();
        return chain.AsReadOnly();
    }

    private static bool ValidMetadata(string? value)
        => value is null || value is { Length: > 0 and <= 512 } &&
            !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
