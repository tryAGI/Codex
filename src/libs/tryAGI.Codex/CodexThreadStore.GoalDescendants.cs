namespace tryAGI.Codex;

/// <summary>Root goal generation observed before an admitted descendant turn. Historical
/// attribution is not permission to start a child or to charge a replacement goal.</summary>
/// <param name="OriginThreadId">Original descendant journal, never a history fork.</param>
/// <param name="ParentThreadId">Immediate graph parent at capture.</param>
/// <param name="RootThreadId">Account-bound agent tree root.</param>
/// <param name="GoalId">Active root goal generation at capture.</param>
/// <param name="RootRevision">Observed root journal revision.</param>
/// <param name="GraphRevision">Observed validated identity graph revision.</param>
public sealed record CodexDescendantGoalAttribution(Guid OriginThreadId, Guid ParentThreadId, Guid RootThreadId,
    Guid GoalId, long RootRevision, string GraphRevision);

/// <summary>Outcome of reconciling an exact descendant provider receipt into the root goal.</summary>
public enum CodexDescendantGoalUsageOutcome
{
    /// <summary>No attributable, complete token counters were durably observed.</summary>
    NoEvidence,
    /// <summary>The history was copied from another thread and cannot charge the original root.</summary>
    InheritedReceipt,
    /// <summary>The original goal was cleared or replaced.</summary>
    StaleGoal,
    /// <summary>The exact usage receipt was already applied.</summary>
    AlreadyApplied,
    /// <summary>The original root goal durably accounted the receipt.</summary>
    Applied
}

public sealed partial class CodexThreadStore
{
    internal async Task<CodexDescendantGoalAttribution?> CaptureDescendantGoalAttributionAsync(
        CodexAgentIdentityDirectory directory, CodexNamedAgentIdentity descendant, CancellationToken ct)
    {
        if (directory.Graph.AccountId != accountId || descendant.RootThreadId != directory.Graph.RootThreadId ||
            descendant.ThreadId == descendant.RootThreadId || descendant.ParentThreadId is not { } parent ||
            directory.GetActive(descendant.ThreadId) != descendant)
            throw new ArgumentException("Expected a validated, active descendant identity.");
        CodexThreadDocument root;
        try { root = await ReadAsync(descendant.RootThreadId, ct).ConfigureAwait(false); }
        catch (KeyNotFoundException) { return null; } // Graph-only root has no goal to attribute.
        if (!CanAttributeRootGoal(root) || root.Goal is not { } goal) return null;
        return new(descendant.ThreadId, parent, root.Id, goal.Id, root.Revision, directory.Graph.Revision);
    }

    private static bool CanAttributeRootGoal(CodexThreadDocument root)
        => ReadStateEligible(root) && root.ArchivedAt is null && root.Goal is { Status: "active" };

    private static void ValidateDescendantGoalAttribution(CodexStoredTurn turn)
    {
        if (turn.DescendantGoal is not { } binding) return;
        if (turn.GoalContinuation is not null || binding.OriginThreadId == Guid.Empty ||
            binding.ParentThreadId == Guid.Empty || binding.RootThreadId == Guid.Empty ||
            binding.GoalId == Guid.Empty || binding.OriginThreadId == binding.ParentThreadId ||
            binding.OriginThreadId == binding.RootThreadId || binding.RootRevision < 1 ||
            string.IsNullOrWhiteSpace(binding.GraphRevision) || binding.GraphRevision.Length > 1024)
            throw new InvalidDataException("Invalid native descendant goal attribution.");
    }

