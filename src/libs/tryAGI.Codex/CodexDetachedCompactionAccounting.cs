using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Original terminal of a pre-turn or speculative compaction, retained even if its checkpoint is discarded.</summary>
/// <param name="RequestId">Original reservation identity.</param>
/// <param name="RequestSha256">Exact admitted request digest.</param>
/// <param name="ResponseId">Provider terminal identity.</param>
/// <param name="SourceRevision">Revision of the captured compaction input.</param>
/// <param name="Binding">Original funding and terminal provenance.</param>
/// <param name="Terminal">Original provider terminal, not a checkpoint or reconstructed estimate.</param>
public sealed record CodexStoredDetachedCompactionReceipt(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] string ResponseId,
    [property: JsonRequired] long SourceRevision,
    [property: JsonRequired] CodexStoredInferenceAccounting Binding,
    [property: JsonRequired] JsonElement Terminal);

internal static class CodexDetachedCompactionAccounting
{
    // The native document also has its own byte bound. Never evict unpaid evidence.
    private const int MaximumReceipts = 4096;

    private static bool ValidId(string? value) =>
        value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static void ValidateInline(CodexStoredTurn turn)
    {
        foreach (var receipt in turn.Dispatches ?? [])
            if (receipt?.CompactionAccounting is { } binding &&
                (receipt.Kind != "inference" || receipt.State != "succeeded" ||
                 receipt.InferenceCompletion is not null || receipt.MailSampling is not null ||
                 binding.RequestKind != "compaction" || binding.AccountingKind is not null ||
                 receipt.AbnormalAccounting is not null || !CodexInferenceAccounting.Valid(binding, turn.Id) ||
                 receipt.Result is not { ValueKind: JsonValueKind.Object } terminal ||
                 !ValidId(CodexRealtimeProtocol.Text(terminal, "id"))))
                throw new InvalidDataException("Invalid inline compaction accounting receipt.");
    }

