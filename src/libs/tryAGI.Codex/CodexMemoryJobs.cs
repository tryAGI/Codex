using System.Text;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Account/version-bound memory job identity.</summary>
/// <param name="Scope">Authenticated memory namespace.</param>
/// <param name="ThreadId">Stage-1 thread, or null for the singleton phase-2 job.</param>
public sealed record CodexMemoryJobKey(CodexMemoryScope Scope, Guid? ThreadId);

/// <summary>Durable memory job lifecycle.</summary>
public enum CodexMemoryJobStatus
{
    /// <summary>Awaiting a claim.</summary>
    Pending,
    /// <summary>Owned by one fenced worker.</summary>
    Running,
    /// <summary>Completed successfully.</summary>
    Succeeded,
    /// <summary>Failed with a retry policy.</summary>
    Failed,
}

/// <summary>Persisted metadata for a memory job, separate from original reasoning history.</summary>
/// <param name="Revision">Host CAS revision.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="InputWatermark">Latest input watermark, monotonic across enqueues.</param>
/// <param name="ClaimedWatermark">Immutable input snapshot held by the current owner.</param>
/// <param name="LastSuccessWatermark">Latest successful extraction or consolidation watermark.</param>
/// <param name="RetryRemaining">Remaining automatic stage-1 retries.</param>
/// <param name="RetryAt">Earliest permitted retry.</param>
/// <param name="FinishedAt">Last terminal timestamp, retained across phase-2 enqueues.</param>
/// <param name="WorkerId">Current worker identity.</param>
/// <param name="OwnershipToken">Current fencing token.</param>
/// <param name="LeaseUntil">Lease expiration.</param>
/// <param name="LastError">Bounded failure classification, not a provider secret.</param>
public sealed record CodexMemoryJobState(long Revision, CodexMemoryJobStatus Status, long InputWatermark,
    long? ClaimedWatermark, long? LastSuccessWatermark, int RetryRemaining, DateTimeOffset? RetryAt,
    DateTimeOffset? FinishedAt, Guid? WorkerId, Guid? OwnershipToken, DateTimeOffset? LeaseUntil, string? LastError);

/// <summary>One consistent metadata-only observation of the job and current source.</summary>
/// <param name="Key">Exact job identity.</param>
/// <param name="State">Existing job state, or null.</param>
/// <param name="SourceEnabled">Stage-1 source is currently permitted and not polluted.</param>
/// <param name="SourceWatermark">Stage-1 source revision; null for phase 2.</param>
/// <param name="OutputWatermark">Existing stage-1 output revision, if any.</param>
/// <param name="ResetEpoch">Namespace reset generation; zero for legacy observations.</param>
/// <param name="SourceModeRevision">Independent thread memory-mode generation.</param>
public sealed record CodexMemoryJobObservation(CodexMemoryJobKey Key, CodexMemoryJobState? State,
    bool SourceEnabled, long? SourceWatermark, long? OutputWatermark, long ResetEpoch = 0, long SourceModeRevision = 0);

/// <summary>Generated stage-1 output for one exact source revision.</summary>
/// <param name="SourceWatermark">Claimed source revision.</param>
/// <param name="RawMemory">Extracted memory text.</param>
/// <param name="RolloutSummary">Summary of the immutable source.</param>
/// <param name="RolloutSlug">Optional artifact naming hint.</param>
public sealed record CodexMemoryExtractionOutput(long SourceWatermark, string RawMemory, string RolloutSummary,
    string? RolloutSlug = null);

/// <summary>One exact stage-1 snapshot used by consolidation.</summary>
/// <param name="ThreadId">Source thread.</param>
/// <param name="SourceWatermark">Selected stage-1 output watermark.</param>
public sealed record CodexMemorySelectedSnapshot(Guid ThreadId, long SourceWatermark);

/// <summary>Immutable output reference and exact baseline for successful consolidation.</summary>
/// <param name="ArtifactRevision">Host-verified immutable generated artifact revision.</param>
/// <param name="Selected">Exact input snapshots actually used.</param>
public sealed record CodexMemoryConsolidationOutput(string ArtifactRevision, IReadOnlyList<CodexMemorySelectedSnapshot> Selected);

