using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class CodexFileOAuthVaultTests
{
    [Fact]
    public async Task EncryptedSessionAndPendingRotationSurviveReopeningVault()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new PrivateDirectory();
        var key = RandomNumberGenerator.GetBytes(32);
        var tokens = Tokens("first");
        using var first = new CodexEncryptedOAuthSessionStore(new CodexFileOAuthVault(fixture.Path), key);
        Assert.True(await first.TrySignInAsync("session/private", null, tokens, ct));
        var recordPath = Assert.Single(Directory.GetFiles(fixture.Path, "*.session"));
        var persisted = await File.ReadAllBytesAsync(recordPath, ct);
        Assert.DoesNotContain(tokens.AccessToken, Encoding.UTF8.GetString(persisted));
        Assert.DoesNotContain(tokens.RefreshToken, Encoding.UTF8.GetString(persisted));
        Assert.DoesNotContain(tokens.AccountId, Encoding.UTF8.GetString(persisted));
        Assert.DoesNotContain("session/private", recordPath);

        using var reopened = new CodexEncryptedOAuthSessionStore(new CodexFileOAuthVault(fixture.Path), key);
        var initial = Assert.IsType<CodexOAuthSessionInfo>(await reopened.GetSessionAsync("session/private", ct));
        var attempt = Guid.NewGuid();
        await using (var lease = await reopened.AcquireAsync("session/private", ct))
            Assert.True(await lease.TryBeginRefreshAsync(initial.Revision, attempt, ct));

        using var recovered = new CodexEncryptedOAuthSessionStore(new CodexFileOAuthVault(fixture.Path), key);
        Assert.True((await recovered.GetSessionAsync("session/private", ct))!.RefreshPending);
        await using (var lease = await recovered.AcquireAsync("session/private", ct))
        {
            Assert.False(await lease.TryBeginRefreshAsync(initial.Revision, Guid.NewGuid(), ct));
            Assert.False(await lease.TryCompleteRefreshAsync(initial.Revision, Guid.NewGuid(), Tokens("second"), ct));
            Assert.True(await lease.TryCompleteRefreshAsync(initial.Revision, attempt, Tokens("second"), ct));
        }
        var current = Assert.IsType<CodexOAuthSessionInfo>(await first.GetSessionAsync("session/private", ct));
        Assert.NotEqual(initial.Revision, current.Revision);
        Assert.False(current.RefreshPending);
        Assert.False(await first.TrySignOutAsync("session/private", initial.Revision, ct));
        Assert.True(await first.TrySignOutAsync("session/private", current.Revision, ct));
        Assert.Null(await recovered.GetSessionAsync("session/private", ct));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.session"));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
        // Never remove the lock inode on sign-out.
        Assert.Single(Directory.GetFiles(fixture.Path, "*.lock"));
    }

    [Fact]
    public async Task IndependentInstancesSerializeLeasesAndHonorCancellation()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new PrivateDirectory();
        var first = new CodexFileOAuthVault(fixture.Path);
        var second = new CodexFileOAuthVault(fixture.Path, TimeSpan.FromSeconds(2));
        var version = Guid.NewGuid().ToString("N");
        var lease = await first.AcquireAsync("session", ct);
        try
        {
            Assert.True(await lease.CompareExchangeAsync(null, new(version, new byte[] { 1, 2, 3 }), ct));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var waiting = second.AcquireAsync("session", cancellation.Token).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            // A different session is not blocked by this lease.
            await using var other = await second.AcquireAsync("other", ct);
            Assert.Null(await other.ReadAsync(ct));
        }
        finally { await lease.DisposeAsync(); }
        await using var recovered = await second.AcquireAsync("session", ct);
        var current = Assert.IsType<CodexOAuthVaultRecord>(await recovered.ReadAsync(ct));
        Assert.Equal(version, current.Version);
        Assert.Equal(new byte[] { 1, 2, 3 }, current.Ciphertext.ToArray());
        Assert.False(await recovered.CompareExchangeAsync(null, new(Guid.NewGuid().ToString("N"), new byte[] { 4 }), ct));
    }

    [Fact]
    public async Task ConcurrentCompareExchangeOnOneLeaseOnlyCommitsOneGeneration()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new PrivateDirectory();
        var vault = new CodexFileOAuthVault(fixture.Path);
        await using var lease = await vault.AcquireAsync("session", ct);
        var a = new CodexOAuthVaultRecord(Guid.NewGuid().ToString("N"), new byte[] { 1 });
        var b = new CodexOAuthVaultRecord(Guid.NewGuid().ToString("N"), new byte[] { 2 });
        var results = await Task.WhenAll(lease.CompareExchangeAsync(null, a, ct).AsTask(),
            lease.CompareExchangeAsync(null, b, ct).AsTask());
        Assert.Single(results, value => value);
        var current = Assert.IsType<CodexOAuthVaultRecord>(await lease.ReadAsync(ct));
        Assert.Equal(results[0] ? a.Version : b.Version, current.Version);
        Assert.False(await lease.CompareExchangeAsync(results[0] ? b.Version : a.Version, null, ct));
        Assert.True(await lease.CompareExchangeAsync(current.Version, null, ct));
        Assert.Null(await lease.ReadAsync(ct));
    }

    [Fact]
    public async Task CorruptionIsNotTreatedAsAbsentAndCannotBeOverwritten()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new PrivateDirectory();
        var key = RandomNumberGenerator.GetBytes(32);
        using var store = new CodexEncryptedOAuthSessionStore(new CodexFileOAuthVault(fixture.Path), key);
        Assert.True(await store.TrySignInAsync("session", null, Tokens("first"), ct));
        var path = Assert.Single(Directory.GetFiles(fixture.Path, "*.session"));
        var original = await File.ReadAllBytesAsync(path, ct);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, ct);
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(async () => await store.GetSessionAsync("session", ct));
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(async () =>
            await store.TrySignInAsync("session", null, Tokens("replacement"), ct));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, ct));

        original[^1] ^= 1;
        await File.WriteAllBytesAsync(path, original, ct);
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(async () => await store.GetSessionAsync("session", ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task VaultRejectsNonPrivateFilesAndSymlinkRecords()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new PrivateDirectory();
        using var outside = new PrivateDirectory();
        var vault = new CodexFileOAuthVault(fixture.Path);
        await using (var lease = await vault.AcquireAsync("session", ct))
            Assert.True(await lease.CompareExchangeAsync(null, new(Guid.NewGuid().ToString("N"), new byte[] { 1 }), ct));
        var record = Assert.Single(Directory.GetFiles(fixture.Path, "*.session"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Assert.ThrowsAsync<IOException>(() => vault.AcquireAsync("session", ct).AsTask());
        File.Delete(record);
        var target = System.IO.Path.Combine(outside.Path, "target");
        await File.WriteAllTextAsync(target, "unchanged", ct);
        File.CreateSymbolicLink(record, target);
        await Assert.ThrowsAsync<IOException>(() => vault.AcquireAsync("session", ct).AsTask());
        Assert.Equal("unchanged", await File.ReadAllTextAsync(target, ct));
    }

    [Fact]
    public async Task CancellationBeforeCommitPreservesOriginalRecord()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new PrivateDirectory();
        var vault = new CodexFileOAuthVault(fixture.Path);
        await using var lease = await vault.AcquireAsync("session", ct);
        var version = Guid.NewGuid().ToString("N");
        Assert.True(await lease.CompareExchangeAsync(null, new(version, new byte[] { 1 }), ct));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.CompareExchangeAsync(version,
            new(Guid.NewGuid().ToString("N"), new byte[] { 2 }), canceled.Token).AsTask());
        Assert.Equal(version, (await lease.ReadAsync(ct))!.Version);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    private static void RequireUnix() => Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(),
        "The durable file vault requires macOS or Linux.");

    private static CodexOAuthTokens Tokens(string suffix) =>
        new("access-secret-" + suffix, "refresh-secret-" + suffix, "account-private", DateTimeOffset.UtcNow.AddHours(1), "user-private");

    private sealed class PrivateDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("codex-oauth-vault-");
        internal string Path => directory.FullName;
        public void Dispose() => directory.Delete(recursive: true);
    }
}
