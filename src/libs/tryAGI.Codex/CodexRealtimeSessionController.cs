using System.Net.WebSockets;
using System.Security.Authentication;
using System.Threading.Channels;

namespace tryAGI.Codex;

/// <summary>Outcome of an existing-call lifecycle operation.</summary>
public enum CodexRealtimeLifecycleStatus
{
    /// <summary>The exact existing call has a live, fenced attachment.</summary>
    Attached,
    /// <summary>Transport detached; logical call and history remain open.</summary>
    Detached,
    /// <summary>Authoritatively closed logical session.</summary>
    Ended,
    /// <summary>Transient failure; retry budget or deadline is exhausted.</summary>
    Retryable,
    /// <summary>Credentials or account identity rejected.</summary>
    AuthenticationRejected,
    /// <summary>Provider quota or rate limit denied attachment.</summary>
    QuotaRejected,
    /// <summary>Non-retryable transport or protocol failure.</summary>
    Fatal,
    /// <summary>Local/remote history or ownership remains unconfirmed.</summary>
    HistoryRecoveryRequired,
    /// <summary>Explicit cancellation; no new external effect was admitted.</summary>
    Cancelled,
}

/// <summary>Authoritative host reconciliation of a lost attachment. Open means the same
/// call and its history have been verified; Unknown never authorizes a reconnect.</summary>
public enum CodexRealtimeRecoveryDecision
{
    /// <summary>Same logical call is open and the history has been reconciled.</summary>
    Open,
    /// <summary>Same logical call is conclusively ended and history reconciled.</summary>
    Ended,
    /// <summary>Authoritative probe temporarily unavailable.</summary>
    Retryable,
    /// <summary>Authorization or account rejected.</summary>
    AuthenticationRejected,
    /// <summary>Quota denied.</summary>
    QuotaRejected,
    /// <summary>Non-retryable provider failure.</summary>
    Fatal,
    /// <summary>Cannot prove call identity/status or complete history.</summary>
    Unknown,
}

/// <summary>Bounded existing-call reconnection and drain policy.</summary>
public sealed record CodexRealtimeReconnectOptions
{
    /// <summary>Maximum connection attempts within one deadline, including the first.</summary>
    public int MaximumAttempts { get; init; } = 4;
    /// <summary>Overall attach/recovery deadline.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Initial exponential retry delay.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Bound for draining already-read observations before detach/end.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Exact persisted identity passed to a caller-owned existing-call transport factory.
/// A factory may join this call only; it must never create a replacement call.</summary>
public sealed record CodexRealtimeAttachRequest(
    string CallId, string Protocol, string AccountId, Guid AttachmentId, DateTimeOffset StartBefore);

/// <summary>Durably admitted new direct socket creation. SessionId is a logical host identity,
/// not a provider call ID. The factory must use the authorized account and create only once.</summary>
public sealed record CodexRealtimeDirectConnectRequest(Guid ThreadId, string SessionId, string Protocol,
    string AccountId, Guid AttachmentId, DateTimeOffset StartBefore);

/// <summary>Exact undelivered observation retained while the host reconciles an interrupted journal write.</summary>
public sealed record CodexRealtimePendingObservation(Guid AttachmentId, string Id, CodexRealtimeEvent Event);

/// <summary>Authoritative recovery input, with the local journal already verified. Reconcile
/// any provider-side event gap before returning Open or Ended.</summary>
public sealed record CodexRealtimeRecoveryContext(CodexThreadDocument Thread, CodexStoredRealtimeSession Session,
    CodexRealtimePendingObservation? PendingObservation);

/// <summary>One host-owned logical realtime session backed by the existing native thread CAS
/// journal. Reconnect joins the original call, never creates a new paid call, never replays
/// provider commands, and never infers complete remote history from a local read alone.</summary>
public sealed class CodexRealtimeSessionController : IAsyncDisposable
{
    private readonly CodexThreadStore store;
    private readonly Guid threadId;
    private readonly string sessionId;
    private readonly string ownerId;
    private readonly Func<CodexRealtimeAttachRequest, CancellationToken, Task<CodexRealtimeAttachedCall>>? connect;
    private readonly Func<CodexRealtimeDirectConnectRequest, CancellationToken, Task<CodexRealtimeConnection>>? directConnect;
    private readonly CodexRealtimeReconnectOptions policy;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly object sync = new();
    private CancellationTokenSource? activeAttempt;
    private Live? current;
    private CodexRealtimePendingObservation? pending;
    private bool disposed;

