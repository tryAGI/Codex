using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Host-owned OAuth session state. The revision identifies one credential generation.</summary>
public sealed class CodexOAuthSessionState(string revision, CodexOAuthTokens tokens, Guid? refreshAttemptId = null)
{
    /// <summary>Opaque generation of the stored credentials.</summary>
    public string Revision { get; } = revision;
    /// <summary>Tokens are accessible only to the secure storage adapter.</summary>
    [JsonIgnore] public CodexOAuthTokens Tokens { get; } = tokens;
    /// <summary>Durable marker for a rotation whose outcome is not yet committed.</summary>
    public Guid? RefreshAttemptId { get; } = refreshAttemptId;
    /// <summary>Returns a redacted diagnostic string.</summary>
    public override string ToString() => "CodexOAuthSessionState [redacted]";
}

/// <summary>Secure host storage. All cooperating processes must serialize access to the same session.</summary>
public interface ICodexOAuthSessionStore
{
    /// <summary>Acquires an exclusive session lease. Login, logout and replacement must use the same coordination.</summary>
    ValueTask<ICodexOAuthSessionLease> AcquireAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Exclusive access to a host-owned OAuth session. Every successful write must be durable and atomic.</summary>
public interface ICodexOAuthSessionLease : IAsyncDisposable
{
    /// <summary>Reads the current session, or null if logged out.</summary>
    ValueTask<CodexOAuthSessionState?> ReadAsync(CancellationToken cancellationToken);
    /// <summary>Records an attempt before any request is sent. A pending attempt cannot be overwritten.</summary>
    ValueTask<bool> TryBeginRefreshAsync(string expectedRevision, Guid attemptId, CancellationToken cancellationToken);
    /// <summary>Atomically replaces credentials, advances the revision and clears the matching pending attempt.</summary>
    ValueTask<bool> TryCompleteRefreshAsync(string expectedRevision, Guid attemptId, CodexOAuthTokens tokens, CancellationToken cancellationToken);
}

/// <summary>Recoverable or terminal OAuth session problems, without secrets or provider response bodies.</summary>
public enum CodexOAuthRefreshFailure
{
    /// <summary>An earlier rotation is not committed; explicit recovery is required.</summary>
    PendingAttempt,
    /// <summary>Rotation or storage failed after the durable attempt marker.</summary>
    UncertainRotation,
    /// <summary>The expected generation or credential owner is no longer current.</summary>
    SessionChanged,
    /// <summary>The stored or returned credentials were invalid.</summary>
    InvalidCredentials,
}

/// <summary>A safe, typed OAuth refresh error.</summary>
public sealed class CodexOAuthRefreshException(CodexOAuthRefreshFailure failure)
    : InvalidOperationException("ChatGPT credential refresh failed: " + failure)
{
    /// <summary>The classified failure.</summary>
    public CodexOAuthRefreshFailure Failure { get; } = failure;
}

/// <summary>Serializes refreshes through the host's secure session store without falling back to API keys.</summary>
public sealed class CodexOAuthCredentialProvider : ICodexCredentialProvider
{
    private readonly ICodexOAuthSessionStore store;
    private readonly string sessionId;
    private readonly CodexDeviceAuthorization authorization;
    private readonly TimeProvider clock;
    private readonly TimeSpan refreshSkew;

    /// <summary>Creates a coordinator for one host-owned session.</summary>
    public CodexOAuthCredentialProvider(ICodexOAuthSessionStore store, string sessionId,
        CodexDeviceAuthorization authorization, TimeProvider? timeProvider = null, TimeSpan? refreshSkew = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(authorization);
        this.store = store;
        this.sessionId = sessionId;
        this.authorization = authorization;
        clock = timeProvider ?? TimeProvider.System;
        this.refreshSkew = refreshSkew ?? TimeSpan.FromMinutes(5);
        if (this.refreshSkew < TimeSpan.Zero || this.refreshSkew > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(refreshSkew));
    }

    /// <summary>Returns current credentials or performs a coordinated proactive refresh.</summary>
    public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        => GetAsync(null, cancellationToken);

    /// <summary>Refreshes after an explicit 401, or uses a newer generation already saved by another request.</summary>
    public ValueTask<CodexCredentials> RefreshAfterUnauthorizedAsync(CodexCredentials rejected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        return GetAsync(rejected, cancellationToken);
    }

    private async ValueTask<CodexCredentials> GetAsync(CodexCredentials? rejected, CancellationToken cancellationToken)
    {
        await using var lease = await store.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var state = await lease.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CodexAuthorizationUnavailableException();
        if (state.RefreshAttemptId is not null)
            throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.PendingAttempt);
        var tokens = state.Tokens;
        if (!Valid(tokens) || string.IsNullOrWhiteSpace(state.Revision))
            throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.InvalidCredentials);
        if (rejected is not null && !SameIdentity(rejected, tokens.Credentials))
            throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);

        var expiresSoon = tokens.ExpiresAt - clock.GetUtcNow() <= refreshSkew;
        if (!expiresSoon && (rejected is null || rejected.AccessToken != tokens.AccessToken))
            return tokens.Credentials;

        cancellationToken.ThrowIfCancellationRequested();
        var attempt = Guid.NewGuid();
        // A canceled or lost storage acknowledgement cannot justify dispatching a refresh.
        if (!await lease.TryBeginRefreshAsync(state.Revision, attempt, cancellationToken).ConfigureAwait(false))
            throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);

        // Once rotation has started, caller cancellation must not discard a new refresh token.
        // The durable pending marker blocks further use after a crash or ambiguous outcome.
        using var rotation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CodexOAuthTokens replacement;
        try
        {
            replacement = await authorization.RefreshAsync(tokens,
                rejected is null ? CodexRefreshReason.ProactiveExpiry : CodexRefreshReason.UnauthorizedRecovery,
                rotation.Token).ConfigureAwait(false);
            if (!Valid(replacement) || replacement.ExpiresAt <= clock.GetUtcNow()
                || !SameIdentity(tokens.Credentials, replacement.Credentials))
                throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.InvalidCredentials);
            if (!await lease.TryCompleteRefreshAsync(state.Revision, attempt, replacement, rotation.Token).ConfigureAwait(false))
                throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);
        }
        catch (CodexOAuthRefreshException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Do not expose OAuth error bodies or host storage exceptions that may contain secrets.
            throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.UncertainRotation);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return replacement.Credentials;
    }

    private static bool Valid(CodexOAuthTokens tokens) => !string.IsNullOrWhiteSpace(tokens.AccessToken)
        && !string.IsNullOrWhiteSpace(tokens.RefreshToken) && !string.IsNullOrWhiteSpace(tokens.AccountId);

    private static bool SameIdentity(CodexCredentials previous, CodexCredentials current)
        => previous.AccountId == current.AccountId && previous.IsFedRampAccount == current.IsFedRampAccount
            && (previous.ChatGptUserId is null || previous.ChatGptUserId == current.ChatGptUserId);

    /// <summary>Returns a redacted diagnostic string.</summary>
    public override string ToString() => "CodexOAuthCredentialProvider [redacted]";
}
