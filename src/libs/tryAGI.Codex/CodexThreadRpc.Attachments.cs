using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private static (string Type, string Key) AttachmentIdentity(JsonElement p)
    {
        var type = Text(p, "attachmentType") ?? throw Invalid("attachmentType is required.");
        var key = Text(p, "identityKey") ?? throw Invalid("identityKey is required.");
        CodexThreadStore.ValidateAttachmentIdentity(type, key);
        return (type, key);
    }

    private static int AttachmentLimit(JsonElement p)
    {
        if (!p.TryGetProperty("limit", out var value) || value.ValueKind == JsonValueKind.Null) return 50;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var limit)) throw Invalid("Invalid attachment page limit.");
        return (int)Math.Clamp(limit, 1u, 100u);
    }

    private static JsonObject Attachment(CodexStoredThreadAttachment a) => new()
    {
        ["id"] = a.Id.ToString(), ["attachmentType"] = a.AttachmentType, ["identityKey"] = a.IdentityKey,
        ["payload"] = JsonNode.Parse(a.Payload.GetRawText()), ["createdAt"] = a.CreatedAt
    };

    private async Task NotifyAttachmentAsync(Guid id, CodexStoredThreadAttachment a, string operation)
        => await EmitAsync("thread/attachment/updated", new JsonObject
        {
            ["threadId"] = id.ToString(), ["attachmentType"] = a.AttachmentType, ["identityKey"] = a.IdentityKey,
            ["attachmentId"] = a.Id.ToString(), ["operation"] = operation
        }).ConfigureAwait(false);

    private async Task<JsonElement> AddAttachmentAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "attachmentType", "identityKey", "payload"]);
        var id = Id(p, "threadId"); var (type, key) = AttachmentIdentity(p);
        if (!p.TryGetProperty("payload", out var payload)) throw Invalid("Attachment payload is required.");
        CodexStoredThreadAttachment attachment; bool created;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckAttachmentAdmission(id);
            var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
            (attachment, created) = await store.AddAttachmentAsync(id, before.Revision, type, key, payload, ct).ConfigureAwait(false);
        }
        finally { admission.Release(); }
        if (created) await NotifyAttachmentAsync(id, attachment, "created").ConfigureAwait(false);
        return Element(new JsonObject { ["outcome"] = created ? "created" : "existing", ["attachment"] = Attachment(attachment) });
    }

    private async Task<JsonElement> RemoveAttachmentAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "attachmentType", "identityKey"]);
        var id = Id(p, "threadId"); var (type, key) = AttachmentIdentity(p);
        CodexStoredThreadAttachment? removed;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckAttachmentAdmission(id);
            var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
            removed = await store.RemoveAttachmentAsync(id, before.Revision, type, key, ct).ConfigureAwait(false);
        }
        finally { admission.Release(); }
        if (removed is not null) await NotifyAttachmentAsync(id, removed, "deleted").ConfigureAwait(false);
        return Json("{}");
    }

    private void CheckAttachmentAdmission(Guid id)
    {
        if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
        if (active.TryGetValue(id, out var running) && !running.Started.Task.IsCompleted)
            throw new CodexThreadConflictException();
    }

    private async Task<JsonElement> ListAttachmentsAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "cursor", "limit"]);
        var id = Id(p, "threadId"); var limit = AttachmentLimit(p);
        var document = await store.ReadAsync(id, ct).ConfigureAwait(false);
        if (Flag(document.Settings, "ephemeral")) throw Invalid("Ephemeral threads do not own durable attachments.");
        IEnumerable<CodexStoredThreadAttachment> rows = document.Attachments ?? [];
        if (Text(p, "cursor") is { } cursor)
        {
            var parts = cursor.Split('|');
            if (parts.Length != 3 || parts[0] != id.ToString()
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamp)
                || !Guid.TryParseExact(parts[2], "D", out var attachmentId)) throw Invalid("Invalid attachment cursor.");
            rows = rows.Where(a => a.CreatedAt > stamp || a.CreatedAt == stamp
                && string.CompareOrdinal(a.Id.ToString(), attachmentId.ToString()) > 0);
        }
        var ordered = rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id.ToString(), StringComparer.Ordinal).Take(limit + 1).ToArray();
        var page = ordered.Take(limit).ToArray();
        var next = ordered.Length > limit
            ? string.Create(CultureInfo.InvariantCulture, $"{id}|{page[^1].CreatedAt}|{page[^1].Id}") : null;
        return Element(new JsonObject { ["data"] = new JsonArray(page.Select(a => (JsonNode)Attachment(a)).ToArray()), ["nextCursor"] = next });
    }

    private async Task<JsonElement> ListAttachmentOwnersAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["attachmentType", "identityKey", "archived", "cursor", "limit"]);
        var (type, key) = AttachmentIdentity(p); var limit = AttachmentLimit(p);
        bool? archived = p.TryGetProperty("archived", out var value) && value.ValueKind != JsonValueKind.Null
            ? Flag(p, "archived") : null;
        string? anchor = null;
        if (Text(p, "cursor") is { } cursor)
        {
            try
            {
                var data = Json(cursor);
                if (data.ValueKind != JsonValueKind.Object) throw Invalid("Invalid attachment owner cursor.");
                ValidateFields(data, ["attachment_type", "identity_key", "archived", "thread_id"]);
                bool? filter = data.TryGetProperty("archived", out var a) && a.ValueKind != JsonValueKind.Null
                    ? Flag(data, "archived") : null;
                if (Text(data, "attachment_type") != type || Text(data, "identity_key") != key || filter != archived
                    || !data.TryGetProperty("archived", out _)) throw Invalid("Invalid attachment owner cursor.");
                anchor = Id(data, "thread_id").ToString();
            }
            catch (JsonException) { throw Invalid("Invalid attachment owner cursor."); }
        }
        var documents = await list(ct).ConfigureAwait(false);
        if (documents.Count > 1000) throw new CodexThreadRpcException(-32010, "Native discovery exceeds its bounded catalog; no incomplete success returned.");
        // Archived owners will use the same predicate once the archive lifecycle is implemented.
        var owners = documents.Where(d => store.OwnsThread(d) && !Flag(d.Settings, "ephemeral") && archived != true
            && d.Attachments?.Any(a => CodexThreadStore.MatchesAttachment(a, type, key)) == true
            && (anchor is null || string.CompareOrdinal(d.Id.ToString(), anchor) > 0))
            .Select(d => d.Id.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(limit + 1).ToArray();
        var page = owners.Take(limit).ToArray();
        var next = owners.Length > limit ? new JsonObject { ["attachment_type"] = type, ["identity_key"] = key,
            ["archived"] = archived, ["thread_id"] = page[^1] }.ToJsonString() : null;
        return Element(new JsonObject
        {
            ["data"] = new JsonArray(page.Select(id => (JsonNode)new JsonObject { ["threadId"] = id, ["archived"] = false }).ToArray()),
            ["nextCursor"] = next
        });
    }
}
