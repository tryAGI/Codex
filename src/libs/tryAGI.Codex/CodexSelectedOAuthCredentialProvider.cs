namespace tryAGI.Codex;

public sealed partial class CodexEncryptedAccountSessionDirectory
{
    /// <summary>Creates a refresh-capable provider pinned to the currently selected login, workspace and
    /// selection epoch. Every credential read or refresh acquires the directory before the credential
    /// lease; a switch or logout invalidates old providers, including switching back to the same account.
    /// The host still fences actual transport admission against the selected epoch: previously returned
    /// bearer tokens and in-flight provider requests cannot be recalled by local storage.</summary>
    public async Task<CodexOAuthCredentialProvider> CreateSelectedCredentialProviderAsync(CodexDeviceAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        await using var directory = await AcquireDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        var entry = snapshot.Sessions.SingleOrDefault(session => session.SessionId == snapshot.ActiveSessionId);
        if (entry?.UserId is null || entry.SelectedWorkspaceAccountId is not { } account)
            throw new CodexAuthorizationUnavailableException();
        var id = ResolveCredentialId(entry.SessionId, account);
        var store = new SelectedStore(this, entry.SessionId, entry.UserId, account, snapshot.SelectionEpoch, id);
        return new(store, id, authorization, clock);
    }

    private sealed class SelectedStore(CodexEncryptedAccountSessionDirectory owner, string session,
        string user, string account, string epoch, string id) : ICodexOAuthSessionStore
    {
        public async ValueTask<ICodexOAuthSessionLease> AcquireAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (sessionId != id) throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);
            var directory = await owner.AcquireDirectoryAsync(cancellationToken).ConfigureAwait(false);
            ICodexOAuthSessionLease? credential = null;
            IAsyncDisposable? policy = null;
            try
            {
                var snapshot = await owner.ReadDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                var entry = Find(snapshot, session, account);
                if (snapshot.SelectionEpoch != epoch || snapshot.ActiveSessionId != session ||
                    entry?.UserId != user || entry.SelectedWorkspaceAccountId != account ||
                    owner.ResolveCredentialId(session, account) != id)
                    throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);

                credential = await owner.credentials.AcquireAsync(id, cancellationToken).ConfigureAwait(false);
                var fence = await ObserveAsync(credential, entry, account, cancellationToken).ConfigureAwait(false);
                if (fence is null || fence.Restricted) throw new CodexAuthorizationUnavailableException();
                if (fence.RefreshPending) throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.PendingAttempt);
                // A policy may authorize refreshing an expired credential, but never an unrelated identity.
                policy = await owner.acquirePolicy(fence, cancellationToken).ConfigureAwait(false);
                if (policy is null) throw new CodexAuthorizationUnavailableException();
                cancellationToken.ThrowIfCancellationRequested();
                return new SelectedLease(directory, credential, policy);
            }
            catch
            {
                try { if (policy is not null) await policy.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try { if (credential is not null) await credential.DisposeAsync().ConfigureAwait(false); }
                    finally { await directory.DisposeAsync().ConfigureAwait(false); }
                }
                throw;
            }
        }
    }

    private sealed class SelectedLease(ICodexOAuthVaultLease directory, ICodexOAuthSessionLease credential,
        IAsyncDisposable policy) : ICodexOAuthSessionLease
    {
        public ValueTask<CodexOAuthSessionState?> ReadAsync(CancellationToken cancellationToken)
            => credential.ReadAsync(cancellationToken);

        public ValueTask<bool> TryBeginRefreshAsync(string expectedRevision, Guid attemptId, CancellationToken cancellationToken)
            => credential.TryBeginRefreshAsync(expectedRevision, attemptId, cancellationToken);

        public ValueTask<bool> TryCompleteRefreshAsync(string expectedRevision, Guid attemptId, CodexOAuthTokens tokens, CancellationToken cancellationToken)
            => credential.TryCompleteRefreshAsync(expectedRevision, attemptId, tokens, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try { await policy.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { await credential.DisposeAsync().ConfigureAwait(false); }
                finally { await directory.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }
}
