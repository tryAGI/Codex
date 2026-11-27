using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Rust-compatible account rate limit window. Unknown duration and reset values remain null.</summary>
public sealed record CodexRpcRateLimitWindow(
    [property: JsonPropertyName("usedPercent")] int UsedPercent,
    [property: JsonPropertyName("windowDurationMins")] long? WindowDurationMins,
    [property: JsonPropertyName("resetsAt")] long? ResetsAt);

/// <summary>Provider-reported credit state and optional decimal balance.</summary>
public sealed record CodexRpcCreditsSnapshot(
    [property: JsonPropertyName("hasCredits")] bool HasCredits,
    [property: JsonPropertyName("unlimited")] bool Unlimited,
    [property: JsonPropertyName("balance")] string? Balance);

/// <summary>Account rate limit snapshot. Unobserved upstream fields are not inferred.</summary>
public sealed record CodexRpcRateLimitSnapshot(
    [property: JsonPropertyName("limitId")] string? LimitId,
    [property: JsonPropertyName("limitName")] string? LimitName,
    [property: JsonPropertyName("primary")] CodexRpcRateLimitWindow? Primary,
    [property: JsonPropertyName("secondary")] CodexRpcRateLimitWindow? Secondary,
    [property: JsonPropertyName("credits")] CodexRpcCreditsSnapshot? Credits,
    [property: JsonPropertyName("planType")] string? PlanType)
{
    /// <summary>The model whose metadata describes this metered alias, when observed.</summary>
    [JsonPropertyName("normalModelSlug")] public string? NormalModelSlug { get; init; }
    /// <summary>Backend spend-control limit, when observed.</summary>
    [JsonPropertyName("individualLimit")] public JsonElement? IndividualLimit { get; init; }
    /// <summary>Backend-reported spend-control status, when observed.</summary>
    [JsonPropertyName("spendControlReached")] public bool? SpendControlReached { get; init; }
    /// <summary>Backend-specific reason for a reached limit, when observed.</summary>
    [JsonPropertyName("rateLimitReachedType")] public string? RateLimitReachedType { get; init; }
}

/// <summary>Native projection for account/rateLimits/read. Does not expose an unverified account's usage.</summary>
public sealed record CodexRpcAccountRateLimits(
    [property: JsonPropertyName("ordinaryUsageAllowed")] bool? OrdinaryUsageAllowed,
    [property: JsonPropertyName("rateLimits")] CodexRpcRateLimitSnapshot RateLimits,
    [property: JsonPropertyName("rateLimitsByLimitId")] IReadOnlyDictionary<string, CodexRpcRateLimitSnapshot> RateLimitsByLimitId)
{
    /// <summary>Reset-credit information, when supplied.</summary>
    [JsonPropertyName("rateLimitResetCredits")] public JsonElement? RateLimitResetCredits { get; init; }
    /// <summary>Verified account identifier, when explicitly available.</summary>
    [JsonPropertyName("accountId")] public string? AccountId { get; init; }
    /// <summary>Backend-owned account banner, when supplied.</summary>
    [JsonPropertyName("rateLimitUpsell")] public JsonElement? RateLimitUpsell { get; init; }

    /// <summary>Projects an identity-validated usage observation without inventing missing fields.</summary>
    public static CodexRpcAccountRateLimits FromVerifiedUsage(CodexAccountUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.IdentityValidation != "matched")
            throw new CodexProtocolException("Account rate limits require a verified active account.");
        CodexRpcCreditsSnapshot? credits = usage.CreditState is { HasCredits: bool hasCredits, Unlimited: bool unlimited }
            ? new(hasCredits, unlimited, usage.Credits?.ToString(CultureInfo.InvariantCulture)) : null;

        var limits = new Dictionary<string, CodexRpcRateLimitSnapshot>(StringComparer.Ordinal);
        foreach (var (id, limit) in usage.RateLimitsByLimitId)
        {
            if (id != limit.LimitId || string.IsNullOrWhiteSpace(id))
                throw new CodexProtocolException("Invalid account rate limit identity.");
            limits.Add(id, new(id, limit.LimitName, Window(limit.Windows, "primary_window"),
                Window(limit.Windows, "secondary_window"), id == "codex" ? credits : null, usage.Plan));
        }
        if (!limits.TryGetValue("codex", out var ordinary))
        {
            ordinary = new("codex", null, Window(usage.Windows, "primary_window"),
                Window(usage.Windows, "secondary_window"), credits, usage.Plan);
            limits.Add("codex", ordinary);
        }
        // Reset times and percentages do not authorize ordinary included usage.
        return new(usage.OrdinaryUsageAllowed, ordinary, new System.Collections.ObjectModel.ReadOnlyDictionary<string, CodexRpcRateLimitSnapshot>(limits));
    }

    private static CodexRpcRateLimitWindow? Window(IReadOnlyList<CodexAccountUsageWindow> windows, string name)
    {
        var value = windows.FirstOrDefault(window => window.Name == name);
        if (value is null || !double.IsFinite(value.UsedPercent) || value.UsedPercent is < 0 or > 100)
            return null;
        return new((int)Math.Round(value.UsedPercent, MidpointRounding.AwayFromZero),
            value.DurationSeconds > 0 && value.DurationSeconds % 60 == 0 ? value.DurationSeconds / 60 : null,
            value.ResetAt.ToUnixTimeSeconds());
    }
}

public sealed partial class CodexClient
{
    /// <summary>Reads the Rust account/rateLimits/read projection through the existing OAuth usage endpoint.
    /// A custom endpoint cannot receive ChatGPT account credentials through this adapter.</summary>
    public async Task<CodexRpcAccountRateLimits> GetVerifiedAccountRateLimitsAsync(CancellationToken cancellationToken = default)
    {
        var expected = new Uri("https://chatgpt.com/backend-api/codex/");
        if (!string.Equals(options.BackendBaseUri.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal))
            throw new CodexProviderRouteException(CodexProviderRouteFailure.DestinationDenied);
        return CodexRpcAccountRateLimits.FromVerifiedUsage(await GetAccountUsageAsync(cancellationToken).ConfigureAwait(false));
    }
}
