using System.Security.Cryptography;

namespace tryAGI.Codex;

/// <summary>Exact action class; none of these values grants authority by itself.</summary>
public enum CodexApprovalKind
{
    /// <summary>Standalone or tool command execution.</summary>
    Command,
    /// <summary>Workspace patch or file change.</summary>
    FileChange,
    /// <summary>Permission expansion request.</summary>
    Permissions,
    /// <summary>Tool or MCP invocation.</summary>
    Tool,
    /// <summary>Guardian-reviewed action.</summary>
    Guardian,
}

/// <summary>Immutable correlation for an action. Host code computes the fingerprint from the
/// exact resolved action, including every permission, environment and argument that affects it.
/// Never accept the fingerprint or authenticated identities solely from model output.</summary>
/// <param name="Kind">Action class.</param>
/// <param name="ConnectionId">Authenticated connection identity.</param>
/// <param name="ActionId">Host-generated identity of this execution attempt.</param>
/// <param name="ActionSha256">Lower-case SHA-256 of the exact action bytes.</param>
/// <param name="ThreadId">Owning thread, when applicable.</param>
/// <param name="TurnId">Owning turn, when applicable.</param>
/// <param name="ItemId">Owning item/tool call, when applicable.</param>
/// <param name="EnvironmentId">Owning execution environment, when applicable.</param>
public sealed record CodexApprovalAction(CodexApprovalKind Kind, string ConnectionId, string ActionId,
    string ActionSha256, string? ThreadId = null, string? TurnId = null, string? ItemId = null,
    string? EnvironmentId = null)
{
    /// <summary>Computes a fingerprint of the host's exact action representation.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> exactAction) =>
        Convert.ToHexStringLower(SHA256.HashData(exactAction));
}

/// <summary>Persisted approval lifecycle. Consumed is a one-time dispatch admission, not an execution receipt.</summary>
public enum CodexApprovalState
{
    /// <summary>Awaiting explicit decision.</summary>
    Pending,
    /// <summary>Approved, but not yet consumed.</summary>
    Approved,
    /// <summary>Explicitly denied.</summary>
    Denied,
    /// <summary>Cancelled before admission.</summary>
    Cancelled,
    /// <summary>Previously approved authority withdrawn before admission.</summary>
    Revoked,
    /// <summary>Decision or use deadline elapsed.</summary>
    Expired,
    /// <summary>One dispatch admission consumed the approval.</summary>
    Consumed,
}

/// <summary>Account-scoped approval journal record; contains correlation and fingerprints, not action secrets.</summary>
/// <param name="Id">Stable host-generated request ID.</param>
/// <param name="AccountId">Authenticated account.</param>
/// <param name="Action">Exact action scope.</param>
/// <param name="Revision">CAS revision, initially 1.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="CreatedAt">Creation timestamp.</param>
/// <param name="ExpiresAt">Exclusive deadline for decisions and consumption.</param>
/// <param name="UpdatedAt">Latest transition timestamp.</param>
/// <param name="ActorId">Last authenticated reviewer/cancellation actor, if present.</param>
public sealed record CodexApprovalRecord(Guid Id, string AccountId, CodexApprovalAction Action, long Revision,
    CodexApprovalState State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt,
    string? ActorId = null)
{
    /// <summary>Optional immutable correlation for a native approval server request.</summary>
    public CodexApprovalRpcBinding? RpcBinding { get; init; }
}

/// <summary>Host-owned durable atomic storage, partitioned by account and request ID.
/// CompareExchange must return true only after a durable commit. A thrown write may have
/// committed; callers must never dispatch on an uncertain outcome.</summary>
public interface ICodexApprovalStorage
{
    /// <summary>Reads a record from the specified account partition; null means absent.</summary>
    Task<CodexApprovalRecord?> ReadAsync(string accountId, Guid requestId, CancellationToken cancellationToken);

    /// <summary>Atomically commits a record if the revision matches; null means the key must not exist.</summary>
    Task<bool> CompareExchangeAsync(string accountId, Guid requestId, long? expectedRevision,
        CodexApprovalRecord record, CancellationToken cancellationToken);
}

/// <summary>Trusted host authorization input for an approval mutation.</summary>
/// <param name="Record">Current persisted request.</param>
/// <param name="ActorId">Authenticated actor supplied by the host.</param>
/// <param name="RequestedState">Requested decision, cancellation or revocation.</param>
public sealed record CodexApprovalAuthorization(CodexApprovalRecord Record, string ActorId,
    CodexApprovalState RequestedState);

