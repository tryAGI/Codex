using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace tryAGI.Codex;

/// <summary>The ChatGPT plan's Codex allowance is used up (<c>usage_limit_reached</c>). This is a normal account
/// state that clears by itself: do not retry it with reconnect backoff, and do not fall back to API-key billing.
/// Wait with <see cref="GetRecheckDelay"/>, then try again or re-read <see cref="CodexClient.GetAccountUsageAsync"/>.</summary>
public sealed partial class CodexUsageLimitException : CodexProtocolException
{
    /// <summary>The provider error type for an exhausted allowance.</summary>
    public const string ErrorType = "usage_limit_reached";

    /// <summary>The App Server <c>codexErrorInfo</c> value for an exhausted allowance.</summary>
    public const string AppServerErrorInfo = "usageLimitExceeded";

    private static readonly TimeSpan DefaultMinimumRecheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultMaximumRecheck = TimeSpan.FromMinutes(15);

    internal CodexUsageLimitException(int? statusCode, string? planType, DateTimeOffset? resetsAt, TimeSpan? limitWindow, string? limitId)
        : base(Describe(planType, resetsAt), statusCode)
    {
        PlanType = planType; ResetsAt = resetsAt; LimitWindow = limitWindow; LimitId = limitId;
    }

    /// <summary>The ChatGPT plan the server named, for example <c>plus</c> or <c>pro</c>.</summary>
    public string? PlanType { get; }

    /// <summary>When the server expects the allowance to return. A hint only: it can return earlier,
    /// and a time in the past does not mean the limit is gone.</summary>
    public DateTimeOffset? ResetsAt { get; }

    /// <summary>Length of the limit window the server reported, if any.</summary>
    public TimeSpan? LimitWindow { get; }

    /// <summary>The active limit's identifier (<c>x-codex-active-limit</c>), if any.</summary>
    public string? LimitId { get; }

    /// <summary>How long to wait before checking again. The reset hint is clamped to
    /// [<paramref name="minimum"/>, <paramref name="maximum"/>] (default one to fifteen minutes), so an
    /// allowance that returns early is noticed and a past or missing hint never causes a tight retry loop.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="minimum">Shortest wait; also used when there is no future reset hint.</param>
    /// <param name="maximum">Longest wait between checks.</param>
    public TimeSpan GetRecheckDelay(DateTimeOffset now, TimeSpan? minimum = null, TimeSpan? maximum = null)
    {
        var low = minimum ?? DefaultMinimumRecheck;
        var high = maximum ?? DefaultMaximumRecheck;
        if (low <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (high < low) throw new ArgumentOutOfRangeException(nameof(maximum));
        return ResetsAt is { } reset && reset > now ? TimeSpan.FromTicks(Math.Clamp((reset - now).Ticks, low.Ticks, high.Ticks)) : low;
    }

    /// <summary>Whether an App Server <c>TurnError</c> (for example <c>turn.error</c> of <c>turn/completed</c>)
    /// reports an exhausted allowance. The App Server sends no reset time; use <see cref="GetRecheckDelay"/>
    /// of an exception you construct, or a fixed bounded interval.</summary>
    public static bool IsAppServerUsageLimit(JsonElement turnError) =>
        turnError.ValueKind == JsonValueKind.Object &&
        turnError.TryGetProperty("codexErrorInfo", out var info) && info.ValueKind == JsonValueKind.String &&
        info.ValueEquals(AppServerErrorInfo);

    /// <summary>Reads an HTTP error body. Null unless it is HTTP 429 with <c>error.type</c> <see cref="ErrorType"/>;
    /// only bounded, identifier-shaped values are kept, never provider text.</summary>
    internal static CodexUsageLimitException? TryRead(int statusCode, JsonElement error, string? limitHeader)
    {
        if (statusCode != 429 || !error.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !type.ValueEquals(ErrorType))
            return null;
        string? plan = error.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { } s && Identifier().IsMatch(s) ? s : null;
        DateTimeOffset? resets = null;
        if (error.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var seconds) &&
            seconds is > 0 and < 253402300800) resets = DateTimeOffset.FromUnixTimeSeconds(seconds);
        TimeSpan? window = error.TryGetProperty("limit_window_minutes", out var w) && w.ValueKind == JsonValueKind.Number &&
            w.TryGetInt32(out var minutes) && minutes is > 0 and <= ushort.MaxValue ? TimeSpan.FromMinutes(minutes) : null;
        var limit = limitHeader is not null && Identifier().IsMatch(limitHeader) ? limitHeader : null;
        return new(statusCode, plan, resets, window, limit);
    }

    private static string Describe(string? planType, DateTimeOffset? resetsAt) =>
        "Codex usage limit reached" + (planType is null ? "" : $" for the {planType} plan") +
        (resetsAt is { } reset ? "; the server expects it to reset at " + reset.ToString("u", CultureInfo.InvariantCulture) : "") +
        ". Wait and check again; provider text is omitted.";

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex Identifier();
}
