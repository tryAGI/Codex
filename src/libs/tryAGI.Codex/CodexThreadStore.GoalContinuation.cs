namespace tryAGI.Codex;

/// <summary>Durable identity of a host-authorized automatic goal turn, distinct from a user message.
/// Copied history retains its origin; it does not authorize execution on another thread.</summary>
/// <param name="OriginThreadId">Thread which admitted the continuation.</param>
/// <param name="GoalId">Exact goal generation.</param>
public sealed record CodexGoalContinuationBinding(Guid OriginThreadId, Guid GoalId);

public sealed partial class CodexThreadStore
{
    internal const string GoalTurnTrigger = "native:goal";

    internal static bool CanContinueGoal(CodexThreadDocument thread)
        => ReadStateEligible(thread) && thread.ArchivedAt is null && thread.Goal is { Status: "active" }
            && thread.DeferredGoalId is null && thread.QueuedSubmissions is not { Length: > 0 }
            && (thread.Turns.LastOrDefault() is null or { Status: "completed" });

    private static CodexThreadDocument PrepareGoalTurn(CodexThreadDocument thread, string? trigger, Guid? goalId)
    {
        if (trigger == GoalTurnTrigger)
        {
            if (goalId is null || !CanContinueGoal(thread) || thread.Goal?.Id != goalId)
                throw new CodexThreadConflictException();
            return thread;
        }
        if (goalId is not null) throw new ArgumentException("Goal continuation identity requires native goal trigger.");
        // An explicit or queued turn consumes the fork deferral in the same admission CAS.
        return thread with
        {
            DeferredGoalId = null,
            Goal = thread.Goal is { EmptyContinuationCount: > 0 } goal
                ? goal with { EmptyContinuationCount = null } : thread.Goal
        };
    }

    private static void ValidateGoalContinuation(CodexStoredTurn turn)
    {
        if (turn.GoalContinuation is { } binding)
        {
            if (turn.TurnTrigger != GoalTurnTrigger || binding.GoalId == Guid.Empty || binding.OriginThreadId == Guid.Empty
                || turn.Input.Length == 0 || CodexRealtimeProtocol.Text(turn.Input[0], "role") != "user"
                || turn.RealtimeSteeringInputIndices?.Contains(0) == true
                || turn.ClientUserMessageIds is not { Length: > 0 } clients || clients[0] is not null)
                throw new InvalidDataException("Invalid native goal continuation binding.");
        }
        else if (turn.TurnTrigger == GoalTurnTrigger)
            throw new InvalidDataException("Native goal continuation has no durable binding.");
    }

    private static void RequireGoalContinuationDispatch(CodexThreadDocument thread, CodexStoredTurn turn, string? nativeGoalTool = null)
    {
        if (turn.GoalContinuation is not { } binding) return;
        if (binding.OriginThreadId != thread.Id || thread.Goal is not { } goal || goal.Id != binding.GoalId ||
            goal.Status != "active" && !(goal.Status == "budgetLimited" && nativeGoalTool is CodexNativeGoalTools.Get or CodexNativeGoalTools.Update))
            throw new InvalidOperationException("The automatic goal turn no longer has an active goal generation.");
    }

    private CodexThreadDocument ApplyGoalTerminal(CodexThreadDocument thread, CodexStoredTurn turn)
    {
        if (turn.GoalContinuation is not { } binding || binding.OriginThreadId != thread.Id
            || thread.Goal is not { Status: "active" } goal || goal.Id != binding.GoalId) return thread;
        // A failed or interrupted automatic turn does not authorize another dispatch.
        // Match the three-empty-response circuit breaker for completed empty turns.
        if (turn.Status == "interrupted") return thread;
        var hasActivity = turn.Input.Length > 1 || turn.Dispatches.Any(d => d.Kind == "tool")
            || turn.Output.Any(item =>
            {
                var role = CodexRealtimeProtocol.Text(item, "role");
                var type = CodexRealtimeProtocol.Text(item, "type");
                if (role == "assistant" && type is null or "message")
                {
                    if (!item.TryGetProperty("content", out var content)) return false;
                    return content.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => !string.IsNullOrWhiteSpace(content.GetString()),
                        System.Text.Json.JsonValueKind.Array => content.EnumerateArray().Any(part =>
                            !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(part, "text"))),
                        _ => false
                    };
                }
                if (type == "reasoning") return item.TryGetProperty("summary", out var summary)
                    && summary.ValueKind == System.Text.Json.JsonValueKind.Array
                    && summary.EnumerateArray().Any(part => !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(part, "text")));
                return true;
            });
        var empty = turn.Status == "completed" && !hasActivity ? (goal.EmptyContinuationCount ?? 0) + 1 : 0;
        var status = turn.Status == "failed" || empty >= 3 ? "blocked" : goal.Status;
        if (status == goal.Status && empty == (goal.EmptyContinuationCount ?? 0)) return thread;
        return thread with { Goal = goal with
        {
            Status = status, EmptyContinuationCount = empty == 0 ? null : empty, UpdatedAt = time.GetUtcNow()
        } };
    }
}
