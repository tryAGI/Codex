using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Host authorization request for a goal mutation. Origin is caller-supplied provenance,
/// not proof of a user's authorization.</summary>
/// <param name="ThreadId">Account-bound thread identity.</param>
/// <param name="AccountId">Authenticated journal account.</param>
/// <param name="ExpectedRevision">Journal revision shown to the authorizer.</param>
/// <param name="Method">Goal RPC being authorized.</param>
/// <param name="Origin">User, automatic or omitted provenance.</param>
/// <param name="Parameters">Validated raw request for host policy.</param>
public sealed record CodexThreadGoalMutation(Guid ThreadId, string AccountId, long ExpectedRevision,
    string Method, string? Origin, JsonElement Parameters);

public sealed partial class CodexThreadRpc
{
    private sealed record GoalAuthority(Func<CodexThreadGoalMutation, CancellationToken, Task<bool>> Authorize, long? MaximumTokenBudget);
    private GoalAuthority? goalAuthority;

    /// <summary>Enables durable goal RPCs with an explicit host authority and optional managed
    /// budget ceiling. This installs no autonomous model runner; the host must authorize continuation separately.</summary>
    public void EnableThreadGoals(Func<CodexThreadGoalMutation, CancellationToken, Task<bool>> authorize, long? maximumTokenBudget = null)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        CodexThreadStore.ValidateGoalBudget(maximumTokenBudget, null);
        if (Interlocked.CompareExchange(ref goalAuthority, new(authorize, maximumTokenBudget), null) is not null)
            throw new InvalidOperationException("Native goal authority is already installed.");
    }

    internal static JsonObject? Goal(Guid id, CodexStoredThreadGoal? goal) => goal is null ? null : new JsonObject
    {
        ["threadId"] = id.ToString(), ["objective"] = goal.Objective, ["status"] = goal.Status,
        ["tokenBudget"] = goal.TokenBudget, ["tokensUsed"] = goal.TokensUsed, ["timeUsedSeconds"] = goal.TimeUsedSeconds,
        ["createdAt"] = goal.CreatedAt.ToUnixTimeSeconds(), ["updatedAt"] = goal.UpdatedAt.ToUnixTimeSeconds()
    };

    private async Task<JsonElement> GoalAsync(string method, JsonElement p, CancellationToken ct)
    {
        var authority = Volatile.Read(ref goalAuthority) ?? throw new CodexThreadRpcException(-32601, "Native goals are not enabled.");
        var id = Id(p, "threadId");
        if (method == "thread/goal/get")
        {
            ValidateFields(p, ["threadId"]);
            var thread = await store.ReadAsync(id, ct).ConfigureAwait(false);
            CodexThreadStore.RequireGoalThread(thread, false);
            return Element(new JsonObject { ["goal"] = Goal(id, thread.Goal) });
        }

        var clearing = method == "thread/goal/clear";
        ValidateFields(p, clearing ? ["threadId", "origin"] : ["threadId", "origin", "objective", "status", "tokenBudget"]);
        var origin = Text(p, "origin");
        if (origin is not (null or "user" or "automatic")) throw Invalid("Unknown goal mutation origin.");
        var objective = Text(p, "objective");
        if (objective is not null) objective = CodexThreadStore.NormalizeGoalObjective(objective);
        var status = Text(p, "status");
        if (status is not (null or "active" or "paused" or "blocked" or "usageLimited" or "budgetLimited" or "complete"))
            throw Invalid("Unknown goal status.");
        var budgetSpecified = p.TryGetProperty("tokenBudget", out var value);
        long? budget = null;
        if (budgetSpecified && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var requested))
                throw Invalid("Goal token budget must be an integer or null.");
            budget = requested;
        }
        if (budgetSpecified) CodexThreadStore.ValidateGoalBudget(budget, authority.MaximumTokenBudget);
        var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
        CodexThreadStore.RequireGoalThread(before, true);
        if (!clearing && objective is null && before.Goal is null) throw Invalid("Cannot update goal: no goal exists.");
        if (!await authority.Authorize(new(id, before.AccountId, before.Revision, method, origin, p.Clone()), ct).ConfigureAwait(false))
            throw new CodexThreadRpcException(-32000, "Goal mutation was not authorized.");

        CodexThreadDocument updated;
        bool cleared = false;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckAttachmentAdmission(id);
            if (clearing) (updated, cleared) = await store.ClearGoalAsync(id, before.Revision, ct).ConfigureAwait(false);
            else updated = await store.SetGoalAsync(id, before.Revision, objective, status, budgetSpecified, budget,
                authority.MaximumTokenBudget, ct).ConfigureAwait(false);
        }
        finally { admission.Release(); }
        if (clearing)
        {
            if (cleared && Volatile.Read(ref liveGoalProgress))
            {
                ReconcileGoalProgress(updated, null);
                await FlushGoalProgressAsync(id, ct).ConfigureAwait(false);
            }
            else if (cleared) await EmitAsync("thread/goal/cleared", new JsonObject { ["threadId"] = id.ToString() }).ConfigureAwait(false);
            return Element(new JsonObject { ["cleared"] = cleared });
        }
        if (Volatile.Read(ref liveGoalProgress))
        {
            ReconcileGoalProgress(updated, null);
            await FlushGoalProgressAsync(id, ct).ConfigureAwait(false);
        }
        else await EmitAsync("thread/goal/updated", new JsonObject
        {
            ["threadId"] = id.ToString(), ["turnId"] = null, ["goal"] = Goal(id, updated.Goal)
        }).ConfigureAwait(false);
        if (updated.Goal?.Status == "active")
        {
            await TryWakeQueueAsync(id).ConfigureAwait(false);
            await TryWakeGoalAsync(id, "goalSet").ConfigureAwait(false);
        }
        return Element(new JsonObject { ["goal"] = Goal(id, updated.Goal) });
    }

    private async Task NotifyTerminalGoalUsageAsync(CodexThreadDocument thread, CodexStoredTurn turn)
    {
        if (Volatile.Read(ref liveGoalProgress))
        {
            ReconcileGoalProgress(thread, turn.Id);
            return;
        }
        if (Volatile.Read(ref goalAuthority) is null || thread.Goal is not { } goal) return;
        var accounted = (goal.UsageReceipts ?? []).Select(receipt => receipt.Id).ToHashSet();
        var continuation = turn.GoalContinuation is { } auto && auto.OriginThreadId == thread.Id && auto.GoalId == goal.Id;
        if (!continuation && !turn.Dispatches.Any(dispatch => dispatch.GoalInference is { } binding
            && binding.OriginThreadId == thread.Id && binding.GoalId == goal.Id && accounted.Contains(binding.UsageId))) return;
        // The result was committed together with the dispatch and is available without another read.
        await EmitAsync("thread/goal/updated", new JsonObject
        {
            ["threadId"] = thread.Id.ToString(), ["turnId"] = turn.Id.ToString(), ["goal"] = Goal(thread.Id, goal)
        }).ConfigureAwait(false);
    }
}
