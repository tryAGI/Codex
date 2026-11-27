namespace tryAGI.Codex;

public sealed partial record CodexThreadDocument
{
    /// <summary>Durable goal snapshot. No persisted status independently authorizes model execution.</summary>
    public CodexStoredThreadGoal? Goal { get; init; }

    /// <summary>Forked goal generation whose continuation waits for the next explicit turn.</summary>
    public Guid? DeferredGoalId { get; init; }

    /// <summary>Monotonic active-turn goal accounting checkpoint. Never inherited by a fork.</summary>
    public CodexGoalExecutionClock? GoalExecutionClock { get; init; }
}

/// <summary>Versioned thread goal. Editing an objective preserves its identity and accrued usage.</summary>
/// <param name="Id">Goal generation, used to fence delayed usage accounting.</param>
/// <param name="Objective">Concrete goal objective.</param>
/// <param name="Status">App Server goal status.</param>
/// <param name="TokenBudget">Optional positive token limit.</param>
/// <param name="TokensUsed">Observed token usage.</param>
/// <param name="TimeUsedSeconds">Observed elapsed execution time.</param>
/// <param name="CreatedAt">Goal creation time.</param>
/// <param name="UpdatedAt">Last goal mutation time.</param>
/// <param name="UsageReceipts">Bounded durable deduplication receipts for observed usage.</param>
public sealed record CodexStoredThreadGoal(Guid Id, string Objective, string Status, long? TokenBudget,
    long TokensUsed, long TimeUsedSeconds, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    CodexGoalUsageReceipt[]? UsageReceipts = null)
{
    /// <summary>Consecutive empty automatic continuations; reset by explicit goal changes or turns.</summary>
    public int? EmptyContinuationCount { get; init; }

    /// <summary>Goal turns with a known failed exec handler and no successful tool; reset by success or explicit goal mutation.</summary>
    public int? ExecutionFailureCount { get; init; }

    /// <summary>Unreported fractional seconds retained across live accounting boundaries.</summary>
    public long? ElapsedRemainderTicks { get; init; }
}

/// <summary>Trusted accounting lane for one observed goal usage event.</summary>
public enum CodexGoalAccountingMode
{
    /// <summary>Only account an active goal.</summary>
    ActiveStatusOnly,
    /// <summary>Account an active or already budget-limited goal.</summary>
    ActiveOnly,
    /// <summary>Also account the final in-flight usage of a completed goal.</summary>
    ActiveOrComplete,
    /// <summary>Also account in-flight usage after pause, block or usage limitation.</summary>
    ActiveOrStopped,
    /// <summary>Account an original descendant receipt, including late usage of a stopped or completed goal.</summary>
    AttributedDescendant,
}

/// <summary>Stable receipt for one observed goal usage delta.</summary>
/// <param name="Id">Host-generated stable event identity.</param>
/// <param name="Tokens">Nonnegative token delta.</param>
/// <param name="Seconds">Nonnegative elapsed-time delta.</param>
/// <param name="Mode">Authorized accounting lane.</param>
public sealed record CodexGoalUsageReceipt(Guid Id, long Tokens, long Seconds, CodexGoalAccountingMode Mode);
