using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Archives a durable idle thread at the expected revision without deleting history.</summary>
    public Task<CodexThreadDocument> ArchiveAsync(Guid threadId, long expectedRevision, CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, thread =>
        {
            RequireDurableThread(thread);
            RequireUnarchived(thread);
            RequireSettledArchiveWriter(thread);
            return thread with { ArchivedAt = time.GetUtcNow() };
        }, cancellationToken);

    /// <summary>Restores a durable archived thread at the expected revision without starting execution.</summary>
    public Task<CodexThreadDocument> UnarchiveAsync(Guid threadId, long expectedRevision, CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, thread =>
        {
            RequireDurableThread(thread);
            if (thread.ArchivedAt is null) throw new ArgumentException("Thread is not archived.");
            RequireSettledArchiveWriter(thread);
            return thread with { ArchivedAt = null };
        }, cancellationToken);

    internal static CodexThreadDocument RequireUnarchived(CodexThreadDocument thread)
        => thread.ArchivedAt is null ? thread : throw new ArgumentException("Thread is archived; unarchive it first.");

    internal static void RequireDurableThread(CodexThreadDocument thread)
    {
        if (thread.Settings.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind == JsonValueKind.True)
            throw new ArgumentException("Ephemeral threads cannot be archived or restored.");
    }

    private static void RequireSettledArchiveWriter(CodexThreadDocument thread)
    {
        if (thread.Turns.LastOrDefault()?.Status == "inProgress" || HasOpenRealtime(thread))
            throw new InvalidOperationException("Thread has an active or unsettled execution owner.");
    }

    internal static bool HasOpenRealtime(CodexThreadDocument thread)
        => thread.RealtimeSessions?.Any(session => session.State != "ended") == true;
}
