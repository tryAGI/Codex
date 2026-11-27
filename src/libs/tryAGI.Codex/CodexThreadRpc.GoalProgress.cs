using System.Text.Json.Nodes;

namespace tryAGI.Codex;

// The live lane carries only wire-visible goal state. Receipts, execution clocks and
// the rest of the potentially large thread document never enter its delivery queue.
internal sealed record CodexGoalNotice(Guid ThreadId, long Revision, Guid? TurnId, CodexStoredThreadGoal? Goal)
{
    internal static CodexStoredThreadGoal? Project(CodexStoredThreadGoal? goal)
        => goal is null ? null : goal with
        {
            UsageReceipts = null, EmptyContinuationCount = null, ElapsedRemainderTicks = null,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(goal.CreatedAt.ToUnixTimeSeconds()),
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(goal.UpdatedAt.ToUnixTimeSeconds())
        };

    internal static CodexGoalNotice From(CodexThreadDocument thread, Guid? turnId)
        => new(thread.Id, thread.Revision, turnId, Project(thread.Goal));
}

public sealed partial class CodexThreadStore
{
    internal event Action<CodexGoalNotice>? GoalProgressCommitted;

    private CodexGoalNotice? CaptureGoalNotice(CodexThreadDocument before, Guid? turnId, bool mayCreate = false)
        => GoalProgressCommitted is null || before.Goal is null && !mayCreate ? null : CodexGoalNotice.From(before, turnId);

    private void NotifyGoalCommit(CodexThreadDocument next, CodexGoalNotice? baseline)
    {
        var listeners = GoalProgressCommitted;
        if (listeners is null || baseline is null) return;
        var current = CodexGoalNotice.From(next, baseline.TurnId);
        if (current.Goal == baseline.Goal) return;
        foreach (var listener in listeners.GetInvocationList())
        {
            try { ((Action<CodexGoalNotice>)listener)(current); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { /* Observers cannot invalidate an acknowledged storage commit. */ }
        }
    }
}

public sealed partial class CodexThreadRpc
{
    private readonly Lock goalNoticeGate = new();
    private readonly Dictionary<Guid, GoalNoticeStream> goalNotices = [];
    private const int MaximumGoalNoticeStreams = 1024;
    private bool liveGoalProgress;
    private bool goalNoticesStopped;

    /// <summary>Opt into account-scoped live goal notifications from confirmed commits in this
    /// store instance. Enable goal RPCs first. Goal snapshots are coalesced per thread, ordered
    /// by committed revision, and never block provider or tool execution. A reconnect or
    /// uncertain commit is reconciled through the authoritative goal read.</summary>
    public void EnableLiveGoalProgress()
    {
        if (Volatile.Read(ref goalAuthority) is null)
            throw new InvalidOperationException("Enable native thread goals before live goal progress.");
        lock (goalNoticeGate)
        {
            if (goalNoticesStopped || Volatile.Read(ref stopping))
                throw new InvalidOperationException("The native thread host is draining.");
            if (liveGoalProgress) return;
            store.GoalProgressCommitted += OfferGoalProgress;
            Volatile.Write(ref liveGoalProgress, true);
        }
    }

    private void OfferGoalProgress(CodexGoalNotice notice)
    {
        lock (goalNoticeGate)
        {
            if (goalNoticesStopped) return;
            if (!goalNotices.TryGetValue(notice.ThreadId, out var stream))
            {
                // Preserve the revision high-water mark rather than evicting a stream and
                // accepting an older concurrent commit. A full catalog needs a new subscriber.
                if (goalNotices.Count >= MaximumGoalNoticeStreams)
                {
                    Interlocked.Increment(ref notificationFailures);
                    return;
                }
                goalNotices.Add(notice.ThreadId, stream = new(this));
            }
            stream.Offer(notice);
        }
    }

    private void ReconcileGoalProgress(CodexThreadDocument thread, Guid? turnId)
    {
        if (!Volatile.Read(ref liveGoalProgress)) return;
        lock (goalNoticeGate)
        {
            if (thread.Goal is null && !goalNotices.ContainsKey(thread.Id)) return;
            OfferGoalProgress(CodexGoalNotice.From(thread, turnId));
        }
    }

    private async Task StopGoalProgressAsync(CancellationToken ct)
    {
        GoalNoticeStream[] streams;
        lock (goalNoticeGate)
        {
            if (goalNoticesStopped) return;
            goalNoticesStopped = true;
            store.GoalProgressCommitted -= OfferGoalProgress;
            streams = goalNotices.Values.ToArray();
            goalNotices.Clear();
        }
        try { await Task.WhenAll(streams.Select(stream => stream.FlushAsync())).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        { Interlocked.Increment(ref notificationFailures); }
        finally { foreach (var stream in streams) stream.Stop(); }
    }

    private async Task FlushGoalProgressAsync(Guid id, CancellationToken ct)
    {
        GoalNoticeStream? stream;
        lock (goalNoticeGate) goalNotices.TryGetValue(id, out stream);
        if (stream is null) return;
        try { await stream.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        { Interlocked.Increment(ref notificationFailures); }
    }

    // One in-flight and one replaceable pending state per thread. A later clear or
    // replacement cannot overtake an earlier in-flight delivery. Intermediate
    // progress can be coalesced because this notification is a complete snapshot.
    private sealed class GoalNoticeStream(CodexThreadRpc owner)
    {
        private readonly Lock gate = new();
        private CodexGoalNotice? pending;
        private CodexStoredThreadGoal? latest;
        private long offered;
        private Task? worker;
        private bool stopped;

        internal void Offer(CodexGoalNotice notice)
        {
            lock (gate)
            {
                if (stopped || notice.Revision <= offered) return;
                var first = offered == 0;
                offered = notice.Revision;
                if (!first && notice.Goal == latest) return;
                latest = notice.Goal;
                pending = notice;
                worker ??= Task.Run(DrainAsync);
            }
        }

        internal Task FlushAsync() { lock (gate) return worker ?? Task.CompletedTask; }
        internal void Stop() { lock (gate) { stopped = true; pending = null; } }

        private async Task DrainAsync()
        {
            while (true)
            {
                CodexGoalNotice notice;
                lock (gate)
                {
                    if (stopped || pending is not { } next)
                    {
                        worker = null;
                        return;
                    }
                    notice = next;
                    pending = null;
                }
                if (notice.Goal is null)
                    await owner.EmitAsync("thread/goal/cleared", new JsonObject
                    {
                        ["threadId"] = notice.ThreadId.ToString()
                    }).ConfigureAwait(false);
                else
                    await owner.EmitAsync("thread/goal/updated", new JsonObject
                    {
                        ["threadId"] = notice.ThreadId.ToString(), ["turnId"] = notice.TurnId?.ToString(),
                        ["goal"] = Goal(notice.ThreadId, notice.Goal)
                    }).ConfigureAwait(false);
            }
        }
    }
}
