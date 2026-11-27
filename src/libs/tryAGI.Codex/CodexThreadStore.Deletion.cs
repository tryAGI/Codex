namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    internal bool SupportsDeletion => storage is ICodexThreadDeletionStorage;

    internal sealed record DeletionSnapshot(CodexThreadDeletionPlan Plan, IReadOnlyDictionary<Guid, CodexThreadDocument> Documents);

    internal async Task<DeletionSnapshot> ReadDeletionSnapshotAsync(Guid id, CancellationToken ct)
    {
        Id(id);
        if (storage is not ICodexThreadDeletionStorage deletion)
            throw new InvalidOperationException("Permanent thread deletion is unavailable.");
        lock (ephemeralGate)
            if (ephemeral.ContainsKey(id)) throw new ArgumentException("Ephemeral threads cannot be permanently deleted.");

        var plan = await deletion.ReadDeletionPlanAsync(accountId, id, ct).ConfigureAwait(false);
        if (plan is null || plan.AccountId != accountId || plan.RootThreadId != id
            || string.IsNullOrWhiteSpace(plan.GraphRevision) || plan.GraphRevision.Length > 1024
            || plan.Targets is null || plan.Targets.Count is < 1 or > 4096)
            throw new InvalidOperationException("Invalid native deletion scope.");
        // Freeze the approved scope; the embedding host cannot mutate a returned list in place.
        var targets = plan.Targets.ToArray();
        if (targets.Length is < 1 or > 4096 || targets[^1] is not { ParentThreadId: null } root || root.ThreadId != id)
            throw new InvalidOperationException("Deletion scope must end with its requested root.");
        var ancestors = new HashSet<Guid>();
        foreach (var target in targets.Reverse())
        {
            if (target is null || target.ThreadId == Guid.Empty || target.Revision is < 1
                || target.ThreadId != id && (target.ParentThreadId is not { } parent || !ancestors.Contains(parent))
                || !ancestors.Add(target.ThreadId))
                throw new InvalidOperationException("Invalid or cyclic native deletion scope.");
        }

        var documents = new Dictionary<Guid, CodexThreadDocument>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            CodexThreadDocument? thread;
            try { thread = await ReadAsync(target.ThreadId, ct).ConfigureAwait(false); }
            catch (KeyNotFoundException) { thread = null; }
            if (thread?.Revision != target.Revision) throw new CodexThreadConflictException();
            if (thread is null) continue;
            RequireDurableThread(thread);
            documents.Add(thread.Id, thread);
        }
        return new(plan with { Targets = Array.AsReadOnly(targets) }, documents);
    }

    internal async Task CommitDeletionAsync(DeletionSnapshot snapshot, CancellationToken ct)
    {
        if (storage is not ICodexThreadDeletionStorage deletion)
            throw new InvalidOperationException("Permanent thread deletion is unavailable.");
        foreach (var thread in snapshot.Documents.Values) RequireSettledArchiveWriter(thread);
        // Never emulate this with per-row writes or sequential deletes.
        ct.ThrowIfCancellationRequested();
        bool committed;
        try { committed = await deletion.CompareDeleteAsync(snapshot.Plan, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Any exception after dispatch can mean a lost durable commit acknowledgement.
            throw new IOException("Thread deletion commit is unconfirmed.", error);
        }
        if (!committed) throw new CodexThreadConflictException();
    }
}
