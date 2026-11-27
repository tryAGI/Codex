using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Original funding and model binding of a validated native inference receipt.
/// Never copied to the provider or interpreted as evidence that the ledger committed.</summary>
/// <param name="OriginThreadId">Original thread, not a fork inheriting the receipt.</param>
/// <param name="OriginTurnId">Original native turn.</param>
/// <param name="AccountId">Original account owning the reservation.</param>
/// <param name="AuthOwnerSha256">Funding account/user/restriction digest.</param>
/// <param name="RequestedModel">Model used to reserve and settle usage, not the reported provider alias.</param>
/// <param name="TerminalSha256">Canonical digest of the exact stored provider terminal.</param>
public sealed record CodexStoredInferenceAccounting(
    [property: JsonRequired] Guid OriginThreadId,
    [property: JsonRequired] Guid OriginTurnId,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] string AuthOwnerSha256,
    [property: JsonRequired] string RequestedModel,
    [property: JsonRequired] string TerminalSha256)
{
    /// <summary>Null for ordinary native inference (including legacy receipts), or compaction.
    /// Compaction's wire turn may refer to a not-yet-admitted or already completed turn.</summary>
    public string? RequestKind { get; init; }

    /// <summary>Null for validated completion; otherwise an explicitly separate accounting-only
    /// terminal or incomplete observation. Never use either kind as model output or sampling proof.</summary>
    public string? AccountingKind { get; init; }
    /// <summary>Original provider event type when accounting is not a validated completion.</summary>
    public string? TerminalEventType { get; init; }
    /// <summary>Original observation time of incomplete usage, if reported.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Calculate the same funding-owner digest when reserving usage. No token is retained.</summary>
    public static string FundingOwnerDigest(CodexCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return CodexContextWindow.AuthHash(CodexThreadCompactionScheduler.AuthOwner.From(credentials));
    }
}

/// <summary>Immutable, private reconciliation evidence. The host must verify the original
/// reservation, not allocate a new one. Do not log or retain terminal output unnecessarily.</summary>
/// <param name="Binding">Original funding, model, turn and terminal correlation.</param>
/// <param name="RequestId">Original host reservation identifier, not provider response ID.</param>
/// <param name="RequestSha256">Digest of the originally admitted request.</param>
/// <param name="ResponseId">Provider response identity.</param>
/// <param name="Terminal">Exact durable provider terminal, not a reconstructed usage estimate.</param>
public sealed record CodexInferenceAccountingReceipt(CodexStoredInferenceAccounting Binding,
    string RequestId, string RequestSha256, string ResponseId, JsonElement Terminal);

/// <summary>Outcome of an explicit host-ledger reconciliation.</summary>
public enum CodexInferenceAccountingOutcome
{
    /// <summary>No validated durable receipt with original reservation provenance is available.</summary>
    NoEvidence,
    /// <summary>The receipt belongs to the source of a fork; reconcile the source instead.</summary>
    InheritedReceipt,
    /// <summary>The matching existing reservation was atomically reconciled according to its accounting kind.</summary>
    Settled,
    /// <summary>The exact matching accounting evidence was already committed.</summary>
    AlreadySettled,
    /// <summary>No matching reservation exists; do not create one during recovery.</summary>
    MissingReservation,
    /// <summary>The stored reservation or completion conflicts with the evidence.</summary>
    Conflict,
    /// <summary>The external result is not proven; no automatic retry is permitted.</summary>
    Unknown,
}

/// <summary>Opt-in host ledger reconciliation. Implementations must atomically compare the existing
/// reservation and any settled receipt against the supplied identities, funding owner, requested model,
/// request digest and terminal digest. Concurrent calls for the same reservation must be idempotent.
/// Never reserve again, invoke the provider, estimate missing usage, or replace conflicting evidence.
/// An ordinary ICodexUsageObserver is deliberately not sufficient for this contract.</summary>
public interface ICodexInferenceAccountingReconciler
{
    /// <summary>Reconcile only the existing original reservation; return Unknown if its outcome cannot be proven.</summary>
    Task<CodexInferenceAccountingOutcome> ReconcileCompletedAsync(CodexInferenceAccountingReceipt receipt,
        CancellationToken cancellationToken);
}

internal static class CodexInferenceAccounting
{
    internal static CodexStoredInferenceAccounting Capture(CodexThreadDocument thread, Guid turnId,
        CodexThreadCompactionScheduler.AuthOwner owner, string model, JsonElement terminal)
        => new(thread.Id, turnId, owner.AccountId, CodexContextWindow.AuthHash(owner), model,
            CodexRetainedJournalMetadata.ItemHash(terminal));

