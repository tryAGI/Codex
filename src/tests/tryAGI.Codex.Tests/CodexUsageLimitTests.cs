using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexUsageLimitTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("PRIVATE_TOKEN", "PRIVATE_ACCOUNT"));
    }

    private sealed class Handler(HttpStatusCode status, string body, string? activeLimit = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (activeLimit is not null) response.Headers.Add("x-codex-active-limit", activeLimit);
            return Task.FromResult(response);
        }
    }

    private static async Task<CodexProtocolException> StreamAsync(HttpMessageHandler handler)
    {
        using var client = new CodexClient(new Credentials(), handler: handler);
        return await Assert.ThrowsAnyAsync<CodexProtocolException>(async () =>
        {
            await foreach (var item in client.StreamResponseAsync(new("test-model", "instructions"), [], TestContext.Current.CancellationToken)) { _ = item; }
        });
    }

    [Fact]
    public async Task ExhaustedAllowanceIsTypedWithItsAdvisoryWindow()
    {
        var error = Assert.IsType<CodexUsageLimitException>(await StreamAsync(new Handler(HttpStatusCode.TooManyRequests, """
            {"error":{"type":"usage_limit_reached","message":"PRIVATE provider text","plan_type":"pro","resets_at":1738888888,"limit_window_minutes":300}}
            """, "codex_primary")));
        Assert.Equal(429, error.StatusCode);
        Assert.Equal("pro", error.PlanType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1738888888), error.ResetsAt);
        Assert.Equal(TimeSpan.FromMinutes(300), error.LimitWindow);
        Assert.Equal("codex_primary", error.LimitId);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"type":"rate_limit_exceeded","message":"slow down"}}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"type":"insufficient_quota","code":"insufficient_quota"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"type":"usage_limit_reached"}}""")]
    [InlineData(HttpStatusCode.TooManyRequests, "not json")]
    public async Task OtherFailuresStayOrdinaryErrors(HttpStatusCode status, string body) =>
        Assert.IsNotType<CodexUsageLimitException>(await StreamAsync(new Handler(status, body)));

    [Fact]
    public async Task UnsafeOrMalformedHintsAreDropped()
    {
        var error = Assert.IsType<CodexUsageLimitException>(await StreamAsync(new Handler(HttpStatusCode.TooManyRequests, """
            {"error":{"type":"usage_limit_reached","plan_type":"PRIVATE TEXT","resets_at":"soon","limit_window_minutes":-5}}
            """, "PRIVATE LIMIT")));
        Assert.Null(error.PlanType); Assert.Null(error.ResetsAt); Assert.Null(error.LimitWindow); Assert.Null(error.LimitId);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public void RecheckDelayTreatsTheResetAsAHint()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        static CodexUsageLimitException At(DateTimeOffset? reset) => new(429, null, reset, null, null);

        // Future reset: wait for it, but never longer than the maximum, so an early return is noticed.
        Assert.Equal(TimeSpan.FromMinutes(5), At(now.AddMinutes(5)).GetRecheckDelay(now));
        Assert.Equal(TimeSpan.FromMinutes(15), At(now.AddHours(3)).GetRecheckDelay(now));
        Assert.Equal(TimeSpan.FromMinutes(1), At(now.AddSeconds(2)).GetRecheckDelay(now));
        // Past or missing reset: the minimum interval, never a tight loop.
        Assert.Equal(TimeSpan.FromMinutes(1), At(now.AddMinutes(-10)).GetRecheckDelay(now));
        Assert.Equal(TimeSpan.FromMinutes(1), At(null).GetRecheckDelay(now));
        Assert.Equal(TimeSpan.FromSeconds(30), At(null).GetRecheckDelay(now, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => At(null).GetRecheckDelay(now, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => At(null).GetRecheckDelay(now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("""{"message":"limit","codexErrorInfo":"usageLimitExceeded"}""", true)]
    [InlineData("""{"message":"limit","codexErrorInfo":"rateLimitExceeded"}""", false)]
    [InlineData("""{"message":"limit","codexErrorInfo":{"httpConnectionFailed":{"httpStatusCode":429}}}""", false)]
    [InlineData("""{"message":"limit"}""", false)]
    [InlineData("null", false)]
    public void AppServerTurnErrorsAreClassified(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, CodexUsageLimitException.IsAppServerUsageLimit(document.RootElement));
    }
}
