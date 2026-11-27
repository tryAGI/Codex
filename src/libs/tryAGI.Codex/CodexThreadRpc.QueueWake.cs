namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private int automaticQueueEnabled;
    private long queueWakeFailures;

    /// <summary>Enables automatic dispatch of durable queued messages on loaded idle threads.
    /// The host opts in to model execution through the existing authorized turn runner.
    /// Interrupted or unresolved turns require an explicit restart.</summary>
    public void EnableAutomaticThreadQueue()
    {
        EnableThreadQueue();
        Interlocked.Exchange(ref automaticQueueEnabled, 1);
    }

    /// <summary>Automatic wake attempts which could not start a turn. An unsuccessful attempt
    /// never deletes a pending submission or authorizes a retry of an uncertain admission.</summary>
    public long QueueWakeFailures => Interlocked.Read(ref queueWakeFailures);

    private async Task TryWakeQueueAsync(Guid id, Guid? completedTurnId = null)
    {
        if (Volatile.Read(ref automaticQueueEnabled) == 0 || Volatile.Read(ref stopping)
            || !loaded.ContainsKey(id) || active.ContainsKey(id) || archivePending.ContainsKey(id)) return;
        try
        {
            var snapshot = await store.ReadAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (snapshot.ArchivedAt is not null || !CodexThreadStore.ReadStateEligible(snapshot)
                || snapshot.QueuedSubmissions is not { Length: > 0 } pending) return;
            var latest = snapshot.Turns.LastOrDefault();
            // A user interruption survives reconnect and later enqueues. Only an explicitly
            // started turn can clear it. Unknown or failed outcomes also fail closed.
            if (latest is not null && latest.Status != "completed") return;
            if (completedTurnId is { } expected && latest?.Id != expected) return;
            // StartAsync checks loaded ownership, the idle state, exact revision and queue identity
            // again under admission; StartTurnAsync consumes the item in the same durable CAS.
            // There is no polling loop and no automatic retry of an ambiguous write.
            await StartQueuedAsync(snapshot, pending[0], CancellationToken.None).ConfigureAwait(false);
        }
        catch (CodexThreadConflictException) { }
        catch (KeyNotFoundException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Interlocked.Increment(ref queueWakeFailures);
        }
    }
}
