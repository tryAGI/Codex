namespace tryAGI.Codex;

public sealed partial class CodexMacOsSqliteAgentStorage
{
    /// <inheritdoc />
    public Task<bool> CompareCloseNamedAgentSubtreeAsync(string accountId, Guid rootThreadId, Guid childThreadId,
        string expectedGraphRevision, CancellationToken cancellationToken)
    {
        Account(accountId);
        Id(rootThreadId); Id(childThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGraphRevision);
        if (rootThreadId == childThreadId) throw new ArgumentException("The root cannot be closed as a spawned agent.");
        return database.WriteAsync(sql =>
        {
            var revision = RequireRoot(sql, rootThreadId);
            if (Revision(revision) != expectedGraphRevision) return false;
            var rows = TreeRows(sql, rootThreadId);
            var directory = validator.ValidateAgentIdentitySnapshot(Snapshot(rootThreadId, revision, rows), rootThreadId);
            var incoming = rows.SingleOrDefault(row => row.Edge.ChildThreadId == childThreadId);
            if (incoming?.Identity is null) return false;
            _ = directory.GetRecorded(childThreadId);
            var subtree = Descendants(childThreadId, rows).Prepend(incoming).ToArray();
            // SQLite serializes this preflight with every grant, journal and graph writer.
            // Never close an active runtime or lose an outstanding admission.
            if (subtree.Any(row => !Settled(sql, row.Edge.ChildThreadId)))
                return false;
            var open = subtree.Where(row => row.Edge.Status == CodexThreadSpawnEdgeStatus.Open).ToArray();
            if (open.Length == 0) return true;
            // Reverse breadth-first order is child-first. All edges commit together.
            foreach (var row in open.Reverse())
                ExpectOne(sql.Change("UPDATE agent_edges SET status=?2 WHERE child=?1 AND status=?3;",
                    row.Edge.ChildThreadId, (int)CodexThreadSpawnEdgeStatus.Closed, (int)CodexThreadSpawnEdgeStatus.Open));
            AdvanceRoot(sql, rootThreadId, revision);
            return true;
        }, cancellationToken);
    }

}