    /// <summary>Reconcile one original, correlated descendant inference receipt into its captured
    /// root goal. No provider replay, graph traversal or history scan of other descendants is
    /// performed. The child revision must be the exact observed revision. Repeat the same
    /// receipt after an ambiguous root CAS acknowledgement; a stable receipt ID prevents double
    /// charging. The root generation is never inferred from the current goal.</summary>
    public async Task<CodexDescendantGoalUsageOutcome> ReconcileDescendantGoalUsageAsync(Guid childThreadId,
        long expectedChildRevision, Guid turnId, string requestId, CancellationToken cancellationToken = default)
    {
        Id(turnId); Token(requestId);
        var child = await CurrentAsync(childThreadId, expectedChildRevision, cancellationToken).ConfigureAwait(false);
        return await AccountDescendantGoalAsync(child, turnId, requestId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CodexDescendantGoalUsageOutcome> AccountDescendantGoalAsync(CodexThreadDocument child,
        Guid turnId, string requestId, CancellationToken ct)
    {
        var turn = child.Turns.SingleOrDefault(item => item.Id == turnId)
            ?? throw new KeyNotFoundException("Descendant turn not found.");
        if (turn.DescendantGoal is not { } attribution) return CodexDescendantGoalUsageOutcome.NoEvidence;
        if (attribution.OriginThreadId != child.Id) return CodexDescendantGoalUsageOutcome.InheritedReceipt;
        var receipt = turn.Dispatches.SingleOrDefault(item => item.CallId == requestId);
        if (receipt is not { Kind: "inference", GoalInference: { } binding, Result: { } evidence } ||
            binding.OriginThreadId != child.Id || binding.GoalId != attribution.GoalId ||
            binding.GoalThreadId != attribution.RootThreadId)
            return CodexDescendantGoalUsageOutcome.NoEvidence;
        // Reuse the existing correlated terminal/accounting evidence rather than treating
        // arbitrary tool output, projected bytes or an unproven incomplete response as usage.
        var accounting = receipt.InferenceCompletion?.Accounting ?? receipt.CompactionAccounting ?? receipt.AbnormalAccounting;
        if (accounting is null || accounting.OriginThreadId != child.Id || accounting.AccountId != accountId ||
            child.AccountId != accountId || !CodexInferenceAccounting.Valid(accounting, turn.Id) ||
            accounting.TerminalSha256 != CodexRetainedJournalMetadata.ItemHash(evidence) ||
            !CodexAbnormalInferenceAccounting.ValidId(CodexAbnormalInferenceAccounting.ResponseId(accounting, evidence)))
            return CodexDescendantGoalUsageOutcome.NoEvidence;
        var delta = GoalTokenDelta(evidence, accounting.AccountingKind == CodexAbnormalInferenceAccounting.IncompleteObservation);
        if (delta is null or 0) return CodexDescendantGoalUsageOutcome.NoEvidence;
        return await ApplyAttributedDescendantGoalAsync(attribution, binding, delta.Value, ct).ConfigureAwait(false);
    }

    private async Task<CodexDescendantGoalUsageOutcome> ApplyAttributedDescendantGoalAsync(
        CodexDescendantGoalAttribution attribution, CodexGoalInferenceBinding binding, long delta, CancellationToken ct)
    {
        // One read of the original root and one CAS per observed receipt. Known conflicts
        // can rebase; an ambiguous write never automatically repeats the operation.
        for (var attempt = 0; ; attempt++)
        {
            CodexThreadDocument root;
            try { root = await ReadAsync(attribution.RootThreadId, ct).ConfigureAwait(false); }
            catch (KeyNotFoundException) { return CodexDescendantGoalUsageOutcome.StaleGoal; }
            if (root.Goal is not { } goal || goal.Id != attribution.GoalId)
                return CodexDescendantGoalUsageOutcome.StaleGoal;
            if (root.Revision < attribution.RootRevision)
                throw new InvalidDataException("Root goal journal predates descendant attribution.");
            var updated = ApplyGoalUsage(goal, binding.UsageId, delta, 0, CodexGoalAccountingMode.AttributedDescendant);
            if (ReferenceEquals(goal, updated)) return CodexDescendantGoalUsageOutcome.AlreadyApplied;
            var observed = ObserveLocalGoalMutation(root);
            if (!ReferenceEquals(goal, observed.Goal))
                updated = ApplyGoalUsage(observed.Goal!, binding.UsageId, delta, 0, CodexGoalAccountingMode.AttributedDescendant);
            try
            {
                _ = await WriteGoalAsync(observed, updated, ct,
                    CaptureGoalNotice(root, root.Turns.LastOrDefault() is { Status: "inProgress" } activeTurn ? activeTurn.Id : null)).ConfigureAwait(false);
                return CodexDescendantGoalUsageOutcome.Applied;
            }
            catch (CodexThreadConflictException) when (attempt < 3) { }
        }
    }
}