/// <summary>Account-bound durable, one-action approval journal. This is a host-side authority
/// primitive, not a JSON-RPC response handler. The embedding host must authenticate callers
/// before using it, resolve action scope itself, and authorize every reviewer mutation.
/// Turn/session-wide permission grants require a separate policy and are not implied here.</summary>
public sealed class CodexApprovalStore
{
    private readonly ICodexApprovalStorage storage;
    private readonly string accountId;
    private readonly Func<CodexApprovalAuthorization, CancellationToken, ValueTask> authorize;
    private readonly TimeProvider clock;
    private readonly TimeSpan maximumLifetime;
    internal string AccountId => accountId;
    internal bool IsUnexpired(CodexApprovalRecord record)
    {
        var now = clock.GetUtcNow();
        return record.AccountId == accountId && now < record.ExpiresAt && now >= record.UpdatedAt;
    }

    /// <summary>Creates an approval journal with an explicit storage and reviewer authorization boundary.</summary>
    public CodexApprovalStore(ICodexApprovalStorage storage, string accountId,
        Func<CodexApprovalAuthorization, CancellationToken, ValueTask> authorize,
        TimeProvider? clock = null, TimeSpan? maximumLifetime = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ValidateIdentity(accountId, nameof(accountId));
        this.accountId = accountId;
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        this.clock = clock ?? TimeProvider.System;
        this.maximumLifetime = maximumLifetime ?? TimeSpan.FromMinutes(15);
        if (this.maximumLifetime <= TimeSpan.Zero || this.maximumLifetime > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime));
    }

    /// <summary>Creates a pending request. Exact repeated identities are idempotent, but an
    /// existing identity with a different action or deadline is never replaced or resurrected.</summary>
    public async Task<CodexApprovalRecord> RequestAsync(Guid id, CodexApprovalAction action,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        (await AdmitAsync(id, action, expiresAt, null, cancellationToken).ConfigureAwait(false)).Record;

    internal async Task<CodexApprovalRpcAdmission> AdmitAsync(Guid id, CodexApprovalAction action,
        DateTimeOffset expiresAt, CodexApprovalRpcBinding? rpcBinding, CancellationToken cancellationToken)
    {
        ValidateId(id);
        ValidateAction(action);
        var existing = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return new(MatchRequest(existing, action, expiresAt, rpcBinding), false);
        var now = clock.GetUtcNow();
        if (expiresAt <= now || expiresAt - now > maximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        var pending = new CodexApprovalRecord(id, accountId, action, 1, CodexApprovalState.Pending,
            now, expiresAt, now) { RpcBinding = rpcBinding };
        cancellationToken.ThrowIfCancellationRequested();
        if (await storage.CompareExchangeAsync(accountId, id, null, pending, cancellationToken).ConfigureAwait(false))
            return new(pending, true);
        existing = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        return existing is not null ? new(MatchRequest(existing, action, expiresAt, rpcBinding), false) : throw Conflict();
    }

    /// <summary>Reads a request, atomically materializing expiration before returning it.</summary>
    public async Task<CodexApprovalRecord?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var record = await ReadCoreAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null || !CanExpire(record.State) || clock.GetUtcNow() < record.ExpiresAt)
                return record;
            var expired = Transition(record, CodexApprovalState.Expired, record.ActorId);
            if (await storage.CompareExchangeAsync(accountId, id, record.Revision, expired, cancellationToken).ConfigureAwait(false))
                return expired;
        }
        throw Conflict();
    }

    /// <summary>Applies an explicitly authorized approve/deny decision to the exact pending revision.</summary>
    public Task<CodexApprovalRecord> DecideAsync(Guid id, CodexApprovalAction expectedAction,
        long expectedRevision, bool approved, string actorId, CancellationToken cancellationToken = default) =>
        MutateAsync(id, expectedAction, expectedRevision, actorId,
            approved ? CodexApprovalState.Approved : CodexApprovalState.Denied, cancellationToken);

    /// <summary>Cancels an exact pending or approved request before consumption.</summary>
    public Task<CodexApprovalRecord> CancelAsync(Guid id, CodexApprovalAction expectedAction,
        long expectedRevision, string actorId, CancellationToken cancellationToken = default) =>
        MutateAsync(id, expectedAction, expectedRevision, actorId, CodexApprovalState.Cancelled, cancellationToken);

    /// <summary>Revokes exact, previously approved authority before it has been consumed.</summary>
    public Task<CodexApprovalRecord> RevokeAsync(Guid id, CodexApprovalAction expectedAction,
        long expectedRevision, string actorId, CancellationToken cancellationToken = default) =>
        MutateAsync(id, expectedAction, expectedRevision, actorId, CodexApprovalState.Revoked, cancellationToken);

    /// <summary>Atomically consumes one unexpired approval for the exact action. Null is a denial.
    /// Only the successful caller may admit the action; the returned deadline must still be checked
    /// by the execution adapter at use. Never execute on a thrown or ambiguous CAS outcome and never
    /// reconstruct authority by reading a Consumed record after restart. Revocation after consumption
    /// cannot undo a dispatched effect; active execution cancellation is a separate host operation.</summary>
    public async Task<CodexApprovalRecord?> TryConsumeAsync(Guid id, CodexApprovalAction expectedAction,
        CancellationToken cancellationToken = default)
    {
        ValidateAction(expectedAction);
        var current = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Action != expectedAction || current.State != CodexApprovalState.Approved ||
            clock.GetUtcNow() >= current.ExpiresAt)
            return null;
        var consumed = Transition(current, CodexApprovalState.Consumed, current.ActorId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await storage.CompareExchangeAsync(accountId, id, current.Revision, consumed, cancellationToken).ConfigureAwait(false))
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        // A delayed durable write must not extend the grant's original deadline.
        return clock.GetUtcNow() < consumed.ExpiresAt ? consumed : null;
    }

    private async Task<CodexApprovalRecord> MutateAsync(Guid id, CodexApprovalAction expectedAction,
        long expectedRevision, string actorId, CodexApprovalState state, CancellationToken ct)
    {
        ValidateAction(expectedAction);
        ValidateIdentity(actorId, nameof(actorId));
        var current = await ReadAsync(id, ct).ConfigureAwait(false);
        if (current is null || current.Action != expectedAction || current.Revision != expectedRevision ||
            !Allowed(current.State, state))
            throw Conflict();
        await authorize(new(current, actorId, state), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (clock.GetUtcNow() >= current.ExpiresAt)
        {
            _ = await ReadAsync(id, ct).ConfigureAwait(false);
            throw Conflict();
        }
        var updated = Transition(current, state, actorId);
        if (!await storage.CompareExchangeAsync(accountId, id, current.Revision, updated, ct).ConfigureAwait(false))
            throw Conflict();
        return state == CodexApprovalState.Approved && clock.GetUtcNow() >= updated.ExpiresAt
            ? (await ReadAsync(id, ct).ConfigureAwait(false))!
            : updated;
    }

    private async Task<CodexApprovalRecord?> ReadCoreAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var record = await storage.ReadAsync(accountId, id, ct).ConfigureAwait(false);
        if (record is null) return null;
        if (record.AccountId != accountId || record.Id != id || record.Revision < 1 ||
            !Enum.IsDefined(record.State) || record.ExpiresAt <= record.CreatedAt ||
            record.UpdatedAt < record.CreatedAt ||
            (record.State is CodexApprovalState.Approved or CodexApprovalState.Consumed &&
                string.IsNullOrWhiteSpace(record.ActorId)))
            throw new InvalidDataException("Approval storage returned an invalid account, identity or state.");
        ValidateAction(record.Action);
        return record;
    }

    private CodexApprovalRecord Transition(CodexApprovalRecord current, CodexApprovalState state, string? actor)
    {
        var now = clock.GetUtcNow();
        if (now < current.UpdatedAt) throw new InvalidOperationException("Approval clock moved backwards; refusing mutation.");
        return current with { Revision = checked(current.Revision + 1), State = state, UpdatedAt = now, ActorId = actor };
    }

    private static bool CanExpire(CodexApprovalState state) => state is CodexApprovalState.Pending or CodexApprovalState.Approved;
    private static bool Allowed(CodexApprovalState current, CodexApprovalState next) => next switch
    {
        CodexApprovalState.Approved or CodexApprovalState.Denied => current == CodexApprovalState.Pending,
        CodexApprovalState.Cancelled => CanExpire(current),
        CodexApprovalState.Revoked => current == CodexApprovalState.Approved,
        _ => false,
    };
    private static CodexApprovalRecord MatchRequest(CodexApprovalRecord current, CodexApprovalAction action,
        DateTimeOffset expiresAt, CodexApprovalRpcBinding? rpcBinding) =>
        current.Action == action && current.ExpiresAt == expiresAt && current.RpcBinding == rpcBinding ? current : throw Conflict();

    private static InvalidOperationException Conflict() => new("Approval is unknown, stale, mismatched or no longer actionable.");
    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Approval ID must not be empty.", nameof(id));
    }
    private static void ValidateIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid approval identity.", name);
    }
    private static void ValidateAction(CodexApprovalAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Enum.IsDefined(action.Kind) || action.ActionSha256 is not { Length: 64 } ||
            action.ActionSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Approval requires a known action kind and exact SHA-256.", nameof(action));
        ValidateIdentity(action.ConnectionId, nameof(action.ConnectionId));
        ValidateIdentity(action.ActionId, nameof(action.ActionId));
        foreach (var identity in new[] { action.ThreadId, action.TurnId, action.ItemId, action.EnvironmentId })
            if (identity is not null) ValidateIdentity(identity, nameof(action));
        if ((action.TurnId is not null && action.ThreadId is null) || (action.ItemId is not null && action.TurnId is null))
            throw new ArgumentException("Approval scope is missing its parent identity.", nameof(action));
    }
}
