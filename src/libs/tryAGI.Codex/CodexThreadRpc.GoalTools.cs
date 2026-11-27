using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private CodexGoalToolPolicy? goalTools;

    /// <summary>Enables native model-facing get_goal, create_goal and update_goal for this host.
    /// Mutation authorization is distinct from an RPC origin string and must be bound to the
    /// exact call, goal generation and revision. The existing thread runner retains tool admission.</summary>
    public void EnableThreadGoalTools(Func<CodexGoalToolAuthorization, CancellationToken, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        var authority = Volatile.Read(ref goalAuthority)
            ?? throw new InvalidOperationException("Enable native goals before native goal tools.");
        var policy = new CodexGoalToolPolicy(authorize, authority.MaximumTokenBudget, (thread, turn) =>
            EmitAsync("thread/goal/updated", new JsonObject
            {
                ["threadId"] = thread.Id.ToString(), ["turnId"] = turn.ToString(), ["goal"] = Goal(thread.Id, thread.Goal)
            }));
        if (Interlocked.CompareExchange(ref goalTools, policy, null) is not null)
            throw new InvalidOperationException("Native goal tool authority is already installed.");
    }
}
