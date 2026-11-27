using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexAccountRpcTests
{
    private static CodexAccountSelection Selection(string epoch = "selection-a", string auth = "chatgpt") =>
        new(epoch, CodexAccountKind.ChatGpt, auth, true, "account", "user", "user@example.test", "pro", "Workspace");
    private static CodexAccountObservation<T> Observe<T>(CodexAccountSelection selection, T value) =>
        new(selection.SelectionEpoch, selection.AccountId, selection.UserId, value);

    [Fact]
    public async Task AccountReadPreservesWireShapeAndOnlyRefreshesManagedAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        var selection = Selection();
        var calls = new List<bool>();
        var rpc = new CodexAccountRpc((refresh, _) =>
        {
            calls.Add(refresh);
            return Task.FromResult(selection);
        });
        var account = await rpc.InvokeAsync("account/read", Json("""{"refreshToken":true}"""), ct);
        Assert.Equal("chatgpt", account.GetProperty("account").GetProperty("type").GetString());
        Assert.Equal("pro", account.GetProperty("account").GetProperty("planType").GetString());
        Assert.Equal("user", account.GetProperty("userId").GetString());
        Assert.Equal("Workspace", account.GetProperty("account").GetProperty("workspaceName").GetString());
        Assert.True(account.GetProperty("requiresOpenaiAuth").GetBoolean());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("workspaceRouting").ValueKind);
        Assert.DoesNotContain("selection-a", account.GetRawText());
        Assert.Equal(new[] { false, true }, calls);
        selection = Selection("selection-b", "chatgptAuthTokens");
        calls.Clear();
        await rpc.InvokeAsync("account/read", Json("""{"refreshToken":true}"""), ct);
        Assert.Equal(new[] { false }, calls);
        selection = new("logged-out", CodexAccountKind.None, null, true);
        var loggedOut = await rpc.InvokeAsync("account/read", cancellationToken: ct);
        Assert.Equal(JsonValueKind.Null, loggedOut.GetProperty("account").ValueKind);
        Assert.Equal(JsonValueKind.Null, loggedOut.GetProperty("userId").ValueKind);
    }

    [Theory]
    [InlineData("apiKey", CodexAccountKind.ApiKey)]
    [InlineData("amazonBedrock", CodexAccountKind.AmazonBedrock)]
    [InlineData("gcp", CodexAccountKind.Gcp)]
    public async Task AccountReadDoesNotDispatchUsageForNonSubscriptionAccounts(string type, CodexAccountKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var rpc = new CodexAccountRpc((_, _) => Task.FromResult(new CodexAccountSelection("epoch", kind, null, false)),
            (_, _) => throw new InvalidOperationException("Unexpected rate request."),
            (_, _, _) => throw new InvalidOperationException("Unexpected token request."));
        var account = await rpc.InvokeAsync("account/read", cancellationToken: ct);
        Assert.Equal(type, account.GetProperty("account").GetProperty("type").GetString());
        await Assert.ThrowsAsync<CodexAuthorizationUnavailableException>(() => rpc.InvokeAsync("account/rateLimits/read", cancellationToken: ct));
        await Assert.ThrowsAsync<CodexAuthorizationUnavailableException>(() => rpc.InvokeAsync("account/usage/read", cancellationToken: ct));
    }

    [Fact]
    public async Task RateLimitsUseVerifiedIndependentBucketsAndKeepUnknownMetadataNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var selection = Selection();
        var rates = Rates();
        var rpc = new CodexAccountRpc((_, _) => Task.FromResult(selection),
            (owner, _) => Task.FromResult(Observe(owner, rates)));
        var result = await rpc.InvokeAsync("account/rateLimits/read", cancellationToken: ct);
        Assert.False(result.GetProperty("ordinaryUsageAllowed").GetBoolean());
        Assert.Equal(100, result.GetProperty("rateLimits").GetProperty("primary").GetProperty("usedPercent").GetInt32());
        var other = result.GetProperty("rateLimitsByLimitId").GetProperty("other");
        Assert.Equal(20, other.GetProperty("primary").GetProperty("usedPercent").GetInt32());
        Assert.Null(other.GetProperty("spendControlReached").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("rateLimitResetCredits").ValueKind);
        Assert.Equal("account", result.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task RejectsStaleObservationAndSwitchDuringReadEvenIfUserAndAccountAreSame()
    {
        var ct = TestContext.Current.CancellationToken;
        var selection = Selection();
        var rpc = new CodexAccountRpc((_, _) => Task.FromResult(selection),
            (owner, _) =>
            {
                selection = selection with { SelectionEpoch = "switched-back" };
                return Task.FromResult(Observe(owner, Rates()));
            });
        var stale = await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => rpc.InvokeAsync("account/rateLimits/read", cancellationToken: ct));
        Assert.Equal(CodexOAuthRefreshFailure.SessionChanged, stale.Failure);
        var wrong = new CodexAccountRpc((_, _) => Task.FromResult(selection),
            (owner, _) => Task.FromResult(Observe(owner, Rates()) with { UserId = "other" }));
        await Assert.ThrowsAsync<CodexProtocolException>(() => wrong.InvokeAsync("account/rateLimits/read", cancellationToken: ct));
        var wrongAccount = new CodexAccountRpc((_, _) => Task.FromResult(selection),
            (owner, _) => Task.FromResult(Observe(owner, Rates() with { AccountId = "other" })));
        await Assert.ThrowsAsync<CodexProtocolException>(() => wrongAccount.InvokeAsync("account/rateLimits/read", cancellationToken: ct));
    }

    [Fact]
    public async Task TokenUsagePreservesNativeDollarAndCreditAmountsWithoutInvention()
    {
        var ct = TestContext.Current.CancellationToken;
        var selection = Selection();
        var usage = new CodexAccountTokenUsage(new(100, null, null, 2, null),
            [new("2026-11-27", 50)], new("thread-a", 12, null, 5,
            [new("model", "medium", "default", 12, 3, 2, 5, 1, 6)]));
        var rpc = new CodexAccountRpc((_, _) => Task.FromResult(selection), readUsage: (owner, id, _) =>
        {
            Assert.Equal("thread-a", id);
            return Task.FromResult(Observe(owner, usage));
        });
        var result = await rpc.InvokeAsync("account/usage/read", Json("""{"threadId":"thread-a"}"""), ct);
        Assert.Equal(100L, result.GetProperty("summary").GetProperty("lifetimeTokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("summary").GetProperty("peakDailyTokens").ValueKind);
        Assert.Equal(50L, result.GetProperty("dailyUsageBuckets")[0].GetProperty("tokens").GetInt64());
        var thread = result.GetProperty("threadUsage");
        Assert.Equal(5L, thread.GetProperty("nativeUsageUsdMicros").GetInt64());
        Assert.Equal(12L, thread.GetProperty("estimatedUsageCreditsMicros").GetInt64());
        Assert.Equal(JsonValueKind.Null, thread.GetProperty("estimatedUsageUsdMicros").ValueKind);
        Assert.Equal(2L, thread.GetProperty("groups")[0].GetProperty("cachedInputTokens").GetInt64());
        var bad = new CodexAccountRpc((_, _) => Task.FromResult(selection), readUsage: (owner, _, _) =>
            Task.FromResult(Observe(owner, usage)));
        await Assert.ThrowsAsync<CodexProtocolException>(() => bad.InvokeAsync("account/usage/read", Json("""{"threadId":"thread-b"}"""), ct));
    }

    [Fact]
    public async Task NotificationPublishesOnlyObservedBucketAndRejectsStaleOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var selection = Selection();
        var original = selection;
        var events = new List<(string Method, JsonElement Payload)>();
        var rpc = new CodexAccountRpc((_, _) => Task.FromResult(selection), notify: (owner, method, value, _) =>
        {
            Assert.Equal(selection.SelectionEpoch, owner.SelectionEpoch);
            events.Add((method, value.Clone()));
            return Task.CompletedTask;
        });
        await rpc.NotifyAccountUpdatedAsync(selection, cancellationToken: ct);
        await rpc.NotifyRateLimitsUpdatedAsync(selection, Observe(selection, Rates()), "other", ct);
        Assert.Equal("account/updated", events[0].Method);
        Assert.Equal("chatgpt", events[0].Payload.GetProperty("authMode").GetString());
        Assert.DoesNotContain("user@example.test", events[0].Payload.GetRawText());
        Assert.Equal("account/rateLimits/updated", events[1].Method);
        Assert.Equal("other", events[1].Payload.GetProperty("rateLimits").GetProperty("limitId").GetString());
        Assert.False(events[1].Payload.TryGetProperty("ordinaryUsageAllowed", out _));
        selection = selection with { SelectionEpoch = "new" };
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => rpc.NotifyAccountUpdatedAsync(original, cancellationToken: ct));
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => rpc.NotifyRateLimitsUpdatedAsync(original, Observe(original, Rates()), "other", ct));
        Assert.Equal(2, events.Count);
    }

    [Theory]
    [InlineData("account/read", "{\"refreshToken\":1}")]
    [InlineData("account/read", "{\"refreshToken\":true,\"refreshToken\":false}")]
    [InlineData("account/rateLimits/read", "{\"accountId\":\"other\"}")]
    [InlineData("account/usage/read", "{\"threadId\":123}")]
    public async Task InvalidParametersNeverReachHost(string method, string json)
    {
        var rpc = new CodexAccountRpc((_, _) => throw new InvalidOperationException("Unexpected host read."));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync(method, Json(json), TestContext.Current.CancellationToken));
    }

    private static CodexRpcAccountRateLimits Rates()
    {
        var ordinary = new CodexRpcRateLimitSnapshot("codex", null, new(100, 300, 1234), null, null, "pro");
        var other = new CodexRpcRateLimitSnapshot("other", "Other", new(20, 60, 2345), null, null, "pro");
        return new(false, ordinary, new Dictionary<string, CodexRpcRateLimitSnapshot> { ["codex"] = ordinary, ["other"] = other })
        { AccountId = "account" };
    }

    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
