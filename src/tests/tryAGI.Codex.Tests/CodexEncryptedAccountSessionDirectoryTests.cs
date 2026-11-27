using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexEncryptedAccountSessionDirectoryTests
{
    [Fact]
    public async Task RegisteredAccountsAndSelectionSurviveReopeningWithoutExposingMetadata()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var first = fixture.Open();
        var empty = await first.ReadAsync(false, ct);
        Assert.Null(empty.ActiveSessionId);
        Assert.Empty(empty.Sessions);
        var a = Entry("session-a", "user-a", "workspace-a");
        var b = Entry("session-b", "user-b", "workspace-b");
        await fixture.SaveCredential(a, ct);
        await fixture.SaveCredential(b, ct);
        Assert.True(await first.TryRegisterSessionAsync(empty.Revision, a, ct));
        var one = await first.ReadAsync(false, ct);
        Assert.False(await first.TryRegisterSessionAsync(empty.Revision, b, ct));
        Assert.True(await first.TryRegisterSessionAsync(one.Revision, b, ct));
        var registered = await first.ReadAsync(false, ct);
        Assert.NotEqual(one.SelectionEpoch, registered.SelectionEpoch);
        Assert.Null(registered.ActiveSessionId);
        Assert.False(await first.TryRegisterSessionAsync(registered.Revision, b, ct));

        var rpc = Rpc(first);
        var selected = await rpc.InvokeAsync("accountSession/switch", Json("""{"sessionId":"session-a","accountId":"workspace-a"}"""), ct);
        Assert.Equal("session-a", selected.GetProperty("activeSessionId").GetString());
        var previous = await first.ReadAsync(false, ct);
        using var reopened = fixture.Open();
        var persisted = await reopened.ReadAsync(false, ct);
        Assert.Equal(previous.Revision, persisted.Revision);
        Assert.Equal(previous.SelectionEpoch, persisted.SelectionEpoch);
        Assert.Equal("session-a", persisted.ActiveSessionId);
        var switched = await Rpc(reopened).InvokeAsync("accountSession/switch", Json("""{"sessionId":"session-b","accountId":"workspace-b"}"""), ct);
        Assert.Equal("session-b", switched.GetProperty("activeSessionId").GetString());
        var next = await first.ReadAsync(false, ct);
        Assert.NotEqual(previous.SelectionEpoch, next.SelectionEpoch);
        Assert.Equal("session-b", next.ActiveSessionId);
        foreach (var path in Directory.GetFiles(fixture.Path, "*.session"))
        {
            var ciphertext = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, ct));
            Assert.DoesNotContain(a.Email!, ciphertext);
            Assert.DoesNotContain(b.Email!, ciphertext);
            Assert.DoesNotContain("access-secret", ciphertext);
        }
        using var anotherOwner = fixture.Open(owner: "other-owner");
        Assert.Empty((await anotherOwner.ReadAsync(false, ct)).Sessions);
    }

    [Fact]
    public async Task PendingRotationAndChangedCredentialGenerationFenceSelection()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var entry = Entry("session-a", "user-a", "workspace-a");
        await fixture.SaveCredential(entry, ct);
        Assert.True(await directory.TryRegisterSessionAsync((await directory.ReadAsync(false, ct)).Revision, entry, ct));
        var before = await directory.ReadAsync(false, ct);
        var old = Assert.IsType<CodexAccountSessionCredentialFence>(await directory.ReadCredentialFenceAsync(entry.SessionId, "workspace-a", ct));
        var credentialId = Fixture.CredentialId(entry.SessionId, "workspace-a");
        var info = (await fixture.Credentials.GetSessionAsync(credentialId, ct))!;
        var attempt = Guid.NewGuid();
        await using (var lease = await fixture.Credentials.AcquireAsync(credentialId, ct))
            Assert.True(await lease.TryBeginRefreshAsync(info.Revision, attempt, ct));
        var pending = Assert.IsType<CodexAccountSessionCredentialFence>(await directory.ReadCredentialFenceAsync(entry.SessionId, "workspace-a", ct));
        Assert.True(pending.RefreshPending);
        Assert.NotEqual(old.VaultVersion, pending.VaultVersion);
        var replacement = Select(before, old);
        Assert.False(await directory.TryCommitSelectionAsync(before.Revision, old, replacement, ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Rpc(directory).InvokeAsync("accountSession/switch",
            Json("""{"sessionId":"session-a","accountId":"workspace-a"}"""), ct));
        await using (var lease = await fixture.Credentials.AcquireAsync(credentialId, ct))
            Assert.True(await lease.TryCompleteRefreshAsync(info.Revision, attempt, Fixture.Tokens(entry), ct));
        Assert.False(await directory.TryCommitSelectionAsync(before.Revision, old, replacement, ct));
        var result = await Rpc(directory).InvokeAsync("accountSession/switch", Json("""{"sessionId":"session-a","accountId":"workspace-a"}"""), ct);
        Assert.Equal(entry.SessionId, result.GetProperty("activeSessionId").GetString());
    }

    [Fact]
    public async Task CommitRechecksPolicyAndCannotModifyUnrelatedDirectoryMetadata()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var entry = Entry("session-a", "user-a", "workspace-a");
        await fixture.SaveCredential(entry, ct);
        Assert.True(await directory.TryRegisterSessionAsync((await directory.ReadAsync(false, ct)).Revision, entry, ct));
        var before = await directory.ReadAsync(false, ct);
        var fence = Assert.IsType<CodexAccountSessionCredentialFence>(await directory.ReadCredentialFenceAsync(entry.SessionId, "workspace-a", ct));
        var replacement = Select(before, fence);
        var malicious = replacement with { Sessions = [replacement.Sessions[0] with { DisplayName = "modified" }] };
        Assert.False(await directory.TryCommitSelectionAsync(before.Revision, fence, malicious, ct));
        fixture.Deny = true;
        Assert.False(await directory.TryCommitSelectionAsync(before.Revision, fence, replacement, ct));
        Assert.Equal(before.Revision, (await directory.ReadAsync(false, ct)).Revision);
        fixture.Deny = false;
        Assert.True(await directory.TryCommitSelectionAsync(before.Revision, fence, replacement, ct));
        Assert.False(await directory.TryCommitSelectionAsync(before.Revision, fence, Select(before, fence), ct));
        Assert.Equal(entry.DisplayName, (await directory.ReadAsync(false, ct)).Sessions[0].DisplayName);
        Assert.Equal(0, fixture.ActivePolicyLeases);
    }

    [Fact]
    public async Task CredentialIdentityAndOwnerKeyAreMandatoryAndMetadataRefreshIsExplicitlyUnsupported()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var entry = Entry("session-a", "user-a", "workspace-a");
        var empty = await directory.ReadAsync(false, ct);
        Assert.False(await directory.TryRegisterSessionAsync(empty.Revision, entry, ct));
        await fixture.SaveCredential(entry with { UserId = "other-user" }, ct);
        Assert.False(await directory.TryRegisterSessionAsync(empty.Revision, entry, ct));
        Assert.Null(await directory.ReadCredentialFenceAsync(entry.SessionId, "workspace-a", ct));
        var actual = entry with { UserId = "other-user" };
        Assert.True(await directory.TryRegisterSessionAsync(empty.Revision, actual, ct));
        using var wrongKey = fixture.Open(key: RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<CodexProtocolException>(() => wrongKey.ReadAsync(false, ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => directory.ReadAsync(true, ct));
        Assert.Null((await directory.ReadAsync(false, ct)).ActiveSessionId);
    }

    private static CodexAccountSessionDirectorySnapshot Select(CodexAccountSessionDirectorySnapshot before,
        CodexAccountSessionCredentialFence fence) => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), fence.SessionId,
        before.Sessions.Select(entry => entry.SessionId == fence.SessionId ? entry with
        { SelectedWorkspaceAccountId = fence.AccountId, LastUsedAt = Math.Max(entry.LastUsedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) } : entry).ToArray());

    private static CodexAccountSessionEntry Entry(string session, string user, string account) =>
        new(session, user, user + "@example.test", "Display " + user, null, 1, account, [new(account, "Workspace", null, "workspace")]);

    private static CodexAccountSessionsRpc Rpc(CodexEncryptedAccountSessionDirectory storage) =>
        new(storage, (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private static void RequireUnix() => Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "The file vault requires macOS or Linux.");

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("codex-directory-");
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        internal string Path => directory.FullName;
        internal CodexEncryptedOAuthSessionStore Credentials { get; }
        internal bool Deny { get; set; }
        internal int ActivePolicyLeases { get; private set; }

        internal Fixture() => Credentials = new(new CodexFileOAuthVault(Path), key);

        internal CodexEncryptedAccountSessionDirectory Open(string owner = "owner", byte[]? key = null) =>
            new(new CodexFileOAuthVault(Path), Credentials, owner, key ?? this.key, CredentialId, (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (Deny) return ValueTask.FromResult<IAsyncDisposable?>(null);
                ActivePolicyLeases++;
                return ValueTask.FromResult<IAsyncDisposable?>(new Policy(this));
            });

        internal async Task SaveCredential(CodexAccountSessionEntry entry, CancellationToken ct) =>
            Assert.True(await Credentials.TrySignInAsync(CredentialId(entry.SessionId, entry.SelectedWorkspaceAccountId!), null, Tokens(entry), ct));

        internal static string CredentialId(string session, string account) => "credential/" + session + "/" + account;

        internal static CodexOAuthTokens Tokens(CodexAccountSessionEntry entry) =>
            new("access-secret", "refresh-secret", entry.SelectedWorkspaceAccountId!, DateTimeOffset.UtcNow.AddHours(1), entry.UserId);

        public void Dispose()
        {
            Credentials.Dispose();
            CryptographicOperations.ZeroMemory(key);
            directory.Delete(recursive: true);
        }

        private sealed class Policy(Fixture fixture) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { fixture.ActivePolicyLeases--; return ValueTask.CompletedTask; }
        }
    }
}
