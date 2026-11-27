using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private int readStateEnabled;

    /// <summary>Enables the experimental read-state snapshots, update RPC and notifications for
    /// this host's negotiated client surface. Terminal receipts are durable independently of this flag.</summary>
    public void EnableThreadReadState() => Interlocked.Exchange(ref readStateEnabled, 1);

    private bool ReadStateEnabled => Volatile.Read(ref readStateEnabled) != 0;

    private static JsonObject ReadState(CodexThreadDocument thread)
    {
        var first = thread.ReadReceipt?.FirstUnreadTurn;
        return new JsonObject
        {
            ["firstUnread"] = first switch
            {
                null => null,
                "" => new JsonObject { ["type"] = "threadStart" },
                _ => new JsonObject { ["type"] = "turn", ["turnId"] = first }
            },
            ["revision"] = CodexThreadStore.ReadStateRevision(thread)
        };
    }

    private void AddReadState(JsonObject result, CodexThreadDocument thread)
    {
        if (ReadStateEnabled && CodexThreadStore.ReadStateEligible(thread))
            result["readState"] = ReadState(thread);
    }

    private JsonElement AddReadStates(JsonElement page, IReadOnlyList<CodexThreadDocument> documents)
    {
        if (!ReadStateEnabled) return page;
        var byId = documents.Where(store.OwnsThread).Where(CodexThreadStore.ReadStateEligible)
            .ToDictionary(thread => thread.Id.ToString(), StringComparer.Ordinal);
        var states = new JsonObject();
        foreach (var entry in page.GetProperty("data").EnumerateArray())
            if (Text(entry, "id") is { } id && byId.TryGetValue(id, out var thread)) states[id] = ReadState(thread);
        var result = Object(page);
        result["readStates"] = states;
        return Element(result);
    }

    private async Task<JsonElement> UpdateReadStateAsync(JsonElement p, CancellationToken ct)
    {
        if (!ReadStateEnabled) throw new CodexThreadRpcException(-32601, "Experimental thread read state is not enabled.");
        ValidateFields(p, ["threadId", "expectedRevision", "operation"]);
        var id = Id(p, "threadId");
        var revision = Text(p, "expectedRevision") ?? throw Invalid("Read state revision is required.");
        if (!p.TryGetProperty("operation", out var op) || op.ValueKind != JsonValueKind.Object)
            throw Invalid("Read state operation is required.");
        ValidateFields(op, ["type"]);
        var type = Text(op, "type");
        if (type is not ("read" or "unread")) throw Invalid("Unsupported read state operation.");

        CodexThreadStore.ReadStateUpdate outcome;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckAttachmentAdmission(id);
            outcome = await store.UpdateReadStateAsync(id, revision, type == "unread", ct).ConfigureAwait(false);
        }
        finally { admission.Release(); }

        if (!outcome.Applied)
            throw new CodexThreadRpcException(-32600, "read state changed")
            {
                ErrorData = Element(new JsonObject { ["reason"] = "readStateConflict", ["readState"] = ReadState(outcome.Thread) })
            };
        await EmitAsync("thread/readState/changed", new JsonObject
        {
            ["threadId"] = id.ToString(), ["readState"] = ReadState(outcome.Thread)
        }).ConfigureAwait(false);
        return Element(new JsonObject { ["readState"] = ReadState(outcome.Thread) });
    }

    private async Task NotifyTerminalReadStateAsync(CodexThreadDocument completed, Guid turnId)
    {
        if (!ReadStateEnabled || completed.ReadReceipt?.LastPublishedTurnId != turnId) return;
        try
        {
            // Re-read the current receipt after the durable terminal event. Another client may
            // already have acknowledged it; never deliver an older unread snapshot in that case.
            var current = await store.ReadAsync(completed.Id, CancellationToken.None).ConfigureAwait(false);
            if (!CodexThreadStore.ReadStateEligible(current) || current.ReadReceipt?.LastPublishedTurnId != turnId) return;
            await EmitAsync("thread/readState/changed", new JsonObject
            {
                ["threadId"] = completed.Id.ToString(), ["readState"] = ReadState(current)
            }).ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { Interlocked.Increment(ref notificationFailures); }
    }
}
