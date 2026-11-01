using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexAccountLimitBucketsTests
{
    private const string UpstreamShape = """
        {"plan_type":"pro","rate_limit":{"allowed":true,"limit_reached":false,
          "primary_window":{"used_percent":42,"limit_window_seconds":3600,"reset_after_seconds":120,"reset_at":1735693200}},
         "additional_rate_limits":[{"limit_name":"Other quota","metered_feature":"codex_other",
           "rate_limit":{"allowed":false,"limit_reached":true,"primary_window":{
             "used_percent":88,"limit_window_seconds":1800,"reset_after_seconds":600,"reset_at":1735693200}}}],
         "credits":{"has_credits":true,"unlimited":false,"balance":"12.3456789"},
         "account_id":"must-not-retain","user_id":"must-not-retain"}
        """;

    private static CodexAccountUsage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CodexClient.ParseAccountUsage(document.RootElement, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void MeteredBucketCannotReplaceOrdinaryPermissionOrWindows()
    {
        var usage = Parse(UpstreamShape);
        Assert.True(usage.Allowed);
        Assert.Equal(42, Assert.Single(usage.Windows).UsedPercent);
        Assert.Equal(2, usage.RateLimitsByLimitId.Count);
        var other = usage.RateLimitsByLimitId["codex_other"];
        Assert.Equal("Other quota", other.LimitName);
        Assert.False(other.Allowed); Assert.True(other.LimitReached);
        Assert.Equal(88, Assert.Single(other.Windows).UsedPercent);
        Assert.Equal(600, other.Windows[0].ResetAfterSeconds);
        Assert.Equal(120, usage.Windows[0].ResetAfterSeconds);
        Assert.Equal(12.3456789m, usage.Credits);
        Assert.Equal(new CodexAccountCreditState(true, false), usage.CreditState);
        Assert.Equal(0, usage.RejectedAdditionalLimits);
        var serialized = JsonSerializer.Serialize(usage);
        Assert.DoesNotContain("must-not-retain", serialized);
        Assert.DoesNotContain("account_id", serialized);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"additional_rate_limits\":null,\"credits\":null,\"rate_limit\":null}")]
    public void MissingUsageIsUnknownRatherThanZeroOrRecovery(string json)
    {
        var usage = Parse(json);
        Assert.Null(usage.Allowed); Assert.Null(usage.Credits); Assert.Null(usage.CreditState);
        Assert.Empty(usage.Windows);
        Assert.Null(usage.RateLimitsByLimitId["codex"].Allowed);
        Assert.Null(usage.RateLimitsByLimitId["codex"].LimitReached);
        Assert.Equal(0, usage.RejectedAdditionalLimits);
    }

    [Fact]
    public void ConflictingAndMalformedIdentitiesAreReportedWithoutMerging()
    {
        var usage = Parse("""
            {"rate_limit":{"allowed":true},"additional_rate_limits":[
              {"metered_feature":"codex","limit_name":"collision","rate_limit":{"allowed":false}},
              {"metered_feature":"other","limit_name":"first","rate_limit":{"allowed":false}},
              {"metered_feature":"other","limit_name":"duplicate","rate_limit":{"allowed":true}},
              {"metered_feature":"missing-name"},false]}
            """);
        Assert.True(usage.Allowed);
        Assert.True(usage.RateLimitsByLimitId["codex"].Allowed);
        Assert.False(usage.RateLimitsByLimitId["other"].Allowed);
        Assert.Equal("first", usage.RateLimitsByLimitId["other"].LimitName);
        Assert.Equal(4, usage.RejectedAdditionalLimits);
    }

    [Fact]
    public void MissingAdditionalDetailsRetainIdentityWithUnknownPermission()
    {
        var usage = Parse("""
            {"additional_rate_limits":[{"metered_feature":"other","limit_name":"Other","rate_limit":null}]}
            """);
        var other = usage.RateLimitsByLimitId["other"];
        Assert.Null(other.Allowed); Assert.Null(other.LimitReached); Assert.Empty(other.Windows);
    }

    [Fact]
    public void UnlimitedCreditsDoNotInventBalanceOrOrdinaryPermission()
    {
        var usage = Parse("""{"credits":{"has_credits":false,"unlimited":true,"balance":null}}""");
        Assert.Equal(new CodexAccountCreditState(false, true), usage.CreditState);
        Assert.Null(usage.Credits); Assert.Null(usage.Allowed);
        var malformed = Parse("""{"credits":{"has_credits":"true","unlimited":1}}""");
        Assert.Equal(new CodexAccountCreditState(null, null), malformed.CreditState);
    }

    [Fact]
    public void MalformedContainerAndWindowCannotFabricateQuota()
    {
        Assert.Equal(1, Parse("""{"additional_rate_limits":{}}""").RejectedAdditionalLimits);
        var usage = Parse("""
            {"rate_limit":{"allowed":"true","primary_window":{"used_percent":"bad"}},
             "additional_rate_limits":[{"metered_feature":"other","limit_name":"Other","rate_limit":{
              "allowed":false,"primary_window":{"used_percent":10,"limit_window_seconds":60,"reset_at":1,"reset_after_seconds":-1}}}]}
            """);
        Assert.Null(usage.Allowed); Assert.Empty(usage.Windows);
        Assert.Null(Assert.Single(usage.RateLimitsByLimitId["other"].Windows).ResetAfterSeconds);
    }

    [Fact]
    public void OlderSavedSnapshotsRemainReadable()
    {
        var value = JsonSerializer.Deserialize<CodexAccountUsage>("""
            {"ObservedAt":"2026-10-01T00:00:00Z","Plan":"pro","Allowed":true,"Credits":1,"Windows":[]}
            """);
        Assert.NotNull(value); Assert.True(value.Allowed); Assert.Equal(1m, value.Credits);
        Assert.Empty(value.RateLimitsByLimitId); Assert.Null(value.CreditState);
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("test-token", "test-account"));
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/backend-api/wham/usage", (request.RequestUri ?? throw new InvalidDataException("Required data was null.")).AbsolutePath);
            Assert.Equal("Bearer", (request.Headers.Authorization ?? throw new InvalidDataException("Required data was null.")).Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(UpstreamShape, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task AuthenticatedReadReturnsIndependentBucketsWithoutModelCall()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler());
        var usage = await client.GetAccountUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, usage.RateLimitsByLimitId.Count);
        Assert.True(usage.Allowed);
        Assert.False(usage.RateLimitsByLimitId["codex_other"].Allowed);
    }
}
