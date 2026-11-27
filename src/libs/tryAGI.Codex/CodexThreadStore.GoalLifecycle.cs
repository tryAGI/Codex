using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Original local goal associated with a native turn at admission. An explicitly created
/// goal may bind a previously unbound turn; a delayed terminal cannot stop a replacement goal.</summary>
/// <param name="OriginThreadId">Original executing thread.</param>
/// <param name="GoalId">Exact goal generation.</param>
public sealed record CodexGoalTurnBinding(Guid OriginThreadId, Guid GoalId);

/// <summary>Observed reason a terminal turn stopped its original goal.</summary>
public enum CodexGoalStopReason
{
    /// <summary>Nonretryable or exhausted turn failure.</summary>
    TurnError,
    /// <summary>Typed provider allowance exhaustion, distinct from token budget exhaustion.</summary>
    UsageLimit,
    /// <summary>Three consecutive empty automatic goal turns.</summary>
    EmptyResponse,
    /// <summary>Three goal turns with a known failed execution handler and no successful tool.</summary>
    ExecutionUnavailable,
}

/// <summary>Host-observed tool outcome. Returning a textual result alone does not establish
/// whether the handler succeeded, was denied, or failed while executing.</summary>
public enum CodexToolOutcome
{
    /// <summary>Handler completed successfully.</summary>
    CompletedSuccess,
    /// <summary>Handler returned an unsuccessful result.</summary>
    CompletedUnsuccessful,
    /// <summary>The handler actually executed and failed.</summary>
    HandlerFailed,
    /// <summary>The action was blocked without execution.</summary>
    Blocked,
    /// <summary>The action was aborted.</summary>
    Aborted,
}

/// <summary>Goal generation and execution classification captured at durable tool admission,
/// with an optional exact host-observed outcome settled alongside the original tool receipt.</summary>
/// <param name="OriginThreadId">Original thread that admitted the call.</param>
/// <param name="GoalId">Active goal at admission.</param>
/// <param name="IsExecution">Default-namespace exec function, not an arbitrary named tool.</param>
/// <param name="Outcome">Known handler outcome, or null if not established.</param>
public sealed record CodexStoredGoalToolAttempt(Guid OriginThreadId, Guid GoalId, bool IsExecution,
    CodexToolOutcome? Outcome = null);

public sealed partial class CodexThreadStore
{
    private static CodexGoalTurnBinding? CaptureTurnGoal(CodexThreadDocument thread)
        => ReadStateEligible(thread) && thread.Goal is { Status: "active" or "budgetLimited" } goal
            ? new(thread.Id, goal.Id) : null;

    private static CodexStoredGoalToolAttempt? CaptureGoalToolAttempt(CodexThreadDocument thread, string kind, JsonElement request)
    {
        if (kind != "tool" || !ReadStateEligible(thread) || thread.Goal is not { Status: "active" } goal ||
            request.ValueKind != JsonValueKind.Object || CodexRealtimeProtocol.Text(request, "kind") is not ("Function" or "Custom") ||
            CodexRealtimeProtocol.Text(request, "name") is null) return null;
        var isExecution = CodexRealtimeProtocol.Text(request, "kind") == "Function" &&
            CodexRealtimeProtocol.Text(request, "name") == "exec" &&
            CodexToolCalls.NormalizeNamespace(CodexRealtimeProtocol.Text(request, "namespace")) == "functions";
        return new(thread.Id, goal.Id, isExecution);
    }

    private static void ValidateGoalLifecycle(CodexStoredTurn turn)
    {
        if (turn.GoalLifecycle is { } binding &&
            (binding.OriginThreadId == Guid.Empty || binding.GoalId == Guid.Empty ||
             turn.GoalContinuation is { } auto && (auto.OriginThreadId != binding.OriginThreadId || auto.GoalId != binding.GoalId)))
            throw new InvalidDataException("Invalid native goal lifecycle binding.");
        if (turn.GoalStopReason is { } reason &&
            (!Enum.IsDefined(reason) || turn.GoalLifecycle is null && turn.GoalContinuation is null ||
             turn.Status == "inProgress" || reason is CodexGoalStopReason.UsageLimit or CodexGoalStopReason.TurnError && turn.Status != "failed"))
            throw new InvalidDataException("Invalid native goal terminal reason.");
    }

