using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexEncryptedOAuthSessionStoreTests
{
    private static CodexOAuthTokens Tokens(string suffix, string account = "account", string user = "user") =>
        new("access-secret-" + suffix, "refresh-secret-" + suffix, account, DateTimeOffset.UtcNow.AddHours(1), user);

    [Fact]
    public async Task RoundTripKeepsOnlyCiphertextInVaultAndRedactsDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        var vault = new Vault();
        var key = RandomNumberGenerator.GetBytes(32);
        using var store = new CodexEncryptedOAuthSessionStore(vault, key);
        var tokens = Tokens("one");
        Assert.True(await store.TrySignInAsync("session", null, tokens, ct));
        Assert.False(await store.TrySignInAsync("session", null, Tokens("two"), ct));
        var record = vault.Records["session"];
        Assert.DoesNotContain(tokens.AccessToken, Encoding.UTF8.GetString(record.Ciphertext.Span));
        Assert.DoesNotContain(tokens.RefreshToken, Encoding.UTF8.GetString(record.Ciphertext.Span));
        Assert.DoesNotContain(tokens.RefreshToken, JsonSerializer.Serialize(record));
        Assert.DoesNotContain(tokens.RefreshToken, record.ToString());
        using var reopened = new CodexEncryptedOAuthSessionStore(vault, key);
        var status = Assert.IsType<CodexOAuthSessionInfo>(await reopened.GetSessionAsync("session", ct));
        Assert.False(status.RefreshPending);
        await using var lease = await reopened.AcquireAsync("session", ct);
        var state = Assert.IsType<CodexOAuthSessionState>(await lease.ReadAsync(ct));
        Assert.Equal(status.Revision, state.Revision);
        Assert.Equal(tokens.AccessToken, state.Tokens.AccessToken);
        Assert.Equal(tokens.RefreshToken, state.Tokens.RefreshToken);
        Assert.Equal(tokens.ChatGptUserId, state.Tokens.ChatGptUserId);
        Assert.DoesNotContain(tokens.RefreshToken, JsonSerializer.Serialize(state));
    }

    [Fact]
    public async Task PendingMarkerSurvivesAdapterRestartAndFencesRotationAndLogout()
    {
        var ct = TestContext.Current.CancellationToken;
        var vault = new Vault();
        var key = RandomNumberGenerator.GetBytes(32);
        using var first = new CodexEncryptedOAuthSessionStore(vault, key);
        using var second = new CodexEncryptedOAuthSessionStore(vault, key);
        Assert.True(await first.TrySignInAsync("session", null, Tokens("one"), ct));
        var original = Assert.IsType<CodexOAuthSessionInfo>(await first.GetSessionAsync("session", ct));
        var attempt = Guid.NewGuid();
        await using (var lease = await first.AcquireAsync("session", ct))
            Assert.True(await lease.TryBeginRefreshAsync(original.Revision, attempt, ct));
        Assert.True((await second.GetSessionAsync("session", ct))!.RefreshPending);
        await using (var lease = await second.AcquireAsync("session", ct))
        {
            Assert.False(await lease.TryBeginRefreshAsync(original.Revision, Guid.NewGuid(), ct));
            Assert.False(await lease.TryCompleteRefreshAsync(original.Revision, Guid.NewGuid(), Tokens("two"), ct));
            Assert.False(await lease.TryCompleteRefreshAsync(original.Revision, attempt, Tokens("two", "other"), ct));
            Assert.True(await lease.TryCompleteRefreshAsync(original.Revision, attempt, Tokens("two"), ct));
        }
        var rotated = Assert.IsType<CodexOAuthSessionInfo>(await first.GetSessionAsync("session", ct));
        Assert.NotEqual(original.Revision, rotated.Revision);
        Assert.False(rotated.RefreshPending);
        Assert.False(await first.TrySignOutAsync("session", original.Revision, ct));
        Assert.True(await first.TrySignOutAsync("session", rotated.Revision, ct));
        Assert.Null(await second.GetSessionAsync("session", ct));
    }

    [Fact]
    public async Task CiphertextCannotBeMovedBetweenSessionsOrVersionsOrModified()
    {
        var ct = TestContext.Current.CancellationToken;
        var vault = new Vault();
        using var store = new CodexEncryptedOAuthSessionStore(vault, RandomNumberGenerator.GetBytes(32));
        Assert.True(await store.TrySignInAsync("session-a", null, Tokens("one"), ct));
        var record = vault.Records["session-a"];
        vault.Records["session-b"] = record;
        var moved = await Assert.ThrowsAsync<CodexOAuthRefreshException>(async () => await store.GetSessionAsync("session-b", ct));
        Assert.Equal(CodexOAuthRefreshFailure.InvalidCredentials, moved.Failure);
        vault.Records["session-a"] = record with { Version = Guid.NewGuid().ToString("N") };
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(async () => await store.GetSessionAsync("session-a", ct));
        var altered = record.Ciphertext.ToArray();
        altered[^1] ^= 1;
        vault.Records["session-a"] = record with { Ciphertext = altered };
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(async () => await store.GetSessionAsync("session-a", ct));
        Assert.False(await store.TrySignInAsync("session-c", "not-current", Tokens("two"), ct));
    }

    [Fact]
    public async Task ConcurrentSignInOnlyCommitsOneGenerationAndWrongIdentityCannotReplaceIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var vault = new Vault();
        var key = RandomNumberGenerator.GetBytes(32);
        using var first = new CodexEncryptedOAuthSessionStore(vault, key);
        using var second = new CodexEncryptedOAuthSessionStore(vault, key);
        var results = await Task.WhenAll(first.TrySignInAsync("session", null, Tokens("one"), ct).AsTask(),
            second.TrySignInAsync("session", null, Tokens("two"), ct).AsTask());
        Assert.Single(results, result => result);
        var info = Assert.IsType<CodexOAuthSessionInfo>(await first.GetSessionAsync("session", ct));
        var error = await Assert.ThrowsAsync<CodexOAuthRefreshException>(async () =>
            await second.TrySignInAsync("session", info.Revision, Tokens("other", "other-account"), ct));
        Assert.Equal(CodexOAuthRefreshFailure.SessionChanged, error.Failure);
        Assert.Equal(info.Revision, (await first.GetSessionAsync("session", ct))!.Revision);
    }

    [Fact]
    public async Task PendingSessionCanBeExplicitlyReauthorizedButNotSilentlyOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var vault = new Vault();
        using var store = new CodexEncryptedOAuthSessionStore(vault, RandomNumberGenerator.GetBytes(32));
        Assert.True(await store.TrySignInAsync("session", null, Tokens("one"), ct));
        var info = (await store.GetSessionAsync("session", ct))!;
        await using (var lease = await store.AcquireAsync("session", ct))
            Assert.True(await lease.TryBeginRefreshAsync(info.Revision, Guid.NewGuid(), ct));
        Assert.False(await store.TrySignInAsync("session", null, Tokens("replacement"), ct));
        Assert.True(await store.TrySignInAsync("session", info.Revision, Tokens("replacement"), ct));
        var current = (await store.GetSessionAsync("session", ct))!;
        Assert.NotEqual(info.Revision, current.Revision);
        Assert.False(current.RefreshPending);
    }

    private sealed class Vault : ICodexOAuthVault
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, CodexOAuthVaultRecord> Records { get; } = new(StringComparer.Ordinal);
        public async ValueTask<ICodexOAuthVaultLease> AcquireAsync(string sessionId, CancellationToken cancellationToken)
        {
            var gate = locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new Lease(this, sessionId, gate);
        }

        private sealed class Lease(Vault vault, string id, SemaphoreSlim gate) : ICodexOAuthVaultLease
        {
            public ValueTask<CodexOAuthVaultRecord?> ReadAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(vault.Records.TryGetValue(id, out var current) ? current : null);
            }

            public ValueTask<bool> CompareExchangeAsync(string? expectedVersion, CodexOAuthVaultRecord? replacement, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                vault.Records.TryGetValue(id, out var current);
                if (current?.Version != expectedVersion) return ValueTask.FromResult(false);
                if (replacement is null) vault.Records.TryRemove(id, out _);
                else vault.Records[id] = replacement;
                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }
}
