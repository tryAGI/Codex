using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Account-wide token statistics reported by the ChatGPT profile. Missing values are unknown, not zero.</summary>
/// <param name="ObservedAt">When the statistics were read.</param>
/// <param name="LifetimeTokens">Tokens used over the account's lifetime.</param>
/// <param name="PeakDailyTokens">Most tokens used in one day.</param>
/// <param name="LongestRunningTurnSeconds">Duration of the longest turn.</param>
/// <param name="CurrentStreakDays">Consecutive days of use up to today.</param>
/// <param name="LongestStreakDays">Longest run of consecutive days of use.</param>
/// <param name="DailyUsageBuckets">Per-day token counts, if reported.</param>
public sealed record CodexAccountTokenActivity(DateTimeOffset ObservedAt, long? LifetimeTokens,
    long? PeakDailyTokens, long? LongestRunningTurnSeconds, long? CurrentStreakDays,
    long? LongestStreakDays, IReadOnlyList<CodexDailyTokenActivity>? DailyUsageBuckets);

/// <summary>Tokens used on one day.</summary>
/// <param name="StartDate">Day start as reported by the provider.</param>
/// <param name="Tokens">Tokens used that day.</param>
public sealed record CodexDailyTokenActivity(string StartDate, long Tokens);

/// <summary>Provider estimates in integer millionths. Missing values are unknown, not zero.
/// Native USD is distinct from estimated USD; neither is inferred from credits.</summary>
/// <param name="ThreadId">Provider thread ID.</param>
/// <param name="EstimatedUsageCreditsMicros">Estimated credits, in millionths.</param>
/// <param name="EstimatedUsageUsdMicros">Estimated US dollars, in millionths.</param>
/// <param name="NativeUsageUsdMicros">Provider-reported US dollars, in millionths.</param>
/// <param name="Groups">Usage split by model, reasoning effort and speed.</param>
public sealed record CodexThreadUsage(string ThreadId, long EstimatedUsageCreditsMicros,
    long? EstimatedUsageUsdMicros, long? NativeUsageUsdMicros, IReadOnlyList<CodexThreadUsageGroup> Groups);

/// <summary>Usage of one thread for one model configuration.</summary>
/// <param name="Model">Model name.</param>
/// <param name="ReasoningEffort">Reasoning effort.</param>
/// <param name="Speed">Service speed tier.</param>
/// <param name="EstimatedUsageCreditsMicros">Estimated credits, in millionths.</param>
/// <param name="NativeUsageUsdMicros">Provider-reported US dollars, in millionths.</param>
/// <param name="NetNewInputTokens">Input tokens not served from cache.</param>
/// <param name="CachedInputTokens">Input tokens served from cache.</param>
/// <param name="InputTokens">All input tokens.</param>
/// <param name="OutputTokens">Output tokens, including reasoning.</param>
/// <param name="TotalTokens">Input plus output tokens.</param>
public sealed record CodexThreadUsageGroup(string? Model, string? ReasoningEffort, string? Speed,
    long EstimatedUsageCreditsMicros, long? NativeUsageUsdMicros, long? NetNewInputTokens,
    long? CachedInputTokens, long? InputTokens, long? OutputTokens, long? TotalTokens);

/// <summary>Result of <see cref="CodexClient.GetThreadsUsageAsync"/>.</summary>
/// <param name="ObservedAt">When the usage was read.</param>
/// <param name="Threads">Threads the provider returned usage for.</param>
/// <param name="UnavailableThreadIds">Requested threads without available usage.</param>
public sealed record CodexThreadUsageSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<CodexThreadUsage> Threads,
    IReadOnlyList<string> UnavailableThreadIds);

public sealed partial class CodexClient
{
    /// <summary>One explicit account activity read. Shared account statistics are not per-worker billing.</summary>
    public async Task<CodexAccountTokenActivity> GetAccountTokenActivityAsync(CancellationToken cancellationToken = default)
    {
        using var document = await ReadProviderUsageAsync("profiles/me", null, cancellationToken).ConfigureAwait(false);
        var stats = RequiredUsageProperty(document.RootElement, "stats", JsonValueKind.Object);
        IReadOnlyList<CodexDailyTokenActivity>? buckets = null;
        if (stats.TryGetProperty("daily_usage_buckets", out var daily) && daily.ValueKind != JsonValueKind.Null)
        {
            if (daily.ValueKind != JsonValueKind.Array) throw InvalidUsage();
            buckets = daily.EnumerateArray().Select(item => new CodexDailyTokenActivity(
                (UsageText(item, "start_date", required: true) ?? throw InvalidUsage()), (UsageInteger(item, "tokens", required: true) ?? throw InvalidUsage()))).ToArray();
        }
        return new(DateTimeOffset.UtcNow, UsageInteger(stats, "lifetime_tokens"), UsageInteger(stats, "peak_daily_tokens"),
            UsageInteger(stats, "longest_running_turn_sec"), UsageInteger(stats, "current_streak_days"),
            UsageInteger(stats, "longest_streak_days"), buckets);
    }