/// <summary>Kind of one atomic job mutation.</summary>
public enum CodexMemoryJobChangeKind
{
    /// <summary>Claim or take over an expired job.</summary>
    Claim,
    /// <summary>Renew an owned phase-2 lease.</summary>
    Heartbeat,
    /// <summary>Record failure and retry policy.</summary>
    Fail,
    /// <summary>Install or delete extraction output and enqueue phase 2 when needed.</summary>
    CompleteStage1,
    /// <summary>Install consolidated artifact pointer and exact selected baseline.</summary>
    CompletePhase2,
    /// <summary>Advance the singleton phase-2 input watermark.</summary>
    EnqueuePhase2,
}

/// <summary>
/// An immutable prepared CAS. Retain the exact mutation and operation ID across ambiguous
/// outcomes; never refresh a preimage and reuse its operation ID.
/// </summary>
/// <param name="Key">Exact job identity.</param>
/// <param name="OperationId">Stable host-supplied idempotency identity.</param>
/// <param name="Kind">Atomic transition kind.</param>
/// <param name="ExpectedRevision">Observed CAS revision; null means insert only.</param>
/// <param name="Next">Proposed next job state.</param>
/// <param name="ExpectedSourceWatermark">Optional stage-1 source/policy fence checked at commit.</param>
/// <param name="MaximumRunningClaims">Optional scope-wide atomic stage-1 concurrency cap.</param>
/// <param name="Extraction">Stage-1 output; null on CompleteStage1 means no output.</param>
/// <param name="Consolidation">Phase-2 exact artifact and selection.</param>
/// <param name="ExpectedWorkerId">Expected current worker for an owned transition.</param>
/// <param name="ExpectedOwnershipToken">Expected current lease fence for an owned transition.</param>
/// <param name="ResetEpoch">Exact observed namespace reset generation.</param>
/// <param name="SourceModeRevision">Exact observed thread memory-mode generation.</param>
public sealed record CodexMemoryJobChange(CodexMemoryJobKey Key, Guid OperationId, CodexMemoryJobChangeKind Kind,
    long? ExpectedRevision, CodexMemoryJobState Next, long? ExpectedSourceWatermark = null,
    int? MaximumRunningClaims = null, CodexMemoryExtractionOutput? Extraction = null,
    CodexMemoryConsolidationOutput? Consolidation = null, Guid? ExpectedWorkerId = null,
    Guid? ExpectedOwnershipToken = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long ResetEpoch = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long SourceModeRevision = 0);

/// <summary>Claim planning outcome before the storage CAS.</summary>
public enum CodexMemoryJobClaimOutcome
{
    /// <summary>A prepared claim must still commit before execution.</summary>
    Prepared,
    /// <summary>Source is disabled, polluted or unavailable.</summary>
    Disabled,
    /// <summary>Stage-1 output or previous success covers the requested revision.</summary>
    UpToDate,
    /// <summary>Another worker holds a fresh lease.</summary>
    Running,
    /// <summary>Retry backoff is active.</summary>
    RetryBackoff,
    /// <summary>Stage-1 retry budget is exhausted for this input.</summary>
    RetryExhausted,
    /// <summary>Phase-2 success cooldown is active.</summary>
    Cooldown,
    /// <summary>Source watermark would regress.</summary>
    StaleSource,
}

/// <summary>Result of planning a claim; Prepared is not yet a granted lease.</summary>
/// <param name="Outcome">Planning decision.</param>
/// <param name="Change">Exact mutation for a prepared claim.</param>
public sealed record CodexMemoryJobClaim(CodexMemoryJobClaimOutcome Outcome, CodexMemoryJobChange? Change = null);

/// <summary>Storage settlement of one atomic mutation.</summary>
public enum CodexMemoryJobSettlement
{
    /// <summary>Committed, or exact idempotent replay of a previously committed mutation.</summary>
    Applied,
    /// <summary>Known CAS conflict without mutation.</summary>
    Conflict,
    /// <summary>Known concurrency-cap rejection without mutation.</summary>
    Capacity,
    /// <summary>Known authorization or source-policy rejection without mutation.</summary>
    Denied,
}

/// <summary>Durable receipt for the exact operation.</summary>
/// <param name="Key">Job identity.</param>
/// <param name="OperationId">Original operation identity.</param>
/// <param name="Settlement">Definitive atomic result.</param>
/// <param name="CommittedRevision">New job revision when Applied; otherwise null.</param>
public sealed record CodexMemoryJobReceipt(CodexMemoryJobKey Key, Guid OperationId,
    CodexMemoryJobSettlement Settlement, long? CommittedRevision);