    private sealed class Live(Guid id, CodexRealtimeConnection connection, CodexRealtimeAttachedCall? sideband)
    {
        public Guid Id { get; } = id;
        public CodexRealtimeConnection Connection { get; } = connection;
        public CodexRealtimeAttachedCall? Sideband { get; } = sideband;
        public ValueTask DisposeAsync() => Sideband is null ? Connection.DisposeAsync() : Sideband.DisposeAsync();
        public CancellationTokenSource Stop { get; } = new();
        public Task<CodexRealtimeLifecycleStatus>? Pump { get; set; }
        public bool StopRequested { get; set; }
    }

    /// <summary>Create a controller for an already registered session. The supplied factory is
    /// trusted to attach only to the exact call in the request, and not to send session setup.</summary>
    public CodexRealtimeSessionController(CodexThreadStore store, Guid threadId, string sessionId, string ownerId,
        Func<CodexRealtimeAttachRequest, CancellationToken, Task<CodexRealtimeAttachedCall>> connect,
        CodexRealtimeReconnectOptions? options = null)
        : this(store, threadId, sessionId, ownerId, options)
        => this.connect = connect ?? throw new ArgumentNullException(nameof(connect));

    /// <summary>Create a controller for a registered direct session using a host-owned connection factory.
    /// The same durable controller and observer pump are used; the factory is never retried after admission.</summary>
    public static CodexRealtimeSessionController CreateDirect(CodexThreadStore store, Guid threadId, string sessionId,
        string ownerId, Func<CodexRealtimeDirectConnectRequest, CancellationToken, Task<CodexRealtimeConnection>> connect,
        CodexRealtimeReconnectOptions? options = null)
        => new(store, threadId, sessionId, ownerId, options, connect);

    private CodexRealtimeSessionController(CodexThreadStore store, Guid threadId, string sessionId, string ownerId,
        CodexRealtimeReconnectOptions? options,
        Func<CodexRealtimeDirectConnectRequest, CancellationToken, Task<CodexRealtimeConnection>> connect)
        : this(store, threadId, sessionId, ownerId, options)
        => directConnect = connect ?? throw new ArgumentNullException(nameof(connect));

    private CodexRealtimeSessionController(CodexThreadStore store, Guid threadId, string sessionId, string ownerId,
        CodexRealtimeReconnectOptions? options)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.threadId = threadId != Guid.Empty ? threadId : throw new ArgumentException("Thread ID is required.", nameof(threadId));
        this.sessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : throw new ArgumentException("Session ID is required.", nameof(sessionId));
        this.ownerId = !string.IsNullOrWhiteSpace(ownerId) ? ownerId : throw new ArgumentException("Owner ID is required.", nameof(ownerId));
        policy = options ?? new();
        if (policy.MaximumAttempts is < 1 or > 8 || policy.Timeout <= TimeSpan.Zero ||
            policy.Timeout > TimeSpan.FromMinutes(2) || policy.RetryDelay < TimeSpan.Zero ||
            policy.RetryDelay > TimeSpan.FromSeconds(5) || policy.DrainTimeout <= TimeSpan.Zero ||
            policy.DrainTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    /// <summary>The transport ownership contract selected by the host.</summary>
    public CodexRealtimeTransportKind TransportKind => directConnect is null
        ? CodexRealtimeTransportKind.ExistingCall : CodexRealtimeTransportKind.DirectWebSocket;

    internal bool IsBoundTo(CodexThreadStore journal, Guid thread, string session, string owner) =>
        ReferenceEquals(store, journal) && threadId == thread && sessionId == session && ownerId == owner;

    internal Guid? AttachmentId { get { lock (sync) return current?.Id; } }

    /// <summary>Current connection for outbound commands. Use <see cref="PumpAsync"/> as the
    /// sole inbound reader so that observations are persisted before delivery.</summary>
    public CodexRealtimeConnection? Connection
    {
        get { lock (sync) return current?.Connection; }
    }

    /// <summary>Attach or reattach to a cleanly detached session. RecoveryRequired is fenced
    /// until explicit authoritative reconciliation. Cancellation stops all pending retries.</summary>
    public async Task<CodexRealtimeLifecycleStatus> AttachAsync(CancellationToken cancellationToken = default)
    {
        await transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var attempt = BeginAttempt(cancellationToken);
            try { return await AttachCoreAsync(attempt.Token).ConfigureAwait(false); }
            finally { EndAttempt(attempt); }
        }
        finally { transitions.Release(); }
    }

