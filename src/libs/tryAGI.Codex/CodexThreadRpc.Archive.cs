using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private readonly ConcurrentDictionary<Guid, byte> archivePending = new();

    private void RequireNoArchiveTransition(Guid id)
    {
        if (archivePending.ContainsKey(id))
            throw new CodexThreadRpcException(-32010, "Thread archive transition is in progress.");
    }

    private async Task<JsonElement> ArchiveAsync(JsonElement p, bool archived, CancellationToken ct)
    {
        ValidateFields(p, ["threadId"]);
        var id = Id(p, "threadId");
        if (!archivePending.TryAdd(id, 0)) throw new CodexThreadConflictException();
        try
        {
            Running? running;
            await realtimeAdmission.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await admission.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
                    var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
                    CodexThreadStore.RequireDurableThread(before);
                    if (archived == (before.ArchivedAt is not null))
                        throw Invalid(archived ? "Thread is already archived." : "Thread is not archived.");
                    EnsureArchiveVoiceSettled(before);
                    active.TryGetValue(id, out running);
                    if (!archived && running is not null)
                        throw new CodexThreadConflictException();
                    if (before.Turns.LastOrDefault() is { Status: "inProgress" } live && running?.Id != live.Id)
                        throw new CodexThreadRpcException(-32010, "Thread has a foreign or unreconciled execution owner.");
                }
                finally { admission.Release(); }
            }
            finally { realtimeAdmission.Release(); }

            // Never hold admission while a worker drains its tools or delivers notifications.
            if (running is not null)
            {
                ct.ThrowIfCancellationRequested();
                running.Control.Interrupt();
                try { await running.Completion.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (TimeoutException) { throw new CodexThreadRpcException(-32010, "Thread shutdown is unconfirmed; archive was not committed."); }
            }

            CodexThreadDocument updated;
            await realtimeAdmission.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await admission.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
                    var current = await store.ReadAsync(id, ct).ConfigureAwait(false);
                    EnsureArchiveVoiceSettled(current);
                    if (active.ContainsKey(id) || current.Turns.LastOrDefault()?.Status == "inProgress")
                        throw new CodexThreadRpcException(-32010, "Thread execution has not settled; archive transition was not committed.");
                    updated = archived
                        ? await store.ArchiveAsync(id, current.Revision, ct).ConfigureAwait(false)
                        : await store.UnarchiveAsync(id, current.Revision, ct).ConfigureAwait(false);
                    loaded.TryRemove(id, out _);
                }
                finally { admission.Release(); }
            }
            finally { realtimeAdmission.Release(); }

            await EmitAsync(archived ? "thread/archived" : "thread/unarchived",
                new JsonObject { ["threadId"] = id.ToString() }).ConfigureAwait(false);
            return archived ? Json("{}") : Element(new JsonObject { ["thread"] = Thread(updated, false) });
        }
        finally { archivePending.TryRemove(id, out _); }
    }

    private void EnsureArchiveVoiceSettled(CodexThreadDocument thread)
    {
        if (realtime.ContainsKey(thread.Id) || CodexThreadStore.HasOpenRealtime(thread))
            throw new CodexThreadRpcException(-32010, "Stop the realtime session and settle its journal before archiving.");
    }
}
