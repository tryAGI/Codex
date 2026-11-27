namespace tryAGI.Codex;

/// <summary>Trusted source metadata for event-driven startup discovery; never a reason to read the rollout.</summary>
/// <param name="UpdatedAt">Authoritative time of the last source update.</param>
/// <param name="SourceKind">Canonical Rust session-source classification supplied by the host.</param>
/// <param name="Archived">Whether the source is archived and excluded from startup discovery.</param>
public sealed record CodexMemorySourceDiscovery(DateTimeOffset UpdatedAt, string SourceKind, bool Archived = false)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceKind);
        if (SourceKind.Length > 128 || SourceKind.Any(char.IsControl))
            throw new ArgumentException("Invalid memory source classification.");
    }
}

/// <summary>Exact metadata-only startup scan.</summary>
/// <param name="Scope">Authenticated account/version namespace.</param>
/// <param name="CurrentThreadId">Thread opening the session; excluded from candidates and used as worker identity.</param>
/// <param name="EarliestUpdate">Inclusive retention age cutoff.</param>
/// <param name="LatestUpdate">Inclusive idle cutoff.</param>
/// <param name="AllowedSources">Host-approved session sources; an empty set admits no candidates.</param>
/// <param name="ScanLimit">Maximum source candidates before any job or output probe.</param>
public sealed record CodexMemoryStartupScan(CodexMemoryScope Scope, Guid CurrentThreadId,
    DateTimeOffset EarliestUpdate, DateTimeOffset LatestUpdate, IReadOnlyList<string> AllowedSources, int ScanLimit)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(Scope.AccountId);
        ArgumentNullException.ThrowIfNull(AllowedSources);
        if (Scope.Version is not (CodexMemoryVersion.V1 or CodexMemoryVersion.V2) ||
            CurrentThreadId == Guid.Empty || ScanLimit is < 0 or > 5000 || AllowedSources.Count > 32 ||
            AllowedSources.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 128 || s.Any(char.IsControl)) ||
            AllowedSources.Distinct(StringComparer.Ordinal).Count() != AllowedSources.Count)
            throw new ArgumentException("Invalid or unbounded memory startup scan.");
    }

    internal bool Includes(CodexMemoryStartupCandidate candidate) =>
        candidate.ThreadId != Guid.Empty && candidate.ThreadId != CurrentThreadId && candidate.Watermark >= 0 &&
        candidate.UpdatedAt >= EarliestUpdate && candidate.UpdatedAt <= LatestUpdate &&
        AllowedSources.Contains(candidate.SourceKind, StringComparer.Ordinal);
}

/// <summary>One exact source generation, discovered without materializing its history.</summary>
/// <param name="ThreadId">Logical native thread identity.</param>
/// <param name="Watermark">Authoritative extraction input generation.</param>
/// <param name="UpdatedAt">Last source update.</param>
/// <param name="SourceKind">Canonical session-source classification.</param>
public sealed record CodexMemoryStartupCandidate(Guid ThreadId, long Watermark, DateTimeOffset UpdatedAt, string SourceKind);

/// <summary>Indexed, account/version-bound startup candidate source.</summary>
public interface ICodexMemoryStartupSource
{
    /// <summary>
    /// Returns at most ScanLimit active, unarchived, eligible source identities in descending
    /// update order (stable tie-breaker). Apply age/idle/source/current-thread filters before
    /// the limit. Do not load job bodies, extracted content or original history.
    /// </summary>
    ValueTask<IReadOnlyList<CodexMemoryStartupCandidate>> ScanStartupAsync(CodexMemoryStartupScan scan,
        CancellationToken cancellationToken);
}

/// <summary>Host-selected bounded startup policy.</summary>
/// <param name="AllowedSources">Explicit interactive session sources.</param>
/// <param name="MaximumAge">Maximum source age.</param>
/// <param name="MinimumIdle">Minimum inactivity before extraction.</param>
/// <param name="MaximumClaims">Maximum grants in this startup and concurrent running stage-1 cap.</param>
/// <param name="ScanLimit">Maximum candidate metadata probes, up to the Rust 5000-source limit.</param>
/// <param name="Lease">Claim lease duration, default one hour.</param>
public sealed record CodexMemoryStartupPolicy(IReadOnlyList<string> AllowedSources,
    TimeSpan MaximumAge, TimeSpan MinimumIdle, int MaximumClaims = 8, int ScanLimit = 5000,
    TimeSpan? Lease = null);

/// <summary>A definitively committed claim and its immutable source metadata.</summary>
/// <param name="Candidate">Exact candidate generation.</param>
/// <param name="Change">Prepared and committed claim; its operation ID is the ownership token.</param>
public sealed record CodexMemoryStartupClaim(CodexMemoryStartupCandidate Candidate, CodexMemoryJobChange Change)
{
    /// <summary>Creates a one-shot sampling admission for this committed claim.</summary>
    public CodexMemorySamplingAdmission CreateAdmission(Guid attemptId) =>
        new(Change.Key, Change.Next.WorkerId!.Value, Change.OperationId, Candidate.Watermark, attemptId);
}