    /// <summary>Persist all relevant observations before notifying the optional observer,
    /// including in voice-only sessions. Unexpected closure fences the old attachment.</summary>
    public Task<CodexRealtimeLifecycleStatus> PumpAsync(Func<CodexRealtimeEvent, CancellationToken, Task>? observer = null,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var live = current ?? throw new InvalidOperationException("Realtime transport is not attached.");
            if (live.StopRequested || live.Pump is not null) throw new InvalidOperationException("Realtime transport already has a reader or is stopping.");
            return live.Pump = PumpCoreAsync(live, observer, cancellationToken);
        }
    }

    /// <summary>Detach only the transport. Wait for accepted observations to reach durable
    /// history, then release the exact CAS attachment token. No session.close is sent.</summary>
    public async Task<CodexRealtimeLifecycleStatus> DetachAsync(CancellationToken cancellationToken = default)
    {
        CancelAttempt();
        await transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await DetachCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { transitions.Release(); }
    }

    /// <summary>Recover a fenced session and optionally reconnect it. An attached session
    /// left by a dead owner can be fenced only by supplying its exact lost attachment ID,
    /// after the host proves the old transport is gone. Never steals a live attachment.</summary>
    public async Task<CodexRealtimeLifecycleStatus> RecoverAsync(
        Func<CodexRealtimeRecoveryContext, CancellationToken, Task<CodexRealtimeRecoveryDecision>> reconcile,
        bool reconnect = true, Guid? provenLostAttachmentId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconcile);
        await transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var attempt = BeginAttempt(cancellationToken);
            try
            {
                var ct = attempt.Token;
                var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                var session = Session(thread);
                if (session.State == "ended") return CodexRealtimeLifecycleStatus.Ended;
                if (session.State == "attached")
                {
                    if (Connection is not null || provenLostAttachmentId is null || session.AttachmentId != provenLostAttachmentId)
                        return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                    if (!await FenceAsync(provenLostAttachmentId, ct).ConfigureAwait(false))
                        return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                    thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                    session = Session(thread);
                }
                if (session.State == "detached")
                    return reconnect ? await AttachCoreAsync(ct).ConfigureAwait(false) : CodexRealtimeLifecycleStatus.Detached;
                if (session.State == "ending")
                {
                    if (!await FenceAsync(null, ct).ConfigureAwait(false)) return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                    thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                    session = Session(thread);
                }
                if (session.State != "recoveryRequired") return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                if (Volatile.Read(ref pending) is { } retained)
                {
                    await PersistAsync(retained, true, ct).ConfigureAwait(false);
                    Interlocked.CompareExchange(ref pending, null, retained);
                    thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                    session = Session(thread);
                }
                _ = CodexThreadStore.RecoverRealtimeHistory(session);
                for (var number = 0; number < policy.MaximumAttempts; number++)
                {
                    ct.ThrowIfCancellationRequested();
                    var decision = await reconcile(new(thread, session, Volatile.Read(ref pending)), ct).ConfigureAwait(false);
                    if (decision == CodexRealtimeRecoveryDecision.Retryable && number + 1 < policy.MaximumAttempts)
                    {
                        await RetryDelayAsync(number, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (decision is not (CodexRealtimeRecoveryDecision.Open or CodexRealtimeRecoveryDecision.Ended))
                        return RecoveryStatus(decision);
                    if (directConnect is not null && decision == CodexRealtimeRecoveryDecision.Open)
                        return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                    try
                    {
                        thread = await store.ConfirmRealtimeHistoryRecoveryAsync(threadId, thread.Revision,
                            sessionId, ownerId, decision == CodexRealtimeRecoveryDecision.Ended, ct).ConfigureAwait(false);
                    }
                    catch (CodexThreadConflictException)
                    {
                        // The authoritative probe covered the earlier revision only. Probe the new
                        // history again rather than accepting a different journal blindly.
                        thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                        session = Session(thread);
                        if (session.State != "recoveryRequired") return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                        continue;
                    }
                    if (decision == CodexRealtimeRecoveryDecision.Ended) return CodexRealtimeLifecycleStatus.Ended;
                    return reconnect ? await AttachCoreAsync(ct).ConfigureAwait(false) : CodexRealtimeLifecycleStatus.Detached;
                }
                return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
            }
            catch (OperationCanceledException) { return CodexRealtimeLifecycleStatus.Cancelled; }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            { return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired; }
            finally { EndAttempt(attempt); }
        }
        finally { transitions.Release(); }
    }

