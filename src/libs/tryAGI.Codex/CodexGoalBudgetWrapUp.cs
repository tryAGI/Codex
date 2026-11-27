using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Host authorization request for one tool-free, final provider request after a goal reaches its budget.
/// It does not authorize new substantive work, additional requests or a budget increase.</summary>
/// <param name="AccountId">Authenticated journal account.</param>
/// <param name="ThreadId">Original goal thread.</param>
/// <param name="TurnId">Currently admitted turn.</param>
/// <param name="GoalId">Goal generation whose budget was reached.</param>
/// <param name="ExpectedRevision">Observed durable journal revision.</param>
/// <param name="Objective">Original goal task data.</param>
/// <param name="TokensUsed">Observed token usage.</param>
/// <param name="TokenBudget">Exhausted goal budget.</param>
public sealed record CodexGoalBudgetWrapUpContext(string AccountId, Guid ThreadId, Guid TurnId, Guid GoalId,
    long ExpectedRevision, string Objective, long TokensUsed, long TokenBudget);

/// <summary>Durable fence for the single authorized budget wrap-up inference.</summary>
/// <param name="GoalId">Original goal generation.</param>
/// <param name="AuthorizedRevision">Exact snapshot revision offered to host policy.</param>
/// <param name="TokensUsed">Observed usage when authorization was granted.</param>
/// <param name="StateSha256">Digest of the observed goal state.</param>
public sealed record CodexStoredGoalWrapUp(Guid GoalId, long AuthorizedRevision, long TokensUsed, string StateSha256);

internal sealed record CodexNativeRequestPlan(CodexResponseOptions Options, string? LocalCompletion = null, bool TerminalRequest = false);

public sealed partial class CodexThreadStore
{
    internal static bool GoalBudgetLimitedForTurn(CodexThreadDocument thread, CodexStoredTurn turn)
        => thread.Goal is { Status: "budgetLimited" } goal &&
            (turn.GoalContinuation is { } continuation && continuation.OriginThreadId == thread.Id && continuation.GoalId == goal.Id
             || turn.Dispatches.Any(receipt => receipt.GoalInference is { GoalThreadId: null } binding &&
                binding.OriginThreadId == thread.Id && binding.GoalId == goal.Id));

    private static void RequireGoalWrapUp(CodexThreadDocument thread, CodexStoredTurn turn, JsonElement request,
        CodexGoalSteeringKey? key, CodexStoredGoalWrapUp permit)
    {
        if (thread.Goal is not { Status: "budgetLimited" } goal || !GoalBudgetLimitedForTurn(thread, turn) ||
            key is null || permit.GoalId != goal.Id || permit.TokensUsed != goal.TokensUsed ||
            permit.AuthorizedRevision < 1 || permit.AuthorizedRevision > thread.Revision ||
            permit.StateSha256 != key.Fingerprint || turn.Dispatches.Any(receipt => receipt.GoalWrapUp is not null) ||
            !request.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0 ||
            !request.TryGetProperty("parallel_tool_calls", out var parallel) || parallel.ValueKind != JsonValueKind.False ||
            CodexRealtimeProtocol.Text(request, "tool_choice") is not ("auto" or "none") ||
            !request.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Budget wrap-up requires one exact, tool-free request for the exhausted goal.");
        var last = input.EnumerateArray().LastOrDefault(CodexGoalSteeringContext.IsFragment);
        if (last.ValueKind == JsonValueKind.Undefined ||
            !thread.Turns.SelectMany(item => item.GoalSteeringTransitions ?? [])
                .Any(transition => transition.GoalId == goal.Id && transition.StateSha256 == permit.StateSha256 &&
                    transition.MessageSha256 == CodexRetainedJournalMetadata.ItemHash(last)))
            throw new InvalidOperationException("Budget wrap-up must retain the exact exhausted-goal context.");
    }

    private static string? SuppressSteeredTool(CodexThreadDocument thread, CodexStoredTurn turn,
        CodexGoalSteeringKey key, string? nativeGoalTool, bool terminalOnly)
    {
        var goal = thread.Goal;
        var sameObjective = key.SameObjective(goal);
        var localGoalTool = nativeGoalTool is CodexNativeGoalTools.Get or CodexNativeGoalTools.Update;
        if (!sameObjective || key.Status != goal?.Status &&
            !(key.Status == "active" && goal?.Status == "budgetLimited"))
            return "The goal objective, generation or status changed after sampling; inspect the current goal before requesting more work.";
        if ((terminalOnly || GoalBudgetLimitedForTurn(thread, turn)) && !localGoalTool)
            return "The goal budget is exhausted; this tool was not dispatched. Wrap up the verified progress.";
        return null;
    }

    private static JsonElement SuppressedGoalToolResult(CodexToolCall call, string reason)
        => CodexNativeGoalTools.Parse(new JsonObject
        {
            ["type"] = call.Kind == CodexToolCallKind.Custom ? "custom_tool_call_output" : "function_call_output",
            ["call_id"] = call.Id, ["output"] = CodexNativeGoalTools.Error(reason).Text
        }.ToJsonString());

    private static void ValidateGoalWrapUpAndSuppression(CodexStoredTurn turn)
    {
        var wrapUps = 0;
        foreach (var receipt in turn.Dispatches)
        {
            if (receipt.GoalWrapUp is { } wrapUp &&
                (++wrapUps > 1 || receipt.Kind != "inference" || receipt.GoalInference is not { GoalThreadId: null } binding ||
                 binding.GoalId != wrapUp.GoalId || wrapUp.AuthorizedRevision < 1 || wrapUp.TokensUsed < 0 ||
                 wrapUp.StateSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Invalid native goal budget wrap-up receipt.");
            if (receipt.GoalToolSuppression is { } reason &&
                (receipt.Kind != "tool" || receipt.State != "failed" || receipt.GoalToolResult is not null ||
                 string.IsNullOrWhiteSpace(reason) || reason.Length > 1024 || receipt.Result is not { } result ||
                 CodexRealtimeProtocol.Text(result, "type") is not ("function_call_output" or "custom_tool_call_output") ||
                 CodexRealtimeProtocol.Text(result, "call_id") != receipt.CallId ||
                 CodexRealtimeProtocol.Text(result, "output") != CodexNativeGoalTools.Error(reason).Text))
                throw new InvalidDataException("Invalid native goal tool suppression receipt.");
        }
    }
}