/// <summary>One completed startup scan and its fresh committed claim grants.</summary>
/// <param name="Scanned">Number of candidate identities returned by the bounded scan.</param>
/// <param name="Probed">Number of candidate job metadata observations actually made.</param>
/// <param name="Claims">Only fresh, definitively committed claims.</param>
public sealed record CodexMemoryStartupBatch(int Scanned, int Probed, IReadOnlyList<CodexMemoryStartupClaim> Claims);

/// <summary>An ambiguous claim settlement; the caller must reconcile the exact operation rather than inventing another claim.</summary>
public sealed class CodexMemoryStartupSettlementException(CodexMemoryStartupBatch preceding,
    CodexMemoryJobChange change, Exception innerException)
    : IOException("Memory startup claim requires exact-operation reconciliation.", innerException)
{
    /// <summary>Already settled claims, which must not be lost or reissued.</summary>
    public CodexMemoryStartupBatch Preceding { get; } = preceding;
    /// <summary>Original claim operation with an unknown outcome.</summary>
    public CodexMemoryJobChange Change { get; } = change;
}

/// <summary>
/// Bounded event-driven startup discovery and stage-1 claiming. Only the selected candidate
/// metadata is observed; no model request or reasoning history read occurs in this coordinator.
/// The host explicitly invokes it at startup or after authoritative source publication.
/// </summary>
public sealed class CodexMemoryStartup(CodexMemoryScope scope, CodexMemoryJobs jobs,
    ICodexMemoryStartupSource source, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>Scans recent idle sources and claims at most the configured number of eligible jobs.</summary>
    public async Task<CodexMemoryStartupBatch> ClaimAsync(Guid currentThreadId, CodexMemoryStartupPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.AllowedSources);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(source);
        var lease = policy.Lease ?? TimeSpan.FromHours(1);
        if (policy.MaximumClaims is < 0 or > 1000 || policy.MaximumAge < TimeSpan.Zero ||
            policy.MaximumAge > TimeSpan.FromDays(36500) || policy.MinimumIdle < TimeSpan.Zero ||
            policy.MinimumIdle > TimeSpan.FromDays(36500) || lease <= TimeSpan.Zero || lease > TimeSpan.FromDays(1))
            throw new ArgumentException("Invalid memory startup policy.", nameof(policy));

        var now = clock.GetUtcNow();
        var scan = new CodexMemoryStartupScan(scope, currentThreadId, now - policy.MaximumAge,
            now - policy.MinimumIdle, policy.AllowedSources.ToArray(), policy.ScanLimit);
        scan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (policy.MaximumClaims == 0 || scan.ScanLimit == 0 || scan.AllowedSources.Count == 0 ||
            scan.EarliestUpdate > scan.LatestUpdate)
            return new(0, 0, []);

        var candidates = (await source.ScanStartupAsync(scan, cancellationToken).ConfigureAwait(false)).ToArray();
        if (candidates.Length > scan.ScanLimit || candidates.Select(c => c.ThreadId).Distinct().Count() != candidates.Length)
            throw new InvalidDataException("Memory startup source exceeded its bounded scan or returned duplicate threads.");
        for (var i = 0; i < candidates.Length; i++)
            if (!scan.Includes(candidates[i]) || i > 0 && candidates[i - 1].UpdatedAt < candidates[i].UpdatedAt)
                throw new InvalidDataException("Memory startup source returned out-of-scope or unordered metadata.");

        var claims = new List<CodexMemoryStartupClaim>();
        var probed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claims.Count >= policy.MaximumClaims) break;
            var key = new CodexMemoryJobKey(scope, candidate.ThreadId);
            var observed = await jobs.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            probed++;
            if (!observed.SourceEnabled || observed.SourceWatermark != candidate.Watermark) continue;
            var planned = jobs.PrepareClaim(observed, currentThreadId, Guid.NewGuid(), lease, policy.MaximumClaims);
            if (planned.Outcome != CodexMemoryJobClaimOutcome.Prepared || planned.Change is not { } change) continue;

            CodexMemoryJobReceipt receipt;
            try { receipt = await jobs.ApplyAsync(change, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new CodexMemoryStartupSettlementException(new(candidates.Length, probed, claims.ToArray()), change, error);
            }
            if (receipt.Settlement == CodexMemoryJobSettlement.Applied) claims.Add(new(candidate, change));
            // Capacity is a scope-wide cap; avoid probing remaining candidate jobs when already full.
            else if (receipt.Settlement == CodexMemoryJobSettlement.Capacity) break;
        }
        return new(candidates.Length, probed, claims.ToArray());
    }
}
