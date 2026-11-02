using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>The signed-in account's shared Codex rate limits and credits.</summary>
/// <param name="ObservedAt">When the usage was read.</param>
/// <param name="Plan">ChatGPT plan type.</param>
/// <param name="Allowed">The provider currently allows Codex use.</param>
/// <param name="Credits">Remaining credit balance, if the account has credits.</param>
/// <param name="Windows">Rate-limit windows of ordinary Codex use (for example the 5-hour and weekly windows).</param>
public sealed record CodexAccountUsage(DateTimeOffset ObservedAt, string? Plan, bool? Allowed,
    decimal? Credits, IReadOnlyList<CodexAccountUsageWindow> Windows)
{
    /// <summary>Independent metered buckets; legacy Windows/Allowed remain the ordinary Codex view.</summary>
    public IReadOnlyDictionary<string, CodexAccountUsageLimit> RateLimitsByLimitId { get; init; } =
        new Dictionary<string, CodexAccountUsageLimit>(StringComparer.Ordinal);

    /// <summary>Credit availability, if reported.</summary>
    public CodexAccountCreditState? CreditState { get; init; }

    /// <summary>Malformed or conflicting additional buckets were not silently merged with ordinary usage.</summary>
    public int RejectedAdditionalLimits { get; init; }

    /// <summary>Included-usage permission validated against active account/user after the HTTP read.</summary>
    public bool? OrdinaryUsageAllowed { get; init; }

    /// <summary>
    /// Outcome of checking that the login did not change during the read: <c>matched</c>, <c>auth_unavailable</c>,
    /// or another value meaning the result may belong to a different account.
    /// </summary>
    public string? IdentityValidation { get; init; }
}

/// <summary>One rate-limit window.</summary>
/// <param name="Name">Window name, for example <c>primary</c> or <c>secondary</c>.</param>
/// <param name="UsedPercent">Share of the window used, 0–100.</param>
/// <param name="DurationSeconds">Window length.</param>
/// <param name="ResetAt">When the window resets.</param>
public sealed record CodexAccountUsageWindow(string Name, double UsedPercent, int DurationSeconds, DateTimeOffset ResetAt)
{
    /// <summary>Seconds until reset, as reported by the provider.</summary>
    public int? ResetAfterSeconds { get; init; }
}

/// <summary>An additional metered bucket with its own limits.</summary>
/// <param name="LimitId">Provider bucket ID.</param>
/// <param name="LimitName">Display name.</param>
/// <param name="Allowed">Use of this bucket is currently allowed.</param>
/// <param name="LimitReached">The bucket is exhausted.</param>
/// <param name="Windows">Rate-limit windows of this bucket.</param>
public sealed record CodexAccountUsageLimit(string LimitId, string? LimitName, bool? Allowed,
    bool? LimitReached, IReadOnlyList<CodexAccountUsageWindow> Windows);

/// <summary>Credit availability.</summary>
/// <param name="HasCredits">The account has a positive credit balance.</param>
/// <param name="Unlimited">Credits are not limited.</param>
public sealed record CodexAccountCreditState(bool? HasCredits, bool? Unlimited);