    internal static void Validate(CodexThreadDocument thread)
    {
        if (thread.DetachedCompactionReceipts is not { } receipts) return;
        if (receipts.Length > MaximumReceipts)
            throw new InvalidDataException("Detached compaction accounting exceeds its bound.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in receipts)
            if (receipt is null || !ValidId(receipt.RequestId) || !ids.Add(receipt.RequestId) ||
                !ValidHash(receipt.RequestSha256) || !ValidId(receipt.ResponseId) ||
                receipt.SourceRevision < 1 || receipt.SourceRevision >= thread.Revision ||
                receipt.Binding is not { RequestKind: "compaction" } binding ||
                binding.OriginThreadId != thread.Id || binding.AccountId != thread.AccountId ||
                !CodexInferenceAccounting.Valid(binding, binding.OriginTurnId) ||
                receipt.Terminal.ValueKind != JsonValueKind.Object ||
                CodexAbnormalInferenceAccounting.ResponseId(binding, receipt.Terminal) != receipt.ResponseId)
                throw new InvalidDataException("Invalid detached compaction accounting receipt.");
        if ((thread.Turns ?? []).Any(turn => (turn?.Dispatches ?? []).Any(receipt =>
            receipt is not null && ids.Contains(receipt.CallId))))
            throw new InvalidDataException("Duplicate native accounting reservation identity.");
    }

    private static bool Same(CodexStoredDetachedCompactionReceipt left, CodexStoredDetachedCompactionReceipt right)
        => left.RequestId == right.RequestId && left.RequestSha256 == right.RequestSha256 &&
            left.ResponseId == right.ResponseId && left.SourceRevision == right.SourceRevision &&
            left.Binding == right.Binding && JsonElement.DeepEquals(left.Terminal, right.Terminal);

    internal static bool Appended(CodexThreadDocument before, CodexThreadDocument after)
    {
        var prior = before.DetachedCompactionReceipts ?? [];
        var next = after.DetachedCompactionReceipts ?? [];
        return next.Length > prior.Length && prior.Select((receipt, index) => Same(receipt, next[index])).All(same => same);
    }

    internal static bool OnlyReceiptsAppended(CodexThreadDocument before, CodexThreadDocument after) =>
        Appended(before, after) &&
        after.Revision - before.Revision == (after.DetachedCompactionReceipts?.Length ?? 0) -
            (before.DetachedCompactionReceipts?.Length ?? 0) &&
        JsonElement.DeepEquals(CodexThreadStore.Encode(before with { Revision = 1, UpdatedAt = default, DetachedCompactionReceipts = null }),
            CodexThreadStore.Encode(after with { Revision = 1, UpdatedAt = default, DetachedCompactionReceipts = null }));

    internal static bool Matches(CodexStoredDetachedCompactionReceipt left, CodexStoredDetachedCompactionReceipt right)
        => Same(left, right);
}

// Detached compaction has no live turn owner to settle an inference dispatch.
// Its terminal accounting is an append-only observation, independent of adoption.
internal sealed class CodexDetachedCompactionObserver(
    CodexThreadStore store, ICodexUsageObserver budget, CodexThreadCompactionCandidate candidate,
    CodexThreadCompactionScheduler.AuthOwner owner) : ICodexUsageObserver, ICodexValidatedResponseObserver, ICodexAbnormalResponseObserver
{
    private string? requestId;
    private string? requestHash;
    public bool RequiresPreparedRequest => true;

    public Task ReserveAsync(string id, string model, CancellationToken ct) => budget.ReserveAsync(id, model, ct);
    public Task<CodexResponsesIdentity?> ReserveWithIdentityAsync(string id, string model, CancellationToken ct)
        => budget.ReserveWithIdentityAsync(id, model, ct);

    public async Task PreparedAsync(string id, string model, JsonElement request, CancellationToken ct)
    {
        var input = request.GetProperty("input").EnumerateArray().ToArray();
        if (requestId is not null || model != candidate.Model || input.Length != candidate.History.Count + 1 ||
            CodexRealtimeProtocol.Text(input[^1], "type") != "compaction_trigger" ||
            !CodexThreadStore.SameHistory(input.Take(input.Length - 1).ToArray(), candidate.History))
            throw new CodexProtocolException("Detached compaction request does not match its captured candidate.");
        requestId = id;
        requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.GetRawText())));
        await budget.PreparedAsync(id, model, request, ct).ConfigureAwait(false);
    }

    public async Task CompleteValidatedAsync(string id, CodexResponseOptions settings, string responseId,
        JsonElement response, IReadOnlyList<JsonElement> output, CancellationToken ct)
    {
        try { await RecordTerminalAsync(id, settings, responseId, response, ct).ConfigureAwait(false); }
        finally
        {
            // Even a failed native journal write must not suppress known provider usage.
            await budget.CompleteAsync(id, settings.Model, response, ct).ConfigureAwait(false);
        }
    }

    private async Task RecordTerminalAsync(string id, CodexResponseOptions settings, string responseId,
        JsonElement evidence, CancellationToken ct, string? abnormalEventType = null, CodexObservedUsage? usage = null)
    {
        if (id != requestId || requestHash is null || settings.RequestKind != "compaction" ||
            settings.Model != candidate.Model || settings.Identity is not { } identity ||
            !Guid.TryParse(identity.ThreadId, out var threadId) || threadId != candidate.ThreadId ||
            !Guid.TryParse(identity.TurnId, out var turnId) || turnId == Guid.Empty)
            throw new CodexProtocolException("Detached compaction terminal does not match its original reservation.");
        var binding = new CodexStoredInferenceAccounting(candidate.ThreadId, turnId, owner.AccountId,
            CodexContextWindow.AuthHash(owner), settings.Model, CodexRetainedJournalMetadata.ItemHash(evidence))
        { RequestKind = "compaction" };
        if (abnormalEventType is not null)
            binding = CodexAbnormalInferenceAccounting.Classify(binding, abnormalEventType, usage);
        if (!CodexInferenceAccounting.Valid(binding, turnId) || !CodexAbnormalInferenceAccounting.ValidId(responseId) ||
            CodexAbnormalInferenceAccounting.ResponseId(binding, evidence) != responseId)
            throw new CodexProtocolException("Detached compaction evidence identity mismatch.");
        await store.RecordDetachedCompactionAccountingAsync(candidate.ThreadId,
            new(id, requestHash, responseId, candidate.Revision, binding, evidence.Clone()), ct).ConfigureAwait(false);
    }

    public async Task CompleteUnvalidatedAsync(string id, CodexResponseOptions settings, string eventType,
        string responseId, JsonElement response, CancellationToken ct)
    {
        try { await RecordTerminalAsync(id, settings, responseId, response, ct, eventType).ConfigureAwait(false); }
        finally { await budget.CompleteAsync(id, settings.Model, response, ct).ConfigureAwait(false); }
    }

    public async Task ObserveIncompleteAsync(string id, CodexResponseOptions settings, JsonElement payload,
        CodexObservedUsage usage, CancellationToken ct)
    {
        try
        {
            if (usage.ResponseId is { } responseId)
                await RecordTerminalAsync(id, settings, responseId, payload, ct, usage.EventType, usage).ConfigureAwait(false);
        }
        finally { await budget.IncompleteWithUsageAsync(id, settings.Model, usage, ct).ConfigureAwait(false); }
    }

    public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        => budget.CompleteAsync(id, model, response, ct);
    public Task IncompleteAsync(string id, string model, CancellationToken ct)
        => budget.IncompleteAsync(id, model, ct);
    public Task IncompleteWithUsageAsync(string id, string model, CodexObservedUsage? usage, CancellationToken ct)
        => budget.IncompleteWithUsageAsync(id, model, usage, ct);
    public Task NotDispatchedAsync(string id, string model, CancellationToken ct)
        => budget.NotDispatchedAsync(id, model, ct);
}

