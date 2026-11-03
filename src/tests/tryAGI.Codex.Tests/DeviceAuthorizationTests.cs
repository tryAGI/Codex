using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class DeviceAuthorizationTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    private static string IdToken(string id) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
        { ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = id } }.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    [Fact]
    public async Task DeviceFlowWaitsForUserAndExchangesOnlyReturnedCode()
    {
        var polls = 0; var exchanges = 0;
        using var auth = new CodexDeviceAuthorization(handler: new Handler(async (r, ct) =>
        {
            Assert.Equal("auth.openai.com", (r.RequestUri ?? throw new InvalidDataException("Required data was null.")).Host);
            switch (r.RequestUri.AbsolutePath)
            {
                case "/api/accounts/deviceauth/usercode": return Json(new() { ["device_auth_id"] = "private", ["usercode"] = "ABCD", ["interval"] = "3" });
                case "/api/accounts/deviceauth/token":
                    if (++polls == 1) return new(HttpStatusCode.Forbidden);
                    return Json(new() { ["authorization_code"] = "approved-code", ["code_verifier"] = "verifier", ["code_challenge"] = "challenge" });
                default:
                    exchanges++; var body = await (r.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct);
                    Assert.Contains("code=approved-code", body); Assert.Contains("code_verifier=verifier", body);
                    return Json(new() { ["access_token"] = "access", ["refresh_token"] = "refresh", ["id_token"] = IdToken("account"), ["expires_in"] = 7200 });
            }
        }));
        var code = await auth.BeginAsync(TestContext.Current.CancellationToken); Assert.Equal("ABCD", code.UserCode); Assert.Equal(TimeSpan.FromSeconds(3), code.PollInterval);
        Assert.Null(await auth.PollAsync(code, TestContext.Current.CancellationToken)); Assert.Equal(0, exchanges);
        var tokens = await auth.PollAsync(code, TestContext.Current.CancellationToken); Assert.Equal("account", (tokens ?? throw new InvalidDataException("Required data was null.")).AccountId); Assert.Equal(1, exchanges);
    }

    [Fact]
    public async Task RefreshCannotSwitchAccount()
    {
        using var auth = new CodexDeviceAuthorization(handler: new Handler((_, _) => Task.FromResult(Json(new()
        { ["access_token"] = "new", ["refresh_token"] = "rotated", ["id_token"] = IdToken("other") }))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => auth.RefreshAsync(new("old", "refresh", "original", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(CodexRefreshReason.ProactiveExpiry, "proactive_expiry")]
    [InlineData(CodexRefreshReason.Scheduled, "scheduled")]
    [InlineData(CodexRefreshReason.UnauthorizedRecovery, "unauthorized_recovery")]
    [InlineData(CodexRefreshReason.Forced, "forced")]
    public async Task RefreshReasonIsSentOnlyWhenGiven(CodexRefreshReason? reason, string? wire)
    {
        string? body = null;
        using var auth = new CodexDeviceAuthorization(handler: new Handler(async (r, ct) =>
        {
            body = await (r.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct);
            return Json(new() { ["access_token"] = "new", ["refresh_token"] = "rotated", ["id_token"] = IdToken("account") });
        }));
        var previous = new CodexOAuthTokens("old", "refresh", "account", DateTimeOffset.UtcNow);
        var ct = TestContext.Current.CancellationToken;
        var tokens = reason is { } value ? await auth.RefreshAsync(previous, value, ct) : await auth.RefreshAsync(previous, ct);
        Assert.Equal("rotated", tokens.RefreshToken);
        var fields = (body ?? "").Split('&');
        Assert.Contains("grant_type=refresh_token", fields);
        if (wire is null) Assert.DoesNotContain(fields, f => f.StartsWith("refresh_reason=", StringComparison.Ordinal));
        else Assert.Contains("refresh_reason=" + wire, fields);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => auth.RefreshAsync(previous, (CodexRefreshReason)42, ct));
    }

    [Fact]
    public async Task ExpiredCodeNeverContactsIssuer()
    {
        using var auth = new CodexDeviceAuthorization(handler: new Handler((_, _) => throw new InvalidOperationException("must not call")));
        await Assert.ThrowsAsync<CodexProtocolException>(() => auth.PollAsync(new()
        { UserCode = "expired", DeviceAuthId = "private", VerificationUri = new("https://auth.openai.com/codex/device"), PollInterval = TimeSpan.FromSeconds(5), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, TestContext.Current.CancellationToken));
    }
}