    /// <summary>One explicit read for 1–100 distinct provider thread IDs. This never runs a model,
    /// retries an unavailable result, settles a local ledger or starts background polling.</summary>
    public async Task<CodexThreadUsageSnapshot> GetThreadsUsageAsync(IReadOnlyList<string> threadIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadIds);
        var ids = threadIds.ToArray();
        var requested = new HashSet<string>(ids, StringComparer.Ordinal);
        if (ids.Length is < 1 or > 100 || requested.Count != ids.Length || ids.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Expected 1–100 distinct provider thread IDs.", nameof(threadIds));
        var body = new JsonObject { ["thread_ids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
        using var document = await ReadProviderUsageAsync("usage/thread_usage/query", body, cancellationToken).ConfigureAwait(false);
        var rows = RequiredUsageProperty(document.RootElement, "threads", JsonValueKind.Array);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = new Dictionary<string, CodexThreadUsage>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var id = (UsageText(row, "thread_id", required: true) ?? throw InvalidUsage());
            // Validate every identity before discarding unavailable amounts, as pinned Rust does.
            if (!requested.Contains(id) || !seen.Add(id)) throw InvalidUsage();
            var credits = UsageInteger(row, "estimated_usage_credits_micros");
            var usd = UsageInteger(row, "estimated_usage_usd_micros");
            var nativeUsd = UsageInteger(row, "native_usage_usd_micros");
            var groups = new List<CodexThreadUsageGroup>();
            if (row.TryGetProperty("groups", out var values) && values.ValueKind != JsonValueKind.Null)
            {
                if (values.ValueKind != JsonValueKind.Array) throw InvalidUsage();
                foreach (var group in values.EnumerateArray())
                    groups.Add(new(UsageText(group, "model"), UsageText(group, "reasoning_effort"), UsageText(group, "speed"),
                        (UsageInteger(group, "estimated_usage_credits_micros", required: true) ?? throw InvalidUsage()),
                        UsageInteger(group, "native_usage_usd_micros"), UsageInteger(group, "net_new_input_tokens"),
                        UsageInteger(group, "cached_input_tokens"), UsageInteger(group, "input_tokens"),
                        UsageInteger(group, "output_tokens"), UsageInteger(group, "total_tokens")));
            }
            if (credits is { } amount) available.Add(id, new(id, amount, usd, nativeUsd, groups.ToArray()));
        }
        return new(DateTimeOffset.UtcNow, ids.Where(available.ContainsKey).Select(id => available[id]).ToArray(),
            ids.Where(id => !available.ContainsKey(id)).ToArray());
    }

    private async Task<JsonDocument> ReadProviderUsageAsync(string path, JsonObject? body, CancellationToken ct)
    {
        var auth = await credentials.GetCredentialsAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budget = body is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(60);
        deadline.CancelAfter(options.RequestTimeout < budget ? options.RequestTimeout : budget);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(new Uri(options.BackendBaseUri.AbsoluteUri.TrimEnd('/') + "/"), "../wham/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.Add("originator", options.Originator);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        var text = await ReadBoundedAsync(response.Content, options.MaximumMessageBytes, deadline.Token).ConfigureAwait(false);
        var active = await credentials.GetCredentialsAsync(deadline.Token).ConfigureAwait(false);
        if (!string.Equals(auth.AccountId, active.AccountId, StringComparison.Ordinal) ||
            !string.Equals(auth.ChatGptUserId, active.ChatGptUserId, StringComparison.Ordinal) ||
            auth.IsFedRampAccount != active.IsFedRampAccount)
            throw new CodexProtocolException("Active account changed during provider usage read; do not attribute this result.");
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw InvalidUsage(); }
    }

    private static JsonElement RequiredUsageProperty(JsonElement value, string key, JsonValueKind kind) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == kind
            ? item : throw InvalidUsage();
    private static long? UsageInteger(JsonElement value, string key, bool required = false)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidUsage();
        if (!value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null)
            return required ? throw InvalidUsage() : null;
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : throw InvalidUsage();
    }
    private static string? UsageText(JsonElement value, string key, bool required = false)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidUsage();
        if (!value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null)
            return required ? throw InvalidUsage() : null;
        return item.ValueKind == JsonValueKind.String ? item.GetString() : throw InvalidUsage();
    }
    private static CodexProtocolException InvalidUsage() => new("Invalid provider usage response; response body omitted.");
}
