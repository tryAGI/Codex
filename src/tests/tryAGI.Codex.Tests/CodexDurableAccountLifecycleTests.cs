using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexDurableAccountLifecycleTests
{
    [Fact]
    public async Task LoginSwitchBackAndLogoutFenceOldCredentialProviders()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var authority = fixture.Authority(directory);
        var a = await Login(authority, "a", ct);
        using var authorization = new CodexDeviceAuthorization(handler: new NoNetwork());
        var first = await directory.CreateSelectedCredentialProviderAsync(authorization, ct);
        Assert.Equal("account-a", (await first.GetCredentialsAsync(ct)).AccountId);
        var b = await Login(authority, "b", ct);
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => first.GetCredentialsAsync(ct).AsTask());
        using var reopened = fixture.Open();
        var snapshot = await reopened.ReadAsync(false, ct);
        Assert.Equal(b.SessionId, snapshot.ActiveSessionId);
        Assert.Equal(2, snapshot.Sessions.Count);
        var second = await reopened.CreateSelectedCredentialProviderAsync(authorization, ct);
        Assert.Equal("account-b", (await second.GetCredentialsAsync(ct)).AccountId);

        var rpc = new CodexAccountSessionsRpc(reopened, (_, _, token) =>
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        using var request = JsonDocument.Parse($$"""{"sessionId":"{{a.SessionId}}","accountId":"account-a"}""");
        await rpc.InvokeAsync("accountSession/switch", request.RootElement, ct);
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => first.GetCredentialsAsync(ct).AsTask());
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => second.GetCredentialsAsync(ct).AsTask());
        var current = await reopened.CreateSelectedCredentialProviderAsync(authorization, ct);
        Assert.Equal("account-a", (await current.GetCredentialsAsync(ct)).AccountId);
        var reopenedAuthority = fixture.Authority(reopened);
        var target = await reopenedAuthority.AuthorizeAsync(CodexAccountLifecycleOperation.Logout, ct);
        Assert.True(await reopenedAuthority.TryLogoutAsync(target, ct));
        var loggedOut = await directory.ReadAsync(false, ct);
        Assert.Null(loggedOut.ActiveSessionId);
        Assert.Equal(b.SessionId, Assert.Single(loggedOut.Sessions).SessionId);
        Assert.Null(await fixture.Credentials.GetSessionAsync(Fixture.CredentialId(a.SessionId, "account-a"), ct));
        Assert.NotNull(await fixture.Credentials.GetSessionAsync(Fixture.CredentialId(b.SessionId, "account-b"), ct));
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => current.GetCredentialsAsync(ct).AsTask());
        Assert.Equal(0, fixture.ActiveGrants);
    }

    [Fact]
    public async Task InterruptedLoginRollsBackOnlyStagedGenerationAndRejectsReplay()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var authority = fixture.Authority(directory);
        var old = await Login(authority, "old", ct);
        var before = await directory.ReadAsync(false, ct);
        var next = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct);
        fixture.Vault.FailOnce = (id, replacement) => IsDirectory(id) && IsNormal(replacement) ? Fault.Before : Fault.None;
        var loginId = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => authority.TryCommitLoginAsync(next, loginId, Tokens("new"), ct));
        var stagedId = Fixture.CredentialId(next.SessionId, "account-new");
        Assert.NotNull(await fixture.Credentials.GetSessionAsync(stagedId, ct));
        using var reopened = fixture.Open();
        var recovered = await reopened.ReadAsync(false, ct);
        Assert.Equal(old.SessionId, recovered.ActiveSessionId);
        Assert.Equal(old.SessionId, Assert.Single(recovered.Sessions).SessionId);
        Assert.NotEqual(before.SelectionEpoch, recovered.SelectionEpoch);
        Assert.Null(await fixture.Credentials.GetSessionAsync(stagedId, ct));
        Assert.False(await fixture.Authority(reopened).TryCommitLoginAsync(next, loginId, Tokens("new"), ct));
        Assert.NotNull(await fixture.Credentials.GetSessionAsync(Fixture.CredentialId(old.SessionId, "account-old"), ct));
    }

    [Fact]
    public async Task LostFinalLoginAcknowledgementDoesNotDeleteCommittedCredentials()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var authority = fixture.Authority(directory);
        var target = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct);
        fixture.Vault.FailOnce = (id, replacement) => IsDirectory(id) && IsNormal(replacement) ? Fault.After : Fault.None;
        var loginId = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => authority.TryCommitLoginAsync(target, loginId, Tokens("a"), ct));
        using var reopened = fixture.Open();
        var snapshot = await reopened.ReadAsync(false, ct);
        Assert.Equal(target.SessionId, snapshot.ActiveSessionId);
        Assert.NotNull(await fixture.Credentials.GetSessionAsync(Fixture.CredentialId(target.SessionId, "account-a"), ct));
        Assert.False(await fixture.Authority(reopened).TryCommitLoginAsync(target, loginId, Tokens("a"), ct));
        Assert.Single(snapshot.Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutCleanupRecoversBeforeOrAfterLostDeletionAcknowledgement(bool after)
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var authority = fixture.Authority(directory);
        var other = await Login(authority, "other", ct);
        var active = await Login(authority, "active", ct);
        var id = Fixture.CredentialId(active.SessionId, "account-active");
        var target = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.Logout, ct);
        fixture.Vault.FailOnce = (key, replacement) => key == id && replacement is null
            ? after ? Fault.After : Fault.Before : Fault.None;
        await Assert.ThrowsAsync<IOException>(() => authority.TryLogoutAsync(target, ct));
        using var reopened = fixture.Open();
        var snapshot = await reopened.ReadAsync(false, ct);
        Assert.Null(snapshot.ActiveSessionId);
        Assert.Equal(other.SessionId, Assert.Single(snapshot.Sessions).SessionId);
        Assert.Null(await fixture.Credentials.GetSessionAsync(id, ct));
        Assert.NotNull(await fixture.Credentials.GetSessionAsync(Fixture.CredentialId(other.SessionId, "account-other"), ct));
        Assert.False(await fixture.Authority(reopened).TryLogoutAsync(target, ct));
    }

    [Fact]
    public async Task RecoveryNeverDeletesAnUnexpectedNewGeneration()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var authority = fixture.Authority(directory);
        var target = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct);
        fixture.Vault.FailOnce = (id, replacement) => IsDirectory(id) && IsNormal(replacement) ? Fault.Before : Fault.None;
        await Assert.ThrowsAsync<IOException>(() => authority.TryCommitLoginAsync(target, Guid.NewGuid(), Tokens("a"), ct));
        var id = Fixture.CredentialId(target.SessionId, "account-a");
        var staged = (await fixture.Credentials.GetSessionAsync(id, ct))!;
        // Simulate a non-cooperating legacy credential writer. Recovery must fail closed.
        Assert.True(await fixture.Credentials.TrySignInAsync(id, staged.Revision, Tokens("a"), ct));
        var unexpected = (await fixture.Credentials.GetSessionAsync(id, ct))!;
        using var reopened = fixture.Open();
        await Assert.ThrowsAsync<CodexProtocolException>(() => reopened.ReadAsync(false, ct));
        Assert.Equal(unexpected.Revision, (await fixture.Credentials.GetSessionAsync(id, ct))!.Revision);
    }

    [Fact]
    public async Task PolicyAndSelectionFencesRejectStaleAdmissionsBeforeWriting()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        var authority = fixture.Authority(directory);
        var stale = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct);
        var empty = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.Logout, ct);
        Assert.True(await authority.TryLogoutAsync(empty, ct));
        Assert.False(await authority.TryCommitLoginAsync(stale, Guid.NewGuid(), Tokens("stale"), ct));
        var denied = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct);
        fixture.Deny = true;
        Assert.False(await authority.TryCommitLoginAsync(denied, Guid.NewGuid(), Tokens("denied"), ct));
        Assert.Empty((await directory.ReadAsync(false, ct)).Sessions);
        fixture.Deny = false;
        var active = await Login(authority, "active", ct);
        var logout = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.Logout, ct);
        var id = Fixture.CredentialId(active.SessionId, "account-active");
        var info = (await fixture.Credentials.GetSessionAsync(id, ct))!;
        Assert.True(await fixture.Credentials.TrySignInAsync(id, info.Revision, Tokens("active"), ct));
        Assert.False(await authority.TryLogoutAsync(logout, ct));
        Assert.Equal(active.SessionId, (await directory.ReadAsync(false, ct)).ActiveSessionId);
        fixture.Deny = true;
        var fresh = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.Logout, ct);
        Assert.False(await authority.TryLogoutAsync(fresh, ct));
        Assert.NotNull(await fixture.Credentials.GetSessionAsync(id, ct));
    }

    [Fact]
    public async Task NativeDeviceLoginAndLogoutUseTheDurableLifecycleThroughDispatcher()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        using var directory = fixture.Open();
        using var handler = new AuthHandler();
        using var authorization = new CodexDeviceAuthorization(handler: handler);
        var events = new List<JsonElement>();
        var login = new CodexDeviceLoginRpc(authorization, fixture.Authority(directory), async (method, payload) =>
        {
            Assert.Equal("account/login/completed", method);
            // The success notification must follow the durable publication, not merely OAuth exchange.
            using var observer = fixture.Open();
            Assert.NotNull((await observer.ReadAsync(false, ct)).ActiveSessionId);
            events.Add(payload.Clone());
        });
        var sessions = new CodexAccountSessionsRpc(directory, (_, _, token) =>
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var dispatcher = new CodexNativeRpcDispatcher(login: login, sessions: sessions);
        using var start = JsonDocument.Parse("""{"id":"login","method":"account/login/start","params":{"type":"chatgptDeviceCode"}}""");
        var response = await dispatcher.DispatchAsync(start.RootElement, ct);
        var id = Guid.Parse(response.GetProperty("result").GetProperty("loginId").GetString()!);
        Assert.Empty((await directory.ReadAsync(false, ct)).Sessions);
        Assert.True(await dispatcher.RunPendingLoginAsync(id, ct));
        Assert.Equal(3, handler.Calls);
        Assert.True(Assert.Single(events).GetProperty("success").GetBoolean());
        var selected = await directory.CreateSelectedCredentialProviderAsync(authorization, ct);
        Assert.Equal("test-account", (await selected.GetCredentialsAsync(ct)).AccountId);

        using var logout = JsonDocument.Parse("""{"id":"logout","method":"account/logout"}""");
        var result = await dispatcher.DispatchAsync(logout.RootElement, ct);
        Assert.True(result.TryGetProperty("result", out _));
        using var reopened = fixture.Open();
        Assert.Empty((await reopened.ReadAsync(false, ct)).Sessions);
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => selected.GetCredentialsAsync(ct).AsTask());
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task PendingJournalFormatCannotBeDowngradedAndLegacyDirectoryRemainsReadable()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var legacy = await fixture.WriteLegacyDirectoryAsync(ct);
        using var directory = fixture.Open();
        Assert.Equal(legacy, (await directory.ReadAsync(false, ct)).Revision);
        var authority = fixture.Authority(directory);
        var target = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct);
        fixture.Vault.FailOnce = (id, replacement) => IsDirectory(id) && IsNormal(replacement) ? Fault.Before : Fault.None;
        await Assert.ThrowsAsync<IOException>(() => authority.TryCommitLoginAsync(target, Guid.NewGuid(), Tokens("a"), ct));
        var path = fixture.DirectoryRecordPath;
        var original = await File.ReadAllBytesAsync(path, ct);
        Assert.Equal(2, original[40]);
        var changed = original.ToArray();
        changed[40] = 1;
        await File.WriteAllBytesAsync(path, changed, ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => directory.ReadAsync(false, ct));
        Assert.NotNull(await fixture.Credentials.GetSessionAsync(Fixture.CredentialId(target.SessionId, "account-a"), ct));
        await File.WriteAllBytesAsync(path, original, ct);
        Assert.Empty((await directory.ReadAsync(false, ct)).Sessions);
        Assert.Null(await fixture.Credentials.GetSessionAsync(Fixture.CredentialId(target.SessionId, "account-a"), ct));
    }

    private sealed class AuthHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal("auth.openai.com", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"https://api.openai.com/auth":{"chatgpt_account_id":"test-account","chatgpt_user_id":"test-user"}}"""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var body = path.EndsWith("/usercode", StringComparison.Ordinal) ? """{"device_auth_id":"private","user_code":"TEST-CODE","interval":"1"}""" :
                path.EndsWith("/deviceauth/token", StringComparison.Ordinal) ? """{"authorization_code":"test-code","code_verifier":"test-verifier"}""" :
                path == "/oauth/token" ? $$"""{"access_token":"header.{{claims}}.signature","refresh_token":"test-refresh","expires_in":3600}""" :
                throw new InvalidOperationException("Unexpected provider request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static bool IsDirectory(string id) => id.StartsWith("account-directory-v1-", StringComparison.Ordinal);
    private static bool IsNormal(CodexOAuthVaultRecord? replacement) => replacement is not null && replacement.Ciphertext.Span[0] == 1;
    private static CodexOAuthTokens Tokens(string suffix) => new("access-secret-" + suffix, "refresh-secret-" + suffix,
        "account-" + suffix, DateTimeOffset.UtcNow.AddHours(1), "user-" + suffix);
    private static async Task<CodexAccountLifecycleTarget> Login(CodexDurableAccountLifecycleAuthority authority, string suffix, CancellationToken ct)
    {
        var target = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct);
        Assert.True(await authority.TryCommitLoginAsync(target, Guid.NewGuid(), Tokens(suffix), ct));
        return target;
    }
    private static void RequireUnix() => Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "File vault requires macOS or Linux.");

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected provider request.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo path = Directory.CreateTempSubdirectory("codex-account-lifecycle-");
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        internal FaultVault Vault { get; }
        internal CodexEncryptedOAuthSessionStore Credentials { get; }
        internal bool Deny { get; set; }
        internal int ActiveGrants { get; private set; }
        private static string DirectoryId => "account-directory-v1-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("owner")));
        internal string DirectoryRecordPath => Path.Combine(path.FullName, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(DirectoryId))) + ".session");

        internal async Task<string> WriteLegacyDirectoryAsync(CancellationToken ct)
        {
            var revision = Guid.NewGuid().ToString("N");
            var plain = Encoding.UTF8.GetBytes($$"""{"Revision":"{{revision}}","SelectionEpoch":"legacy-epoch","ActiveSessionId":null,"Sessions":[]}""");
            var cipher = new byte[29 + plain.Length];
            cipher[0] = 1;
            RandomNumberGenerator.Fill(cipher.AsSpan(1, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(cipher.AsSpan(1, 12), plain, cipher.AsSpan(29), cipher.AsSpan(13, 16),
                Encoding.UTF8.GetBytes("codex-account-directory-v1\n" + DirectoryId + "\n" + revision));
            await using var lease = await Vault.AcquireAsync(DirectoryId, ct);
            Assert.True(await lease.CompareExchangeAsync(null, new(revision, cipher), ct));
            return revision;
        }

        internal Fixture()
        {
            Vault = new(new CodexFileOAuthVault(path.FullName));
            Credentials = new(Vault, key);
        }

        internal CodexEncryptedAccountSessionDirectory Open() =>
            new(Vault, Credentials, "owner", key, CredentialId, (_, ct) => Acquire(ct));

        internal CodexDurableAccountLifecycleAuthority Authority(CodexEncryptedAccountSessionDirectory directory) =>
            new(directory, (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                (_, _, _, ct) => Acquire(ct));

        private ValueTask<IAsyncDisposable?> Acquire(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Deny) return ValueTask.FromResult<IAsyncDisposable?>(null);
            ActiveGrants++;
            return ValueTask.FromResult<IAsyncDisposable?>(new Grant(this));
        }
        internal static string CredentialId(string session, string account) => "credential/" + session + "/" + account;

        public void Dispose()
        {
            Credentials.Dispose();
            CryptographicOperations.ZeroMemory(key);
            path.Delete(recursive: true);
        }
        private sealed class Grant(Fixture owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { owner.ActiveGrants--; return ValueTask.CompletedTask; }
        }
    }

    private enum Fault { None, Before, After }

    private sealed class FaultVault(ICodexOAuthVault inner) : ICodexOAuthVault
    {
        internal Func<string, CodexOAuthVaultRecord?, Fault>? FailOnce { get; set; }
        public async ValueTask<ICodexOAuthVaultLease> AcquireAsync(string sessionId, CancellationToken cancellationToken) =>
            new Lease(this, sessionId, await inner.AcquireAsync(sessionId, cancellationToken));

        private sealed class Lease(FaultVault owner, string id, ICodexOAuthVaultLease innerLease) : ICodexOAuthVaultLease
        {
            public ValueTask<CodexOAuthVaultRecord?> ReadAsync(CancellationToken cancellationToken) => innerLease.ReadAsync(cancellationToken);
            public async ValueTask<bool> CompareExchangeAsync(string? expectedVersion, CodexOAuthVaultRecord? replacement, CancellationToken cancellationToken)
            {
                var fault = owner.FailOnce?.Invoke(id, replacement) ?? Fault.None;
                if (fault != Fault.None) owner.FailOnce = null;
                if (fault == Fault.Before) throw new IOException("Injected loss before commit.");
                var result = await innerLease.CompareExchangeAsync(expectedVersion, replacement, cancellationToken);
                if (fault == Fault.After) throw new IOException("Injected loss after commit.");
                return result;
            }
            public ValueTask DisposeAsync() => innerLease.DisposeAsync();
        }
    }
}
