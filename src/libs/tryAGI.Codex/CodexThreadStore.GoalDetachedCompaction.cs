namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    internal static bool ValidDetachedGoalReceipt(CodexThreadDocument thread, CodexStoredDetachedCompactionReceipt receipt,
        HashSet<Guid> usageIds)
    {
        var binding = receipt.GoalInference;
        var descendant = receipt.DescendantGoal;
        if (binding is null) return descendant is null;
        if (binding.OriginThreadId != thread.Id || binding.GoalId == Guid.Empty || binding.UsageId == Guid.Empty ||
            !usageIds.Add(binding.UsageId) ||
            receipt.Binding.TerminalSha256 != CodexRetainedJournalMetadata.ItemHash(receipt.Terminal)) return false;
        if (binding.GoalThreadId is null) return descendant is null;
        return descendant is not null && descendant.OriginThreadId == thread.Id &&
            descendant.RootThreadId == binding.GoalThreadId && descendant.GoalId == binding.GoalId &&
            descendant.RootThreadId != Guid.Empty && descendant.RootThreadId != thread.Id &&
            descendant.ParentThreadId != Guid.Empty && descendant.ParentThreadId != thread.Id &&
            descendant.RootRevision > 0 && !string.IsNullOrWhiteSpace(descendant.GraphRevision) &&
            descendant.GraphRevision.Length <= 1024;
    }

    private static CodexStoredThreadGoal? ApplyLocalDetachedGoal(CodexStoredThreadGoal? goal,
        Guid threadId, CodexStoredDetachedCompactionReceipt receipt, DateTimeOffset now)
    {
        if (receipt.GoalInference is not { GoalThreadId: null } binding || binding.OriginThreadId != threadId ||
            goal is null || goal.Id != binding.GoalId) return goal;
        var delta = GoalTokenDelta(receipt.Terminal,
            receipt.Binding.AccountingKind == CodexAbnormalInferenceAccounting.IncompleteObservation);
        if (delta is null or 0) return goal;
        var mode = goal.Status == "complete" ? CodexGoalAccountingMode.ActiveOrComplete : CodexGoalAccountingMode.ActiveOrStopped;
        return ApplyGoalUsageCore(goal, binding.UsageId, delta.Value, 0, mode, now);
    }

    private CodexThreadDocument AccountLocalDetachedGoal(CodexThreadDocument before,
        CodexThreadDocument next, CodexStoredDetachedCompactionReceipt receipt)
    {
        var now = time.GetUtcNow();
        var goal = ApplyLocalDetachedGoal(before.Goal, before.Id, receipt, now);
        if (ReferenceEquals(goal, before.Goal)) return next;

        // Do not count speculative compaction duration as execution time. If an
        // independently running goal is active in this store epoch, checkpoint its
        // existing execution clock before a usage-driven budget transition.
        var observed = ObserveLocalGoalMutation(before);
        if (!ReferenceEquals(observed.Goal, before.Goal))
            goal = ApplyLocalDetachedGoal(observed.Goal, before.Id, receipt, now);
        return RebindGoalClock(next with { Goal = goal, GoalExecutionClock = observed.GoalExecutionClock });
    }

    // The pre-turn adoption path allows only appended detached receipts. Prove
    // their exact derived local goal delta rather than ignoring goal mutations.
    internal static bool OnlyDetachedGoalAccountingChanged(CodexThreadDocument before, CodexThreadDocument after)
    {
        if (before.GoalExecutionClock is not null || after.GoalExecutionClock is not null) return false;
        var goal = before.Goal;
        var now = after.Goal?.UpdatedAt ?? default;
        foreach (var receipt in (after.DetachedCompactionReceipts ?? []).Skip(before.DetachedCompactionReceipts?.Length ?? 0))
            goal = ApplyLocalDetachedGoal(goal, before.Id, receipt, now);
        return SameGoalSnapshot(goal, after.Goal);
    }

    /// <summary>Reconcile a specific original detached descendant compaction receipt after
    /// an uncertain root write. No provider call, candidate recreation or history scan
    /// of other descendants occurs. Repeat the same receipt, not the paid compaction.</summary>
    public async Task<CodexDescendantGoalUsageOutcome> ReconcileDetachedDescendantGoalUsageAsync(Guid childThreadId,
        long expectedChildRevision, string requestId, CancellationToken cancellationToken = default)
    {
        Token(requestId);
        var child = await CurrentAsync(childThreadId, expectedChildRevision, cancellationToken).ConfigureAwait(false);
        return await AccountDetachedDescendantGoalAsync(child, requestId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CodexDescendantGoalUsageOutcome> AccountDetachedDescendantGoalAsync(
        CodexThreadDocument child, string requestId, CancellationToken ct)
    {
        var receipt = child.DetachedCompactionReceipts?.SingleOrDefault(item => item.RequestId == requestId);
        if (receipt is not { GoalInference: { GoalThreadId: not null } binding, DescendantGoal: { } attribution } ||
            child.AccountId != accountId || !ValidDetachedGoalReceipt(child, receipt, []))
            return CodexDescendantGoalUsageOutcome.NoEvidence;
        var delta = GoalTokenDelta(receipt.Terminal,
            receipt.Binding.AccountingKind == CodexAbnormalInferenceAccounting.IncompleteObservation);
        if (delta is null or 0) return CodexDescendantGoalUsageOutcome.NoEvidence;
        return await ApplyAttributedDescendantGoalAsync(attribution, binding, delta.Value, ct).ConfigureAwait(false);
    }
}
