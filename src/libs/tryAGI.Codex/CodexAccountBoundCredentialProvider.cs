namespace tryAGI.Codex;

/// <summary>One immutable funding-account binding for a client/ledger lifetime.
/// Token rotation is allowed; account or known-user replacement requires a fresh binding.</summary>
/// <param name="inner">Provider whose credentials are checked.</param>
public sealed class CodexAccountBoundCredentialProvider(ICodexCredentialProvider inner) : ICodexCredentialProvider
{
    private readonly object gate = new();
    private string? account;
    private string? user;
    private bool restricted;

    /// <inheritdoc />
    /// <exception cref="CodexProtocolException">The credentials now belong to a different account or user.</exception>
    public async ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var active = await inner.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (account is null)
            { account = active.AccountId; user = active.ChatGptUserId; restricted = active.IsFedRampAccount; }
            else
            {
                if (!string.Equals(account, active.AccountId, StringComparison.Ordinal) ||
                    restricted != active.IsFedRampAccount ||
                    user is not null && !string.Equals(user, active.ChatGptUserId, StringComparison.Ordinal))
                    throw new CodexProtocolException("Credentials changed funding identity; create a new client and ledger before dispatch.");
                // Legacy identity can become known on normal OAuth refresh. Never invent it.
                user ??= active.ChatGptUserId;
            }
        }
        return active;
    }
    /// <inheritdoc />
    public override string ToString() => "CodexAccountBoundCredentialProvider [redacted]";
}
