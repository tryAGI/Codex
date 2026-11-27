using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Host-owned scope and authority revision for one asynchronous hook queue.
/// Replacing the authority requires disposing this queue, not reusing its results.</summary>
public sealed record CodexAsyncHookScope(string AccountId, string ConnectionId, string ThreadId,
    string AuthorityRevision);

/// <summary>Settled async hook result. Only context and warnings can be projected into a later
/// step; no permission, input rewrite, tool output replacement or stop decision is exposed.</summary>
public sealed record CodexAsyncHookCompletion(CodexHookContext Context, JsonElement Run,
    IReadOnlyList<string> AdditionalContexts, IReadOnlyList<string> Warnings);

/// <summary>Disposition of an asynchronous scheduling request, not proof of execution.</summary>
public enum CodexAsyncHookSchedule
{
    /// <summary>Admitted into the local bounded queue.</summary>
    Scheduled,
    /// <summary>Identical run was previously admitted; it will not be replayed.</summary>
    Duplicate,
    /// <summary>Queue capacity was exhausted.</summary>
    CapacityExceeded,
}

/// <summary>Bounded, explicitly owned asynchronous hook lifetime. Async hook work may outlive
/// the originating turn, but never the queue's authority. The host must drain results at its
/// own step/turn boundary and recheck current projection policy. This queue is not durable;
/// the dispatch adapter owns persistent deduplication of ambiguous effects.</summary>
public sealed class CodexAsyncHookQueue : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    private readonly int maximumOutstanding;
    private readonly int maximumRemembered;
    private readonly Dictionary<string, Identity> remembered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> running = new(StringComparer.Ordinal);
    private readonly Queue<CodexAsyncHookCompletion> completed = new();
    private bool closed;
    private Task? closeTask;

    /// <summary>Creates a queue for one resolved host authority. The owner must dispose
    /// it on session close, disconnect, grant revocation or configuration replacement.</summary>
    public CodexAsyncHookQueue(CodexAsyncHookScope scope, TimeSpan? hookTimeout = null,
        int maximumOutstanding = 32, int maximumRemembered = 4096, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ConnectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AuthorityRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutstanding);
        if (maximumRemembered < maximumOutstanding) throw new ArgumentOutOfRangeException(nameof(maximumRemembered));
        timeout = hookTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(hookTimeout));
        Scope = scope;
        this.maximumOutstanding = maximumOutstanding;
        this.maximumRemembered = maximumRemembered;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Immutable host authority for this queue.</summary>
    public CodexAsyncHookScope Scope { get; }

    /// <summary>Number of pending executions and completed results awaiting drain.</summary>
    public int Outstanding { get { lock (gate) return running.Count + completed.Count; } }

    internal CodexAsyncHookSchedule Schedule(CodexHookInvocation invocation,
        Func<CodexHookInvocation, CancellationToken, Task<CodexAsyncHookCompletion>> run)
    {
        if (invocation.Context.AccountId != Scope.AccountId || invocation.Context.ConnectionId != Scope.ConnectionId ||
            invocation.Context.ThreadId != Scope.ThreadId)
            throw new UnauthorizedAccessException("Async hook belongs to a different host scope.");
        var identity = new Identity(invocation.Context, invocation.Definition,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation.Payload.GetRawText()))));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (remembered.TryGetValue(invocation.RunId, out var existing))
            {
                if (existing != identity) throw new CodexProtocolException("Conflicting async hook run identity.");
                return CodexAsyncHookSchedule.Duplicate;
            }
            if (running.Count + completed.Count >= maximumOutstanding || remembered.Count >= maximumRemembered)
                return CodexAsyncHookSchedule.CapacityExceeded;
            remembered.Add(invocation.RunId, identity);
            // The worker body cannot enter the queue lock before its task has been registered.
            running.Add(invocation.RunId, Task.Run(() => RunAsync(invocation, run)));
            return CodexAsyncHookSchedule.Scheduled;
        }
    }

    private async Task RunAsync(CodexHookInvocation invocation,
        Func<CodexHookInvocation, CancellationToken, Task<CodexAsyncHookCompletion>> run)
    {
        try
        {
            using var deadline = new CancellationTokenSource(timeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, deadline.Token);
            var result = await run(invocation, linked.Token).ConfigureAwait(false);
            lock (gate)
            {
                // Keep completion insertion and pending removal in one atomic transition.
                running.Remove(invocation.RunId);
                if (!closed) completed.Enqueue(result);
            }
        }
        finally
        {
            lock (gate) running.Remove(invocation.RunId);
        }
    }

    /// <summary>Atomically drains settled results, in completion order, for the exact current
    /// authority. This does not publish model context or execute tools.</summary>
    public IReadOnlyList<CodexAsyncHookCompletion> Drain(CodexAsyncHookScope currentAuthority)
    {
        if (currentAuthority != Scope) throw new UnauthorizedAccessException("Async hook authority changed.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var result = completed.ToArray();
            completed.Clear();
            return Array.AsReadOnly(result);
        }
    }

    /// <summary>Waits for currently scheduled runs to settle without consuming results.
    /// Caller cancellation affects only the wait, not the owned hook lifetime.</summary>
    public Task WaitForPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task[] tasks;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            tasks = running.Values.ToArray();
        }
        return Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    /// <summary>Fences all results and cancels outstanding work. No stale context is returned
    /// after disposal. A host adapter must still stop its actual external effects.</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closeTask is not null) return new(closeTask);
            closed = true;
            completed.Clear();
            var tasks = running.Values.ToArray();
            closeTask = Task.Run(async () =>
            {
                try
                {
                    try { await lifetime.CancelAsync().ConfigureAwait(false); }
                    catch (AggregateException) { }
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                finally { lifetime.Dispose(); }
            });
            return new(closeTask);
        }
    }

    private sealed record Identity(CodexHookContext Context, CodexHookDefinition Definition, string PayloadDigest);
}
