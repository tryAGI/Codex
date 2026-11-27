using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private Func<CodexThreadDeletionPlan, CancellationToken, Task<bool>>? authorizeDeletion;

    /// <summary>Installs the host's explicit permanent-deletion authorization policy. The policy
    /// receives the immutable account, root and complete candidate scope before any interruption
    /// or deletion. A storage adapter alone does not enable this destructive operation.</summary>
    public void EnableThreadDeletion(Func<CodexThreadDeletionPlan, CancellationToken, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        if (!store.SupportsDeletion) throw new InvalidOperationException("Permanent deletion requires transactional storage.");
        if (Interlocked.CompareExchange(ref authorizeDeletion, authorize, null) is not null)
            throw new InvalidOperationException("Thread deletion authorization is already installed.");
    }

    private async Task<JsonElement> DeleteThreadAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId"]);
        var id = Id(p, "threadId");
        var authorize = Volatile.Read(ref authorizeDeletion)
            ?? throw new CodexThreadRpcException(-32601, "Native permanent deletion has not been enabled by the host.");
        var initial = await store.ReadDeletionSnapshotAsync(id, ct).ConfigureAwait(false);
        if (!await authorize(initial.Plan, ct).ConfigureAwait(false))
            throw new CodexThreadRpcException(-32000, "Permanent thread deletion was not authorized.");

        var reserved = new List<Guid>();
        var running = new Dictionary<Guid, Running>();
        try
        {
            foreach (var target in initial.Plan.Targets)
            {
                if (!archivePending.TryAdd(target.ThreadId, 0)) throw new CodexThreadConflictException();
                reserved.Add(target.ThreadId);
            }

            await realtimeAdmission.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await admission.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    CheckDeletionHost();
                    var confirmed = await store.ReadDeletionSnapshotAsync(id, ct).ConfigureAwait(false);
                    RequireSameDeletionScope(initial.Plan, confirmed.Plan);
                    if (!initial.Plan.Targets.SequenceEqual(confirmed.Plan.Targets)) throw new CodexThreadConflictException();
                    foreach (var target in confirmed.Plan.Targets)
                    {
                        var targetId = target.ThreadId;
                        if (realtime.ContainsKey(targetId)) throw DeleteBusy();
                        active.TryGetValue(targetId, out var local);
                        if (confirmed.Documents.TryGetValue(targetId, out var thread))
                        {
                            if (CodexThreadStore.HasOpenRealtime(thread)) throw DeleteBusy();
                            if (thread.Turns.LastOrDefault() is { Status: "inProgress" } live && local?.Id != live.Id)
                                throw DeleteBusy();
                        }
                        else if (local is not null) throw DeleteBusy();
                        if (local is not null) running.Add(targetId, local);
                    }
                }
                finally { admission.Release(); }
            }
            finally { realtimeAdmission.Release(); }

            // All subtree preflight and authorization succeeded before any owned worker is stopped.
            ct.ThrowIfCancellationRequested();
            foreach (var worker in running.Values) worker.Control.Interrupt();
            try { await Task.WhenAll(running.Values.Select(worker => worker.Completion)).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw DeleteBusy(); }

            await realtimeAdmission.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await admission.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    CheckDeletionHost();
                    var final = await store.ReadDeletionSnapshotAsync(id, ct).ConfigureAwait(false);
                    RequireSameDeletionScope(initial.Plan, final.Plan);
                    for (var i = 0; i < final.Plan.Targets.Count; i++)
                    {
                        var target = final.Plan.Targets[i];
                        if (active.ContainsKey(target.ThreadId) || realtime.ContainsKey(target.ThreadId)) throw DeleteBusy();
                        // A changed revision must still belong to the exact turn we drained, not a new turn or missing document.
                        if (target.Revision != initial.Plan.Targets[i].Revision
                            && (!running.TryGetValue(target.ThreadId, out var worker)
                                || !final.Documents.TryGetValue(target.ThreadId, out var drained)
                                || drained.Turns.LastOrDefault()?.Id != worker.Id))
                            throw new CodexThreadConflictException();
                        if (final.Documents.TryGetValue(target.ThreadId, out var thread)
                            && (thread.Turns.LastOrDefault()?.Status == "inProgress" || CodexThreadStore.HasOpenRealtime(thread)))
                            throw DeleteBusy();
                    }
                    try { await store.CommitDeletionAsync(final, ct).ConfigureAwait(false); }
                    catch (IOException) { throw new CodexThreadRpcException(-32010, "Thread deletion outcome is unconfirmed; inspect durable state before retrying."); }
                    foreach (var target in final.Plan.Targets) loaded.TryRemove(target.ThreadId, out _);
                }
                finally { admission.Release(); }
            }
            finally { realtimeAdmission.Release(); }

            foreach (var target in initial.Plan.Targets)
                await EmitAsync("thread/deleted", new JsonObject { ["threadId"] = target.ThreadId.ToString() }).ConfigureAwait(false);
            return Json("{}");
        }
        finally
        {
            foreach (var target in reserved) archivePending.TryRemove(target, out _);
        }
    }

    private void CheckDeletionHost()
    {
        if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
    }

    private static CodexThreadRpcException DeleteBusy()
        => new(-32010, "A thread in the deletion scope has an active or unreconciled execution owner.");

    private static void RequireSameDeletionScope(CodexThreadDeletionPlan approved, CodexThreadDeletionPlan actual)
    {
        if (approved.AccountId != actual.AccountId || approved.RootThreadId != actual.RootThreadId
            || approved.GraphRevision != actual.GraphRevision
            || !approved.Targets.Select(t => (t.ThreadId, t.ParentThreadId))
                .SequenceEqual(actual.Targets.Select(t => (t.ThreadId, t.ParentThreadId))))
            throw new CodexThreadConflictException();
    }
}
