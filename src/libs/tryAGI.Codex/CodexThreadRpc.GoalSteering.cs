namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private bool goalSteeringEnabled;

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
}
