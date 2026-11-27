using System.Text.Json;
using Xunit;

namespace tryAGI.Codex.Tests;

public sealed class CodexAccountRateLimitsTests
{
    [Fact]
    public void ProjectionPreservesBucketsAndDoesNotInferEntitlement()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(2000000000);
        var windows = new[] { new CodexAccountUsageWindow("primary_window", 41.5, 3600, now) };
        var usage = new CodexAccountUsage(now, "pro", true, 12.5m, windows)
        {
            IdentityValidation = "matched",
            CreditState = new(true, false),
            RateLimitsByLimitId = new Dictionary<string, CodexAccountUsageLimit>
            {
                ["codex"] = new("codex", null, true, false, windows),
                ["other"] = new("other", "Other", null, null, []),
            },
        };
        var result = CodexRpcAccountRateLimits.FromVerifiedUsage(usage);
        Assert.Null(result.OrdinaryUsageAllowed);
        Assert.Equal(42, result.RateLimits.Primary!.UsedPercent);
        Assert.Equal(60L, result.RateLimits.Primary.WindowDurationMins);
        Assert.Equal(2000000000L, result.RateLimits.Primary.ResetsAt);
        Assert.Equal("12.5", result.RateLimits.Credits!.Balance);
        Assert.Null(result.RateLimitsByLimitId["other"].Credits);
        Assert.Null(result.RateLimits.IndividualLimit);
        Assert.Null(result.AccountId);
        Assert.Contains("\"rateLimitsByLimitId\"", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mismatch")]
    [InlineData("missing_identity")]
    [InlineData("auth_unavailable")]
    [InlineData("restricted")]
    public void UnverifiedAccountUsageIsNeverProjected(string? validation)
    {
        var usage = new CodexAccountUsage(DateTimeOffset.UtcNow, null, true, 10, [])
        {
            IdentityValidation = validation,
        };
        Assert.Throws<CodexProtocolException>(() => CodexRpcAccountRateLimits.FromVerifiedUsage(usage));
    }

    [Fact]
    public void IncompleteCreditFlagsAreNotFabricated()
    {
        var usage = new CodexAccountUsage(DateTimeOffset.UtcNow, null, null, 20, [])
        {
            IdentityValidation = "matched", CreditState = new(null, false),
            OrdinaryUsageAllowed = false,
        };
        var result = CodexRpcAccountRateLimits.FromVerifiedUsage(usage);
        Assert.Null(result.RateLimits.Credits);
        Assert.False(result.OrdinaryUsageAllowed);
    }
}
