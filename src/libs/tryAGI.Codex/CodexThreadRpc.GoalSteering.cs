namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private bool goalSteeringEnabled;
    private Func<CodexGoalBudgetWrapUpContext, CancellationToken, Task<bool>>? goalWrapUpAuthority;

    /// <summary>Enables durable, generation-fenced model context for goal changes at
    /// sampling boundaries. Existing host permissions and provider admission still apply.
    /// No additional inference is authorized merely by enabling steering.</summary>
    public void EnableThreadGoalSteering()
    {
        if (Volatile.Read(ref goalAuthority) is null)
            throw new InvalidOperationException("Enable native thread goals before goal steering.");
        if (Volatile.Read(ref stopping))
            throw new InvalidOperationException("The native thread host is draining.");
        Volatile.Write(ref goalSteeringEnabled, true);
    }

    /// <summary>Allows at most one additional tool-free provider request to summarize an exhausted
    /// goal. The host must separately authorize that exact captured goal and usage snapshot;
    /// normal provider budget admission remains mandatory. Without approval, finish locally.</summary>
    public void EnableThreadGoalBudgetWrapUp(Func<CodexGoalBudgetWrapUpContext, CancellationToken, Task<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        EnableThreadGoalSteering();
        if (Interlocked.CompareExchange(ref goalWrapUpAuthority, authorize, null) is not null)
            throw new InvalidOperationException("Goal budget wrap-up authority is already installed.");
    }
}
