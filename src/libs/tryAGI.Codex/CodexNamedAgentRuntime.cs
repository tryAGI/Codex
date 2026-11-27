using System.Runtime.CompilerServices;

namespace tryAGI.Codex;

/// <summary>One trusted host's local named-agent execution registry for an account and root.
/// Runs only the existing CodexThreadAgent. Idle agents have no resident runner to evict.
/// Local capacity and status subscriptions are in-process; durable storage arbitrates each
/// recipient across hosts. Share one instance per root within a host. This is not an external
/// scheduler, distributed worker lease manager, or automatic permission grant.</summary>
public sealed class CodexNamedAgentRuntime
{
    private readonly CodexThreadStore store;
    private readonly ICodexNamedAgentLifecycleStorage storage;
    private readonly Guid root;
    private readonly int maximumConcurrentExecutions;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Invocation> running = [];

    /// <summary>Bind an existing thread store and one named graph root.</summary>
    public CodexNamedAgentRuntime(CodexThreadStore store, Guid rootThreadId, int maximumConcurrentExecutions)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (rootThreadId == Guid.Empty) throw new ArgumentException("Agent tree root is required.", nameof(rootThreadId));
        if (maximumConcurrentExecutions is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentExecutions));
        this.store = store;
        storage = store.RequireNamedAgentLifecycleStorage();
        root = rootThreadId;
        this.maximumConcurrentExecutions = maximumConcurrentExecutions;
    }

    /// <summary>Number of locally reserved or running invocations. A reservation is held until
    /// its existing native runner and terminal observation have both settled.</summary>
    public int ActiveExecutions { get { lock (gate) return running.Count; } }

    /// <summary>Run one host-authorized idle follow-up through the native runner. The caller retains
    /// the exact claim/turn IDs. Capacity rejection has no durable effect. Queue-only mail is
    /// not a wake. A definitely acquired grant is reconciled when its own invocation returns;
    /// uncertain grant acquisition is surfaced for explicit reconciliation. This method never
    /// retries a model call or automatically assumes owner loss.</summary>
    public async Task<CodexThreadAgentResult?> RunWakeAsync(CodexNamedAgentWakeRequest request,
        CodexThreadAgent agent, Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(request.Options);
        if (request.ThreadId == Guid.Empty || request.ThreadId == root || request.ClaimId == Guid.Empty ||
            request.TurnId == Guid.Empty || string.IsNullOrWhiteSpace(request.OwnerId) ||
            request.OwnerId.Length > 512 || request.OwnerId.Any(char.IsControl) ||
            request.ExpectedMailboxRevision < 0 || request.ExpectedThreadRevision < 1 ||
            request.MaximumRequests is < 1 or > 32 || !agent.IsBoundTo(store))
            throw new ArgumentException("Invalid named-agent wake invocation.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        var invocation = new Invocation(request);
        lock (gate)
        {
            if (running.Count >= maximumConcurrentExecutions || running.ContainsKey(request.ThreadId))
            {
                invocation.Control.Dispose();
                throw new InvalidOperationException("Local named-agent execution capacity is unavailable.");
            }
            running.Add(request.ThreadId, invocation);
        }

        var attempted = false;
        var grantConfirmed = false;
        var final = invocation.Current with { Status = CodexNamedAgentExecutionStatus.Unconfirmed, IsLocal = false };
        try
        {
            // Use the existing graceful interruption control. Its token is cancelled only
            // after already admitted tool callbacks finish, so a wait/interrupt does not
            // tear down an in-flight side effect prematurely.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.Control.Token);
            var ct = linked.Token;
            var mailbox = await store.ReadAgentMailboxAsync(root, request.ThreadId, ct).ConfigureAwait(false);
            if (mailbox.Revision != request.ExpectedMailboxRevision) throw new CodexThreadConflictException();
            if (mailbox.Claim is not null) throw new InvalidOperationException("An unsettled wake claim already exists.");
            if (!mailbox.HasWakeRequest)
            {
                final = final with { Status = CodexNamedAgentExecutionStatus.NotAdmitted };
                return null;
            }
            ct.ThrowIfCancellationRequested();
            attempted = true;
            if (!await storage.AuthorizeWakeAsync(root, request.ThreadId, request.ClaimId, request.TurnId,
                    request.OwnerId, ct).ConfigureAwait(false))
                throw new CodexThreadConflictException();
            grantConfirmed = true;

            var result = await store.RunControlledAgentMailboxWakeAsync(agent, root, request.ThreadId,
                request.ExpectedMailboxRevision, request.ExpectedThreadRevision, request.ClaimId, request.TurnId,
                request.OwnerId, request.Options, execute, request.MaximumRequests, invocation.Control,
                admitted => invocation.Publish(FromDocument(admitted, request.TurnId, true)), ct).ConfigureAwait(false);
            if (result is not null)
                final = FromDocument(result.Thread, request.TurnId, false);
            return result;
        }
        finally
        {
            // The runner returned or threw. The journal, not the exception type, determines
            // terminal status. No automatic model replay or owner-loss recovery is performed.
            if (final.Status == CodexNamedAgentExecutionStatus.Unconfirmed)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    // Only a definitely acquired grant owned by this now-returned invocation
                    // can be reconciled automatically. An uncertain grant is left untouched.
                    var grant = grantConfirmed
                        ? await ReconcileStoppedWakeCoreAsync(request, deadline.Token).ConfigureAwait(false)
                        : null;
                    var observed = await ReadRecordedAsync(request.ThreadId, request.TurnId, deadline.Token).ConfigureAwait(false);
                    final = observed.Status is CodexNamedAgentExecutionStatus.Running ||
                            attempted && grant != CodexAgentWakeGrantState.Fenced &&
                            observed.Status == CodexNamedAgentExecutionStatus.NotAdmitted
                        ? final with { ThreadRevision = observed.ThreadRevision }
                        : observed;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Keep unconfirmed evidence; never replace an original runner exception.
                }
            }
            lock (gate)
            {
                invocation.Publish(final);
                running.Remove(request.ThreadId);
            }
            invocation.Control.Dispose();
        }
    }

    /// <summary>Observe one exact turn. A nonlocal running observation is not a proof of
    /// residency or a reason to recover a lost owner.</summary>
    public async Task<CodexNamedAgentExecutionSnapshot> InspectAsync(Guid callerThreadId, Guid threadId,
        Guid turnId, CancellationToken cancellationToken = default)
    {
        await RequireTargetAsync(callerThreadId, threadId, turnId, cancellationToken).ConfigureAwait(false);
        var local = Find(threadId, turnId);
        if (local is not null) return local.Current;
        return await ReadRecordedAsync(threadId, turnId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Wait for this exact locally registered invocation to end, without polling.
    /// Cancellation cancels only the wait. An unloaded or externally owned turn is returned
    /// as one durable observation; cross-host waits require the host's event subscription.</summary>
    public async Task<CodexNamedAgentExecutionSnapshot> WaitAsync(Guid callerThreadId, Guid threadId,
        Guid turnId, CancellationToken cancellationToken = default)
    {
        await RequireTargetAsync(callerThreadId, threadId, turnId, cancellationToken).ConfigureAwait(false);
        var local = Find(threadId, turnId);
        return local is null
            ? await ReadRecordedAsync(threadId, turnId, cancellationToken).ConfigureAwait(false)
            : await local.Settled.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Watch an exact local invocation: initial snapshot followed by coalesced changes.
    /// A completed invocation ends the stream; it never follows a replacement turn. If it is
    /// nonlocal, return one durable observation. Subscribe and capture state atomically.</summary>
    public async IAsyncEnumerable<CodexNamedAgentExecutionSnapshot> WatchAsync(Guid callerThreadId, Guid threadId,
        Guid turnId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await RequireTargetAsync(callerThreadId, threadId, turnId, cancellationToken).ConfigureAwait(false);
        var local = Find(threadId, turnId);
        if (local is null)
        {
            yield return await ReadRecordedAsync(threadId, turnId, cancellationToken).ConfigureAwait(false);
            yield break;
        }
        CodexNamedAgentExecutionSnapshot? last = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (snapshot, changed) = local.Capture();
            if (snapshot != last)
            {
                last = snapshot;
                yield return snapshot;
            }
            if (!snapshot.IsLocal) yield break;
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Request graceful interruption of the exact local invocation; root and self
    /// targets are forbidden. An unloaded or remote invocation is not started or cancelled.
    /// Requested does not mean the turn has settled; use WaitAsync for that evidence.</summary>
    public async Task<CodexNamedAgentInterruptResult> InterruptAsync(Guid callerThreadId, Guid threadId,
        Guid turnId, CancellationToken cancellationToken = default)
    {
        if (threadId == root || threadId == callerThreadId)
            throw new ArgumentException("An agent cannot interrupt itself or the root.");
        await RequireTargetAsync(callerThreadId, threadId, turnId, cancellationToken).ConfigureAwait(false);
        var local = Find(threadId, turnId);
        if (local is null) return new(await ReadRecordedAsync(threadId, turnId, cancellationToken).ConfigureAwait(false), false);
        var before = local.Current;
        cancellationToken.ThrowIfCancellationRequested();
        if (!before.IsLocal) return new(before, false);
        local.Control.Interrupt();
        return new(before, true);
    }

    /// <summary>Close a settled subtree in one graph transaction. Does not interrupt live
    /// work implicitly, erase history, discard queued mail, or change execution ownership.
    /// Interrupt and wait for local work, or reconcile externally owned turns, before closing.
    /// A caller cannot close itself, the root, or an ancestor containing its own execution.</summary>
    public async Task CloseSettledSubtreeAsync(Guid callerThreadId, Guid childThreadId, string expectedGraphRevision,
        CancellationToken cancellationToken = default)
    {
        if (childThreadId == root || childThreadId == callerThreadId || childThreadId == Guid.Empty)
            throw new ArgumentException("Cannot close the root or the caller's own agent.");
        var directory = await store.ReadAgentIdentitiesAsync(root, cancellationToken).ConfigureAwait(false);
        _ = directory.GetActive(callerThreadId);
        _ = directory.GetRecorded(childThreadId);
        if (directory.RestoreChain(callerThreadId).Any(ancestor => ancestor.ThreadId == childThreadId))
            throw new ArgumentException("Cannot close an ancestor containing the caller.");
        if (directory.Graph.Revision != expectedGraphRevision) throw new CodexThreadConflictException();
        if (!await storage.CompareCloseNamedAgentSubtreeAsync(directory.Graph.AccountId, root, childThreadId,
                expectedGraphRevision, cancellationToken).ConfigureAwait(false))
            throw new CodexThreadConflictException();
    }

    /// <summary>Reconcile the original wake grant after the trusted host has positively
    /// established that its old invocation has stopped and cannot continue. The original
    /// claim/turn/owner identities are mandatory. A committed turn is acknowledged, not
    /// replayed; a proven unadmitted grant is fenced before its prefix is released.
    /// Does not recover a running turn, execute work, or infer owner loss from a timeout.</summary>
    public async Task<CodexAgentWakeGrantState?> ReconcileStoppedWakeAsync(CodexNamedAgentWakeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
            if (running.ContainsKey(request.ThreadId))
                throw new InvalidOperationException("Local invocation is still active.");
        return await ReconcileStoppedWakeCoreAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodexAgentWakeGrantState?> ReconcileStoppedWakeCoreAsync(CodexNamedAgentWakeRequest request,
        CancellationToken ct)
    {
        var grant = await storage.ReadWakeAuthorizationAsync(root, request.ThreadId, request.ClaimId,
            request.TurnId, request.OwnerId, ct).ConfigureAwait(false);
        if (grant is null or CodexAgentWakeGrantState.Consumed) return grant;
        var box = await store.ReadAgentMailboxAsync(root, request.ThreadId, ct).ConfigureAwait(false);
        bool Owns(CodexAgentMailClaim claim) => claim.Id == request.ClaimId &&
            claim.TurnId == request.TurnId && claim.OwnerId == request.OwnerId;
        if (grant == CodexAgentWakeGrantState.Granted)
        {
            if (box.Claim is { } reserved)
            {
                if (!Owns(reserved)) throw new InvalidOperationException("Another wake claim is outstanding.");
                if (await store.ReconcileAgentMailboxWakeAsync(root, request.ThreadId, box.Revision,
                        request.ClaimId, request.OwnerId, ct).ConfigureAwait(false))
                    return CodexAgentWakeGrantState.Consumed;
            }
            if (!await storage.FenceUnadmittedWakeAsync(root, request.ThreadId, request.ClaimId,
                    request.TurnId, request.OwnerId, ct).ConfigureAwait(false))
                throw new CodexThreadConflictException();
            box = await store.ReadAgentMailboxAsync(root, request.ThreadId, ct).ConfigureAwait(false);
        }
        if (box.Claim is { } claim && Owns(claim))
            await store.ReleaseAgentMailAsync(root, request.ThreadId, box.Revision, request.ClaimId,
                request.OwnerId, ct).ConfigureAwait(false);
        return CodexAgentWakeGrantState.Fenced;
    }

    private async Task RequireTargetAsync(Guid callerThreadId, Guid threadId, Guid turnId, CancellationToken ct)
    {
        if (turnId == Guid.Empty) throw new ArgumentException("Exact turn identity is required.", nameof(turnId));
        var directory = await store.ReadAgentIdentitiesAsync(root, ct).ConfigureAwait(false);
        _ = directory.GetActive(callerThreadId);
        _ = directory.GetRecorded(threadId);
    }

    private Invocation? Find(Guid threadId, Guid turnId)
    {
        lock (gate)
            return running.TryGetValue(threadId, out var invocation) && invocation.Current.TurnId == turnId ? invocation : null;
    }

    private async Task<CodexNamedAgentExecutionSnapshot> ReadRecordedAsync(Guid threadId, Guid turnId, CancellationToken ct)
    {
        var document = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
        return FromDocument(document, turnId, false);
    }

    private static CodexNamedAgentExecutionSnapshot FromDocument(CodexThreadDocument document, Guid turnId, bool local)
    {
        var turn = document.Turns.SingleOrDefault(item => item.Id == turnId);
        var status = turn?.Status switch
        {
            "inProgress" => CodexNamedAgentExecutionStatus.Running,
            "completed" => CodexNamedAgentExecutionStatus.Completed,
            "interrupted" => CodexNamedAgentExecutionStatus.Interrupted,
            "failed" => CodexNamedAgentExecutionStatus.Failed,
            null => CodexNamedAgentExecutionStatus.NotAdmitted,
            _ => CodexNamedAgentExecutionStatus.Unconfirmed,
        };
        return new(document.Id, turnId, turn?.OwnerId, status, local, document.Revision);
    }

    private sealed class Invocation(CodexNamedAgentWakeRequest request)
    {
        private readonly object sync = new();
        private CodexNamedAgentExecutionSnapshot current = new(request.ThreadId, request.TurnId, request.OwnerId,
            CodexNamedAgentExecutionStatus.PendingAdmission, true);
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CodexAgentControl Control { get; } = new();
        internal TaskCompletionSource<CodexNamedAgentExecutionSnapshot> Settled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CodexNamedAgentExecutionSnapshot Current { get { lock (sync) return current; } }
        internal (CodexNamedAgentExecutionSnapshot Snapshot, Task Changed) Capture()
        {
            lock (sync) return (current, changed.Task);
        }
        internal void Publish(CodexNamedAgentExecutionSnapshot snapshot)
        {
            lock (sync)
            {
                current = snapshot;
                var previous = changed;
                changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                previous.TrySetResult();
                if (!snapshot.IsLocal) Settled.TrySetResult(snapshot);
            }
        }
    }
}
