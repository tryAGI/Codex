using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexProviderUsageTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        internal string Account = "account-a";
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("secret-token", Account));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return send(request, ct); }
    }
    private static HttpResponseMessage Json(string text, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ThreadReadPreservesProviderAmountsModelsCacheAndUnknownRows()
    {
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/backend-api/wham/usage/thread_usage/query", (request.RequestUri ?? throw new InvalidDataException("Required data was null.")).AbsolutePath);
            Assert.Equal("Bearer", (request.Headers.Authorization ?? throw new InvalidDataException("Required data was null.")).Scheme);
            Assert.Equal("account-a", Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
            using var input = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct));
            Assert.Equal(new[] { "thread-a", "thread-b", "thread-c" }, input.RootElement.GetProperty("thread_ids")
                .EnumerateArray().Select(x => x.GetString()).ToArray());
            return Json("""
                {"threads":[{"thread_id":"thread-b","estimated_usage_credits_micros":null},
                  {"thread_id":"thread-a","estimated_usage_credits_micros":46000000,"estimated_usage_usd_micros":1820000,
                   "native_usage_usd_micros":0,"groups":[{"model":"gpt-6.1-sol","reasoning_effort":"low","speed":"fast",
                    "estimated_usage_credits_micros":46000000,"native_usage_usd_micros":null,"input_tokens":100,
                    "cached_input_tokens":80,"net_new_input_tokens":20,"output_tokens":5,"total_tokens":105}]}]}
                """);
        });
        using var client = new CodexClient(new Credentials(), handler: handler);
        var result = await client.GetThreadsUsageAsync(["thread-a", "thread-b", "thread-c"], TestContext.Current.CancellationToken);
        var row = Assert.Single(result.Threads); Assert.Equal(46000000, row.EstimatedUsageCreditsMicros);
        Assert.Equal(1820000, row.EstimatedUsageUsdMicros); Assert.Equal(0, row.NativeUsageUsdMicros);
        var group = Assert.Single(row.Groups); Assert.Equal("gpt-6.1-sol", group.Model);
        Assert.Equal("low", group.ReasoningEffort); Assert.Equal("fast", group.Speed);
        Assert.Equal(80, group.CachedInputTokens); Assert.Equal(20, group.NetNewInputTokens);
        Assert.Null(group.NativeUsageUsdMicros);
        Assert.Equal(new[] { "thread-b", "thread-c" }, result.UnavailableThreadIds);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("account-a", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("{\"threads\":[{\"thread_id\":\"foreign\",\"estimated_usage_credits_micros\":null}]}")]
    [InlineData("{\"threads\":[{\"thread_id\":\"thread-a\"},{\"thread_id\":\"thread-a\"}]}")]
    [InlineData("{\"threads\":[{\"thread_id\":\"thread-a\",\"estimated_usage_credits_micros\":1.5}]}")]
    [InlineData("{\"threads\":[{\"thread_id\":\"thread-a\",\"estimated_usage_credits_micros\":9223372036854775808}]}")]
    [InlineData("{\"threads\":null}")]
    public async Task InvalidIdentityOrAmountsCannotBecomeFreeUsage(string payload)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Json(payload))));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => client.GetThreadsUsageAsync(["thread-a"], TestContext.Current.CancellationToken));
        Assert.DoesNotContain(payload, error.Message);
    }

    [Fact]
    public async Task ZeroAndUnknownAreDistinctAndSignedIntegerMicrosAreNotConverted()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Json("""
            {"threads":[{"thread_id":"thread-a","estimated_usage_credits_micros":0},
             {"thread_id":"thread-b","estimated_usage_credits_micros":-1,"native_usage_usd_micros":9223372036854775807}]}
            """))));
        var result = await client.GetThreadsUsageAsync(["thread-a", "thread-b"], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.Threads[0].EstimatedUsageCreditsMicros); Assert.Null(result.Threads[0].NativeUsageUsdMicros);
        Assert.Equal(-1, result.Threads[1].EstimatedUsageCreditsMicros);
        Assert.Equal(long.MaxValue, result.Threads[1].NativeUsageUsdMicros); Assert.Empty(result.UnavailableThreadIds);
    }

    [Fact]
    public async Task InvalidBatchNeverDispatches()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("must not dispatch"));
        using var client = new CodexClient(new Credentials(), handler: handler);
        foreach (var ids in new[] { Array.Empty<string>(), new[] { "same", "same" }, new[] { " " },
                     Enumerable.Range(0, 101).Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray() })
            await Assert.ThrowsAsync<ArgumentException>(() => client.GetThreadsUsageAsync(ids, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AccountActivityKeepsSharedStatisticsAndMissingBucketsUnknown()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/backend-api/wham/profiles/me", (request.RequestUri ?? throw new InvalidDataException("Required data was null.")).AbsolutePath);
            return Task.FromResult(Json("""{"stats":{"lifetime_tokens":100,"daily_usage_buckets":[{"start_date":"2026-10-15","tokens":42}]}}"""));
        });
        using var client = new CodexClient(new Credentials(), handler: handler);
        var report = await client.GetAccountTokenActivityAsync(TestContext.Current.CancellationToken); Assert.Equal(100, report.LifetimeTokens);
        Assert.Null(report.PeakDailyTokens); Assert.Equal(42, Assert.Single((report.DailyUsageBuckets ?? throw new InvalidDataException("Required data was null."))).Tokens);
        using var unknown = new CodexClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Json("""{"stats":{}}"""))));
        Assert.Null((await unknown.GetAccountTokenActivityAsync(TestContext.Current.CancellationToken)).DailyUsageBuckets);
    }

    [Fact]
    public async Task AccountSwitchCannotAttributePreviousAccountsUsage()
    {
        var credentials = new Credentials();
        using var client = new CodexClient(credentials, handler: new Handler((_, _) =>
        { credentials.Account = "account-b"; return Task.FromResult(Json("""{"threads":[]}""")); }));
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.GetThreadsUsageAsync(["thread-a"], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnavailableRouteRetainsStatusWithoutZeroOrRetry(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("sensitive body", status)));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => client.GetThreadsUsageAsync(["thread-a"], TestContext.Current.CancellationToken));
        Assert.Equal((int)status, error.StatusCode); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("sensitive", error.Message);
    }
}
