using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    internal const int MaximumAttachments = 100;

    internal static void ValidateAttachmentIdentity(string type, string key)
    {
        if (string.IsNullOrWhiteSpace(type) || Encoding.UTF8.GetByteCount(type) > 256)
            throw new ArgumentException("attachmentType must contain 1 to 256 UTF-8 bytes and not be blank.");
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) > 256)
            throw new ArgumentException("identityKey must contain 1 to 256 UTF-8 bytes and not be blank.");
    }

    internal static void ValidateAttachmentPayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Attachment payload is required.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            payload.WriteTo(writer);
        if (buffer.WrittenCount > 64 * 1024) throw new ArgumentException("Attachment payload exceeds 65536 UTF-8 bytes.");
    }

    private static void ValidateAttachments(CodexStoredThreadAttachment[]? attachments)
    {
        if (attachments is null) return;
        if (attachments.Length > MaximumAttachments) throw new InvalidDataException("Too many stored thread attachments.");
        var identities = new HashSet<(string, string)>();
        var ids = new HashSet<Guid>();
        foreach (var attachment in attachments)
        {
            if (attachment is null || attachment.Id == Guid.Empty || !ids.Add(attachment.Id)
                || !identities.Add((attachment.AttachmentType, attachment.IdentityKey)))
                throw new InvalidDataException("Invalid or duplicate stored thread attachment.");
            ValidateAttachmentIdentity(attachment.AttachmentType, attachment.IdentityKey);
            ValidateAttachmentPayload(attachment.Payload);
        }
    }

    internal static bool MatchesAttachment(CodexStoredThreadAttachment attachment, string type, string key)
        => attachment.AttachmentType == type && attachment.IdentityKey == key;

    internal bool OwnsThread(CodexThreadDocument thread) => thread.AccountId == accountId;

    private static void RequireAttachmentThread(CodexThreadDocument thread)
    {
        if (thread.Settings.TryGetProperty("ephemeral", out var transient) && transient.ValueKind == JsonValueKind.True)
            throw new ArgumentException("Ephemeral threads do not own durable attachments.");
    }

    internal async Task<(CodexStoredThreadAttachment Attachment, bool Created)> AddAttachmentAsync(
        Guid id, long revision, string type, string key, JsonElement payload, CancellationToken ct)
    {
        ValidateAttachmentIdentity(type, key);
        ValidateAttachmentPayload(payload);
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireAttachmentThread(before);
        var attachments = before.Attachments ?? [];
        var existing = attachments.SingleOrDefault(a => MatchesAttachment(a, type, key));
        if (existing is not null) return (existing, false);
        if (attachments.Length >= MaximumAttachments) throw new ArgumentException("Thread attachment capacity exceeded.");
        var attachment = new CodexStoredThreadAttachment(Guid.CreateVersion7(), type, key, payload.Clone(), time.GetUtcNow().ToUnixTimeSeconds());
        // Attachment metadata does not change the reasoning history or thread recency.
        await PublishAsync(before.Revision, before with { Revision = checked(before.Revision + 1), Attachments = [.. attachments, attachment] }, ct).ConfigureAwait(false);
        return (attachment, true);
    }

    internal async Task<CodexStoredThreadAttachment?> RemoveAttachmentAsync(
        Guid id, long revision, string type, string key, CancellationToken ct)
    {
        ValidateAttachmentIdentity(type, key);
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireAttachmentThread(before);
        var attachments = before.Attachments ?? [];
        var existing = attachments.SingleOrDefault(a => MatchesAttachment(a, type, key));
        if (existing is null) return null;
        await PublishAsync(before.Revision, before with { Revision = checked(before.Revision + 1),
            Attachments = attachments.Where(a => a.Id != existing.Id).ToArray() }, ct).ConfigureAwait(false);
        return existing;
    }

    private static CodexStoredThreadAttachment[]? ForkAttachments(CodexThreadDocument source, JsonElement settings, DateTimeOffset now)
    {
        if (settings.TryGetProperty("ephemeral", out var transient) && transient.ValueKind == JsonValueKind.True) return null;
        return source.Attachments?.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id.ToString(), StringComparer.Ordinal)
            .Select(a => a with { Id = Guid.CreateVersion7(), CreatedAt = now.ToUnixTimeSeconds() }).ToArray();
    }
}