    internal static bool Valid(CodexStoredInferenceAccounting binding, Guid turnId) =>
        binding.OriginThreadId != Guid.Empty && turnId != Guid.Empty && binding.OriginTurnId == turnId &&
        binding.RequestKind is null or "compaction" &&
        CodexAbnormalInferenceAccounting.ValidKind(binding) &&
        ValidToken(binding.AccountId) && ValidToken(binding.RequestedModel) &&
        ValidHash(binding.AuthOwnerSha256) && ValidHash(binding.TerminalSha256);

    private static bool ValidToken(string? value) => value is { Length: > 0 and <= 512 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static void WriteBinding(Utf8JsonWriter writer, CodexStoredInferenceAccounting binding)
    {
        writer.WriteStartObject("accounting");
        writer.WriteString("origin_thread", binding.OriginThreadId);
        writer.WriteString("origin_turn", binding.OriginTurnId);
        writer.WriteString("account", binding.AccountId);
        writer.WriteString("owner_sha256", binding.AuthOwnerSha256);
        writer.WriteString("requested_model", binding.RequestedModel);
        writer.WriteString("terminal_sha256", binding.TerminalSha256);
        if (binding.RequestKind is { } kind) writer.WriteString("request_kind", kind);
        if (binding.AccountingKind is { } accountingKind)
        {
            writer.WriteString("accounting_kind", accountingKind);
            writer.WriteString("terminal_event_type", binding.TerminalEventType);
            if (binding.ObservedAt is { } observed) writer.WriteString("observed_at", observed);
        }
        writer.WriteEndObject();
    }
}

public sealed partial class CodexThreadStore
{
    /// <summary>Explicitly reconcile one validated normal inference or inline compaction on a closed turn of its original
    /// thread. Only an opt-in atomic host ledger is called, once. No model/tool dispatch, new reservation,
    /// journal write, or automatic retry occurs. Old receipts without binding remain unresolved.
    /// If the host reports an ambiguous result, inspect the original ledger reservation before retrying.</summary>
    public async Task<CodexInferenceAccountingOutcome> ReconcileInferenceAccountingAsync(Guid threadId,
        long expectedRevision, Guid turnId, string requestId, ICodexInferenceAccountingReconciler reconciler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        Token(requestId);
        var thread = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var turn = thread.Turns.SingleOrDefault(item => item.Id == turnId)
            ?? throw new KeyNotFoundException("Native accounting turn not found.");
        if (turn.Status == "inProgress")
            throw new InvalidOperationException("Recover or finish the live turn before reconciling its accounting.");
        var receipt = turn.Dispatches.SingleOrDefault(item => item.CallId == requestId);
        if (receipt is not { Kind: "inference", State: "succeeded", Result: { } terminal } ||
            (receipt.InferenceCompletion?.Accounting ?? receipt.CompactionAccounting) is not { } binding ||
            CodexRealtimeProtocol.Text(terminal, "id") is not { } responseId)
            return CodexInferenceAccountingOutcome.NoEvidence;
        return await ReconcileAccountingCoreAsync(thread, turn.Id, requestId, receipt.RequestSha256,
            responseId, terminal, binding, reconciler, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CodexInferenceAccountingOutcome> ReconcileAccountingCoreAsync(
        CodexThreadDocument thread, Guid turnId, string requestId, string requestSha256,
        string responseId, JsonElement terminal, CodexStoredInferenceAccounting binding,
        ICodexInferenceAccountingReconciler reconciler, CancellationToken cancellationToken)
    {
        if (binding.AccountId != thread.AccountId)
            throw new InvalidDataException("Inference accounting belongs to another account.");
        if (binding.OriginThreadId != thread.Id)
            return CodexInferenceAccountingOutcome.InheritedReceipt;
        if (!CodexInferenceAccounting.Valid(binding, turnId) || binding.AccountingKind is not null ||
            CodexRealtimeProtocol.Text(terminal, "id") != responseId ||
            binding.TerminalSha256 != CodexRetainedJournalMetadata.ItemHash(terminal))
            throw new InvalidDataException("Durable inference accounting evidence is inconsistent.");
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = await reconciler.ReconcileCompletedAsync(new(binding, requestId, requestSha256,
            responseId, terminal.Clone()), cancellationToken).ConfigureAwait(false);
        return outcome is CodexInferenceAccountingOutcome.Settled or CodexInferenceAccountingOutcome.AlreadySettled or
            CodexInferenceAccountingOutcome.MissingReservation or CodexInferenceAccountingOutcome.Conflict or
            CodexInferenceAccountingOutcome.Unknown ? outcome :
            throw new InvalidOperationException("Host ledger returned an invalid reconciliation outcome.");
    }
}