public sealed partial class CodexClient
{
    /// <summary>Read the current OAuth account's shared quota, not per-session billing.
    /// Never returns email, account identifiers, cookies or credentials.</summary>
    public async Task<CodexAccountUsage> GetAccountUsageAsync(CancellationToken cancellationToken = default)
    {
        var auth = await credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(options.BackendBaseUri.AbsoluteUri.TrimEnd('/') + "/"), "../wham/usage"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.Add("originator", options.Originator);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, options.MaximumMessageBytes, deadline.Token).ConfigureAwait(false));
        // Active login may change while the quota request is in flight.
        var usage = ParseAccountUsage(json.RootElement, DateTimeOffset.UtcNow);
        CodexCredentials active;
        try { active = await credentials.GetCredentialsAsync(deadline.Token).ConfigureAwait(false); }
        catch (CodexAuthorizationUnavailableException)
        { return usage with { IdentityValidation = "auth_unavailable", OrdinaryUsageAllowed = null }; }
        var account = CodexRealtimeProtocol.Text(json.RootElement, "account_id");
        var user = CodexRealtimeProtocol.Text(json.RootElement, "user_id");
        var validation = active.IsFedRampAccount ? "restricted" :
            string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(active.ChatGptUserId) ? "missing_identity" :
            string.Equals(account, active.AccountId, StringComparison.Ordinal) &&
            string.Equals(user, active.ChatGptUserId, StringComparison.Ordinal) ? "matched" : "mismatch";
        return usage with { IdentityValidation = validation, OrdinaryUsageAllowed = validation == "matched" ? usage.Allowed : null };
    }

    internal static CodexAccountUsage ParseAccountUsage(JsonElement root, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Account usage must be an object.");
        IReadOnlyList<CodexAccountUsageWindow> windows = [];
        bool? allowed = null;
        bool? reached = null;
        if (root.TryGetProperty("rate_limit", out var rate) && rate.ValueKind == JsonValueKind.Object)
        {
            allowed = ReadUsageBoolean(rate, "allowed");
            reached = ReadUsageBoolean(rate, "limit_reached");
            windows = ReadUsageWindows(rate);
        }
        var buckets = new Dictionary<string, CodexAccountUsageLimit>(StringComparer.Ordinal)
        { ["codex"] = new("codex", null, allowed, reached, windows) };
        var rejected = 0;
        if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind != JsonValueKind.Null)
        {
            if (additional.ValueKind != JsonValueKind.Array) rejected++;
            else foreach (var item in additional.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { rejected++; continue; }
                var id = CodexRealtimeProtocol.Text(item, "metered_feature");
                var name = CodexRealtimeProtocol.Text(item, "limit_name");
                if (string.IsNullOrWhiteSpace(id) || name is null || buckets.ContainsKey(id)) { rejected++; continue; }
                var details = item.TryGetProperty("rate_limit", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : default;
                buckets.Add(id, new(id, name, ReadUsageBoolean(details, "allowed"),
                    ReadUsageBoolean(details, "limit_reached"), ReadUsageWindows(details)));
            }
        }
        decimal? balance = null;
        CodexAccountCreditState? creditState = null;
        if (root.TryGetProperty("credits", out var creditFlags) && creditFlags.ValueKind == JsonValueKind.Object)
            creditState = new(ReadUsageBoolean(creditFlags, "has_credits"), ReadUsageBoolean(creditFlags, "unlimited"));
        if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object &&
            credits.TryGetProperty("balance", out var value))
        {
            if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var parsed) && parsed >= 0) balance = parsed;
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out parsed) && parsed >= 0) balance = parsed;
        }
        return new(observedAt, CodexRealtimeProtocol.Text(root, "plan_type"), allowed, balance, windows)
        { RateLimitsByLimitId = buckets, CreditState = creditState, RejectedAdditionalLimits = rejected };
    }

    private static bool? ReadUsageBoolean(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var flag) &&
        flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;

    private static IReadOnlyList<CodexAccountUsageWindow> ReadUsageWindows(JsonElement rate)
    {
        var windows = new List<CodexAccountUsageWindow>();
        if (rate.ValueKind != JsonValueKind.Object) return windows;
        foreach (var name in new[] { "primary_window", "secondary_window" })
        {
            if (!rate.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("used_percent", out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used) ||
                !double.IsFinite(used) || used is < 0 or > 100 ||
                !window.TryGetProperty("limit_window_seconds", out var seconds) || seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out var duration) || duration <= 0 ||
                !window.TryGetProperty("reset_at", out var reset) || reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var epoch) || epoch is < 0 or > 253402300799) continue;
            int? resetAfter = window.TryGetProperty("reset_after_seconds", out var after) && after.ValueKind == JsonValueKind.Number &&
                after.TryGetInt32(out var value) && value >= 0 ? value : null;
            windows.Add(new(name, used, duration, DateTimeOffset.FromUnixTimeSeconds(epoch)) { ResetAfterSeconds = resetAfter });
        }
        return windows;
    }
}