public sealed partial class CodexThreadStore
{
    internal async Task<CodexThreadDocument> RecordDetachedCompactionAccountingAsync(Guid threadId,
        CodexStoredDetachedCompactionReceipt receipt, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var before = await ReadAsync(threadId, ct).ConfigureAwait(false);
            var prior = before.DetachedCompactionReceipts?.SingleOrDefault(item => item.RequestId == receipt.RequestId);
            if (prior is not null)
            {
                if (!CodexDetachedCompactionAccounting.Matches(prior, receipt))
                    throw new InvalidOperationException("Conflicting detached compaction reservation receipt.");
                return before;
            }
            var next = before with
            {
                DetachedCompactionReceipts = [.. before.DetachedCompactionReceipts ?? [], receipt],
                Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
            };
            CodexDetachedCompactionAccounting.Validate(next);
            try { return await PublishAsync(before.Revision, next, ct).ConfigureAwait(false); }
            catch (CodexThreadConflictException) when (attempt < 3)
            {
                // Retry only the append after a known CAS conflict. A provider or
                // ambiguous external write is never replayed.
            }
        }
    }

    internal Task<CodexThreadDocument> SettleInlineCompactionAccountingAsync(Guid threadId, long revision,
        Guid turnId, string owner, string requestId, JsonElement terminal, CodexStoredInferenceAccounting binding,
        CancellationToken ct)
        => ChangeAsync(threadId, revision, document =>
        {
            var turn = Active(document, turnId, owner);
            var receipt = turn.Dispatches.SingleOrDefault(item => item.CallId == requestId);
            if (receipt is not { Kind: "inference", State: "dispatching" or "unknown",
                InferenceCompletion: null, MailSampling: null, CompactionAccounting: null })
                throw new InvalidOperationException("Expected an unsettled inline compaction reservation.");
            var updated = turn with
            {
                Dispatches = turn.Dispatches.Select(item => item.CallId == requestId
                    ? item with { State = "succeeded", Result = terminal.Clone(), CompactionAccounting = binding } : item).ToArray(),
            };
            CodexDetachedCompactionAccounting.ValidateInline(updated);
            return PromoteRealtimeArtifacts(AccountGoalInference(Replace(document, updated), receipt, turnId, terminal), turn, updated);
        }, ct);

    /// <summary>Reconcile one original detached compaction reservation, including discarded checkpoints.
    /// No provider dispatch, journal write, replacement reservation or implicit retry is performed.</summary>
    public async Task<CodexInferenceAccountingOutcome> ReconcileDetachedCompactionAccountingAsync(Guid threadId,
        long expectedRevision, string requestId, ICodexInferenceAccountingReconciler reconciler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        Token(requestId);
        var thread = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var receipt = thread.DetachedCompactionReceipts?.SingleOrDefault(item => item.RequestId == requestId);
        if (receipt is null || receipt.Binding.AccountingKind is not null) return CodexInferenceAccountingOutcome.NoEvidence;
        return await ReconcileAccountingCoreAsync(thread, receipt.Binding.OriginTurnId, receipt.RequestId,
            receipt.RequestSha256, receipt.ResponseId, receipt.Terminal, receipt.Binding, reconciler, cancellationToken)
            .ConfigureAwait(false);
    }
}
