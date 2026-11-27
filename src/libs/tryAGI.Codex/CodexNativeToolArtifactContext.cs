using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Observation-only context bound to one already admitted native tool invocation.
/// The embedding host may report real public artifacts while its callback is executing.
/// This context does not grant permission to run a tool, launch an agent or access a file.</summary>
public sealed class CodexNativeToolArtifactContext
{
    private readonly Func<CodexNativeArtifactStage, JsonElement, CancellationToken, Task> observe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool closed;
    private bool mcpInvocationClaimed;

    internal CodexNativeToolArtifactContext(Guid threadId, Guid turnId, string accountId, string callId,
        string itemId, Func<CodexNativeArtifactStage, JsonElement, CancellationToken, Task> observe)
    {
        ThreadId = threadId;
        TurnId = turnId;
        AccountId = accountId;
        CallId = callId;
        ItemId = itemId;
        this.observe = observe;
    }

    /// <summary>The account-bound thread owning the call.</summary>
    public Guid ThreadId { get; }
    /// <summary>The receiving reasoning turn.</summary>
    public Guid TurnId { get; }
    /// <summary>The verified journal account.</summary>
    public string AccountId { get; }
    /// <summary>The exact admitted provider tool call identity.</summary>
    public string CallId { get; }
    /// <summary>The corresponding canonical backing item identity.</summary>
    public string ItemId { get; }

    /// <summary>Persist an actual canonical artifact observation. Only the current host callback
    /// may use this context. The store validates identity, lifecycle and source dispatch.</summary>
    public async Task ObserveAsync(CodexNativeArtifactStage stage, JsonElement item,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (closed) throw new InvalidOperationException("Native tool observation scope has ended.");
            await observe(stage, item, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task ClaimMcpInvocationAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (closed || mcpInvocationClaimed)
                throw new InvalidOperationException("This admitted native tool scope cannot invoke MCP again.");
            mcpInvocationClaimed = true;
        }
        finally { gate.Release(); }
    }

    internal async Task CloseAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { closed = true; }
        finally { gate.Release(); }
    }
}
