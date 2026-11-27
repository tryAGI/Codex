using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Opt-in reconciliation of accounting-only terminal evidence. Implementations must
/// atomically match the original reservation, funding, request and terminal digest, and the
/// accounting kind. A billed unvalidated terminal corresponds to CompleteAsync; an incomplete
/// observation corresponds to IncompleteWithUsageAsync and MUST NOT become a completed inference
/// or an estimated charge. Never create a reservation, replay a request, or replace a conflicting
/// ledger result. Return Unknown for an outcome that is not proven.</summary>
public interface ICodexAbnormalInferenceAccountingReconciler
{
    /// <summary>Reconcile an original reservation with accounting-only evidence. Terminal contains
    /// the raw response for completed_unvalidated, or the raw event envelope for incomplete_observation.
    /// The latter's counters can be read with CodexObservedUsage.Read using the original observation time.</summary>
    Task<CodexInferenceAccountingOutcome> ReconcileObservedAsync(CodexInferenceAccountingReceipt receipt,
        CancellationToken cancellationToken);
}

// This extension is only called after Responses correlation accepts the exact event.
// Legacy observers keep their original accounting and cancellation behavior.
internal interface ICodexAbnormalResponseObserver
{
    Task CompleteUnvalidatedAsync(string id, CodexResponseOptions settings, string eventType,
        string responseId, JsonElement response, CancellationToken ct);
    Task ObserveIncompleteAsync(string id, CodexResponseOptions settings, JsonElement payload,
        CodexObservedUsage usage, CancellationToken ct);
}

internal static class CodexAbnormalInferenceAccounting
{
    internal const string BilledTerminal = "completed_unvalidated";
    internal const string IncompleteObservation = "incomplete_observation";

    internal static bool ValidKind(CodexStoredInferenceAccounting binding) => binding.AccountingKind switch
    {
        null => binding.TerminalEventType is null && binding.ObservedAt is null,
        BilledTerminal => binding.TerminalEventType is "response.completed" or "response.incomplete" &&
            binding.ObservedAt is null,
        IncompleteObservation => binding.TerminalEventType is "response.incomplete" or "response.failed" &&
            binding.ObservedAt is { } observed && observed != default,
        _ => false,
    };

    internal static CodexStoredInferenceAccounting Classify(CodexStoredInferenceAccounting binding,
        string eventType, CodexObservedUsage? usage = null)
        => binding with
        {
            AccountingKind = usage is null ? BilledTerminal : IncompleteObservation,
            TerminalEventType = eventType,
            ObservedAt = usage?.ObservedAt,
        };

    internal static string? ResponseId(CodexStoredInferenceAccounting binding, JsonElement terminal)
    {
        if (terminal.ValueKind != JsonValueKind.Object) return null;
        if (binding.AccountingKind != IncompleteObservation)
            return CodexRealtimeProtocol.Text(terminal, "id");
        if (CodexRealtimeProtocol.Text(terminal, "type") != binding.TerminalEventType ||
            !terminal.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return null;
        var id = CodexRealtimeProtocol.Text(response, "id");
        if (terminal.TryGetProperty("response_id", out var rootId) && rootId.ValueKind != JsonValueKind.Null &&
            (rootId.ValueKind != JsonValueKind.String || rootId.GetString() != id))
            return null;
        return id;
    }

    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 512 } &&
        id.All(character => character is >= '!' and <= '~');

    internal static void Validate(CodexStoredTurn turn)
    {
        foreach (var receipt in turn.Dispatches ?? [])
        {
            if (receipt?.AbnormalAccounting is not { } binding) continue;
            var expectedState = binding.AccountingKind == IncompleteObservation ? "unknown" : "succeeded";
            if (receipt.Kind != "inference" || receipt.State != expectedState ||
                receipt.InferenceCompletion is not null || receipt.MailSampling is not null ||
                receipt.CompactionAccounting is not null || binding.AccountingKind is null ||
                !CodexInferenceAccounting.Valid(binding, turn.Id) ||
                receipt.Result is not { } terminal || !ValidId(ResponseId(binding, terminal)))
                throw new InvalidDataException("Invalid accounting-only inference receipt.");
        }
    }
}