/// <summary>
/// Durable transactional memory scheduler adapter. Implementations must authenticate the scope,
/// enforce current memory permissions and compare the observed revision, and retain operation
/// receipts atomically with the mutation. The native thread store remains authoritative history.
/// </summary>
public interface ICodexMemoryJobStorage
{
    /// <summary>Reads only job/source/output metadata; do not materialize original rollout history.</summary>
    ValueTask<CodexMemoryJobObservation> ReadAsync(CodexMemoryJobKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically settles one exact mutation. Deduplicate identical operation IDs before CAS and
    /// reject reused IDs with different contents. Check stage-1 source eligibility/watermark and
    /// running-job cap in the same transaction. Owned transitions additionally require the
    /// exact authenticated worker, ownership token and an unexpired lease at commit. Stage-1 completion installs/deletes the output
    /// and advances the global input watermark when output changes (including deletion);
    /// never overwrite a newer output. Phase-2 completion publishes the verified immutable
    /// artifact pointer and marks only exact still-eligible selected source revisions. A receipt
    /// and all effects must commit together. Unknown commit outcomes throw, never return Conflict.
    /// </summary>
    ValueTask<CodexMemoryJobReceipt> ApplyAsync(CodexMemoryJobChange change, CancellationToken cancellationToken);

    /// <summary>
    /// Recovers an exact durable receipt after a crash or lost response. Null is unknown;
    /// absence does not authorize dispatching extraction or changing the original operation.
    /// </summary>
    ValueTask<CodexMemoryJobReceipt?> ReadReceiptAsync(CodexMemoryJobKey key, Guid operationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Native, host-authorized memory job planner and CAS coordinator. Planning is side-effect-free
/// over one observed revision; apply never automatically rereads, retries or calls a model.
/// </summary>
public sealed class CodexMemoryJobs
{
    private const int DefaultRetries = 3;
    private static readonly TimeSpan Phase2Cooldown = TimeSpan.FromHours(6);
    private readonly CodexMemoryScope scope;
    private readonly ICodexMemoryJobStorage storage;
    private readonly Func<CodexMemoryJobKey, CancellationToken, ValueTask> authorize;
    private readonly TimeProvider clock;

    /// <summary>Creates a scheduler for one explicitly authorized memory namespace.</summary>
    public CodexMemoryJobs(CodexMemoryScope scope, ICodexMemoryJobStorage storage,
        Func<CodexMemoryJobKey, CancellationToken, ValueTask> authorize, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AccountId);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorize);
        if (scope.Version is not (CodexMemoryVersion.V1 or CodexMemoryVersion.V2))
            throw new ArgumentOutOfRangeException(nameof(scope));
        this.scope = scope;
        this.storage = storage;
        this.authorize = authorize;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Obtains one exact observed revision after authorization.</summary>
    public async ValueTask<CodexMemoryJobObservation> ReadAsync(CodexMemoryJobKey key, CancellationToken cancellationToken = default)
    {
        CheckKey(key);
        await authorize(key, cancellationToken).ConfigureAwait(false);
        var observation = await storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (observation.Key != key) throw new InvalidDataException("Memory job identity mismatch.");
        CheckObservation(observation);
        return observation;
    }

    /// <summary>Plans a stage-1 or singleton phase-2 claim without touching storage.</summary>
    public CodexMemoryJobClaim PrepareClaim(CodexMemoryJobObservation observation, Guid workerId,
        Guid operationId, TimeSpan lease, int maximumRunningClaims = 10)
    {
        CheckObservation(observation);
        CheckIds(workerId, operationId);
        CheckLease(lease);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRunningClaims, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRunningClaims, 1000);
        var stage1 = observation.Key.ThreadId is not null;
        var state = observation.State;
        var now = clock.GetUtcNow();
        if (stage1 && (!observation.SourceEnabled || observation.SourceWatermark is null))
            return new(CodexMemoryJobClaimOutcome.Disabled);
        var input = stage1 ? observation.SourceWatermark!.Value : state?.InputWatermark ?? 0;
        if (stage1 && (observation.OutputWatermark >= input || state?.LastSuccessWatermark >= input))
            return new(CodexMemoryJobClaimOutcome.UpToDate);
        if (stage1 && state is not null && input < state.InputWatermark)
            return new(CodexMemoryJobClaimOutcome.StaleSource);
        if (state?.Status == CodexMemoryJobStatus.Running && state.LeaseUntil > now)
            return new(CodexMemoryJobClaimOutcome.Running);
        var newer = state is null || input > state.InputWatermark;
        if ((!stage1 || !newer) && state?.RetryAt > now)
            return new(CodexMemoryJobClaimOutcome.RetryBackoff);
        if (stage1 && !newer && state!.RetryRemaining <= 0)
            return new(CodexMemoryJobClaimOutcome.RetryExhausted);
        if (!stage1 && state is { LastError: null, FinishedAt: { } finished } && now - finished < Phase2Cooldown)
            return new(CodexMemoryJobClaimOutcome.Cooldown);

        var next = new CodexMemoryJobState(NextRevision(state), CodexMemoryJobStatus.Running, input, input,
            state?.LastSuccessWatermark, newer ? DefaultRetries : state!.RetryRemaining,
            null, null, workerId, operationId, now + lease, null);
        return new(CodexMemoryJobClaimOutcome.Prepared, new(observation.Key, operationId, CodexMemoryJobChangeKind.Claim,
            state?.Revision, next, stage1 ? input : null, stage1 ? maximumRunningClaims : null,
            ResetEpoch: observation.ResetEpoch, SourceModeRevision: observation.SourceModeRevision));
    }

    /// <summary>Plans a lease extension for the owned global phase-2 job.</summary>
    public CodexMemoryJobChange PrepareHeartbeat(CodexMemoryJobObservation observation, Guid workerId,
        Guid ownershipToken, Guid operationId, TimeSpan lease)
    {
        CheckLease(lease);
        if (observation.Key.ThreadId is not null) throw new ArgumentException("Only phase 2 supports heartbeat.");
        var now = clock.GetUtcNow();
        var state = RequireOwner(observation, workerId, ownershipToken, operationId, now);
        return Change(observation, operationId, CodexMemoryJobChangeKind.Heartbeat,
            state with { Revision = NextRevision(state), LeaseUntil = now + lease });
    }

    /// <summary>Plans one owned failure, consuming one retry and recording a bounded backoff.</summary>
    public CodexMemoryJobChange PrepareFailure(CodexMemoryJobObservation observation, Guid workerId,
        Guid ownershipToken, Guid operationId, string failureCode, TimeSpan retryDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > 128 || failureCode.Any(char.IsControl) ||
            retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromDays(7))
            throw new ArgumentException("Invalid memory failure classification or delay.");
        var now = clock.GetUtcNow();
        var state = RequireOwner(observation, workerId, ownershipToken, operationId, now);
        return Change(observation, operationId, CodexMemoryJobChangeKind.Fail,
            Terminal(state, CodexMemoryJobStatus.Failed, now) with
            {
                RetryRemaining = Math.Max(0, state.RetryRemaining - 1), RetryAt = now + retryDelay,
                LastError = failureCode,
            });
    }

