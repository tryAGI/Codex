using System.Collections.Concurrent;
using System.Text.Json;

namespace tryAGI.Codex;

// Captured at request-input capture, not at arrival of the first provider event.
internal sealed record CodexRealtimeReasoningObserver(Action<CodexResponseEvent> Provider, Action<JsonElement> Validated);

public sealed partial class CodexThreadRpc
{
    private readonly ConcurrentDictionary<Guid, CodexRealtimeCoordinator> realtimeObservers = new();

    internal void RegisterRealtimeObserver(Guid threadId, CodexRealtimeCoordinator coordinator)
    {
        if (!realtimeObservers.TryAdd(threadId, coordinator))
            throw new InvalidOperationException("A realtime reasoning observer is already attached to this thread.");
    }

    internal void UnregisterRealtimeObserver(Guid threadId, CodexRealtimeCoordinator coordinator)
        => ((ICollection<KeyValuePair<Guid, CodexRealtimeCoordinator>>)realtimeObservers).Remove(new(threadId, coordinator));
}
