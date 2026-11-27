namespace tryAGI.Codex;

/// <summary>Connects native managed device-code login and explicitly authorized local logout to the
/// encrypted owner directory. The host supplies admission and commit-time policy leases. This adapter
/// does not revoke OAuth credentials at the provider, start a browser, or contact a paid endpoint.
/// All cooperating processes must use journal-aware directory and selected-credential admission.</summary>
public sealed class CodexDurableAccountLifecycleAuthority : ICodexAccountLifecycleAuthority
{
    private readonly CodexEncryptedAccountSessionDirectory directory;
    private readonly Func<CodexAccountLifecycleOperation, CodexAccountLifecycleTarget, CancellationToken, Task> authorize;
    private readonly Func<CodexAccountLifecycleOperation, CodexAccountLifecycleTarget,
        CodexAccountSessionCredentialFence?, CancellationToken, ValueTask<IAsyncDisposable?>> acquirePolicy;

    /// <summary>Creates an authorized local lifecycle adapter. Commit-time policy must validate the
    /// captured target, allowed login method, candidate account/workspace and local logout permission.
    /// The policy lease must remain authoritative until disposal and must not acquire vault locks.
    /// If upstream token revocation is required, the host must arrange it separately; local logout is not revocation.</summary>
    public CodexDurableAccountLifecycleAuthority(CodexEncryptedAccountSessionDirectory directory,
        Func<CodexAccountLifecycleOperation, CodexAccountLifecycleTarget, CancellationToken, Task> authorize,
        Func<CodexAccountLifecycleOperation, CodexAccountLifecycleTarget,
            CodexAccountSessionCredentialFence?, CancellationToken, ValueTask<IAsyncDisposable?>> acquirePolicy)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(acquirePolicy);
        directory.RequireLifecycleStore();
        this.directory = directory;
        this.authorize = authorize;
        this.acquirePolicy = acquirePolicy;
    }

    /// <inheritdoc />
    public async Task<CodexAccountLifecycleTarget> AuthorizeAsync(CodexAccountLifecycleOperation operation, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        var target = await directory.CaptureLifecycleTargetAsync(operation, cancellationToken).ConfigureAwait(false);
        await authorize(operation, target, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return target;
    }

    /// <inheritdoc />
    public Task<bool> TryCommitLoginAsync(CodexAccountLifecycleTarget target, Guid loginId, CodexOAuthTokens tokens,
        CancellationToken cancellationToken) => directory.CommitNewLoginAsync(target, loginId, tokens, acquirePolicy, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryLogoutAsync(CodexAccountLifecycleTarget target, CancellationToken cancellationToken)
        => directory.CommitLocalLogoutAsync(target, acquirePolicy, cancellationToken);
}

public sealed partial class CodexEncryptedAccountSessionDirectory
{
    private const string NoActiveSession = "no-active-session";
    private const string LoginPrefix = "login-";

    internal CodexEncryptedOAuthSessionStore RequireLifecycleStore() => credentials as CodexEncryptedOAuthSessionStore
        ?? throw new InvalidOperationException("The durable lifecycle requires the encrypted OAuth session store.");

    private string ResolveCredentialId(string sessionId, string accountId)
    {
        var id = credentialId(sessionId, accountId);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id == directoryId || id.Any(char.IsControl))
            throw new CodexProtocolException("Invalid account credential mapping.");
        return id;
    }

    private HashSet<string> ReachableCredentialIds(CodexAccountSessionDirectorySnapshot snapshot)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in snapshot.Sessions)
            foreach (var workspace in session.Workspaces)
                if (!ids.Add(ResolveCredentialId(session.SessionId, workspace.AccountId)))
                    throw new CodexProtocolException("Account credential mapping is not injective.");
        return ids;
    }

    internal async Task<CodexAccountLifecycleTarget> CaptureLifecycleTargetAsync(CodexAccountLifecycleOperation operation, CancellationToken ct)
    {
        await using var directory = await AcquireDirectoryAsync(ct).ConfigureAwait(false);
        var current = await ReadDirectoryAsync(directory, ct).ConfigureAwait(false);
        if (operation == CodexAccountLifecycleOperation.DeviceLogin)
            return new(LoginPrefix + NewRevision(), current.SelectionEpoch, null);
        if (current.ActiveSessionId is null) return new(NoActiveSession, current.SelectionEpoch, null);
        var entry = current.Sessions.Single(session => session.SessionId == current.ActiveSessionId);
        var account = entry.SelectedWorkspaceAccountId!;
        await using var credential = await RequireLifecycleStore().AcquireCoreAsync(ResolveCredentialId(entry.SessionId, account), ct).ConfigureAwait(false);
        var state = await credential.ReadAsync(ct).ConfigureAwait(false);
        ValidateCredentialOwner(entry, account, state);
        return new(entry.SessionId, current.SelectionEpoch, Stamp(state));
    }

    internal async Task<bool> CommitNewLoginAsync(CodexAccountLifecycleTarget target, Guid loginId, CodexOAuthTokens tokens,
        Func<CodexAccountLifecycleOperation, CodexAccountLifecycleTarget,
            CodexAccountSessionCredentialFence?, CancellationToken, ValueTask<IAsyncDisposable?>> policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tokens);
        if (loginId == Guid.Empty || target.CredentialRevision is not null ||
            !target.SessionId.StartsWith(LoginPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(target.SessionId[LoginPrefix.Length..], "N", out _) ||
            string.IsNullOrWhiteSpace(tokens.ChatGptUserId) || tokens.IsFedRampAccount ||
            tokens.ExpiresAt <= clock.GetUtcNow()) return false;

        await using var directory = await AcquireDirectoryAsync(ct).ConfigureAwait(false);
        var current = await ReadDirectoryAsync(directory, ct).ConfigureAwait(false);
        if (current.SelectionEpoch != target.SelectionEpoch || current.Sessions.Any(session => session.SessionId == target.SessionId))
            return false;
        var entry = new CodexAccountSessionEntry(target.SessionId, tokens.ChatGptUserId, null, null, null,
            clock.GetUtcNow().ToUnixTimeSeconds(), tokens.AccountId, [new(tokens.AccountId)]);
        var published = new CodexAccountSessionDirectorySnapshot(NewRevision(), NewRevision(), entry.SessionId, [.. current.Sessions, entry]);
        CodexAccountSessionsRpc.Validate(published);
        var id = ResolveCredentialId(entry.SessionId, tokens.AccountId);
        if (ReachableCredentialIds(current).Contains(id)) return false;
        await using var credential = await RequireLifecycleStore().AcquireCoreAsync(id, ct).ConfigureAwait(false);
        if (await credential.ReadAsync(ct).ConfigureAwait(false) is not null) return false;
        var revision = NewRevision();
        var fence = new CodexAccountSessionCredentialFence(revision + ":ready", entry.SessionId, tokens.AccountId,
            tokens.ChatGptUserId, tokens.ExpiresAt, false, tokens.IsFedRampAccount);
        await using var grant = await policy(CodexAccountLifecycleOperation.DeviceLogin, target, fence, ct).ConfigureAwait(false);
        if (grant is null || !Usable(fence)) return false;

        // The cleanup intent is durable BEFORE the new credential is written. The directory
        // still selects the old account. Advance its epoch to invalidate prior admissions and
        // prevent replay of this target after an interrupted login. Only the final CAS publishes the new account.
        var staged = current with { Revision = NewRevision(), SelectionEpoch = NewRevision() };
        var cleanup = new CodexAccountDirectoryCleanup(entry.SessionId, tokens.AccountId, tokens.ChatGptUserId, id, revision);
        if (!await PersistAsync(directory, current, staged, ct, [cleanup]).ConfigureAwait(false)) return false;
        if (!await credential.TryInstallNewAsync(revision, tokens, ct).ConfigureAwait(false))
            throw new CodexProtocolException("Staged login outcome is unconfirmed.");
        if (!Usable(fence)) throw new CodexProtocolException("Staged credentials expired before publication.");
        if (!await PersistAsync(directory, staged, published, ct).ConfigureAwait(false))
            throw new CodexProtocolException("Login publication outcome is unconfirmed.");
        return true;
    }

    internal async Task<bool> CommitLocalLogoutAsync(CodexAccountLifecycleTarget target,
        Func<CodexAccountLifecycleOperation, CodexAccountLifecycleTarget,
            CodexAccountSessionCredentialFence?, CancellationToken, ValueTask<IAsyncDisposable?>> policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var directory = await AcquireDirectoryAsync(ct).ConfigureAwait(false);
        var current = await ReadDirectoryAsync(directory, ct).ConfigureAwait(false);
        if (current.SelectionEpoch != target.SelectionEpoch) return false;
        if (current.ActiveSessionId is null)
        {
            if (target.SessionId != NoActiveSession || target.CredentialRevision is not null) return false;
            await using var emptyGrant = await policy(CodexAccountLifecycleOperation.Logout, target, null, ct).ConfigureAwait(false);
            if (emptyGrant is null) return false;
            // Invalidate pending login admissions on other connections even when already signed out.
            return await PersistAsync(directory, current, current with { Revision = NewRevision(), SelectionEpoch = NewRevision() }, ct).ConfigureAwait(false);
        }
        if (current.ActiveSessionId != target.SessionId) return false;
        _ = ReachableCredentialIds(current);
        var entry = current.Sessions.Single(session => session.SessionId == target.SessionId);
        if (entry.UserId is null) throw new CodexProtocolException("Account session owner is unavailable.");
        var accounts = entry.Workspaces.Select(workspace => (workspace.AccountId, Id: ResolveCredentialId(entry.SessionId, workspace.AccountId)))
            .OrderBy(workspace => workspace.Id, StringComparer.Ordinal).ToArray();
        var held = new List<(string AccountId, string Id, CodexEncryptedOAuthSessionStore.Lease Lease, CodexOAuthSessionState? State)>();
        try
        {
            // Directory -> credential leases (stable ID order) -> host policy. Hold every relevant
            // credential generation through the directory commit; unrelated accounts are untouched.
            foreach (var account in accounts)
            {
                var lease = await RequireLifecycleStore().AcquireCoreAsync(account.Id, ct).ConfigureAwait(false);
                held.Add((account.AccountId, account.Id, lease, null));
                var state = await lease.ReadAsync(ct).ConfigureAwait(false);
                ValidateCredentialOwner(entry, account.AccountId, state);
                held[^1] = (account.AccountId, account.Id, lease, state);
            }
            var selected = held.Single(value => value.AccountId == entry.SelectedWorkspaceAccountId);
            if (Stamp(selected.State) != target.CredentialRevision) return false;
            var fence = selected.State is { } stateValue
                ? new CodexAccountSessionCredentialFence(Stamp(stateValue)!, entry.SessionId, selected.AccountId, entry.UserId,
                    stateValue.Tokens.ExpiresAt, stateValue.RefreshAttemptId is not null, stateValue.Tokens.IsFedRampAccount)
                : null;
            await using var grant = await policy(CodexAccountLifecycleOperation.Logout, target, fence, ct).ConfigureAwait(false);
            if (grant is null) return false;

            var cleanup = held.Where(item => item.State is not null).Select(item =>
                new CodexAccountDirectoryCleanup(entry.SessionId, item.AccountId, entry.UserId, item.Id, item.State!.Revision)).ToArray();
            var removed = new CodexAccountSessionDirectorySnapshot(NewRevision(), NewRevision(), null,
                current.Sessions.Where(session => session.SessionId != entry.SessionId).ToArray());
            // The directory removal and cleanup intent are one CAS. No credential is deleted
            // before selection authority is revoked. Recovery finishes only exact recorded generations.
            if (!await PersistAsync(directory, current, removed, ct, cleanup.Length == 0 ? null : cleanup).ConfigureAwait(false)) return false;
            if (cleanup.Length == 0) return true;
            foreach (var item in held)
                if (item.State is { } state && !await item.Lease.TryDeleteGenerationAsync(state.Revision, item.AccountId, entry.UserId, ct).ConfigureAwait(false))
                    throw new CodexProtocolException("Logout cleanup outcome is unconfirmed.");
            if (!await PersistAsync(directory, removed, removed with { Revision = NewRevision() }, ct).ConfigureAwait(false))
                throw new CodexProtocolException("Logout cleanup publication is unconfirmed.");
            return true;
        }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--)
                await held[i].Lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<CodexAccountSessionDirectorySnapshot> RecoverCleanupAsync(ICodexOAuthVaultLease directory,
        CodexAccountSessionDirectorySnapshot snapshot, IReadOnlyList<CodexAccountDirectoryCleanup> cleanup, CancellationToken ct)
    {
        var reachable = ReachableCredentialIds(snapshot);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // Validate the entire journal before deleting anything. No live directory entry can be a cleanup target.
        foreach (var item in cleanup)
            if (item is null || snapshot.Sessions.Any(session => session.SessionId == item.SessionId) ||
                string.IsNullOrWhiteSpace(item.AccountId) || string.IsNullOrWhiteSpace(item.UserId) ||
                item.CredentialId != ResolveCredentialId(item.SessionId, item.AccountId) ||
                reachable.Contains(item.CredentialId) || !ids.Add(item.CredentialId) ||
                !Guid.TryParseExact(item.Revision, "N", out _)) throw Invalid();
        foreach (var item in cleanup)
        {
            await using var credential = await RequireLifecycleStore().AcquireCoreAsync(item.CredentialId, ct).ConfigureAwait(false);
            if (!await credential.TryDeleteGenerationAsync(item.Revision, item.AccountId, item.UserId, ct).ConfigureAwait(false))
                throw new CodexProtocolException("Account lifecycle recovery requires reconciliation.");
        }
        var recovered = snapshot with { Revision = NewRevision() };
        if (!await PersistAsync(directory, snapshot, recovered, ct).ConfigureAwait(false))
            throw new CodexProtocolException("Account lifecycle recovery outcome is unconfirmed.");
        return recovered;
    }

    private static void ValidateCredentialOwner(CodexAccountSessionEntry entry, string account, CodexOAuthSessionState? state)
    {
        if (state is not null && (state.Tokens.AccountId != account || state.Tokens.ChatGptUserId != entry.UserId ||
            entry.UserId is null || !Guid.TryParseExact(state.Revision, "N", out _)))
            throw new CodexProtocolException("Account credential owner does not match the saved login.");
    }

    private static string? Stamp(CodexOAuthSessionState? state) => state is null ? null :
        state.Revision + ":" + (state.RefreshAttemptId?.ToString("N") ?? "ready");
}
