using System.Globalization;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Provider counters only. A missing counter is unknown, not zero. No derived billing or context breakdown.</summary>
public sealed record CodexObservedTokens
{
    /// <summary>All reported input tokens.</summary>
    public long? InputTokens { get; init; }
    /// <summary>Reported cached input tokens.</summary>
    public long? CachedInputTokens { get; init; }
    /// <summary>Reported cache-write input tokens.</summary>
    public long? CacheWriteInputTokens { get; init; }
    /// <summary>Reported output tokens.</summary>
    public long? OutputTokens { get; init; }
    /// <summary>Reported reasoning output tokens.</summary>
    public long? ReasoningTokens { get; init; }
    /// <summary>Reported total tokens, not a sum inferred from other fields.</summary>
    public long? TotalTokens { get; init; }
    /// <summary>Provider-reported rollout budget units, distinct from tokens and credits.</summary>
    public decimal? RolloutBudgetUnits { get; init; }
}

/// <summary>Observed quota window. Used percentage can exceed 100; missing reset/duration remains unknown.</summary>
/// <param name="UsedPercent">Reported consumption percentage.</param>
/// <param name="WindowMinutes">Reported duration.</param>
/// <param name="ResetAtUnixSeconds">Reported Unix reset time.</param>
public sealed record CodexObservedQuotaWindow(decimal? UsedPercent, long? WindowMinutes, long? ResetAtUnixSeconds);

/// <summary>Observed quota bucket and credit fields, not proof of access or per-request billing.</summary>
public sealed record CodexObservedQuota
{
    /// <summary>Provider bucket identifier.</summary>
    public string? LimitId { get; init; }
    /// <summary>Primary quota window.</summary>
    public CodexObservedQuotaWindow? Primary { get; init; }
    /// <summary>Secondary quota window.</summary>
    public CodexObservedQuotaWindow? Secondary { get; init; }
    /// <summary>Provider explicitly reports credit availability.</summary>
    public bool? HasCredits { get; init; }
    /// <summary>Provider explicitly reports unlimited credits.</summary>
    public bool? UnlimitedCredits { get; init; }
    /// <summary>Provider-reported credit balance, if numeric.</summary>
    public decimal? CreditBalance { get; init; }
    /// <summary>Provider explicitly reports permission.</summary>
    public bool? Allowed { get; init; }
    /// <summary>Provider explicitly reports exhaustion.</summary>
    public bool? LimitReached { get; init; }
    /// <summary>Provider-reported plan identifier.</summary>
    public string? PlanType { get; init; }
}

/// <summary>Bounded accounting metadata correlated with an event. Retains no prompt, transcript, or credentials.
/// Raw events remain separately available on the original event objects.</summary>
public sealed record CodexObservedUsage
{
    /// <summary>Provider event type.</summary>
    public required string EventType { get; init; }
    /// <summary>Local observation time.</summary>
    public DateTimeOffset ObservedAt { get; init; }
    /// <summary>Caller-owned correlation, if available.</summary>
    public CodexResponsesIdentity? Identity { get; init; }
    /// <summary>Local reservation ID, not provider response ID.</summary>
    public string? RequestId { get; init; }
    /// <summary>Provider event identifier.</summary>
    public string? EventId { get; init; }
    /// <summary>Provider response identifier.</summary>
    public string? ResponseId { get; init; }
    /// <summary>Provider voice-session identifier, if explicitly present.</summary>
    public string? ProviderSessionId { get; init; }
    /// <summary>Explicit provider status.</summary>
    public string? Status { get; init; }
    /// <summary>Response or session token counters. Never aggregate session snapshots as deltas.</summary>
    public CodexObservedTokens? Tokens { get; init; }
    /// <summary>Last token usage from a Rust token-count event, when supplied.</summary>
    public CodexObservedTokens? LastTokens { get; init; }
    /// <summary>Provider-reported model context window size.</summary>
    public long? ModelContextWindow { get; init; }
    /// <summary>Explicit used context tokens, not a guess based on billing.</summary>
    public long? ContextUsedTokens { get; init; }
    /// <summary>Explicit remaining context tokens.</summary>
    public long? ContextRemainingTokens { get; init; }
    /// <summary>Whether the last context count includes retained reasoning, when supplied.</summary>
    public bool? LastUsageIncludesReasoning { get; init; }
    /// <summary>Explicit quota metadata.</summary>
    public CodexObservedQuota? Quota { get; init; }
    /// <summary>Bounded shape and numeric/boolean diagnostics for new accounting fields; raw event is separate.</summary>
    public CodexUsageSchemaSnapshot? Schema { get; init; }

