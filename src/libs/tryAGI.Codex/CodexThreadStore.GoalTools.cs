using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Durable local goal-tool result binding. This records an observed mutation,
/// never permission to execute another call or recreate a goal.</summary>
/// <param name="OriginThreadId">Thread that executed the tool.</param>
/// <param name="GoalId">Generation returned by the tool, if any.</param>
/// <param name="Name">Original native tool name.</param>
/// <param name="Status">Returned goal status, if any.</param>
/// <param name="ResultSha256">Digest of the exact function output receipt.</param>
public sealed record CodexStoredGoalToolResult(Guid OriginThreadId, Guid? GoalId, string Name, string? Status,
    string ResultSha256);

public sealed partial class CodexThreadStore
{
    internal sealed record GoalToolCommit(CodexThreadDocument Thread, CodexToolOutput Output, bool Mutated);

    internal async Task<GoalToolCommit> SettleGoalToolAsync(Guid id, long revision, Guid turnId, string owner,
        CodexToolCall call, CodexNativeGoalTools.Command command, bool authorized, CancellationToken ct)
    {
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireGoalThread(before, command.Name != CodexNativeGoalTools.Get);
        var turn = Active(before, turnId, owner);
        var prior = turn.Dispatches.SingleOrDefault(item => item.CallId == call.Id);
        if (prior is not { Kind: "tool", State: "dispatching", GoalToolResult: null } ||
            prior.RequestSha256 != Digest(CodexNativeGoalTools.Request(call)) || call.Name != command.Name ||
            !CodexNativeGoalTools.IsGoalCall(call))
            throw new InvalidOperationException("Expected the exact unsettled native goal tool admission.");
        if (command.Name != CodexNativeGoalTools.Get && !authorized)
            throw new InvalidOperationException("Native goal mutation was not authorized.");
        if (CodexNativeGoalTools.Rejection(command, before.Goal) is { } rejection)
            throw new ArgumentException(rejection);
        RequireGoalContinuationDispatch(before, turn, command.Name);

        var notice = CaptureGoalNotice(before, turnId, mayCreate: command.Name == CodexNativeGoalTools.Create);
        before = ObserveGoalExecutionTime(before, turnId, true);
        var goal = before.Goal;
        var now = time.GetUtcNow();
        if (command.Name == CodexNativeGoalTools.Create)
            goal = new(Guid.CreateVersion7(), command.Objective!, "active", command.Budget, 0, 0, now, now);
        else if (command.Name == CodexNativeGoalTools.Update)
        {
            var status = command.Status!;
            if (goal!.Status == "budgetLimited" && status is "blocked" or "paused")
                status = "budgetLimited";
            goal = goal with { Status = status, UpdatedAt = now };
        }
        var output = CodexNativeGoalTools.Result(id, goal, command.Name == CodexNativeGoalTools.Update && command.Status == "complete");
        var result = CodexNativeGoalTools.Receipt(call, output);
        var committed = prior with
        {
            State = "succeeded", Result = result,
            GoalToolResult = new(id, goal?.Id, command.Name, goal?.Status, CodexRetainedJournalMetadata.ItemHash(result))
        };
        var updated = turn with { Dispatches = turn.Dispatches.Select(item => item.CallId == call.Id ? committed : item).ToArray() };
        var next = Replace(before, updated) with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = now, Goal = goal,
            DeferredGoalId = before.DeferredGoalId == goal?.Id ? before.DeferredGoalId : null
        };
        // The goal state and tool receipt have one CAS. Never emit a result or retry a
        // mutation when the CAS acknowledgement is ambiguous.
        next = PromoteRealtimeArtifacts(RebindGoalClock(next), turn, updated);
        return new(await PublishAsync(before.Revision, next, ct, notice).ConfigureAwait(false), output,
            command.Name != CodexNativeGoalTools.Get);
    }

    private static void ValidateGoalToolResult(CodexStoredDispatch receipt)
    {
        if (receipt.GoalToolResult is not { } binding) return;
        if (receipt.Kind != "tool" || receipt.State != "succeeded" || binding.OriginThreadId == Guid.Empty ||
            binding.GoalId == Guid.Empty || binding.Name is not (CodexNativeGoalTools.Get or CodexNativeGoalTools.Create or CodexNativeGoalTools.Update)
            || (binding.GoalId is null) != (binding.Status is null) ||
            binding.Status is not null && !ValidGoalStatus(binding.Status) ||
            binding.Name != CodexNativeGoalTools.Get && binding.GoalId is null ||
            receipt.Result is not { } result || CodexRealtimeProtocol.Text(result, "type") != "function_call_output" ||
            CodexRealtimeProtocol.Text(result, "call_id") != receipt.CallId ||
            binding.ResultSha256 != CodexRetainedJournalMetadata.ItemHash(result))
            throw new InvalidDataException("Invalid native goal tool result binding.");
    }

    // Recover only an exact, fully settled response's tool-output suffix. If any
    // member of a batch is unknown, retain the receipts without guessing history.
    internal static JsonElement[] RecoverGoalToolOutputs(CodexStoredTurn turn, JsonElement[] output)
    {
        var inference = turn.Dispatches.LastOrDefault(receipt => receipt.InferenceCompletion is not null);
        if (inference?.InferenceCompletion is not { } completion) return output;
        var boundary = completion.OutputStart + completion.OutputCount;
        if (boundary > output.Length) return output;
        var calls = output.Skip(completion.OutputStart).Take(completion.OutputCount)
            .Where(item => CodexRealtimeProtocol.Text(item, "type") is "function_call" or "custom_tool_call").ToArray();
        if (calls.Length == 0) return output;
        var receipts = calls.Select(item => turn.Dispatches.SingleOrDefault(receipt =>
            receipt.CallId == CodexRealtimeProtocol.Text(item, "call_id") && receipt.Kind == "tool")).ToArray();
        if (!receipts.Any(receipt => receipt?.GoalToolResult is not null) ||
            receipts.Any(receipt => receipt is not { State: "succeeded", Result: { } }) ||
            output.Length - boundary > receipts.Length) return output;
        var results = receipts.Select(receipt => receipt!.Result!.Value).ToArray();
        if (results.Where((item, index) => index < output.Length - boundary &&
            !JsonElement.DeepEquals(item, output[boundary + index])).Any()) return output;
        return [.. output, .. results.Skip(output.Length - boundary).Select(item => item.Clone())];
    }
}
