using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private async Task<JsonElement> UpdateMetadataAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "gitInfo", "daybreakEnabled", "projectId"]);
        var id = Id(p, "threadId");
        if (Text(p, "projectId") is not null)
            throw new CodexThreadRpcException(-32601, "Project membership requires the native project storage contract.");
        JsonElement? git = null;
        if (p.TryGetProperty("gitInfo", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Any())
                throw Invalid("gitInfo must include at least one field.");
            ValidateFields(value, ["sha", "branch", "originUrl"]);
            git = value;
        }
        bool? daybreak = p.TryGetProperty("daybreakEnabled", out var preference) && preference.ValueKind != JsonValueKind.Null
            ? Flag(p, "daybreakEnabled") : null;
        if (git is null && daybreak is null) throw Invalid("Thread metadata update must include at least one field.");
        CodexThreadDocument updated;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckAttachmentAdmission(id);
            var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
            updated = await store.UpdateMetadataAsync(id, before.Revision, git, daybreak, ct).ConfigureAwait(false);
        }
        finally { admission.Release(); }
        return Element(new JsonObject { ["thread"] = Thread(updated, false) });
    }

    private static JsonObject? GitInfo(CodexStoredGitInfo? info) => info is null ? null : new JsonObject
    {
        ["sha"] = info.Sha, ["branch"] = info.Branch, ["originUrl"] = info.OriginUrl
    };

    private async Task<JsonElement> SetNameAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "name"]);
        var id = Id(p, "threadId");
        var name = Text(p, "name") ?? throw Invalid("Thread name is required.");
        CodexThreadDocument updated;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckAttachmentAdmission(id);
            var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
            updated = await store.SetNameAsync(id, before.Revision, name, ct).ConfigureAwait(false);
        }
        finally { admission.Release(); }
        await EmitAsync("thread/name/updated", new JsonObject { ["threadId"] = id.ToString(), ["threadName"] = updated.Name }).ConfigureAwait(false);
        return Json("{}");
    }
}
