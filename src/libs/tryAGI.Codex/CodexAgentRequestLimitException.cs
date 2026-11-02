namespace tryAGI.Codex;

/// <summary>The host's local reasoning budget ended; this is not a provider protocol failure.</summary>
/// <param name="maximumRequests">The request budget that was exhausted.</param>
/// <param name="executedToolCalls">Tool calls executed before the budget ran out.</param>
public sealed class CodexAgentRequestLimitException(int maximumRequests, int executedToolCalls)
    : InvalidOperationException($"Native agent exhausted its local budget of {maximumRequests} model requests after {executedToolCalls} executed tool calls; further tools were not executed.")
{
    /// <summary>Stable error code for hosts that report this failure.</summary>
    public const string ErrorCode = "agent_request_limit_exceeded";
    /// <summary>The request budget that was exhausted.</summary>
    public int MaximumRequests { get; } = maximumRequests;
    /// <summary>Tool calls executed before the budget ran out.</summary>
    public int ExecutedToolCalls { get; } = executedToolCalls;
}
