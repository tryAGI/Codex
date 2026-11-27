using System.Net;

namespace tryAGI.Codex;

/// <summary>Exact host-authenticated MCP connection partition. Thread-wide and
/// environment-specific registrations are intentionally distinct.</summary>
public sealed record CodexMcpConnectionScope(string AccountId, string ConnectionId, string ServerName,
    string? ThreadId = null, string? EnvironmentId = null);

/// <summary>Authentication metadata from the host, never inferred from tool discovery.</summary>
public enum CodexMcpAuthenticationStatus
{
    /// <summary>No verified authentication status.</summary>
    Unknown,
    /// <summary>The server does not support authentication.</summary>
    Unsupported,
    /// <summary>Authentication is required but not established.</summary>
    NotLoggedIn,
    /// <summary>Host supplied a bearer credential.</summary>
    BearerToken,
    /// <summary>Host established an OAuth credential.</summary>
    OAuth,
}

/// <summary>Runtime status of a configured server registration.</summary>
public enum CodexMcpRuntimeStatus
{
    /// <summary>Configured, but never opened.</summary>
    NotStarted,
    /// <summary>Waiting for retirement or initialization.</summary>
    Starting,
    /// <summary>Connection is initialized.</summary>
    Connected,
    /// <summary>The server requires authentication; no automatic login is attempted.</summary>
    AuthenticationRequired,
    /// <summary>Connection failed; explicit reconfiguration is required.</summary>
    Failed,
    /// <summary>Initialization or the connection was cancelled.</summary>
    Cancelled,
    /// <summary>Disabled by the host configuration.</summary>
    Disabled,
}

/// <summary>Immutable registration selected by the authenticated host. Increment Revision
/// on changes to configuration, credential authority or grants. AuthorityFingerprint is
/// a lower-case SHA-256 of the host's exact resolved configuration and authority identity,
/// not model-supplied data or secret material retained in the pool.</summary>
public sealed record CodexMcpRegistration(CodexMcpConnectionScope Scope, long Revision,
    string AuthorityFingerprint, bool Enabled = true,
    CodexMcpAuthenticationStatus Authentication = CodexMcpAuthenticationStatus.Unknown);

/// <summary>Read-only runtime inventory; obtaining status never opens a server.</summary>
public sealed record CodexMcpConnectionStatus(CodexMcpRegistration Registration,
    CodexMcpRuntimeStatus RuntimeStatus, string? ProtocolVersion, long? ToolListRevision,
    int? ToolCount, bool ToolCatalogCurrent);