    private static void ValidateGoalToolAttempt(CodexStoredDispatch receipt)
    {
        if (receipt.GoalToolAttempt is not { } attempt) return;
        if (receipt.Kind != "tool" || receipt.GoalToolSuppression is not null ||
            attempt.OriginThreadId == Guid.Empty || attempt.GoalId == Guid.Empty ||
            attempt.Outcome is { } outcome && (!Enum.IsDefined(outcome) || receipt.State != "succeeded" ||
                receipt.Result is not { } result ||
                CodexRealtimeProtocol.Text(result, "type") is not ("function_call_output" or "custom_tool_call_output") ||
                CodexRealtimeProtocol.Text(result, "call_id") != receipt.CallId))
            throw new InvalidDataException("Invalid native goal tool observation.");
    }

    private static CodexThreadDocument ResetGoalExecutionFailures(CodexThreadDocument thread, Guid origin, Guid goalId)
        => origin == thread.Id && thread.Goal is { ExecutionFailureCount: > 0 } goal && goal.Id == goalId
            ? thread with { Goal = goal with { ExecutionFailureCount = null } } : thread;

    private static bool HasGoalActivity(CodexStoredTurn turn)
    {
        if (turn.Input.Length > 1 || turn.Dispatches.Any(receipt => receipt.Kind == "tool") ||
            turn.NativeBackingArtifacts is { Length: > 0 }) return true;
        foreach (var item in turn.Output)
        {
            var role = CodexRealtimeProtocol.Text(item, "role");
            var type = CodexRealtimeProtocol.Text(item, "type");
            // Host steering and mode fragments describe state, not progress by the model.
            if (role is "developer" or "system") continue;
            if (role == "assistant" && type is null or "message")
            {
                if (item.TryGetProperty("content", out var content) &&
                    (content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString()) ||
                     content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(part =>
                         !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(part, "text"))))) return true;
                if (item.TryGetProperty("questions", out var questions) && questions.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return true;
                continue;
            }
            if (type == "reasoning")
            {
                if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array &&
                    summary.EnumerateArray().Any(part => !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(part, "text"))))
                    return true;
                continue;
            }
            return true;
        }
        return false;
    }

    private CodexThreadDocument ApplyGoalTerminal(CodexThreadDocument thread, CodexStoredTurn turn, bool usageLimited)
    {
        var binding = turn.GoalLifecycle ?? (turn.GoalContinuation is { } continuation
            ? new CodexGoalTurnBinding(continuation.OriginThreadId, continuation.GoalId) : null);
        if (binding is null || binding.OriginThreadId != thread.Id || thread.Goal is not { } goal ||
            goal.Id != binding.GoalId || goal.Status is not ("active" or "budgetLimited")) return thread;

        var empty = goal.EmptyContinuationCount ?? 0;
        var failures = goal.ExecutionFailureCount ?? 0;
        var reason = (CodexGoalStopReason?)null;
        var status = goal.Status;
        if (turn.Status == "failed")
        {
            empty = 0;
            if (usageLimited)
            {
                status = "usageLimited";
                reason = CodexGoalStopReason.UsageLimit;
            }
            else if (goal.Status == "active")
            {
                status = "blocked";
                reason = CodexGoalStopReason.TurnError;
            }
        }
        else if (goal.Status == "active")
        {
            var attempts = turn.Dispatches.Select(receipt => receipt.GoalToolAttempt)
                .Where(attempt => attempt is not null && attempt.OriginThreadId == thread.Id && attempt.GoalId == goal.Id).ToArray();
            var success = attempts.Any(attempt => attempt!.Outcome == CodexToolOutcome.CompletedSuccess) ||
                turn.Dispatches.Any(receipt => receipt.GoalToolResult is { } result && result.OriginThreadId == thread.Id && result.GoalId == goal.Id);
            if (success) failures = 0;
            else if (attempts.Any(attempt => attempt is { IsExecution: true, Outcome: CodexToolOutcome.HandlerFailed }))
                failures = Math.Min(3, failures + 1);

            // A confirmed interrupted automatic turn is also an empty response when
            // it has no activity. Lost-owner recovery never invents that observation.
            empty = turn.GoalContinuation is not null && !HasGoalActivity(turn)
                ? Math.Min(3, empty + 1) : 0;
            if (failures >= 3)
            {
                status = "blocked";
                reason = CodexGoalStopReason.ExecutionUnavailable;
            }
            else if (empty >= 3)
            {
                status = "blocked";
                reason = CodexGoalStopReason.EmptyResponse;
            }
        }
        if (status == goal.Status && empty == (goal.EmptyContinuationCount ?? 0) &&
            failures == (goal.ExecutionFailureCount ?? 0)) return thread;
        var next = thread with { Goal = goal with
        {
            Status = status, EmptyContinuationCount = empty == 0 ? null : empty,
            ExecutionFailureCount = failures == 0 ? null : failures, UpdatedAt = time.GetUtcNow()
        } };
        return reason is null ? next : Replace(next, turn with { GoalStopReason = reason });
    }
}
