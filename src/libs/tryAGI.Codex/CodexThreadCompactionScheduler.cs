using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Host-owned, bounded speculative compaction lifetime. Reuse across turns and dispose
/// at session shutdown. The scheduler never installs history or resumes an abandoned turn itself.</summary>
public sealed class CodexThreadCompactionScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<Guid, Slot> slots = [];
    private readonly int capacity;
    private bool disposed;
    private long started, completed, failed, discarded;
    private Task? disposal;

    /// <summary>Create a bounded scheduler. The host must explicitly enable background compaction.</summary>
    public CodexThreadCompactionScheduler(int maximumThreads = 16)
    {
        if (maximumThreads is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumThreads));
        capacity = maximumThreads;
    }

    /// <summary>Speculative attempts started, including requests refused by budget admission.</summary>
    public long Started => Interlocked.Read(ref started);
    /// <summary>Successful provider compaction results, including discarded candidates.</summary>
    public long Completed => Interlocked.Read(ref completed);
    /// <summary>Unsuccessful attempts, including shutdown cancellation.</summary>
    public long Failed => Interlocked.Read(ref failed);
    /// <summary>Candidates discarded because their captured scope or prefix is stale.</summary>
    public long Discarded => Interlocked.Read(ref discarded);

    internal sealed record AuthOwner(string AccountId, string? UserId, bool Restricted)
    {
        internal static AuthOwner From(CodexCredentials credentials) =>
            new(credentials.AccountId, credentials.ChatGptUserId, credentials.IsFedRampAccount);
    }

    internal sealed record Ready(CodexThreadCompactionCandidate Candidate, CodexCompactionResult Result);

    private sealed class Slot(CodexThreadStore store, ICodexUsageObserver budget, AuthOwner owner,
        CodexThreadCompactionCandidate candidate)
    {
        internal CodexThreadStore Store { get; } = store;
        internal ICodexUsageObserver Budget { get; } = budget;
        internal AuthOwner Owner { get; } = owner;
        internal CodexThreadCompactionCandidate Candidate { get; } = candidate;
        internal Task<CodexCompactionResult?> Task { get; set; } = null!;
        internal bool Claimed { get; set; }
        internal bool Consumed { get; set; }
    }

    internal bool TrySchedule(CodexThreadStore store, ICodexUsageObserver budget, AuthOwner owner,
        CodexThreadCompactionCandidate candidate, Func<CancellationToken, Task<CodexCompactionResult>> run)
    {
        lock (gate)
        {
            if (disposed) return false;
            if (slots.TryGetValue(candidate.ThreadId, out var prior))
            {
                if (!prior.Task.IsCompleted || !prior.Consumed || prior.Claimed) return false;
                if (ReferenceEquals(prior.Store, store) && ReferenceEquals(prior.Budget, budget) && prior.Owner == owner &&
                    prior.Candidate.HistorySha256 == candidate.HistorySha256 &&
                    prior.Candidate.ActiveBoundary == candidate.ActiveBoundary &&
                    prior.Candidate.ScopeSha256 == candidate.ScopeSha256 &&
                    prior.Candidate.PriorCheckpointRevision == candidate.PriorCheckpointRevision &&
                    prior.Candidate.PriorCheckpointResponseId == candidate.PriorCheckpointResponseId &&
                    JsonElement.DeepEquals(prior.Candidate.CapturedSettings, candidate.CapturedSettings))
                    return false;
            }
            else if (slots.Count >= capacity)
            {
                var removable = slots.FirstOrDefault(pair => pair.Value.Consumed && !pair.Value.Claimed && pair.Value.Task.IsCompleted);
                if (removable.Value is null) return false;
                slots.Remove(removable.Key);
            }
            var slot = new Slot(store, budget, owner, candidate);
            // Publish the slot under the lock before its asynchronous work can be adopted or disposed.
            slot.Task = Task.Run(() => RunAsync(run));
            slots[candidate.ThreadId] = slot;
            Interlocked.Increment(ref started);
            return true;
        }
    }

    private async Task<CodexCompactionResult?> RunAsync(Func<CancellationToken, Task<CodexCompactionResult>> run)
    {
        try
        {
            var result = await run(shutdown.Token).ConfigureAwait(false);
            Interlocked.Increment(ref completed);
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Financial completion/incomplete accounting belongs to the guarded client.
            // A speculative failure must not fail a previously completed foreground turn.
            Interlocked.Increment(ref failed);
            return null;
        }
    }

    internal async Task<Ready?> TakeAsync(CodexThreadStore store, ICodexUsageObserver budget, AuthOwner owner,
        CodexThreadDocument current, CodexResponseOptions settings, bool wait, CancellationToken ct,
        Guid? activeTurnId = null, string? executionOwner = null)
    {
        Slot slot;
        lock (gate)
        {
            if (disposed || !slots.TryGetValue(current.Id, out var found) || found.Consumed || found.Claimed)
                return null;
            slot = found;
            if (!ReferenceEquals(slot.Store, store) || !ReferenceEquals(slot.Budget, budget) || slot.Owner != owner ||
                !CodexThreadStore.CanAdoptBackgroundCompaction(current, slot.Candidate, settings, activeTurnId, executionOwner))
            {
                slot.Consumed = true;
                Interlocked.Increment(ref discarded);
                return null;
            }
            if (!wait && !slot.Task.IsCompleted) return null;
            slot.Claimed = true;
        }
        try
        {
            var result = await slot.Task.WaitAsync(ct).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed) return null;
                slot.Consumed = true;
                return result is null ? null : new(slot.Candidate, result);
            }
        }
        finally
        {
            lock (gate) slot.Claimed = false;
        }
    }

    internal void RecordPreparationFailure() => Interlocked.Increment(ref failed);
    internal void RecordDiscard() => Interlocked.Increment(ref discarded);

    internal Task WaitForPendingAsync(Guid threadId, CancellationToken ct)
    {
        lock (gate)
            return slots.TryGetValue(threadId, out var slot) ? slot.Task.WaitAsync(ct) : Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            var tasks = slots.Values.Select(slot => (Task)slot.Task).ToArray();
            disposal = Task.Run(async () =>
            {
                try
                {
                    await shutdown.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                finally { shutdown.Dispose(); }
            });
            return new(disposal);
        }
    }
}
