using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Host-owned durable admission and accounting, awaited before tool execution.
/// A dispatched failed stream has unknown usage. Observers must not retain prompts.</summary>
public interface ICodexUsageObserver
{
    /// <summary>Admits a model request before it is sent. Throw to refuse it.</summary>
    Task ReserveAsync(string requestId, string model, CancellationToken cancellationToken);
    /// <summary>Persist caller-owned wire identity with admission before it is sent.
    /// Existing observers keep their original accounting behavior and return no identity.</summary>
    async Task<CodexResponsesIdentity?> ReserveWithIdentityAsync(string requestId, string model, CancellationToken cancellationToken)
    {
        await ReserveAsync(requestId, model, cancellationToken).ConfigureAwait(false);
        return null;
    }
    /// <summary>Records a completed response, including its usage.</summary>
    Task CompleteAsync(string requestId, string model, JsonElement response, CancellationToken cancellationToken);
    /// <summary>Opt in to private prepared payloads; ordinary accounting observers incur no extra serialization.</summary>
    bool RequiresPreparedRequest => false;
    /// <summary>Called after financial reservation and credential resolution, before HTTP dispatch.
    /// A host journal can durably bind the exact request bytes here. Throwing prevents dispatch;
    /// NotDispatchedAsync still handles the financial reservation. The private payload must not be logged.</summary>
    Task PreparedAsync(string requestId, string model, JsonElement request, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>Records a dispatched request whose usage is unknown.</summary>
    Task IncompleteAsync(string requestId, string model, CancellationToken cancellationToken);
    /// <summary>Called only before entering HTTP dispatch. Older observers retain conservative accounting.</summary>
    Task NotDispatchedAsync(string requestId, string model, CancellationToken cancellationToken) =>
        IncompleteAsync(requestId, model, cancellationToken);
}
