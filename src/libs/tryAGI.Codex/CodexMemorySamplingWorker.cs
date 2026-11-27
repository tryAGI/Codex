using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Exact durable authorization boundary for one sampling attempt under a live claim.</summary>
/// <param name="Key">Account, version and stage identity.</param>
/// <param name="WorkerId">Authorized owner of the current lease.</param>
/// <param name="OwnershipToken">Immutable claim fence.</param>
/// <param name="ClaimedWatermark">Exact source generation held by the claim.</param>
/// <param name="AttemptId">Unique sampling attempt identity, never reused for another claim.</param>
public sealed record CodexMemorySamplingAdmission(CodexMemoryJobKey Key, Guid WorkerId,
    Guid OwnershipToken, long ClaimedWatermark, Guid AttemptId)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Key);
        ArgumentNullException.ThrowIfNull(Key.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(Key.Scope.AccountId);
        if (Key.Scope.Version is not (CodexMemoryVersion.V1 or CodexMemoryVersion.V2) ||
            Key.ThreadId == Guid.Empty || WorkerId == Guid.Empty || OwnershipToken == Guid.Empty ||
            AttemptId == Guid.Empty || AttemptId == OwnershipToken || ClaimedWatermark < 0)
            throw new ArgumentException("Invalid memory sampling identity.");
    }

    internal bool Matches(CodexMemoryJobObservation observation, DateTimeOffset now) =>
        observation.Key == Key && observation.State is
            { Status: CodexMemoryJobStatus.Running } state &&
        state.WorkerId == WorkerId && state.OwnershipToken == OwnershipToken &&
        state.ClaimedWatermark == ClaimedWatermark && state.LeaseUntil > now &&
        (Key.ThreadId is null || observation.SourceEnabled && observation.SourceWatermark == ClaimedWatermark &&
            !(observation.OutputWatermark > ClaimedWatermark));
}

/// <summary>
/// Durable one-shot admission capability. True grants one new dispatch only; duplicate
/// attempts and already consumed claims return false, never a replayed true receipt.
/// Check the exact active lease, source eligibility and worker authorization atomically.
/// A lost acknowledgment throws; the worker must not dispatch on an ambiguous outcome.
/// </summary>
public interface ICodexMemorySamplingStorage
{
    /// <summary>Admits one previously undispatched sampling attempt under this claim.</summary>
    ValueTask<bool> TryAdmitSamplingAsync(CodexMemorySamplingAdmission admission, CancellationToken cancellationToken);
}

/// <summary>Result of a memory sampling worker invocation.</summary>
public enum CodexMemorySamplingOutcome
{
    /// <summary>No new durable dispatch admission was granted.</summary>
    NotAdmitted,
    /// <summary>Extraction or consolidation was committed, including a valid no-output result.</summary>
    Completed,
    /// <summary>Sampling failed and a bounded retry disposition was committed.</summary>
    Failed,
    /// <summary>The claim or source changed before settlement; no result was installed.</summary>
    Superseded,
}

/// <summary>Worker outcome and any definitive settlement receipt.</summary>
/// <param name="Outcome">Sampling/settlement outcome.</param>
/// <param name="Receipt">Exact durable receipt when settlement was attempted.</param>
public sealed record CodexMemorySamplingResult(CodexMemorySamplingOutcome Outcome, CodexMemoryJobReceipt? Receipt = null);

/// <summary>
/// A settlement did not return a definitive receipt. Reconcile the exact prepared mutation
/// using its operation ID and the existing job storage; do not sample the model again.
/// This exception is for the trusted host and its change may contain private memory.
/// </summary>
public sealed class CodexMemorySamplingSettlementException(CodexMemoryJobChange change, Exception innerException)
    : IOException("Memory sampling settlement requires exact-operation reconciliation.", innerException)
{
    /// <summary>Original immutable settlement mutation; never reuse its ID with refreshed input.</summary>
    public CodexMemoryJobChange Change { get; } = change;
}