    /// <summary>Read a realtime accounting event with an optional caller-owned backing identity.</summary>
    public static CodexObservedUsage? Read(CodexRealtimeEvent realtimeEvent, CodexResponsesIdentity? identity = null,
        DateTimeOffset? observedAt = null) => Read(realtimeEvent.Payload, identity, observedAt: observedAt);

    /// <summary>Read an accounting event from either native transport. Other events return null.</summary>
    public static CodexObservedUsage? Read(JsonElement payload, CodexResponsesIdentity? identity = null,
        string? requestId = null, DateTimeOffset? observedAt = null)
    {
        var type = Identifier(payload, "type");
        if (type is not ("response.completed" or "response.incomplete" or "response.failed" or "response.done" or
            "session.usage.updated" or "session.closed" or "codex.rate_limits" or "token_count")) return null;
        var at = observedAt ?? DateTimeOffset.UtcNow;
        var response = Property(payload, "response");
        var info = Property(payload, "info");
        var usage = FirstObject(Property(response, "usage"), Property(payload, "usage"), Property(info, "total_token_usage"));
        var context = FirstObject(Property(response, "context_window"), Property(payload, "context_window"));
        var limits = FirstObject(Property(payload, "rate_limits"), Property(payload, "usage_limit"),
            Property(response, "rate_limits"), Property(response, "usage_limit"));
        var credits = FirstObject(Property(payload, "credits"), Property(limits, "credits"));
        var quota = ReadQuota(payload, limits, credits);
        return new()
        {
            EventType = type,
            ObservedAt = at,
            Identity = identity,
            RequestId = requestId,
            EventId = Identifier(payload, "event_id"),
            ResponseId = Identifier(response, "id") ?? Identifier(payload, "response_id"),
            ProviderSessionId = Identifier(payload, "session_id") ?? Identifier(Property(payload, "session"), "id"),
            Status = Identifier(response, "status") ?? Identifier(payload, "status"),
            Tokens = ReadTokens(usage),
            LastTokens = ReadTokens(Property(info, "last_token_usage")),
            ModelContextWindow = Count(info, "model_context_window") ?? Count(response, "model_context_window") ??
                Count(payload, "model_context_window") ?? Count(context, "total_tokens"),
            ContextUsedTokens = Count(context, "used_tokens"),
            ContextRemainingTokens = Count(context, "remaining_tokens"),
            LastUsageIncludesReasoning = Boolean(info, "last_usage_includes_reasoning"),
            Quota = quota,
            Schema = CodexUsageSchema.ObserveAccounting(payload, type, at),
        };
    }

