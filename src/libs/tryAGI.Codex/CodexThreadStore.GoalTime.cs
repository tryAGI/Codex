namespace tryAGI.Codex;

/// <summary>Last confirmed monotonic accounting boundary of a live native turn.
/// A timestamp has meaning only inside its originating store epoch; recovery never
/// charges elapsed time across a lost execution owner or a different epoch.</summary>
/// <param name="TurnId">Exact active turn.</param>
/// <param name="Epoch">Process-local store clock identity.</param>
/// <param name="GoalId">Active goal generation, or null while goal accounting is inactive.</param>
/// <param name="Timestamp">Monotonic clock sample in the originating epoch.</param>
public sealed record CodexGoalExecutionClock(Guid TurnId, Guid Epoch, Guid? GoalId, long Timestamp);

public sealed partial class CodexThreadStore
{
    private readonly Guid goalClockEpoch = Guid.NewGuid();

    private static Guid? ActiveGoalId(CodexThreadDocument thread)
        => thread.Goal is { Status: "active" } goal ? goal.Id : null;

    private CodexThreadDocument BeginGoalExecutionClock(CodexThreadDocument thread, Guid turnId)
        => !ReadStateEligible(thread) ? thread : thread with
        {
            GoalExecutionClock = new(turnId, goalClockEpoch, ActiveGoalId(thread), time.GetTimestamp())
        };

    // Invoke only at an authoritative, owner-fenced execution boundary or a local
    // goal mutation. The cursor and whole-second/remainder accounting share the
    // enclosing CAS. No timers, extra writes or per-tick receipts are necessary.
    private CodexThreadDocument ObserveGoalExecutionTime(CodexThreadDocument thread, Guid turnId, bool liveOwner)
    {
        if (!ReadStateEligible(thread) ||
            thread.Turns.LastOrDefault() is not { Status: "inProgress" } turn || turn.Id != turnId)
            return thread;
        if (thread.GoalExecutionClock is not { } clock || clock.Epoch != goalClockEpoch || clock.TurnId != turnId)
        {
            // Another process or store instance owns the old monotonic timestamp.
            // Never derive elapsed time from UTC or from an unproven recovery interval.
            return liveOwner ? BeginGoalExecutionClock(thread, turnId) : thread with { GoalExecutionClock = null };
        }
        var now = time.GetTimestamp();
        if (now < clock.Timestamp) return thread;
        var goal = thread.Goal;
        if (clock.GoalId is { } goalId && goal is { Status: "active" } && goal.Id == goalId)
        {
            var ticks = Math.Max(0, time.GetElapsedTime(clock.Timestamp, now).Ticks);
            var seconds = ticks / TimeSpan.TicksPerSecond;
            var remainder = ticks % TimeSpan.TicksPerSecond + (goal.ElapsedRemainderTicks ?? 0);
            seconds = checked(seconds + remainder / TimeSpan.TicksPerSecond);
            remainder %= TimeSpan.TicksPerSecond;
            if (ticks != 0)
                goal = goal with
                {
                    TimeUsedSeconds = checked(goal.TimeUsedSeconds + seconds),
                    ElapsedRemainderTicks = remainder == 0 ? null : remainder,
                    UpdatedAt = seconds == 0 ? goal.UpdatedAt : time.GetUtcNow()
                };
        }
        return thread with
        {
            Goal = goal, GoalExecutionClock = clock with { Timestamp = now, GoalId = ActiveGoalId(thread) }
        };
    }

    private CodexThreadDocument ObserveLocalGoalMutation(CodexThreadDocument thread)
        => thread.Turns.LastOrDefault() is { Status: "inProgress" } turn
            ? ObserveGoalExecutionTime(thread, turn.Id, false) : thread;

    private static CodexThreadDocument RebindGoalClock(CodexThreadDocument thread)
        => thread.GoalExecutionClock is { } clock
            ? thread with { GoalExecutionClock = clock with { GoalId = ActiveGoalId(thread) } } : thread;

    private static void ValidateGoalExecutionClock(CodexThreadDocument thread)
    {
        if (thread.GoalExecutionClock is not { } clock) return;
        if (!ReadStateEligible(thread) || clock.Epoch == Guid.Empty || clock.TurnId == Guid.Empty ||
            thread.Turns.LastOrDefault() is not { Status: "inProgress" } active ||
            active.Id != clock.TurnId || clock.GoalId != ActiveGoalId(thread))
            throw new InvalidDataException("Invalid native goal execution clock.");
    }
}
