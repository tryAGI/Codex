using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexQuotaIdentityTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        internal CodexCredentials Active = new("opaque-token", "workspace-a") { ChatGptUserId = "user-a" };
        internal bool LoggedOut;
        internal int Reads;
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
        {
            Reads++;
            if (LoggedOut) throw new CodexAuthorizationUnavailableException();
            return ValueTask.FromResult(Active);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(send(request));
    }
    private static HttpResponseMessage Json(JsonObject value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    private static JsonObject Quota(string? account = "workspace-a", string? user = "user-a", bool allowed = true) => new()
    {
        ["account_id"] = account, ["user_id"] = user,
        ["rate_limit"] = new JsonObject { ["allowed"] = allowed, ["primary_window"] = new JsonObject
            { ["used_percent"] = 42, ["limit_window_seconds"] = 3600, ["reset_at"] = 2000000000 } }
    };
    private static string Token(string account, string? user, bool restricted = false, bool alternateUserKey = false) =>
        "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
        { ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = account,
            [alternateUserKey ? "user_id" : "chatgpt_user_id"] = user, ["chatgpt_account_is_fedramp"] = restricted } }.ToJsonString()))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    [Theory]
    [InlineData("workspace-a", "user-a", false, "matched")]
    [InlineData("workspace-a", "user-a", true, "restricted")]
    [InlineData("other", "user-a", false, "mismatch")]
    [InlineData("workspace-a", "other", false, "mismatch")]
    [InlineData(null, "user-a", false, "missing_identity")]
    [InlineData("workspace-a", null, false, "missing_identity")]
    public async Task AccountAndUserMustMatchActiveNonrestrictedIdentity(string? account, string? user, bool restricted, string validation)
    {
        var credentials = new Credentials { Active = new("opaque-token", "workspace-a")
            { ChatGptUserId = "user-a", IsFedRampAccount = restricted } };
        using var client = new CodexClient(credentials, handler: new Handler(_ => Json(Quota(account, user))));
        var report = await client.GetAccountUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(validation, report.IdentityValidation);
        Assert.Equal(validation == "matched" ? true : (bool?)null, report.OrdinaryUsageAllowed);
        Assert.Single(report.Windows); Assert.True(report.Allowed);
        Assert.Equal(2, credentials.Reads);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("workspace-a", serialized); Assert.DoesNotContain("user-a", serialized);
        Assert.DoesNotContain("opaque-token", serialized);
    }

    [Fact]
    public async Task MatchedIdentityDoesNotConvertProviderDenialToRecovery()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Json(Quota(allowed: false))));
        var report = await client.GetAccountUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("matched", report.IdentityValidation); Assert.False(report.OrdinaryUsageAllowed);
    }

    [Fact]
    public async Task LoginSwitchDuringHttpReadCannotAuthorizePreviousAccount()
    {
        var credentials = new Credentials();
        using var client = new CodexClient(credentials, handler: new Handler(_ =>
        {
            credentials.Active = new("new-token", "workspace-b") { ChatGptUserId = "user-b" };
            return Json(Quota());
        }));
        var report = await client.GetAccountUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("mismatch", report.IdentityValidation); Assert.Null(report.OrdinaryUsageAllowed);
        Assert.Single(report.Windows);
    }

    [Fact]
    public async Task LogoutDuringReadKeepsDisplayWindowsWithUnknownPermission()
    {
        var credentials = new Credentials();
        using var client = new CodexClient(credentials, handler: new Handler(_ =>
        { credentials.LoggedOut = true; return Json(Quota()); }));
        var report = await client.GetAccountUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("auth_unavailable", report.IdentityValidation); Assert.Null(report.OrdinaryUsageAllowed);
        Assert.Single(report.Windows);
    }

    [Fact]
    public async Task LegacyCredentialWithoutUserDoesNotClaimValidatedPermission()
    {
        var credentials = new Credentials { Active = new("legacy-token", "workspace-a") };
        using var client = new CodexClient(credentials, handler: new Handler(_ => Json(Quota())));
        var report = await client.GetAccountUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("missing_identity", report.IdentityValidation); Assert.Null(report.OrdinaryUsageAllowed);
        Assert.True(report.Allowed);
    }

    [Fact]
    public async Task IdTokenMetadataSurvivesTokenProjectionWithoutDisclosure()
    {
        using var oauth = new CodexDeviceAuthorization(handler: new Handler(_ => Json(new()
        { ["access_token"] = "access", ["refresh_token"] = "refresh", ["id_token"] = Token("workspace-a", "user-a", true, true) })));
        var tokens = await oauth.RefreshAsync(new("old", "refresh", "workspace-a", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Equal("user-a", tokens.ChatGptUserId); Assert.True(tokens.IsFedRampAccount);
        Assert.Equal("user-a", tokens.Credentials.ChatGptUserId); Assert.True(tokens.Credentials.IsFedRampAccount);
        var serialized = JsonSerializer.Serialize(tokens);
        Assert.DoesNotContain("user-a", serialized); Assert.DoesNotContain("workspace-a", serialized);
        Assert.DoesNotContain("access", serialized); Assert.DoesNotContain("refresh", serialized);
        Assert.Equal("{}", JsonSerializer.Serialize(tokens.Credentials));
        Assert.DoesNotContain("user-a", tokens.ToString());
    }

    [Fact]
    public async Task RefreshWithoutIdTokenPreservesPriorIdentity()
    {
        using var oauth = new CodexDeviceAuthorization(handler: new Handler(_ => Json(new()
        { ["access_token"] = "new-access", ["refresh_token"] = "new-refresh" })));
        var tokens = await oauth.RefreshAsync(new("old", "refresh", "workspace-a", DateTimeOffset.UtcNow, "user-a", true), TestContext.Current.CancellationToken);
        Assert.Equal("user-a", tokens.ChatGptUserId); Assert.True(tokens.IsFedRampAccount);
    }

    [Fact]
    public async Task RefreshCannotReplaceKnownUser()
    {
        using var oauth = new CodexDeviceAuthorization(handler: new Handler(_ => Json(new()
        { ["access_token"] = "access", ["refresh_token"] = "refresh", ["id_token"] = Token("workspace-a", "other") })));
        await Assert.ThrowsAsync<CodexProtocolException>(() => oauth.RefreshAsync(new("old", "refresh", "workspace-a", DateTimeOffset.UtcNow, "user-a"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitNewIdTokenWithoutUserDoesNotInheritPriorUser()
    {
        using var oauth = new CodexDeviceAuthorization(handler: new Handler(_ => Json(new()
        { ["access_token"] = "access", ["refresh_token"] = "refresh", ["id_token"] = Token("workspace-a", null) })));
        var tokens = await oauth.RefreshAsync(new("old", "refresh", "workspace-a", DateTimeOffset.UtcNow, "user-a"), TestContext.Current.CancellationToken);
        Assert.Null(tokens.ChatGptUserId);
    }

    [Fact]
    public void MalformedOrOversizedClaimsRemainUnavailable()
    {
        Assert.Null(CodexIdentityClaims.Read("not-a-jwt"));
        Assert.Null(CodexIdentityClaims.Read("header.e30.signature"));
        Assert.Null(CodexIdentityClaims.Read(new string('a', 131073)));
        var claims = CodexIdentityClaims.Read(Token("workspace-a", "user-a"));
        Assert.NotNull(claims); Assert.Equal("CodexIdentityClaims [redacted]", claims.ToString());
    }

    [Fact]
    public void PriorFourParameterTokenConstructorRemainsBinaryCompatible()
    {
        Assert.NotNull(typeof(CodexOAuthTokens).GetConstructor(
            [typeof(string), typeof(string), typeof(string), typeof(DateTimeOffset)]));
        var tokens = new CodexOAuthTokens("old", "refresh", "workspace-a", DateTimeOffset.UtcNow);
        Assert.Null(tokens.Credentials.ChatGptUserId);
        Assert.False(tokens.Credentials.IsFedRampAccount);
    }
}