/// <summary>Scope-bound MCP connection reuse. The trusted host supplies an initialized,
/// explicitly authorized client factory, including per-request authorization. The pool
/// never creates network/process authority, grants approval, or retries a tool action.
/// A registration is a single connection generation; failed generations require explicit
/// reconfiguration. Opening is explicit; inspection and configuration never start a server.</summary>
public sealed class CodexMcpConnectionPool : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<CodexMcpConnectionScope, Entry> entries = [];
    private readonly HashSet<CodexMcpClient> ownedClients = [];
    private readonly Func<CodexMcpRegistration, CancellationToken, Task<CodexMcpClient>> factory;
    private bool disposed;
    private Task? disposeTask;

    /// <summary>The factory must honor cancellation, return a ready client for the exact
    /// registration, and dispose all partial transports on failure. It must independently
    /// enforce the current host permissions for every request, including retained clients.</summary>
    public CodexMcpConnectionPool(Func<CodexMcpRegistration, CancellationToken, Task<CodexMcpClient>> factory)
        => this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Registers or replaces one generation without connecting. Replacement
    /// fences old admissions immediately, cancels outstanding startup, and waits for old
    /// connection cleanup. No new generation opens until that cleanup is confirmed.</summary>
    public async Task ConfigureAsync(CodexMcpRegistration registration, CancellationToken cancellationToken = default)
    {
        Validate(registration);
        cancellationToken.ThrowIfCancellationRequested();
        Entry? retiring = null;
        TaskCompletionSource<bool>? barrier = null;
        Entry current;
        lock (gate)
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.TryGetValue(registration.Scope, out var previous))
            {
                if (registration.Revision < previous.Registration.Revision ||
                    registration.Revision == previous.Registration.Revision && registration != previous.Registration)
                    throw new InvalidOperationException("Stale or conflicting MCP registration revision.");
                if (registration == previous.Registration)
                    current = previous;
                else
                {
                    retiring = previous;
                    barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    current = new Entry(registration, barrier.Task);
                    entries[registration.Scope] = current;
                }
            }
            else
            {
                current = new Entry(registration, Task.FromResult(true));
                entries.Add(registration.Scope, current);
            }
        }

        if (retiring is not null)
        {
            // Once the replacement is admitted, caller cancellation cannot abandon retirement.
            try
            {
                await RetireAsync(retiring).ConfigureAwait(false);
                barrier!.TrySetResult(true);
            }
            catch
            {
                barrier!.TrySetResult(false);
                throw;
            }
        }
        else if (!await current.Predecessor.WaitAsync(cancellationToken).ConfigureAwait(false))
            throw new IOException("Previous MCP connection cleanup was not confirmed.");
    }

    /// <summary>Opens an exact enabled registration or joins its existing startup.
    /// Cancellation of one waiter does not cancel another waiter's shared startup.
    /// Replacing or disposing the registration cancels the shared startup itself.</summary>
    public async Task<CodexMcpClient> OpenAsync(CodexMcpConnectionScope scope, long revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        TaskCompletionSource<CodexMcpClient>? admission = null;
        Task<CodexMcpClient> opening;
        lock (gate)
        {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(scope, out entry!) || entry.Registration.Revision != revision)
                throw new InvalidOperationException("No matching MCP registration generation.");
            if (!entry.Registration.Enabled)
                throw new UnauthorizedAccessException("The MCP registration is disabled.");
            if (entry.Opening is null)
            {
                admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Opening = admission.Task;
                entry.RuntimeStatus = CodexMcpRuntimeStatus.Starting;
            }
            opening = entry.Opening;
        }
        if (admission is not null)
            _ = StartAsync(entry, admission);

        var client = await opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            EnsureUsable();
            if (!IsCurrent(entry) || client.State != CodexMcpState.Ready)
                throw new InvalidOperationException("The MCP connection generation is no longer ready.");
            return client;
        }
    }

    /// <summary>Inspects one registration without opening a connection.</summary>
    public CodexMcpConnectionStatus? GetStatus(CodexMcpConnectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            EnsureUsable();
            return entries.TryGetValue(scope, out var entry) ? Snapshot(entry) : null;
        }
    }

    /// <summary>Returns the inventory for one authenticated account and connection only.
    /// Identifiers must come from trusted host context, not model-supplied arguments.</summary>
    public IReadOnlyList<CodexMcpConnectionStatus> ListStatuses(string accountId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (gate)
        {
            EnsureUsable();
            return entries.Values.Where(entry => entry.Registration.Scope.AccountId == accountId &&
                entry.Registration.Scope.ConnectionId == connectionId)
                .OrderBy(entry => entry.Registration.Scope.ServerName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Registration.Scope.ThreadId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Registration.Scope.EnvironmentId, StringComparer.Ordinal)
                .Select(Snapshot).ToArray();
        }
    }

    private async Task StartAsync(Entry entry, TaskCompletionSource<CodexMcpClient> completion)
    {
        CodexMcpClient? client = null;
        try
        {
            if (!await entry.Predecessor.WaitAsync(entry.Token).ConfigureAwait(false))
                throw new IOException("Previous MCP connection cleanup was not confirmed.");
            entry.Token.ThrowIfCancellationRequested();
            client = await factory(entry.Registration, entry.Token).ConfigureAwait(false);
            if (client is null)
                throw new IOException("MCP factory did not return a connection.");
            lock (gate)
            {
                if (!ownedClients.Add(client))
                {
                    // Never close or expose another registration's connection.
                    client = null;
                    throw new IOException("MCP factory reused a connection across registrations.");
                }
                if (client.State != CodexMcpState.Ready)
                    throw new IOException("MCP factory did not return a ready connection.");
                if (!IsCurrent(entry) || entry.Token.IsCancellationRequested)
                    throw new OperationCanceledException("MCP registration retired during startup.", entry.Token);
                entry.Client = client;
                entry.RuntimeStatus = CodexMcpRuntimeStatus.Connected;
                completion.TrySetResult(client);
                return;
            }
        }
        catch (Exception error)
        {
            if (client is not null)
            {
                try { await CloseOwnedAsync(client).ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    entry.CleanupFailed = true;
                    error = new IOException("MCP connection cleanup was not confirmed.", cleanupError);
                }
            }
            lock (gate)
            {
                entry.RuntimeStatus = error switch
                {
                    OperationCanceledException => CodexMcpRuntimeStatus.Cancelled,
                    CodexMcpHttpException { StatusCode: HttpStatusCode.Unauthorized } =>
                        CodexMcpRuntimeStatus.AuthenticationRequired,
                    _ => CodexMcpRuntimeStatus.Failed,
                };
                if (error is OperationCanceledException)
                    completion.TrySetCanceled(entry.Token);
                else
                    completion.TrySetException(error);
            }
        }
    }

    private async Task CloseOwnedAsync(CodexMcpClient client)
    {
        await client.DisposeAsync().ConfigureAwait(false);
        lock (gate) ownedClients.Remove(client);
    }

    private async Task RetireAsync(Entry entry)
    {
        try
        {
            entry.Retirement.Cancel();
            // Chain replacements: C cannot open before A closes even when B never opened.
            var predecessorClosed = await entry.Predecessor.ConfigureAwait(false);
            if (entry.Opening is { } opening)
            {
                try { await opening.ConfigureAwait(false); }
                catch { /* Startup failure is already recorded; cleanup is checked below. */ }
            }
            if (entry.Client is { } client)
                await CloseOwnedAsync(client).ConfigureAwait(false);
            if (!predecessorClosed || entry.CleanupFailed)
                throw new IOException("Previous MCP connection cleanup was not confirmed.");
        }
        finally { entry.Retirement.Dispose(); }
    }

    private bool IsCurrent(Entry entry) => !disposed &&
        entries.TryGetValue(entry.Registration.Scope, out var current) && ReferenceEquals(entry, current);

    private static CodexMcpConnectionStatus Snapshot(Entry entry)
    {
        var client = entry.Client;
        var runtime = client?.State switch
        {
            CodexMcpState.Faulted => CodexMcpRuntimeStatus.Failed,
            CodexMcpState.Closed => CodexMcpRuntimeStatus.Cancelled,
            _ => entry.RuntimeStatus,
        };
        if (entry.Predecessor.IsCompletedSuccessfully && !entry.Predecessor.Result)
            runtime = CodexMcpRuntimeStatus.Failed;
        return new(entry.Registration, runtime, client?.ServerInfo?.ProtocolVersion,
            client?.ToolListRevision, client?.ToolCatalog?.Tools.Count, client?.IsToolCatalogCurrent ?? false);
    }

    private static void Validate(CodexMcpRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var scope = registration.Scope ?? throw new ArgumentException("Missing MCP scope.", nameof(registration));
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ConnectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ServerName);
        if (scope.ThreadId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(scope.ThreadId);
        if (scope.EnvironmentId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scope.EnvironmentId);
            if (scope.ThreadId is null) throw new ArgumentException("Environment scope requires a thread.", nameof(registration));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(registration.Revision);
        if (registration.AuthorityFingerprint is not { Length: 64 } ||
            registration.AuthorityFingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("MCP authority fingerprint must be a lower-case SHA-256.", nameof(registration));
        if (!Enum.IsDefined(registration.Authentication))
            throw new ArgumentException("Unknown MCP authentication status.", nameof(registration));
    }

    private void EnsureUsable() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <summary>Fences all admissions, cancels startup and closes the owned connections.</summary>
    public ValueTask DisposeAsync()
    {
        Entry[] retiring;
        TaskCompletionSource<bool> completion;
        lock (gate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            disposed = true;
            retiring = entries.Values.ToArray();
            entries.Clear();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposeTask = completion.Task;
        }
        _ = DisposeCoreAsync(retiring, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(Entry[] retiring, TaskCompletionSource<bool> completion)
    {
        try
        {
            await Task.WhenAll(retiring.Select(RetireAsync)).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private sealed class Entry(CodexMcpRegistration registration, Task<bool> predecessor)
    {
        internal CodexMcpRegistration Registration { get; } = registration;
        internal Task<bool> Predecessor { get; } = predecessor;
        internal CancellationTokenSource Retirement { get; } = new();
        internal CancellationToken Token => Retirement.Token;
        internal Task<CodexMcpClient>? Opening { get; set; }
        internal CodexMcpClient? Client { get; set; }
        internal bool CleanupFailed { get; set; }
        internal CodexMcpRuntimeStatus RuntimeStatus { get; set; } = registration.Enabled
            ? CodexMcpRuntimeStatus.NotStarted : CodexMcpRuntimeStatus.Disabled;
    }
}
