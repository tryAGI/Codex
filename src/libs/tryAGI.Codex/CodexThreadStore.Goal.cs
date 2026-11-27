using System.Text;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    private const int MaximumGoalUsageReceipts = 4096;

    internal static void RequireGoalThread(CodexThreadDocument thread, bool mutate)
    {
        RequireDurableThread(thread);
        RequireUnarchived(thread);
        if (mutate && !ReadStateEligible(thread))
            throw new ArgumentException("Direct goal mutation is unavailable for this thread source.");
    }

    private static bool ValidGoalStatus(string? status)
        => status is "active" or "paused" or "blocked" or "usageLimited" or "budgetLimited" or "complete";

    internal static string NormalizeGoalObjective(string objective)
    {
        var trimmed = objective.Trim();
        var count = 0;
        foreach (var _ in trimmed.EnumerateRunes())
            if (++count > 4000) throw new ArgumentException("Goal objective must be at most 4000 characters.");
        if (count == 0) throw new ArgumentException("Goal objective must not be empty.");
        return trimmed;
    }

    internal static void ValidateGoalBudget(long? budget, long? maximum)
    {
        if (budget is <= 0) throw new ArgumentException("Goal budgets must be positive when provided.");
        if (budget is { } requested && maximum is { } limit && requested > limit)
            throw new ArgumentException("Goal token budget exceeds the maximum allowed budget.");
    }

    private static void ValidateGoal(CodexStoredThreadGoal? goal)
    {
        if (goal is null) return;
        if (goal.Id == Guid.Empty || goal.Objective is null || NormalizeGoalObjective(goal.Objective) != goal.Objective
            || !ValidGoalStatus(goal.Status) || goal.TokenBudget is <= 0 || goal.TokensUsed < 0 || goal.TimeUsedSeconds < 0
            || goal.CreatedAt == default || goal.UpdatedAt == default
            || goal.Status == "active" && goal.TokenBudget is { } budget && goal.TokensUsed >= budget
            || goal.EmptyContinuationCount is < 0 or > 3
            || goal.ElapsedRemainderTicks is < 0 or >= TimeSpan.TicksPerSecond
            || goal.UsageReceipts is { Length: > MaximumGoalUsageReceipts })
            throw new InvalidDataException("Invalid native goal snapshot.");
        var ids = new HashSet<Guid>();
        foreach (var receipt in goal.UsageReceipts ?? [])
            if (receipt is null || receipt.Id == Guid.Empty || !ids.Add(receipt.Id) || receipt.Tokens < 0
                || receipt.Seconds < 0 || !Enum.IsDefined(receipt.Mode))
                throw new InvalidDataException("Invalid native goal accounting receipt.");
    }

    internal async Task<CodexThreadDocument> SetGoalAsync(Guid id, long revision, string? objective, string? status,
        bool budgetSpecified, long? budget, long? maximumBudget, CancellationToken ct)
    {
        if (objective is not null) objective = NormalizeGoalObjective(objective);
        if (status is not null && !ValidGoalStatus(status)) throw new ArgumentException("Unknown goal status.");
        if (budgetSpecified) ValidateGoalBudget(budget, maximumBudget);
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireGoalThread(before, true);
        before = ObserveLocalGoalMutation(before);
        var previous = before.Goal;
        if (previous is null && objective is null) throw new ArgumentException("Cannot update goal: no goal exists.");
        var effectiveBudget = budgetSpecified ? budget ?? maximumBudget : previous is null ? maximumBudget : previous.TokenBudget;
        var effectiveStatus = status ?? previous?.Status ?? "active";
        if (previous?.Status == "budgetLimited" && effectiveStatus is "paused" or "blocked")
            effectiveStatus = "budgetLimited";
        var tokensUsed = previous?.TokensUsed ?? 0;
        if (effectiveStatus == "active" && effectiveBudget is { } limit && tokensUsed >= limit)
            effectiveStatus = "budgetLimited";
        var now = time.GetUtcNow();
        var goal = new CodexStoredThreadGoal(previous?.Id ?? Guid.CreateVersion7(), objective ?? previous!.Objective,
            effectiveStatus, effectiveBudget, tokensUsed, previous?.TimeUsedSeconds ?? 0, previous?.CreatedAt ?? now,
            now, previous?.UsageReceipts) { ElapsedRemainderTicks = previous?.ElapsedRemainderTicks };
        return await WriteGoalAsync(before, goal, ct).ConfigureAwait(false);
    }

    internal async Task<(CodexThreadDocument Thread, bool Cleared)> ClearGoalAsync(Guid id, long revision, CancellationToken ct)
    {
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireGoalThread(before, true);
        if (before.Goal is null) return (before, false);
        return (await WriteGoalAsync(ObserveLocalGoalMutation(before), null, ct).ConfigureAwait(false), true);
    }

    /// <summary>Accounts one trusted, observed usage delta for the exact goal generation. Duplicate
    /// receipts are idempotent; a replaced or cleared goal is never charged by an old event.
    /// The host must supply actual observed usage and retain the event ID across retries.</summary>
    public async Task<CodexThreadDocument> RecordGoalUsageAsync(Guid threadId, long expectedRevision, Guid expectedGoalId,
        Guid usageId, long tokenDelta, long elapsedSeconds, CodexGoalAccountingMode mode,
        CancellationToken cancellationToken = default)
    {
        Id(expectedGoalId); Id(usageId);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown goal accounting mode.", nameof(mode));
        tokenDelta = Math.Max(0, tokenDelta);
        elapsedSeconds = Math.Max(0, elapsedSeconds);
        var before = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (before.Goal is not { } goal || goal.Id != expectedGoalId) return before;
        var updated = ApplyGoalUsage(goal, usageId, tokenDelta, elapsedSeconds, mode);
        if (ReferenceEquals(goal, updated)) return before;
        var observed = ObserveLocalGoalMutation(before);
        if (!ReferenceEquals(goal, observed.Goal))
            updated = ApplyGoalUsage(observed.Goal!, usageId, tokenDelta, elapsedSeconds, mode);
        return await WriteGoalAsync(observed, updated, cancellationToken).ConfigureAwait(false);
    }

    private CodexStoredThreadGoal ApplyGoalUsage(CodexStoredThreadGoal goal, Guid usageId, long tokenDelta,
        long elapsedSeconds, CodexGoalAccountingMode mode)
    {
        var receipts = goal.UsageReceipts ?? [];
        if (receipts.SingleOrDefault(receipt => receipt.Id == usageId) is { } previous)
        {
            if (previous.Tokens != tokenDelta || previous.Seconds != elapsedSeconds || previous.Mode != mode)
                throw new ArgumentException("Goal usage identity already has different accounting.");
            return goal;
        }
        var eligible = mode switch
        {
            CodexGoalAccountingMode.ActiveStatusOnly => goal.Status == "active",
            CodexGoalAccountingMode.ActiveOnly => goal.Status is "active" or "budgetLimited",
            CodexGoalAccountingMode.ActiveOrComplete => goal.Status is "active" or "budgetLimited" or "complete",
            CodexGoalAccountingMode.ActiveOrStopped => goal.Status is "active" or "paused" or "blocked" or "usageLimited" or "budgetLimited",
            CodexGoalAccountingMode.AttributedDescendant => true,
            _ => false
        };
        if (!eligible || tokenDelta == 0 && elapsedSeconds == 0) return goal;
        if (receipts.Length >= MaximumGoalUsageReceipts) throw new InvalidOperationException("Goal accounting receipt capacity exhausted.");
        var tokens = checked(goal.TokensUsed + tokenDelta);
        var seconds = checked(goal.TimeUsedSeconds + elapsedSeconds);
        var limited = (goal.Status == "active" || mode == CodexGoalAccountingMode.ActiveOrStopped
            || mode == CodexGoalAccountingMode.AttributedDescendant && goal.Status != "complete")
            && goal.TokenBudget is { } budget && tokens >= budget;
        return goal with
        {
            TokensUsed = tokens, TimeUsedSeconds = seconds, Status = limited ? "budgetLimited" : goal.Status,
            UpdatedAt = time.GetUtcNow(), UsageReceipts = [.. receipts, new(usageId, tokenDelta, elapsedSeconds, mode)]
        };
    }

    private Task<CodexThreadDocument> WriteGoalAsync(CodexThreadDocument before, CodexStoredThreadGoal? goal, CancellationToken ct)
        => PublishAsync(before.Revision, RebindGoalClock(before with { Revision = checked(before.Revision + 1), Goal = goal,
            DeferredGoalId = goal?.Id == before.DeferredGoalId ? before.DeferredGoalId : null }), ct);

    private static CodexStoredThreadGoal? ForkGoal(CodexThreadDocument source, System.Text.Json.JsonElement settings)
        => settings.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind == System.Text.Json.JsonValueKind.True
            ? null : source.Goal is { } goal ? goal with { EmptyContinuationCount = null, ElapsedRemainderTicks = null } : null;
}
