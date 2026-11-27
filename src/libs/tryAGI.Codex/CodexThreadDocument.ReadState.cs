namespace tryAGI.Codex;

public sealed partial record CodexThreadDocument
{
    /// <summary>Optional durable attention receipt; absent means the initial read position.</summary>
    public CodexStoredThreadReadReceipt? ReadReceipt { get; init; }
}

/// <summary>Thread-local unread position and opaque revision, independent of the journal revision.</summary>
/// <param name="Revision">Fresh revision for each explicit mark or newly published terminal result.</param>
/// <param name="FirstUnreadTurn">Null means read; empty means explicitly unread from the thread start;
/// otherwise the canonical ID of the earliest unread terminal turn.</param>
/// <param name="LastPublishedTurnId">Most recent terminal result published; preserved across explicit marks.</param>
public sealed record CodexStoredThreadReadReceipt(Guid Revision, string? FirstUnreadTurn, Guid? LastPublishedTurnId);
