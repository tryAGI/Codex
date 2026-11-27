using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private async Task<JsonElement> SetNameAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "name"]);
        var id = Id(p, "threadId");
        var name = Text(p, "name") ?? throw Invalid("Thread name is required.");
        CodexThreadDocument updated;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
            // Do not invalidate the captured revision while a new turn is still being admitted.
            if (active.TryGetValue(id, out var running) && !running.Started.Task.IsCompleted)
                throw new CodexThreadConflictException();
            var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
            updated = await store.SetNameAsync(id, before.Revision, name, ct).ConfigureAwait(false);
        }
        finally { admission.Release(); }
        await EmitAsync("thread/name/updated", new JsonObject { ["threadId"] = id.ToString(), ["threadName"] = updated.Name }).ConfigureAwait(false);
        return Json("{}");
    }
}