public sealed partial class CodexThreadStore
{
    internal Task<CodexThreadDocument> SettleAbnormalInferenceAccountingAsync(Guid threadId, long revision,
        Guid turnId, string owner, string requestId, JsonElement evidence, CodexStoredInferenceAccounting binding,
        CancellationToken ct)
        => ChangeAsync(threadId, revision, document =>
        {
            var turn = Active(document, turnId, owner);
            var receipt = turn.Dispatches.SingleOrDefault(item => item.CallId == requestId);
            if (receipt is not { Kind: "inference", State: "dispatching" or "unknown",
                InferenceCompletion: null, MailSampling: null, CompactionAccounting: null, AbnormalAccounting: null })
                throw new InvalidOperationException("Expected an unsettled inference accounting observation.");
            var updated = turn with
            {
                Dispatches = turn.Dispatches.Select(item => item.CallId == requestId
                    ? item with
                    {
                        State = binding.AccountingKind == CodexAbnormalInferenceAccounting.IncompleteObservation
                            ? "unknown" : "succeeded",
                        Result = evidence.Clone(), AbnormalAccounting = binding,
                    } : item).ToArray(),
            };
            CodexAbnormalInferenceAccounting.Validate(updated);
            // No model output is promoted from an accounting-only observation.
            return Replace(document, updated);
        }, ct);

    /// <summary>Explicitly reconcile one correlated accounting-only terminal of a closed original turn,
    /// or a detached compaction reservation (pass null turnId). The host receives the exact original
    /// evidence and disposition, not a synthetic completed response. No provider call, journal write,
    /// reservation, or automatic retry occurs. Unattributed or legacy evidence remains unresolved.</summary>
    public async Task<CodexInferenceAccountingOutcome> ReconcileAbnormalInferenceAccountingAsync(Guid threadId,
        long expectedRevision, Guid? turnId, string requestId, ICodexAbnormalInferenceAccountingReconciler reconciler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        Token(requestId);
        var thread = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        CodexStoredInferenceAccounting? binding;
        string requestHash;
        JsonElement terminal;
        if (turnId is { } nativeTurn)
        {
            var turn = thread.Turns.SingleOrDefault(item => item.Id == nativeTurn)
                ?? throw new KeyNotFoundException("Native accounting turn not found.");
            if (turn.Status == "inProgress")
                throw new InvalidOperationException("Recover or finish the live turn before reconciling its accounting.");
            var receipt = turn.Dispatches.SingleOrDefault(item => item.CallId == requestId);
            if (receipt is not { Kind: "inference", AbnormalAccounting: { } observed, Result: { } raw })
                return CodexInferenceAccountingOutcome.NoEvidence;
            binding = observed;
            requestHash = receipt.RequestSha256;
            terminal = raw;
        }
        else
        {
            var receipt = thread.DetachedCompactionReceipts?.SingleOrDefault(item => item.RequestId == requestId);
            if (receipt is not { Binding.AccountingKind: not null })
                return CodexInferenceAccountingOutcome.NoEvidence;
            binding = receipt.Binding;
            requestHash = receipt.RequestSha256;
            terminal = receipt.Terminal;
        }

        if (binding.AccountId != thread.AccountId)
            throw new InvalidDataException("Inference accounting belongs to another account.");
        if (binding.OriginThreadId != thread.Id)
            return CodexInferenceAccountingOutcome.InheritedReceipt;
        if (binding.AccountingKind is null || !CodexInferenceAccounting.Valid(binding, turnId ?? binding.OriginTurnId) ||
            CodexAbnormalInferenceAccounting.ResponseId(binding, terminal) is not { } responseId ||
            !CodexAbnormalInferenceAccounting.ValidId(responseId) ||
            binding.TerminalSha256 != CodexRetainedJournalMetadata.ItemHash(terminal))
            throw new InvalidDataException("Durable accounting-only evidence is inconsistent.");
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = await reconciler.ReconcileObservedAsync(new(binding, requestId, requestHash, responseId,
            terminal.Clone()), cancellationToken).ConfigureAwait(false);
        return outcome is CodexInferenceAccountingOutcome.Settled or CodexInferenceAccountingOutcome.AlreadySettled or
            CodexInferenceAccountingOutcome.MissingReservation or CodexInferenceAccountingOutcome.Conflict or
            CodexInferenceAccountingOutcome.Unknown ? outcome :
            throw new InvalidOperationException("Host ledger returned an invalid reconciliation outcome.");
    }
}
