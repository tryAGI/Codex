using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexObservedUsageTests
{
    private static readonly CodexResponsesIdentity Identity = new("session", "thread", "turn") { VoiceSessionId = "voice" };
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void SparseAndEvolvingResponsesKeepUnknownDistinctFromZero()
    {
        var payload = Parse("""
            {"type":"response.completed","event_id":"event","response":{"id":"resp","status":"completed",
            "usage":{"input_tokens":0,"input_tokens_details":{"cached_tokens":0},"output_tokens":12,
            "output_tokens_details":{"reasoning_tokens":3},"codex_rollout_budget_units":1.25,"future":42,
            "private_text":"secret"},"output":[{"type":"message","content":[{"text":"private transcript"}]}]}}
            """);
        var usage = Assert.IsType<CodexObservedUsage>(CodexObservedUsage.Read(payload, Identity, "reservation"));
        Assert.Equal("resp", usage.ResponseId);
        Assert.Equal("event", usage.EventId);
        Assert.Equal("reservation", usage.RequestId);
        Assert.Equal(Identity, usage.Identity);
        var tokens = Assert.IsType<CodexObservedTokens>(usage.Tokens);
        Assert.Equal(0, tokens.InputTokens);
        Assert.Equal(0, tokens.CachedInputTokens);
        Assert.Equal(3, tokens.ReasoningTokens);
        Assert.Equal(1.25m, tokens.RolloutBudgetUnits);
        Assert.Null(tokens.TotalTokens);
        Assert.Null(tokens.CacheWriteInputTokens);
        Assert.Null(usage.ModelContextWindow);
        Assert.Contains(usage.Schema!.Fields, field => field.Path == "response.usage.future" && field.Number == 42);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(usage));
        Assert.DoesNotContain("private transcript", JsonSerializer.Serialize(usage));
    }

    [Fact]
    public void RustTokenCountAndRateLimitFixturesPreserveReportedValues()
    {
        var usage = CodexObservedUsage.Read(Parse("""
            {"type":"token_count","info":{"last_usage_includes_reasoning":true,"model_context_window":128000,
            "total_token_usage":{"input_tokens":200,"cache_write_input_tokens":5,"total_tokens":250},
            "last_token_usage":{"input_tokens":50,"cached_input_tokens":25,"reasoning_output_tokens":10}}}
            """))!;
        Assert.Equal(128000, usage.ModelContextWindow);
        Assert.True(usage.LastUsageIncludesReasoning);
        Assert.Equal(250, usage.Tokens!.TotalTokens);
        Assert.Equal(5, usage.Tokens.CacheWriteInputTokens);
        Assert.Equal(25, usage.LastTokens!.CachedInputTokens);
        Assert.Null(usage.ContextRemainingTokens);
        var quota = CodexObservedUsage.Read(Parse("""
            {"type":"codex.rate_limits","metered_limit_name":"codex-other","plan_type":"pro",
            "rate_limits":{"primary":{"used_percent":101.5,"window_minutes":300,"reset_at":12345},"secondary":{"used_percent":0}},
            "credits":{"has_credits":false,"unlimited":false,"balance":"0.00"}}
            """))!.Quota!;
        Assert.Equal(101.5m, quota.Primary!.UsedPercent);
        Assert.Equal(12345, quota.Primary.ResetAtUnixSeconds);
        Assert.Equal(0, quota.Secondary!.UsedPercent);
        Assert.Null(quota.Secondary.ResetAtUnixSeconds);
        Assert.False(quota.HasCredits);
        Assert.Equal(0m, quota.CreditBalance);
        Assert.Null(quota.Allowed);
    }

    [Fact]
    public void RealtimeSessionSnapshotIsNotInferredAsResponseBilling()
    {
        var evt = new CodexRealtimeEvent("session.usage.updated", Parse("""
            {"type":"session.usage.updated","session_id":"voice-id","usage":{"input_tokens":12},
            "context_window":{"total_tokens":100,"used_tokens":11},"usage_limit":{"allowed":true}}
            """), 25);
        var observed = CodexObservedUsage.Read(evt, Identity)!;
        Assert.Equal("voice-id", observed.ProviderSessionId);
        Assert.Null(observed.ResponseId);
        Assert.Equal(100, observed.ModelContextWindow);
        Assert.Equal(11, observed.ContextUsedTokens);
        Assert.Null(observed.ContextRemainingTokens);
        Assert.True(observed.Quota!.Allowed);
        Assert.Null(CodexObservedUsage.Read(Parse("""{"type":"conversation.item.created","usage":{"input_tokens":12}}""")));
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("token", "account"));
    }

    private sealed class Observer : ICodexUsageObserver
    {
        internal int Complete, Incomplete;
        internal CodexObservedUsage? Partial;
        public Task ReserveAsync(string requestId, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string requestId, string model, JsonElement response, CancellationToken ct)
        { Complete++; return Task.CompletedTask; }
        public Task IncompleteAsync(string requestId, string model, CancellationToken ct)
        { Incomplete++; return Task.CompletedTask; }
        public Task IncompleteWithUsageAsync(string requestId, string model, CodexObservedUsage? usage, CancellationToken ct)
        { Partial = usage; Incomplete++; return Task.CompletedTask; }
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
            result.Headers.Add("x-codex-primary-used-percent", "101");
            result.Headers.Add("x-codex-other-secondary-window-minutes", "10080");
            result.Headers.Add("x-codex-credits-balance", "0");
            return Task.FromResult(result);
        }
    }

    private static async Task Read(CodexClient client, List<CodexResponseDiagnostic> diagnostics)
    {
        await foreach (var _ in client.StreamResponseAsync(new("model", "instructions") { Identity = Identity },
            [], TestContext.Current.CancellationToken, diagnostics.Add)) { }
    }

    [Fact]
    public async Task DuplicateCompletedEventsAccountOnceAndHeadersDoNotInventOtherWindows()
    {
        const string completed = "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp\",\"output\":[],\"usage\":{\"total_tokens\":0}}}\n\n";
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: new Handler(completed + completed));
        var diagnostics = new List<CodexResponseDiagnostic>();
        await Read(client, diagnostics);
        Assert.Equal(1, observer.Complete);
        Assert.Equal(0, observer.Incomplete);
        Assert.Equal(0, Assert.Single(diagnostics, value => value.Phase == "response.completed").Usage!.Tokens!.TotalTokens);
        var quotas = Assert.Single(diagnostics, value => value.Phase == "response.quota_headers").Quotas!;
        Assert.Equal(2, quotas.Count);
        var ordinary = Assert.Single(quotas, quota => quota.LimitId == "codex");
        Assert.Equal(101m, ordinary.Primary!.UsedPercent);
        Assert.Equal(0m, ordinary.CreditBalance);
        Assert.Null(ordinary.Secondary);
        var other = Assert.Single(quotas, quota => quota.LimitId == "codex_other");
        Assert.Equal(10080, other.Secondary!.WindowMinutes);
        Assert.Null(other.Secondary.UsedPercent);
    }

    [Fact]
    public async Task IncompleteReportedUsageIsNotCompletedBilling()
    {
        const string incomplete = "data: {\"type\":\"response.incomplete\",\"response\":{\"id\":\"resp\",\"usage\":{\"output_tokens\":8}}}\n\n";
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: new Handler(incomplete + incomplete));
        var diagnostics = new List<CodexResponseDiagnostic>();
        await Assert.ThrowsAsync<CodexProtocolException>(() => Read(client, diagnostics));
        Assert.Equal(0, observer.Complete);
        Assert.Equal(1, observer.Incomplete);
        Assert.Equal(8, observer.Partial!.Tokens!.OutputTokens);
        Assert.Equal(Identity, observer.Partial.Identity);
        Assert.Null(observer.Partial.Tokens.TotalTokens);
    }

    [Fact]
    public async Task WrappedQuotaErrorRetainsTypedResetAndNeverCompletes()
    {
        const string error = "data: {\"type\":\"error\",\"error\":{\"type\":\"usage_limit_reached\",\"plan_type\":\"pro\",\"resets_at\":1795753000}}\n\n";
        var observer = new Observer();
        using var client = new CodexClient(new Credentials(), usageObserver: observer, handler: new Handler(error));
        var exception = await Assert.ThrowsAsync<CodexUsageLimitException>(() => Read(client, []));
        Assert.Equal("pro", exception.PlanType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795753000), exception.ResetsAt);
        Assert.Equal(0, observer.Complete);
        Assert.Equal(1, observer.Incomplete);
    }
}