    /// <summary>Plans atomic stage-1 success, optionally deleting a previous output.</summary>
    public CodexMemoryJobChange PrepareStage1Success(CodexMemoryJobObservation observation, Guid workerId,
        Guid ownershipToken, Guid operationId, CodexMemoryExtractionOutput? output)
    {
        if (observation.Key.ThreadId is null) throw new ArgumentException("Expected a stage-1 job.");
        var now = clock.GetUtcNow();
        var state = RequireOwner(observation, workerId, ownershipToken, operationId, now);
        var watermark = state.ClaimedWatermark!.Value;
        if (!observation.SourceEnabled || observation.SourceWatermark != watermark ||
            observation.OutputWatermark > watermark)
            throw new InvalidOperationException("Memory source changed or is no longer eligible.");
        if (output is not null)
        {
            if (output.SourceWatermark != watermark ||
                (long)Encoding.UTF8.GetByteCount(output.RawMemory) + Encoding.UTF8.GetByteCount(output.RolloutSummary) >
                    CodexMemoryBackend.MaximumDocumentBytes || output.RolloutSlug?.Length > 256)
                throw new ArgumentException("Extraction output does not match the claim or exceeds its bound.", nameof(output));
        }
        var next = Terminal(state, CodexMemoryJobStatus.Succeeded, now) with { LastSuccessWatermark = watermark };
        return new(observation.Key, operationId, CodexMemoryJobChangeKind.CompleteStage1, state.Revision, next,
            watermark, Extraction: output, ExpectedWorkerId: workerId, ExpectedOwnershipToken: ownershipToken,
            ResetEpoch: observation.ResetEpoch, SourceModeRevision: observation.SourceModeRevision);
    }

