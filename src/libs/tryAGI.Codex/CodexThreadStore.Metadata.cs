namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Persists an explicit display name using the same revision fence as the thread journal.</summary>
    public Task<CodexThreadDocument> SetNameAsync(Guid threadId, long expectedRevision, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Trim();
        if (normalized.Length == 0) throw new ArgumentException("Thread name must not be empty.", nameof(name));
        return ChangeAsync(threadId, expectedRevision, thread => thread with { Name = normalized }, cancellationToken);
    }
}
