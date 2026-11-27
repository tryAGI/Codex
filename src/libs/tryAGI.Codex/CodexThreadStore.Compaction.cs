using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Capture the exact closed native history and tool fence for a compaction attempt.
    /// Callers may run the attempt in the background; capture itself does not run a model.</summary>
    public async Task<CodexThreadCompactionCandidate> CaptureCompactionAsync(Guid threadId, long expectedRevision,
        CodexResponseOptions settings, CancellationToken cancellationToken = default)
    {
        var before = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        return CaptureCompaction(before, settings);
    }

    internal static CodexThreadCompactionCandidate CaptureCompaction(CodexThreadDocument before, CodexResponseOptions settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Instructions);
        if (before.Turns.Length == 0 || before.Turns.Any(turn => turn.Status == "inProgress"))
            throw new InvalidOperationException("Native compaction requires a closed nonempty turn prefix.");
        var scope = CodexThreadCompaction.ScopeHash(settings);
        var history = CodexThreadCompaction.Project(before, before.CompactionCheckpoint, settings.Model, scope, before.Turns.Length);
        return new(before, settings.Model, scope, history);
    }

    /// <summary>Atomically install a validated compaction response if the exact captured thread revision and history still match.
    /// A revision conflict or cancellation does not authorize replaying the provider request. Inspect the stored response ID
    /// after an uncertain storage write. The original journal and dispatch receipts remain unchanged.</summary>
    public async Task<CodexThreadDocument> InstallCompactionAsync(CodexThreadCompactionCandidate candidate,
        CodexCompactionResult result, CodexRetainedContextOptions? retention = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(result);
        var before = await CurrentAsync(candidate.ThreadId, candidate.Revision, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (before.Turns.Length != candidate.ThroughTurnCount ||
            CodexThreadCompaction.PrefixHash(before, candidate.ThroughTurnCount) != candidate.HistorySha256)
            throw new CodexThreadConflictException();
        var actual = CodexThreadCompaction.Project(before, before.CompactionCheckpoint, candidate.Model, candidate.ScopeSha256, before.Turns.Length);
        if (actual.Count != candidate.History.Count ||
            actual.Where((item, index) => !JsonElement.DeepEquals(item, candidate.History[index])).Any())
            throw new CodexThreadConflictException();
        var now = time.GetUtcNow();
        var checkpoint = CodexThreadCompaction.BuildCheckpoint(candidate, result, retention, now);
        var next = before with { CompactionCheckpoint = checkpoint, Format = 2, Revision = checked(before.Revision + 1), UpdatedAt = now };
        CodexThreadCompaction.Validate(next, checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        return await PublishAsync(before.Revision, next, cancellationToken).ConfigureAwait(false);
    }

    // A late durable receipt or history correction may change a covered prefix. Drop only
    // the derived projection in the same journal write, never the new receipt or original turns.
    private static CodexThreadDocument InvalidateChangedCompactionPrefix(CodexThreadDocument document)
    {
        if (document.CompactionCheckpoint is not { } checkpoint) return document;
        if (checkpoint.ThreadId != document.Id || checkpoint.ThroughTurnCount > document.Turns.Length ||
            document.Turns.Take(checkpoint.ThroughTurnCount).Any(turn => turn.Status == "inProgress") ||
            CodexThreadCompaction.PrefixHash(document, checkpoint.ThroughTurnCount) != checkpoint.HistorySha256)
            return document with { CompactionCheckpoint = null };
        return document;
    }
}
