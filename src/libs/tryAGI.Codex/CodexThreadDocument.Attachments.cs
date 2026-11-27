using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial record CodexThreadDocument
{
    /// <summary>Current durable thread-local attachment membership.</summary>
    public CodexStoredThreadAttachment[]? Attachments { get; init; }
}

/// <summary>One immutable attachment identified by its thread-local type and identity key.</summary>
/// <param name="Id">Stable attachment ID, unique to the owning thread.</param>
/// <param name="AttachmentType">Attachment type.</param>
/// <param name="IdentityKey">Exact identity key within the type.</param>
/// <param name="Payload">Opaque JSON metadata; it is not model input.</param>
/// <param name="CreatedAt">Creation time in Unix seconds.</param>
public sealed record CodexStoredThreadAttachment(Guid Id, string AttachmentType, string IdentityKey, JsonElement Payload, long CreatedAt);
