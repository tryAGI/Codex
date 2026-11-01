namespace tryAGI.Codex;

/// <summary>The host's local reasoning budget ended; this is not a provider protocol failure.</summary>
public sealed class CodexAgentRequestLimitException(int maximumRequests, int executedToolCalls)
    : InvalidOperationException($"Native agent exhausted its local budget of {maximumRequests} model requests after {executedToolCalls} executed tool calls; further tools were not executed.")
{
    public const string ErrorCode = "agent_request_limit_exceeded";
    public int MaximumRequests { get; } = maximumRequests;
    public int ExecutedToolCalls { get; } = executedToolCalls;
}