/// <summary>
/// Version-specific phase-1 output contract from Rust memories/write/phase1_output.rs.
/// Redaction is required from the trusted host before any output may reach durable storage.
/// </summary>
public static class CodexMemoryStage1Output
{
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private const int MaximumV2SummaryBytes = 9000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Returns the strict version-specific Responses output schema.</summary>
    public static JsonElement Schema(CodexMemoryVersion version)
    {
        using var document = JsonDocument.Parse(version switch
        {
            CodexMemoryVersion.V1 => """
                {"type":"object","properties":{"rollout_summary":{"type":"string"},"rollout_slug":{"type":["string","null"]},"raw_memory":{"type":"string"}},"required":["rollout_summary","rollout_slug","raw_memory"],"additionalProperties":false}
                """,
            CodexMemoryVersion.V2 => """
                {"type":"object","properties":{"rollout_summary":{"type":"string"},"rollout_slug":{"type":"string"}},"required":["rollout_summary","rollout_slug"],"additionalProperties":false}
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        });
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Decodes and redacts exact stage-1 output. V1 accepts the optional nullable slug;
    /// V2 rejects raw memory and requires a slug. Empty required memory/summary is a
    /// successful no-output result, not an error. V2 summary has a strict UTF-8 byte bound.
    /// </summary>
    public static CodexMemoryExtractionOutput? Parse(CodexMemoryVersion version, long watermark, string json,
        Func<string, string> redact)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(watermark);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(redact);
        if (version is not (CodexMemoryVersion.V1 or CodexMemoryVersion.V2))
            throw new ArgumentOutOfRangeException(nameof(version));
        if (Utf8.GetByteCount(json) > MaximumJsonBytes) throw new InvalidDataException("Memory sample exceeds its JSON bound.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Memory sample must be an object.");
        string? raw = null, summary = null, slug = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new JsonException("Duplicate memory sample field.");
            switch (property.Name)
            {
                case "raw_memory" when version == CodexMemoryVersion.V1:
                    raw = RequiredString(property.Value);
                    break;
                case "rollout_summary":
                    summary = RequiredString(property.Value);
                    break;
                case "rollout_slug":
                    slug = version == CodexMemoryVersion.V1 && property.Value.ValueKind == JsonValueKind.Null
                        ? null : RequiredString(property.Value);
                    break;
                default:
                    throw new JsonException("Unexpected memory sample field.");
            }
        }
        if (summary is null || (version == CodexMemoryVersion.V1 ? raw is null : slug is null))
            throw new JsonException("Missing required memory sample field.");

        raw = raw is null ? "" : Redact(raw, redact);
        summary = Redact(summary, redact);
        slug = slug is null ? null : Redact(slug, redact);
        if (version == CodexMemoryVersion.V2) summary = TruncateSummary(summary);
        if (summary.Length == 0 || version == CodexMemoryVersion.V1 && raw.Length == 0) return null;
        if ((long)Utf8.GetByteCount(raw) + Utf8.GetByteCount(summary) > CodexMemoryBackend.MaximumDocumentBytes ||
            slug?.Length > 256)
            throw new InvalidDataException("Redacted memory output exceeds its storage bound.");
        return new(watermark, raw, summary, slug);
    }

    /// <summary>
    /// Samples through the existing authorized Codex agent, with no tools and one request.
    /// The host must supply budgeted credentials, pinned extraction instructions, sanitized
    /// evidence and a durable admission before calling this helper.
    /// </summary>
    public static async Task<string> SampleAsync(CodexAgent agent, CodexMemoryVersion version,
        CodexResponseOptions options, IReadOnlyList<JsonElement> evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evidence);
        var request = options with
        {
            Tools = [], RequiredFirstFunction = null, ParallelToolCalls = false,
            OutputSchema = Schema(version), OutputSchemaStrict = true,
        };
        var result = await agent.RunAsync(request, evidence,
            (_, _) => throw new InvalidOperationException("Memory extraction must not dispatch tools."),
            maximumRequests: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    private static string RequiredString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new JsonException("Invalid memory sample field type.");

    private static string Redact(string value, Func<string, string> redact) =>
        redact(value) ?? throw new InvalidDataException("Memory redactor returned no value.");

    private static string TruncateSummary(string summary)
    {
        var bytes = Utf8.GetBytes(summary);
        if (bytes.Length <= MaximumV2SummaryBytes) return summary;
        // Keep both boundaries, never split a UTF-8 code point, and include the marker in the cap.
        const string marker = "…[truncated]…";
        var budget = MaximumV2SummaryBytes - Utf8.GetByteCount(marker);
        var prefix = budget / 2;
        var suffix = bytes.Length - (budget - prefix);
        while ((bytes[prefix] & 0xc0) == 0x80) prefix--;
        while ((bytes[suffix] & 0xc0) == 0x80) suffix++;
        return Utf8.GetString(bytes, 0, prefix) + marker + Utf8.GetString(bytes, suffix, bytes.Length - suffix);
    }
}

/// <summary>
/// Runs an already claimed phase-1 or phase-2 job through the existing durable scheduler.
/// The host provides authorized budgeted sampling and immutable evidence/artifact adapters.
/// No provider is contacted before a fresh one-shot admission. The worker never retries
/// sampling or refreshes a prepared settlement operation ID after an unknown outcome.
/// </summary>
public sealed class CodexMemorySamplingWorker
{
    private readonly CodexMemoryJobs jobs;
    private readonly ICodexMemorySamplingStorage admissionStorage;
    private readonly TimeProvider clock;

    /// <summary>Creates a worker over the same scope and clock as its durable scheduler.</summary>
    public CodexMemorySamplingWorker(CodexMemoryJobs jobs, ICodexMemorySamplingStorage admissionStorage,
        TimeProvider? timeProvider = null)
    {
        this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        this.admissionStorage = admissionStorage ?? throw new ArgumentNullException(nameof(admissionStorage));
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Samples stage 1 once and installs the redacted version-specific output. A null sample
    /// means the authorized immutable source contains no useful evidence and needs no output.
    /// </summary>
    public Task<CodexMemorySamplingResult> RunStage1Async(CodexMemorySamplingAdmission admission,
        Guid settlementOperationId, Func<CodexMemorySamplingAdmission, CancellationToken, Task<string?>> sample,
        Func<string, string> redact, TimeSpan? retryDelay = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(redact);
        admission.Validate();
        if (admission.Key.ThreadId is null) throw new ArgumentException("Expected stage-1 claim.", nameof(admission));
        return RunAsync(admission, settlementOperationId, async ct =>
        {
            var json = await sample(admission, ct).ConfigureAwait(false);
            var output = json is null ? null :
                CodexMemoryStage1Output.Parse(admission.Key.Scope.Version, admission.ClaimedWatermark, json, redact);
            return observed => jobs.PrepareStage1Success(observed, admission.WorkerId, admission.OwnershipToken,
                settlementOperationId, output);
        }, retryDelay, cancellationToken);
    }

    /// <summary>
    /// Runs phase 2 once. The host stages an immutable artifact, supplies its exact selected
    /// extraction revisions, and authorizes publication through the existing storage contract.
    /// </summary>
    public Task<CodexMemorySamplingResult> RunPhase2Async(CodexMemorySamplingAdmission admission,
        Guid settlementOperationId,
        Func<CodexMemorySamplingAdmission, CancellationToken, Task<CodexMemoryConsolidationOutput>> sample,
        TimeSpan? retryDelay = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(sample);
        admission.Validate();
        if (admission.Key.ThreadId is not null) throw new ArgumentException("Expected phase-2 claim.", nameof(admission));
        return RunAsync(admission, settlementOperationId, async ct =>
        {
            var output = await sample(admission, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Missing staged consolidation output.");
            var frozen = output with { Selected = output.Selected.ToArray() };
            return observed => jobs.PreparePhase2Success(observed, admission.WorkerId, admission.OwnershipToken,
                settlementOperationId, frozen);
        }, retryDelay, cancellationToken);
    }

    private async Task<CodexMemorySamplingResult> RunAsync(CodexMemorySamplingAdmission admission,
        Guid settlementOperationId,
        Func<CancellationToken, Task<Func<CodexMemoryJobObservation, CodexMemoryJobChange>>> sample,
        TimeSpan? retryDelay, CancellationToken ct)
    {
        admission.Validate();
        if (settlementOperationId == Guid.Empty || settlementOperationId == admission.OwnershipToken ||
            settlementOperationId == admission.AttemptId)
            throw new ArgumentException("Settlement must have a distinct stable operation identity.", nameof(settlementOperationId));
        var delay = retryDelay ?? TimeSpan.FromHours(1);
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        ct.ThrowIfCancellationRequested();
        // A replayed settlement or an ambiguous admission must not authorize sampling.
        if (await jobs.ReadReceiptAsync(admission.Key, settlementOperationId, ct).ConfigureAwait(false) is not null ||
            !await admissionStorage.TryAdmitSamplingAsync(admission, ct).ConfigureAwait(false))
            return new(CodexMemorySamplingOutcome.NotAdmitted);
        ct.ThrowIfCancellationRequested();

        Func<CodexMemoryJobObservation, CodexMemoryJobChange> prepare;
        var failed = false;
        try { prepare = await sample(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not (OperationCanceledException or UnauthorizedAccessException or OutOfMemoryException))
        {
            // Store a bounded failure classification, never provider text or credentials.
            failed = true;
            prepare = observed => jobs.PrepareFailure(observed, admission.WorkerId, admission.OwnershipToken,
                settlementOperationId, "sampling_failed", delay);
        }

        ct.ThrowIfCancellationRequested();
        var latest = await jobs.ReadAsync(admission.Key, ct).ConfigureAwait(false);
        if (!admission.Matches(latest, clock.GetUtcNow()))
            return new(CodexMemorySamplingOutcome.Superseded);

        var change = prepare(latest);
        CodexMemoryJobReceipt receipt;
        try { receipt = await jobs.ApplyAsync(change, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new CodexMemorySamplingSettlementException(change, error);
        }
        return new(receipt.Settlement == CodexMemoryJobSettlement.Applied
            ? failed ? CodexMemorySamplingOutcome.Failed : CodexMemorySamplingOutcome.Completed
            : CodexMemorySamplingOutcome.Superseded, receipt);
    }
}