    internal static IReadOnlyList<CodexObservedQuota> ReadHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (!header.Key.StartsWith("x-codex-", StringComparison.OrdinalIgnoreCase) || header.Key.Length > 128 ||
                map.Count >= 128) continue;
            var values = header.Value.Take(2).ToArray();
            if (values.Length == 1 && values[0].Length <= 128) map.TryAdd(header.Key, values[0]);
        }
        string? Get(string key) => map.GetValueOrDefault(key);
        decimal? DecimalHeader(string key) => decimal.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        long? LongHeader(string key) => long.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
        bool? BoolHeader(string key) => bool.TryParse(Get(key), out var value) ? value : null;
        CodexObservedQuotaWindow? Window(string prefix)
        {
            var used = DecimalHeader(prefix + "-used-percent");
            var duration = LongHeader(prefix + "-window-minutes");
            var reset = LongHeader(prefix + "-reset-at");
            return used is null && duration is null && reset is null ? null : new(used, duration, reset);
        }
        var result = new List<CodexObservedQuota>();
        var ids = new HashSet<string>(StringComparer.Ordinal) { "codex" };
        foreach (var key in map.Keys)
        {
            var normalized = key.ToLowerInvariant();
            foreach (var suffix in new[] { "-primary-used-percent", "-primary-window-minutes", "-primary-reset-at",
                "-secondary-used-percent", "-secondary-window-minutes", "-secondary-reset-at" })
                if (normalized.EndsWith(suffix, StringComparison.Ordinal))
                    ids.Add(normalized[2..^suffix.Length]);
        }
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            var prefix = "x-" + id;
            var primary = Window(prefix + "-primary");
            var secondary = Window(prefix + "-secondary");
            var hasCredits = id == "codex" ? BoolHeader("x-codex-credits-has-credits") : null;
            var unlimited = id == "codex" ? BoolHeader("x-codex-credits-unlimited") : null;
            var balance = id == "codex" ? DecimalHeader("x-codex-credits-balance") : null;
            if (primary is null && secondary is null && hasCredits is null && unlimited is null && balance is null) continue;
            result.Add(new() { LimitId = id.Replace('-', '_'), Primary = primary, Secondary = secondary,
                HasCredits = hasCredits, UnlimitedCredits = unlimited, CreditBalance = balance });
        }
        return result;
    }

    private static CodexObservedQuota? ReadQuota(JsonElement payload, JsonElement limits, JsonElement credits)
    {
        if (limits.ValueKind != JsonValueKind.Object && credits.ValueKind != JsonValueKind.Object) return null;
        return new()
        {
            LimitId = Identifier(payload, "metered_limit_name") ?? Identifier(payload, "limit_name") ?? Identifier(limits, "limit_id"),
            Primary = ReadWindow(FirstObject(Property(limits, "primary"), Property(limits, "primary_window"))),
            Secondary = ReadWindow(FirstObject(Property(limits, "secondary"), Property(limits, "secondary_window"))),
            HasCredits = Boolean(credits, "has_credits"), UnlimitedCredits = Boolean(credits, "unlimited"),
            CreditBalance = Decimal(Property(credits, "balance")), Allowed = Boolean(limits, "allowed"),
            LimitReached = Boolean(limits, "limit_reached"),
            PlanType = Identifier(payload, "plan_type") ?? Identifier(limits, "plan_type"),
        };
    }

    private static CodexObservedQuotaWindow? ReadWindow(JsonElement window) => window.ValueKind != JsonValueKind.Object ? null :
        new(Decimal(Property(window, "used_percent")), Count(window, "window_minutes"),
            Count(window, "reset_at") ?? Count(window, "resets_at"));

    private static CodexObservedTokens? ReadTokens(JsonElement usage) => usage.ValueKind != JsonValueKind.Object ? null : new()
    {
        InputTokens = Count(usage, "input_tokens"),
        CachedInputTokens = Count(usage, "cached_input_tokens") ?? Count(Property(usage, "input_tokens_details"), "cached_tokens") ??
            Count(Property(usage, "input_token_details"), "cached_tokens"),
        CacheWriteInputTokens = Count(usage, "cache_write_input_tokens"),
        OutputTokens = Count(usage, "output_tokens"),
        ReasoningTokens = Count(usage, "reasoning_output_tokens") ?? Count(Property(usage, "output_tokens_details"), "reasoning_tokens") ??
            Count(Property(usage, "output_token_details"), "reasoning_tokens"),
        TotalTokens = Count(usage, "total_tokens"),
        RolloutBudgetUnits = Decimal(Property(usage, "codex_rollout_budget_units")),
    };

    private static JsonElement FirstObject(params JsonElement[] values) =>
        values.FirstOrDefault(value => value.ValueKind == JsonValueKind.Object);
    private static JsonElement Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;
    private static long? Count(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) && n >= 0 ? n : null;
    private static bool? Boolean(JsonElement parent, string name) => Property(parent, name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static decimal? Decimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number :
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 64 } text &&
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    private static string? Identifier(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.String } value &&
        value.GetString() is { Length: > 0 and <= 128 } text &&
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') ? text : null;
}
