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

    internal static CodexThreadCompactionCandidate? CaptureActiveCompaction(CodexThreadDocument before,
        Guid turnId, string owner, CodexResponseOptions settings)
    {
        if (CodexThreadCompaction.CaptureActiveBoundary(before, turnId, owner) is not { } boundary) return null;
        var scope = CodexThreadCompaction.ScopeHash(settings);
        var history = CodexThreadCompaction.Project(before, before.CompactionCheckpoint, settings.Model, scope,
            before.Turns.Length, currentTurnId: turnId, includeActiveOutput: true);
        return new(before, settings.Model, scope, history, boundary);
    }

    // Inline compaction reserves and settles one inference receipt through the same
    // execution journal. Only these two writes may advance the captured revision.
    internal async Task<CodexThreadDocument> InstallActiveCompactionAsync(CodexThreadCompactionCandidate candidate,
        CodexCompactionResult result, string compactionCallId, string owner,
        CodexRetainedContextOptions? retention, CancellationToken ct)
    {
        var boundary = candidate.ActiveBoundary ?? throw new ArgumentException("Expected active compaction candidate.");
        var before = await CurrentAsync(candidate.ThreadId, checked(candidate.Revision + 2), ct).ConfigureAwait(false);
        var turn = Active(before, boundary.TurnId, owner);
        if (before.Turns.Length != candidate.ThroughTurnCount + 1 ||
            turn.Input.Length != boundary.InputCount || turn.Output.Length != boundary.OutputCount ||
            turn.Dispatches.Length != boundary.DispatchCount + 1 ||
            turn.Dispatches[^1] is not { Kind: "inference", State: "succeeded", Result: { } receipt } dispatch ||
            dispatch.CallId != compactionCallId ||
            CodexRealtimeProtocol.Text(receipt, "id") != result.ResponseId ||
            !CodexThreadCompaction.MatchesPrefix(before, candidate.ThroughTurnCount, candidate.HistorySha256, boundary))
            throw new CodexThreadConflictException();
        var actual = CodexThreadCompaction.Project(before, before.CompactionCheckpoint, candidate.Model, candidate.ScopeSha256,
            before.Turns.Length, currentTurnId: boundary.TurnId, includeActiveOutput: true);
        if (!SameHistory(actual, candidate.History)) throw new CodexThreadConflictException();
        var now = time.GetUtcNow();
        var checkpoint = CodexThreadCompaction.BuildCheckpoint(candidate, result, retention, now);
        var next = before with { CompactionCheckpoint = checkpoint, Format = 2, Revision = checked(before.Revision + 1), UpdatedAt = now };
        CodexThreadCompaction.Validate(next, checkpoint);
        ct.ThrowIfCancellationRequested();
        return await PublishAsync(before.Revision, next, ct).ConfigureAwait(false);
    }

    internal static bool SameHistory(IReadOnlyList<JsonElement> left, IReadOnlyList<JsonElement> right)
        => left.Count == right.Count && !left.Where((item, index) => !JsonElement.DeepEquals(item, right[index])).Any();

    /// <summary>Atomically install a validated compaction response if the exact captured thread revision and history still match.
    /// A revision conflict or cancellation does not authorize replaying the provider request. Inspect the stored response ID
    /// after an uncertain storage write. The original journal and dispatch receipts remain unchanged.</summary>
    public async Task<CodexThreadDocument> InstallCompactionAsync(CodexThreadCompactionCandidate candidate,
        CodexCompactionResult result, CodexRetainedContextOptions? retention = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(result);
        if (candidate.ActiveBoundary is not null) throw new ArgumentException("Active checkpoints require the settled inference receipt.", nameof(candidate));
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

    /// <summary>Adopt a speculative closed-prefix compaction at an idle turn boundary.
    /// Appended turns are preserved. Changed captured history, effective settings, account,
    /// or an intervening checkpoint invalidate the candidate. A live execution must not be displaced.</summary>
    public async Task<CodexThreadDocument> InstallBackgroundCompactionAsync(CodexThreadCompactionCandidate candidate,
        CodexCompactionResult result, CodexResponseOptions currentSettings, long expectedRevision,
        CodexRetainedContextOptions? retention = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(currentSettings);
        if (candidate.ActiveBoundary is not null)
            throw new ArgumentException("Background adoption requires a closed prefix.", nameof(candidate));
        var before = await CurrentAsync(candidate.ThreadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (!CanAdoptBackgroundCompaction(before, candidate, currentSettings))
            throw new CodexThreadConflictException();
        var now = time.GetUtcNow();
        var checkpoint = CodexThreadCompaction.BuildCheckpoint(candidate, result, retention, now);
        var next = before with { CompactionCheckpoint = checkpoint, Format = 2, Revision = checked(before.Revision + 1), UpdatedAt = now };
        CodexThreadCompaction.Validate(next, checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        return await PublishAsync(before.Revision, next, cancellationToken).ConfigureAwait(false);
    }

    internal static bool CanAdoptBackgroundCompaction(CodexThreadDocument before, CodexThreadCompactionCandidate candidate,
        CodexResponseOptions currentSettings)
    {
        if (candidate.ActiveBoundary is not null || before.Id != candidate.ThreadId ||
            before.Revision < candidate.Revision || before.AccountId != candidate.AccountId ||
            before.Turns.Any(turn => turn.Status == "inProgress") ||
            !JsonElement.DeepEquals(before.Settings, candidate.CapturedSettings) ||
            currentSettings.Model != candidate.Model || CodexThreadCompaction.ScopeHash(currentSettings) != candidate.ScopeSha256 ||
            before.CompactionCheckpoint?.SourceRevision != candidate.PriorCheckpointRevision ||
            before.CompactionCheckpoint?.ResponseId != candidate.PriorCheckpointResponseId ||
            !CodexThreadCompaction.MatchesPrefix(before, candidate.ThroughTurnCount, candidate.HistorySha256, null))
            return false;
        var actual = CodexThreadCompaction.Project(before, before.CompactionCheckpoint, candidate.Model,
            candidate.ScopeSha256, candidate.ThroughTurnCount);
        return SameHistory(actual, candidate.History);
    }

    // A late durable receipt or history correction may change a covered prefix. Drop only
    // the derived projection in the same journal write, never the new receipt or original turns.
    private static CodexThreadDocument InvalidateChangedCompactionPrefix(CodexThreadDocument document)
    {
        if (document.CompactionCheckpoint is not { } checkpoint) return document;
        if (checkpoint.ThreadId != document.Id ||
            !CodexThreadCompaction.MatchesPrefix(document, checkpoint.ThroughTurnCount, checkpoint.HistorySha256, checkpoint.ActiveBoundary))
            return document with { CompactionCheckpoint = null };
        return document;
    }
}