    /// <summary>Plans successful phase-2 publication against its claimed input watermark.</summary>
    public CodexMemoryJobChange PreparePhase2Success(CodexMemoryJobObservation observation, Guid workerId,
        Guid ownershipToken, Guid operationId, CodexMemoryConsolidationOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.ArtifactRevision);
        ArgumentNullException.ThrowIfNull(output.Selected);
        if (observation.Key.ThreadId is not null || output.ArtifactRevision.Length > 4096 ||
            output.Selected.Count > 10_000 || output.Selected.Any(x => x.ThreadId == Guid.Empty || x.SourceWatermark < 0) ||
            output.Selected.Select(x => x.ThreadId).Distinct().Count() != output.Selected.Count)
            throw new ArgumentException("Invalid consolidation artifact or selection.", nameof(output));
        var now = clock.GetUtcNow();
        var state = RequireOwner(observation, workerId, ownershipToken, operationId, now);
        var next = Terminal(state, CodexMemoryJobStatus.Succeeded, now) with
        {
            LastSuccessWatermark = Math.Max(state.LastSuccessWatermark ?? 0, state.ClaimedWatermark!.Value),
        };
        return new(observation.Key, operationId, CodexMemoryJobChangeKind.CompletePhase2, state.Revision, next,
            Consolidation: output with { Selected = output.Selected.ToArray() },
            ExpectedWorkerId: workerId, ExpectedOwnershipToken: ownershipToken,
            ResetEpoch: observation.ResetEpoch, SourceModeRevision: observation.SourceModeRevision);
    }

    /// <summary>Plans a monotonic global enqueue without stealing a running lease.</summary>
    public CodexMemoryJobChange PrepareEnqueue(CodexMemoryJobObservation observation, Guid operationId, long watermark)
    {
        CheckObservation(observation);
        if (observation.Key.ThreadId is not null || operationId == Guid.Empty || watermark < 0)
            throw new ArgumentException("Invalid global memory enqueue.");
        var state = observation.State;
        var next = state is null
            ? new CodexMemoryJobState(1, CodexMemoryJobStatus.Pending, watermark, null, null, DefaultRetries,
                null, null, null, null, null, null)
            : state with
            {
                Revision = NextRevision(state),
                InputWatermark = watermark > state.InputWatermark ? watermark : checked(state.InputWatermark + 1),
                Status = state.Status == CodexMemoryJobStatus.Running ? state.Status : CodexMemoryJobStatus.Pending,
                RetryAt = state.Status == CodexMemoryJobStatus.Running ? state.RetryAt : null,
                RetryRemaining = Math.Max(state.RetryRemaining, DefaultRetries),
            };
        return new(observation.Key, operationId, CodexMemoryJobChangeKind.EnqueuePhase2, state?.Revision, next,
            ResetEpoch: observation.ResetEpoch, SourceModeRevision: observation.SourceModeRevision);
    }

    /// <summary>
    /// Attempts the exact prepared mutation once. A definitive receipt settles it; exceptions
    /// remain ambiguous and must be reconciled with the same operation ID.
    /// </summary>
    public async ValueTask<CodexMemoryJobReceipt> ApplyAsync(CodexMemoryJobChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        CheckKey(change.Key);
        var owned = change.Kind is CodexMemoryJobChangeKind.Heartbeat or CodexMemoryJobChangeKind.Fail or
            CodexMemoryJobChangeKind.CompleteStage1 or CodexMemoryJobChangeKind.CompletePhase2;
        if (change.OperationId == Guid.Empty || change.ExpectedRevision is <= 0 ||
            change.ResetEpoch < 0 || change.SourceModeRevision < 0 ||
            change.Next.Revision != checked((change.ExpectedRevision ?? 0) + 1) ||
            (owned && (change.ExpectedWorkerId is null || change.ExpectedWorkerId == Guid.Empty ||
                change.ExpectedOwnershipToken is null || change.ExpectedOwnershipToken == Guid.Empty ||
                change.ExpectedOwnershipToken == change.OperationId)) ||
            (!owned && (change.ExpectedWorkerId is not null || change.ExpectedOwnershipToken is not null)))
            throw new ArgumentException("Invalid memory mutation identity or revision.", nameof(change));
        await authorize(change.Key, cancellationToken).ConfigureAwait(false);
        var receipt = await storage.ApplyAsync(change, cancellationToken).ConfigureAwait(false);
        VerifyReceipt(receipt, change.Key, change.OperationId);
        if (receipt.Settlement == CodexMemoryJobSettlement.Applied && receipt.CommittedRevision != change.Next.Revision)
            throw new InvalidDataException("Memory mutation receipt revision mismatch.");
        return receipt;
    }

    /// <summary>Reads a receipt without reading or refreshing the job's current preimage.</summary>
    public async ValueTask<CodexMemoryJobReceipt?> ReadReceiptAsync(CodexMemoryJobKey key, Guid operationId,
        CancellationToken cancellationToken = default)
    {
        CheckKey(key);
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid operation identity.", nameof(operationId));
        await authorize(key, cancellationToken).ConfigureAwait(false);
        var receipt = await storage.ReadReceiptAsync(key, operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null) VerifyReceipt(receipt, key, operationId);
        return receipt;
    }

    private void CheckKey(CodexMemoryJobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Scope != scope || key.ThreadId == Guid.Empty)
            throw new UnauthorizedAccessException("Memory job is outside the authorized namespace.");
    }

    private void CheckObservation(CodexMemoryJobObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        CheckKey(observation.Key);
        if (observation.SourceWatermark is < 0 || observation.OutputWatermark is < 0 ||
            observation.ResetEpoch < 0 || observation.SourceModeRevision < 0)
            throw new InvalidDataException("Invalid memory source watermark.");
        if (observation.State is { } state &&
            (state.Revision < 1 || state.InputWatermark < 0 || state.RetryRemaining < 0 ||
             state.ClaimedWatermark is < 0 || state.ClaimedWatermark > state.InputWatermark ||
             state.LastSuccessWatermark is < 0 || state.LastSuccessWatermark > state.InputWatermark ||
             state.Status is not (CodexMemoryJobStatus.Pending or CodexMemoryJobStatus.Running or
                 CodexMemoryJobStatus.Succeeded or CodexMemoryJobStatus.Failed) ||
             (state.Status == CodexMemoryJobStatus.Running &&
                (state.WorkerId is null || state.WorkerId == Guid.Empty || state.OwnershipToken is null ||
                 state.OwnershipToken == Guid.Empty || state.LeaseUntil is null || state.ClaimedWatermark is null))))
            throw new InvalidDataException("Invalid memory job state.");
    }

    private CodexMemoryJobState RequireOwner(CodexMemoryJobObservation observation, Guid workerId,
        Guid ownershipToken, Guid operationId, DateTimeOffset now)
    {
        CheckObservation(observation);
        CheckIds(workerId, operationId);
        if (ownershipToken == Guid.Empty || ownershipToken == operationId ||
            observation.State is not { Status: CodexMemoryJobStatus.Running } state ||
            state.WorkerId != workerId || state.OwnershipToken != ownershipToken || state.LeaseUntil <= now)
            throw new InvalidOperationException("Memory job lease is stale, expired or foreign.");
        return state;
    }

    private static void CheckIds(Guid workerId, Guid operationId)
    {
        if (workerId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("Worker and operation identities must be nonempty.");
    }

    private static void CheckLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(lease));
    }

    private static long NextRevision(CodexMemoryJobState? state) => checked((state?.Revision ?? 0) + 1);

    private static CodexMemoryJobState Terminal(CodexMemoryJobState state, CodexMemoryJobStatus status, DateTimeOffset now) =>
        state with
        {
            Revision = NextRevision(state), Status = status, ClaimedWatermark = null, WorkerId = null,
            OwnershipToken = null, LeaseUntil = null, FinishedAt = now, RetryAt = null, LastError = null,
        };

    private static CodexMemoryJobChange Change(CodexMemoryJobObservation observation, Guid operationId,
        CodexMemoryJobChangeKind kind, CodexMemoryJobState next) =>
        new(observation.Key, operationId, kind, observation.State?.Revision, next,
            ExpectedWorkerId: observation.State?.WorkerId, ExpectedOwnershipToken: observation.State?.OwnershipToken,
            ResetEpoch: observation.ResetEpoch, SourceModeRevision: observation.SourceModeRevision);

    private static void VerifyReceipt(CodexMemoryJobReceipt receipt, CodexMemoryJobKey key, Guid operationId)
    {
        if (receipt.Key != key || receipt.OperationId != operationId ||
            receipt.Settlement is not (CodexMemoryJobSettlement.Applied or CodexMemoryJobSettlement.Conflict or
                CodexMemoryJobSettlement.Capacity or CodexMemoryJobSettlement.Denied) ||
            (receipt.Settlement == CodexMemoryJobSettlement.Applied
                ? receipt.CommittedRevision is null or <= 0 : receipt.CommittedRevision is not null))
            throw new InvalidDataException("Memory job receipt identity or settlement mismatch.");
    }
}