    /// <summary>Seal all speech and durably admit one logical end operation before calling
    /// an optional caller-authorized provider end effect. No effect is repeated after an
    /// uncertain write or response; detached calls can end without owning media.</summary>
    public async Task<CodexRealtimeLifecycleStatus> EndAsync(Guid operationId,
        Func<CodexRealtimeConnection?, CancellationToken, Task>? endProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("End operation ID is required.", nameof(operationId));
        if (directConnect is not null && endProvider is not null)
            throw new ArgumentException("Direct realtime owns its socket; no separate provider hangup callback is allowed.", nameof(endProvider));
        CancelAttempt();
        await transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var live = GetLive();
            if (directConnect is not null && live is not null)
            {
                if (!await CloseDirectAsync(live, operationId, cancellationToken).ConfigureAwait(false))
                    return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
            }
            else if (live is not null && !await DrainAsync(live, cancellationToken).ConfigureAwait(false))
            {
                await LoseAsync(live).ConfigureAwait(false);
                return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
            }
            CodexThreadAdmission? admission = null;
            Guid? generation = null;
            var observed = false;
            try
            {
                for (var attempt = 0; attempt < policy.MaximumAttempts; attempt++)
                {
                    var thread = await store.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
                    var session = Session(thread);
                    if (session.State == "ended")
                    {
                        if (live is not null) await DisposeLiveAsync(live).ConfigureAwait(false);
                        return CodexRealtimeLifecycleStatus.Ended;
                    }
                    var currentGeneration = session.UsedAttachmentIds?.LastOrDefault();
                    if (session.State is not ("detached" or "attached") || session.AttachmentId != live?.Id ||
                        (observed && generation != currentGeneration))
                    {
                        if (live is not null) await DisposeLiveAsync(live).ConfigureAwait(false);
                        return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                    }
                    generation = currentGeneration;
                    observed = true;
                    try
                    {
                        admission = await store.BeginDrainedRealtimeEndAsync(threadId, thread.Revision, sessionId, ownerId,
                            live?.Id, operationId, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (CodexThreadConflictException) when (attempt + 1 < policy.MaximumAttempts)
                    {
                        // A definitive CAS rejection has no external effect. Recheck the exact
                        // transport generation, then retry against the latest reasoning revision.
                    }
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
            {
                // Even a cancelled storage call may have committed. Never dispatch the end effect
                // without a durable admission receipt, and never fence another end operation.
                if (live is not null) await DisposeLiveAsync(live).ConfigureAwait(false);
                return await ConfirmOrFenceOwnEndAsync(operationId, live?.Id).ConfigureAwait(false);
            }
            if (admission?.Admitted != true)
            {
                if (live is not null) await DisposeLiveAsync(live).ConfigureAwait(false);
                return admission is not null && Session(admission.Thread).State == "ended"
                    ? CodexRealtimeLifecycleStatus.Ended : CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
            }
            try
            {
                if (endProvider is not null)
                    await endProvider(live?.Connection, cancellationToken).ConfigureAwait(false);
                if (live is not null) await DisposeLiveAsync(live).ConfigureAwait(false);
                for (var attempt = 0; attempt < policy.MaximumAttempts; attempt++)
                {
                    var thread = await store.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
                    var session = Session(thread);
                    if (session.State == "ended" && session.EndOperationId == operationId) return CodexRealtimeLifecycleStatus.Ended;
                    try
                    {
                        await store.CompleteRealtimeEndAsync(threadId, thread.Revision, sessionId, ownerId, operationId,
                            cancellationToken).ConfigureAwait(false);
                        return CodexRealtimeLifecycleStatus.Ended;
                    }
                    catch (CodexThreadConflictException) { }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            if (live is not null) await DisposeLiveAsync(live).ConfigureAwait(false);
            return await ConfirmOrFenceOwnEndAsync(operationId, null).ConfigureAwait(false);
        }
        finally { transitions.Release(); }
    }

    private async Task<bool> CloseDirectAsync(Live live, Guid operationId, CancellationToken ct)
    {
        try
        {
            CodexThreadAdmission? admission = null;
            for (var attempt = 0; attempt < policy.MaximumAttempts; attempt++)
            {
                var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                var session = Session(thread);
                if (session.AttachmentId != live.Id || session.EndOperationId is { } prior && prior != operationId)
                    return false;
                try
                {
                    admission = await store.BeginDirectRealtimeCloseAsync(threadId, thread.Revision,
                        sessionId, ownerId, live.Id, operationId, ct).ConfigureAwait(false);
                    break;
                }
                catch (CodexThreadConflictException) when (attempt + 1 < policy.MaximumAttempts) { }
            }
            if (admission?.Admitted == true)
            {
                Task<CodexRealtimeLifecycleStatus> pump;
                lock (sync)
                {
                    // Do not cancel an in-flight receive before sending close. ClientWebSocket
                    // cancellation can abort the socket and discard the provider's final events.
                    live.StopRequested = true;
                    pump = live.Pump ??= PumpCoreAsync(live, null, CancellationToken.None);
                }
                using var close = CancellationTokenSource.CreateLinkedTokenSource(ct);
                close.CancelAfter(policy.DrainTimeout);
                await live.Connection.CloseSessionAsync(close.Token).ConfigureAwait(false);
                await pump.WaitAsync(close.Token).ConfigureAwait(false);
                if (live.Connection.Closure is { End: "remote_close", CloseCode: (int)WebSocketCloseStatus.NormalClosure } &&
                    Volatile.Read(ref pending) is null)
                    return true;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        await DisposeLiveAsync(live).ConfigureAwait(false);
        await ConfirmOrFenceOwnEndAsync(operationId, live.Id).ConfigureAwait(false);
        return false;
    }

    // A lost completion receipt can be reconciled by reading the exact durable end.
    // An uncertain admission may only fence its own operation or original attachment.
    private async Task<CodexRealtimeLifecycleStatus> ConfirmOrFenceOwnEndAsync(Guid operationId, Guid? originalAttachment)
    {
        using var cleanup = new CancellationTokenSource(policy.DrainTimeout);
        var ct = cleanup.Token;
        try
        {
            for (var attempt = 0; attempt < policy.MaximumAttempts; attempt++)
            {
                var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                var session = Session(thread);
                if (session.State == "ended" && session.EndOperationId == operationId)
                    return CodexRealtimeLifecycleStatus.Ended;
                var ownEnd = session.State == "ending" && session.EndOperationId == operationId;
                var ownAttachment = originalAttachment is not null && session.State == "attached" &&
                    session.AttachmentId == originalAttachment &&
                    (session.EndOperationId is null || session.EndOperationId == operationId);
                if (!ownEnd && !ownAttachment) break;
                try
                {
                    await store.RequireRealtimeHistoryRecoveryAsync(threadId, thread.Revision, sessionId, ownerId,
                        ownEnd ? null : originalAttachment, ct).ConfigureAwait(false);
                    break;
                }
                catch (CodexThreadConflictException) { }
                catch (IOException) when (attempt + 1 < policy.MaximumAttempts) { }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
    }

    private async Task<CodexRealtimeLifecycleStatus> AttachCoreAsync(CancellationToken ct)
    {
        var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
        var session = Session(thread);
        if (session.State == "ended") return CodexRealtimeLifecycleStatus.Ended;
        if (session.State == "attached" && GetLive() is { } existing && existing.Id == session.AttachmentId)
            return CodexRealtimeLifecycleStatus.Attached;
        if (session.State != "detached") return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
        var attachmentId = Guid.NewGuid();
        try
        {
            thread = await store.AttachRealtimeSessionAsync(threadId, thread.Revision, sessionId, ownerId, attachmentId, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            // No socket attempt before an acknowledged durable attachment claim.
            await FenceBestEffortAsync(attachmentId).ConfigureAwait(false);
            return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
        }
        session = Session(thread);
        var status = CodexRealtimeLifecycleStatus.Retryable;
        var deadline = DateTimeOffset.UtcNow + policy.Timeout;
        var attempts = directConnect is null ? policy.MaximumAttempts : 1;
        for (var number = 0; number < attempts; number++)
        {
            CodexRealtimeAttachedCall? call = null;
            CodexRealtimeConnection? connection = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                if (Session(thread).AttachmentId != attachmentId)
                    return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
                if (directConnect is not null)
                    connection = await directConnect(new(threadId, sessionId, session.Protocol, thread.AccountId, attachmentId, deadline), ct).ConfigureAwait(false);
                else
                {
                    call = await connect!(new(session.CallId!, session.Protocol, thread.AccountId, attachmentId, deadline), ct).ConfigureAwait(false);
                    connection = call.Connection;
                    if (call.CallId != session.CallId) throw new InvalidOperationException("Existing-call factory returned another call.");
                }
                ct.ThrowIfCancellationRequested();
                if (connection.Protocol != session.Protocol)
                    throw new InvalidOperationException("Realtime factory returned a different wire protocol.");
                thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                if (Session(thread).AttachmentId != attachmentId)
                    throw new InvalidOperationException("Realtime attachment was replaced.");
                lock (sync) current = new(attachmentId, connection, call);
                return CodexRealtimeLifecycleStatus.Attached;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (call is not null) await call.DisposeAsync().ConfigureAwait(false);
                else if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                status = Classify(error);
                if (status != CodexRealtimeLifecycleStatus.Retryable || number + 1 >= attempts) break;
                try { await RetryDelayAsync(number, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { status = CodexRealtimeLifecycleStatus.Cancelled; break; }
            }
        }
        if (!await FenceBestEffortAsync(attachmentId).ConfigureAwait(false)) return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
        // Direct creation may already have reached the provider. Never interpret a
        // failed handshake/initialization as authorization to create another session.
        if (directConnect is not null) return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
        if (status == CodexRealtimeLifecycleStatus.Ended)
        {
            // 404/410 after the activation budget is authoritative for this exact existing call.
            try
            {
                using var cleanup = new CancellationTokenSource(policy.DrainTimeout);
                thread = await store.ReadAsync(threadId, cleanup.Token).ConfigureAwait(false);
                await store.ConfirmRealtimeHistoryRecoveryAsync(threadId, thread.Revision, sessionId, ownerId, true,
                    cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired; }
        }
        return status;
    }

    private async Task<CodexRealtimeLifecycleStatus> PumpCoreAsync(Live live,
        Func<CodexRealtimeEvent, CancellationToken, Task>? observer, CancellationToken cancellationToken)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(live.Stop.Token, cancellationToken);
        var result = CodexRealtimeLifecycleStatus.Retryable;
        try
        {
            await foreach (var item in live.Connection.ReadEventsAsync(read.Token).ConfigureAwait(false))
            {
                var deliver = true;
                if (CodexThreadStore.IsRealtimeHistoryEvent(item))
                {
                    var eventId = CodexRealtimeProtocol.Text(item.Payload, "event_id");
                    var identity = eventId is { Length: > 0 and <= 500 } && !eventId.Any(char.IsControl)
                        ? "provider:" + eventId : Guid.NewGuid().ToString("N");
                    var observation = new CodexRealtimePendingObservation(live.Id, identity, item);
                    Volatile.Write(ref pending, observation);
                    // A parsed event is already accepted from the transport. Do not cancel its
                    // local CAS just because detach/cancellation arrived during the write.
                    using var commit = new CancellationTokenSource(policy.DrainTimeout);
                    deliver = await PersistAsync(observation, false, commit.Token).ConfigureAwait(false);
                    Interlocked.CompareExchange(ref pending, null, observation);
                }
                if (deliver && observer is not null && !read.IsCancellationRequested)
                    await observer(item, read.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result = Volatile.Read(ref pending) is not null ? CodexRealtimeLifecycleStatus.HistoryRecoveryRequired : Classify(error);
        }
        bool stopping;
        lock (sync) stopping = live.StopRequested;
        if (stopping && Volatile.Read(ref pending) is null)
            return CodexRealtimeLifecycleStatus.Cancelled;
        var fenced = await LoseAsync(live).ConfigureAwait(false);
        return !fenced || Volatile.Read(ref pending) is not null ? CodexRealtimeLifecycleStatus.HistoryRecoveryRequired : result;
    }

    private async Task<bool> PersistAsync(CodexRealtimePendingObservation observation, bool recovering, CancellationToken ct)
    {
        var attempted = false;
        for (var number = 0; number < policy.MaximumAttempts; number++)
        {
            var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
            try
            {
                var receipt = recovering
                    ? await store.RecoverRealtimeObservationAsync(threadId, thread.Revision, sessionId, ownerId,
                        observation.AttachmentId, observation.Id, observation.Event, ct).ConfigureAwait(false)
                    : await store.RecordRealtimeObservationAsync(threadId, thread.Revision, sessionId, ownerId,
                        observation.AttachmentId, observation.Id, observation.Event, ct).ConfigureAwait(false);
                return receipt.Admitted || attempted;
            }
            catch (CodexThreadConflictException) when (number + 1 < policy.MaximumAttempts) { }
            catch (IOException) when (number + 1 < policy.MaximumAttempts)
            {
                // Re-read and retry only the same local observation identity. Never replay a
                // remote command. A lost commit receipt is deduplicated by the durable journal.
                attempted = true;
            }
        }
        throw new IOException("Realtime history observation could not be confirmed.");
    }

    private async Task<CodexRealtimeLifecycleStatus> DetachCoreAsync(CancellationToken ct)
    {
        var live = GetLive();
        if (live is null)
        {
            var session = Session(await store.ReadAsync(threadId, ct).ConfigureAwait(false));
            return session.State switch
            {
                "ended" => CodexRealtimeLifecycleStatus.Ended,
                "detached" => CodexRealtimeLifecycleStatus.Detached,
                _ => CodexRealtimeLifecycleStatus.HistoryRecoveryRequired,
            };
        }
        if (!await DrainAsync(live, ct).ConfigureAwait(false))
        {
            await LoseAsync(live).ConfigureAwait(false);
            return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
        }
        try
        {
            if (directConnect is not null)
            {
                await LoseAsync(live).ConfigureAwait(false);
                return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
            }
            await live.Sideband!.DetachAsync(ct).ConfigureAwait(false);
            lock (sync) { if (ReferenceEquals(current, live)) current = null; }
            for (var number = 0; number < policy.MaximumAttempts; number++)
            {
                var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
                var session = Session(thread);
                if (session.State == "detached" && session.UsedAttachmentIds?.LastOrDefault() == live.Id)
                    return CodexRealtimeLifecycleStatus.Detached;
                try
                {
                    await store.DetachRealtimeSessionAsync(threadId, thread.Revision, sessionId, ownerId, live.Id, ct).ConfigureAwait(false);
                    return CodexRealtimeLifecycleStatus.Detached;
                }
                catch (CodexThreadConflictException) { }
                catch (IOException) when (number + 1 < policy.MaximumAttempts) { }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        await LoseAsync(live).ConfigureAwait(false);
        return CodexRealtimeLifecycleStatus.HistoryRecoveryRequired;
    }

    private async Task<bool> DrainAsync(Live live, CancellationToken ct)
    {
        Task<CodexRealtimeLifecycleStatus>? pump;
        lock (sync) { live.StopRequested = true; pump = live.Pump; }
        live.Stop.Cancel();
        if (pump is not null)
        {
            try { await pump.WaitAsync(policy.DrainTimeout, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException) { return false; }
        }
        return Volatile.Read(ref pending) is null;
    }

    private async Task<bool> LoseAsync(Live live)
    {
        await DisposeLiveAsync(live).ConfigureAwait(false);
        return await FenceBestEffortAsync(live.Id).ConfigureAwait(false);
    }

    private async Task DisposeLiveAsync(Live live)
    {
        lock (sync) { if (ReferenceEquals(current, live)) current = null; }
        live.Stop.Cancel();
        await live.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> FenceBestEffortAsync(Guid? expected)
    {
        using var cleanup = new CancellationTokenSource(policy.DrainTimeout);
        try { return await FenceAsync(expected, cleanup.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private async Task<bool> FenceAsync(Guid? expected, CancellationToken ct)
    {
        for (var number = 0; number < policy.MaximumAttempts; number++)
        {
            var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
            var session = Session(thread);
            if (session.State == "recoveryRequired")
                return expected is null || session.UsedAttachmentIds?.LastOrDefault() == expected;
            if (session.State == "ended") return true;
            if (session.AttachmentId != expected || session.State is not ("attached" or "detached" or "ending"))
                return false;
            try
            {
                await store.RequireRealtimeHistoryRecoveryAsync(threadId, thread.Revision, sessionId, ownerId, expected, ct).ConfigureAwait(false);
                return true;
            }
            catch (CodexThreadConflictException) { }
            catch (IOException) when (number + 1 < policy.MaximumAttempts) { }
        }
        return false;
    }

    private CodexStoredRealtimeSession Session(CodexThreadDocument thread)
        => thread.RealtimeSessions?.SingleOrDefault(s => s.SessionId == sessionId && s.OwnerId == ownerId && s.TransportKind == TransportKind)
            ?? throw new InvalidOperationException("Realtime logical session, owner or transport kind no longer matches.");

    private Live? GetLive() { lock (sync) return current; }
    private void ThrowIfDisposed() { lock (sync) ObjectDisposedException.ThrowIf(disposed, this); }

    private CancellationTokenSource BeginAttempt(CancellationToken ct)
    {
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(policy.Timeout);
        lock (sync) activeAttempt = attempt;
        return attempt;
    }

    private void EndAttempt(CancellationTokenSource attempt)
    {
        lock (sync) { if (ReferenceEquals(activeAttempt, attempt)) activeAttempt = null; }
    }

    private void CancelAttempt()
    {
        CancellationTokenSource? attempt;
        lock (sync) attempt = activeAttempt;
        try { attempt?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private Task RetryDelayAsync(int number, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, policy.RetryDelay.TotalMilliseconds * (1 << number))), ct);

    private static CodexRealtimeLifecycleStatus RecoveryStatus(CodexRealtimeRecoveryDecision decision) => decision switch
    {
        CodexRealtimeRecoveryDecision.Retryable => CodexRealtimeLifecycleStatus.Retryable,
        CodexRealtimeRecoveryDecision.AuthenticationRejected => CodexRealtimeLifecycleStatus.AuthenticationRejected,
        CodexRealtimeRecoveryDecision.QuotaRejected => CodexRealtimeLifecycleStatus.QuotaRejected,
        CodexRealtimeRecoveryDecision.Fatal => CodexRealtimeLifecycleStatus.Fatal,
        _ => CodexRealtimeLifecycleStatus.HistoryRecoveryRequired,
    };

    private static CodexRealtimeLifecycleStatus Classify(Exception error) => error switch
    {
        OperationCanceledException => CodexRealtimeLifecycleStatus.Cancelled,
        CodexProtocolException { StatusCode: 404 or 410 } => CodexRealtimeLifecycleStatus.Ended,
        CodexProtocolException { StatusCode: 401 or 403 } or CodexAuthorizationUnavailableException or
            AuthenticationException or UnauthorizedAccessException or CodexRealtimeAccountMismatchException => CodexRealtimeLifecycleStatus.AuthenticationRejected,
        CodexProtocolException { StatusCode: 429 } => CodexRealtimeLifecycleStatus.QuotaRejected,
        CodexProtocolException { StatusCode: 408 or 500 or 502 or 503 or 504 } or
            CodexProtocolException { TransientSidebandTransport: true } or
            WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely or WebSocketError.Faulted }
            => CodexRealtimeLifecycleStatus.Retryable,
        ChannelClosedException { InnerException: { } inner } => Classify(inner),
        CodexProtocolException => CodexRealtimeLifecycleStatus.Fatal,
        IOException => CodexRealtimeLifecycleStatus.Retryable,
        _ => CodexRealtimeLifecycleStatus.Fatal,
    };

    /// <summary>Release transport ownership without ending the logical session or media call.</summary>
    public async ValueTask DisposeAsync()
    {
        CancelAttempt();
        await transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) { if (disposed) return; disposed = true; }
            using var cleanup = new CancellationTokenSource(policy.DrainTimeout);
            try { await DetachCoreAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (GetLive() is { } live) await LoseAsync(live).ConfigureAwait(false); }
        }
        finally { transitions.Release(); }
    }
}

public sealed partial class CodexClient
{
    /// <summary>Create a lifecycle controller with the native existing-call transport factory.
    /// SDK OAuth credentials are fenced to the persisted thread account; external headers
    /// and API keys remain explicitly caller-owned.</summary>
    public CodexRealtimeSessionController CreateRealtimeSessionController(CodexThreadStore store, Guid threadId,
        string sessionId, string ownerId, CodexRealtimeAttachmentOptions? attachment = null,
        CodexRealtimeReconnectOptions? reconnect = null)
        => new(store, threadId, sessionId, ownerId, (request, ct) =>
        {
            var selected = attachment ?? new();
            var account = selected.Authentication == CodexRealtimeAttachmentAuthentication.ClientCredentials &&
                realtimeApiKeyProvider is null ? request.AccountId : selected.ExpectedAccountId;
            if (selected.ExpectedAccountId is not null && account != selected.ExpectedAccountId)
                throw new CodexRealtimeAccountMismatchException();
            var deadline = selected.StartBefore is { } earlier && earlier < request.StartBefore ? earlier : request.StartBefore;
            return AttachExistingRealtimeCallAsync(request.CallId, new CodexRealtimeAttachmentOptions
            {
                Protocol = request.Protocol, SidebandBaseUri = selected.SidebandBaseUri,
                Authentication = selected.Authentication, ExternalHeaderProvider = selected.ExternalHeaderProvider,
                SessionId = selected.SessionId, VoiceSessionId = selected.VoiceSessionId,
                ExpectedAccountId = account, StartBefore = deadline,
            }, ct);
        }, reconnect);
}
