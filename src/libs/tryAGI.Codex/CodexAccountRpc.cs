using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Provider-owned account type; independent from how credentials are stored.</summary>
public enum CodexAccountKind
{
    /// <summary>No authenticated account.</summary>
    None,
    /// <summary>OpenAI API key.</summary>
    ApiKey,
    /// <summary>ChatGPT subscription.</summary>
    ChatGpt,
    /// <summary>Amazon Bedrock.</summary>
    AmazonBedrock,
    /// <summary>Google Cloud.</summary>
    Gcp,
}

/// <summary>Host-verified non-secret account metadata. SelectionEpoch must change on every login, logout,
/// or account switch, even if the user later switches back to the same account. Token rotation alone need not change it.</summary>
public sealed record CodexAccountSelection(
    [property: JsonIgnore] string SelectionEpoch,
    CodexAccountKind Kind,
    string? AuthMode,
    bool RequiresOpenAiAuth,
    [property: JsonIgnore] string? AccountId = null,
    string? UserId = null,
    string? Email = null,
    string? PlanType = null,
    string? WorkspaceName = null,
    bool UsesCodexManagedCredentials = false,
    CodexAccountWorkspaceRouting? WorkspaceRouting = null)
{
    /// <inheritdoc />
    public override string ToString() => "CodexAccountSelection [redacted]";
}

/// <summary>Verified backend routing metadata; not an authorization to connect to a new origin.</summary>
public sealed record CodexAccountWorkspaceRouting(string ChatGptAccountId, string BackendOrigin, string AccountRoutingOverride);

/// <summary>One observation correlated to the account selection that actually authorized its read.</summary>
public sealed record CodexAccountObservation<T>(
    [property: JsonIgnore] string SelectionEpoch,
    [property: JsonIgnore] string? AccountId,
    [property: JsonIgnore] string? UserId,
    [property: JsonIgnore] T Value)
{
    /// <inheritdoc />
    public override string ToString() => "CodexAccountObservation [redacted]";
}

/// <summary>Upstream account-wide token activity. Unknown fields remain null, not zero.</summary>
public sealed record CodexAccountTokenUsageSummary(long? LifetimeTokens, long? PeakDailyTokens,
    long? LongestRunningTurnSec, long? CurrentStreakDays, long? LongestStreakDays);

/// <summary>Daily account token activity.</summary>
public sealed record CodexAccountTokenUsageDailyBucket(string StartDate, long Tokens);

/// <summary>Native thread billing breakdown. Native dollar values are not derived from credit estimates.</summary>
public sealed record CodexAccountThreadUsageGroup(string? Model, string? ReasoningEffort, string? Speed,
    long EstimatedUsageCreditsMicros, long? NetNewInputTokens, long? CachedInputTokens, long? InputTokens,
    long? OutputTokens, long? TotalTokens);

/// <summary>Host-authorized thread billing data.</summary>
public sealed record CodexAccountThreadUsage(string ThreadId, long EstimatedUsageCreditsMicros,
    long? EstimatedUsageUsdMicros, long? NativeUsageUsdMicros, IReadOnlyList<CodexAccountThreadUsageGroup> Groups);

/// <summary>Account-wide token activity or explicitly selected thread estimates, not quota windows.</summary>
public sealed record CodexAccountTokenUsage(CodexAccountTokenUsageSummary Summary,
    IReadOnlyList<CodexAccountTokenUsageDailyBucket>? DailyUsageBuckets, CodexAccountThreadUsage? ThreadUsage = null);

/// <summary>Native App Server account/read, account/rateLimits/read and account/usage/read.
/// Account selection, credential refresh, network and billing access remain under authenticated host policy.</summary>
public sealed class CodexAccountRpc
{
    private readonly Func<bool, CancellationToken, Task<CodexAccountSelection>> readSelection;
    private readonly Func<CodexAccountSelection, CancellationToken, Task<CodexAccountObservation<CodexRpcAccountRateLimits>>>? readRateLimits;
    private readonly Func<CodexAccountSelection, string?, CancellationToken, Task<CodexAccountObservation<CodexAccountTokenUsage>>>? readUsage;
    private readonly Func<CodexAccountSelection, string, JsonElement, CancellationToken, Task>? notify;

    /// <summary>Registers account-bound host adapters. Usage adapters must independently verify ownership and return the observed selection stamp.
    /// The notification adapter must fence actual delivery against the captured selection, atomically with host account switching.</summary>
    public CodexAccountRpc(Func<bool, CancellationToken, Task<CodexAccountSelection>> readSelection,
        Func<CodexAccountSelection, CancellationToken, Task<CodexAccountObservation<CodexRpcAccountRateLimits>>>? readRateLimits = null,
        Func<CodexAccountSelection, string?, CancellationToken, Task<CodexAccountObservation<CodexAccountTokenUsage>>>? readUsage = null,
        Func<CodexAccountSelection, string, JsonElement, CancellationToken, Task>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(readSelection);
        this.readSelection = readSelection;
        this.readRateLimits = readRateLimits;
        this.readUsage = readUsage;
        this.notify = notify;
    }

    /// <summary>Whether this facade implements the given native read RPC.</summary>
    public static bool Handles(string method) => method is "account/read" or "account/rateLimits/read" or "account/usage/read";

    /// <summary>Returns only metadata belonging to the active account selection. No implicit login or provider fallback.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported account RPC.");
        var p = parameters is { ValueKind: not JsonValueKind.Null } supplied ? supplied : Element(new JsonObject());
        Fields(p, method switch { "account/read" => "refreshToken", "account/usage/read" => "threadId", _ => null });
        var refresh = false;
        if (method == "account/read" && p.TryGetProperty("refreshToken", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("refreshToken must be a boolean.");
            refresh = flag.GetBoolean();
        }
        string? threadId = null;
        if (method == "account/usage/read" && p.TryGetProperty("threadId", out var thread) && thread.ValueKind != JsonValueKind.Null)
        {
            if (thread.ValueKind != JsonValueKind.String || !Bounded(thread.GetString())) throw Invalid("Invalid thread ID.");
            threadId = thread.GetString();
        }

        var selection = await CurrentAsync(false, cancellationToken).ConfigureAwait(false);
        if (method == "account/read")
        {
            // The pinned upstream contract ignores forced refresh in externally managed token mode.
            if (refresh && selection.AuthMode == "chatgpt")
            {
                var refreshed = await CurrentAsync(true, cancellationToken).ConfigureAwait(false);
                SameSelection(selection, refreshed);
                selection = refreshed;
            }
            return Element(Account(selection));
        }
        if (selection.Kind != CodexAccountKind.ChatGpt || !Bounded(selection.AccountId) || !Bounded(selection.UserId))
            throw new CodexAuthorizationUnavailableException();
        if (method == "account/rateLimits/read")
        {
            if (readRateLimits is null) throw Unavailable();
            var observed = await readRateLimits(selection, cancellationToken).ConfigureAwait(false);
            await VerifyObservationAsync(selection, observed, cancellationToken).ConfigureAwait(false);
            return Element(RateLimits(observed.Value, selection.AccountId!));
        }

        if (readUsage is null) throw Unavailable();
        var usage = await readUsage(selection, threadId, cancellationToken).ConfigureAwait(false);
        await VerifyObservationAsync(selection, usage, cancellationToken).ConfigureAwait(false);
        if (usage.Value.ThreadUsage is { } threadUsage && threadUsage.ThreadId != threadId)
            throw new CodexProtocolException("Account usage thread identity mismatch.");
        return Element(Usage(usage.Value));
    }

    /// <summary>Publishes an explicit verified account change. No token, email, account ID or selection epoch is sent.</summary>
    public async Task NotifyAccountUpdatedAsync(CodexAccountSelection captured, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (notify is null) throw Unavailable();
        var current = await CurrentAsync(false, cancellationToken).ConfigureAwait(false);
        SameSelection(captured, current);
        if (reason is not (null or "workspaceRoutingReady" or "unknown")) throw Invalid("Unknown account update reason.");
        if (reason == "workspaceRoutingReady" && current.WorkspaceRouting is null) throw Invalid("Workspace routing is not ready.");
        await notify(current, "account/updated", Element(new JsonObject
        { ["authMode"] = current.AuthMode, ["planType"] = current.PlanType, ["reason"] = reason }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes only the explicitly observed bucket. Null fields do not indicate recovery or clear other buckets.</summary>
    public async Task NotifyRateLimitsUpdatedAsync(CodexAccountSelection captured,
        CodexAccountObservation<CodexRpcAccountRateLimits> observation, string limitId, CancellationToken cancellationToken = default)
    {
        if (notify is null) throw Unavailable();
        await VerifyObservationAsync(captured, observation, cancellationToken).ConfigureAwait(false);
        var rates = observation.Value;
        if (rates.AccountId is { } account && account != captured.AccountId)
            throw new CodexProtocolException("Account usage identity mismatch.");
        if (!rates.RateLimitsByLimitId.TryGetValue(limitId, out var bucket) || bucket.LimitId != limitId)
            throw Invalid("No observed rate limit bucket.");
        await notify(captured, "account/rateLimits/updated",
            Element(new JsonObject { ["rateLimits"] = RateLimit(bucket) }), cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodexAccountSelection> CurrentAsync(bool refresh, CancellationToken ct)
    {
        var current = await readSelection(refresh, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!Bounded(current.SelectionEpoch) || !Enum.IsDefined(current.Kind) ||
            current.Kind == CodexAccountKind.None && current.AuthMode is not null ||
            current.Kind == CodexAccountKind.ChatGpt && current.AuthMode is not ("chatgpt" or "chatgptAuthTokens" or "personalAccessToken"))
            throw new CodexProtocolException("Invalid account selection.");
        if (current.WorkspaceRouting is { } routing &&
            (current.Kind != CodexAccountKind.ChatGpt || routing.ChatGptAccountId != current.AccountId ||
            !Uri.TryCreate(routing.BackendOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            routing.AccountRoutingOverride is not ("NO_CONSTRAINT" or "us" or "us_cr")))
            throw new CodexProtocolException("Invalid workspace routing metadata.");
        return current;
    }

    private async Task VerifyObservationAsync<T>(CodexAccountSelection captured, CodexAccountObservation<T> observed, CancellationToken ct)
    {
        if (captured.Kind != CodexAccountKind.ChatGpt || !Bounded(captured.AccountId) || !Bounded(captured.UserId) ||
            captured.SelectionEpoch != observed.SelectionEpoch || captured.AccountId != observed.AccountId ||
            captured.UserId != observed.UserId)
            throw new CodexProtocolException("Account observation identity mismatch.");
        SameSelection(captured, await CurrentAsync(false, ct).ConfigureAwait(false));
    }

    private static void SameSelection(CodexAccountSelection first, CodexAccountSelection current)
    {
        if (first.SelectionEpoch != current.SelectionEpoch || first.Kind != current.Kind ||
            first.AuthMode != current.AuthMode || first.AccountId != current.AccountId || first.UserId != current.UserId)
            throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);
    }

    private static JsonObject Account(CodexAccountSelection current)
    {
        JsonObject? account = current.Kind switch
        {
            CodexAccountKind.None => null,
            CodexAccountKind.ApiKey => new() { ["type"] = "apiKey" },
            CodexAccountKind.ChatGpt => new() { ["type"] = "chatgpt", ["email"] = current.Email,
                ["planType"] = current.PlanType ?? "unknown", ["workspaceName"] = current.WorkspaceName },
            CodexAccountKind.AmazonBedrock => new() { ["type"] = "amazonBedrock", ["usesCodexManagedCredentials"] = current.UsesCodexManagedCredentials },
            CodexAccountKind.Gcp => new() { ["type"] = "gcp" },
            _ => throw new CodexProtocolException("Invalid account kind.")
        };
        return new() { ["account"] = account, ["userId"] = current.Kind == CodexAccountKind.ChatGpt ? current.UserId : null,
            ["requiresOpenaiAuth"] = current.RequiresOpenAiAuth,
            ["workspaceRouting"] = current.WorkspaceRouting is { } routing ? new JsonObject
            { ["chatgptAccountId"] = routing.ChatGptAccountId, ["backendOrigin"] = routing.BackendOrigin,
                ["accountRoutingOverride"] = routing.AccountRoutingOverride } : null };
    }

    private static JsonObject RateLimits(CodexRpcAccountRateLimits rates, string accountId)
    {
        if (rates.AccountId is { } observed && observed != accountId)
            throw new CodexProtocolException("Account rate limit identity mismatch.");
        var buckets = new JsonObject();
        foreach (var (id, bucket) in rates.RateLimitsByLimitId)
        {
            if (id != bucket.LimitId || !Bounded(id)) throw new CodexProtocolException("Rate limit bucket identity mismatch.");
            buckets[id] = RateLimit(bucket);
        }
        return new() { ["ordinaryUsageAllowed"] = rates.OrdinaryUsageAllowed, ["rateLimits"] = RateLimit(rates.RateLimits),
            ["rateLimitsByLimitId"] = buckets, ["accountId"] = rates.AccountId,
            ["rateLimitResetCredits"] = Raw(rates.RateLimitResetCredits), ["rateLimitUpsell"] = Raw(rates.RateLimitUpsell) };
    }

    private static JsonObject RateLimit(CodexRpcRateLimitSnapshot rate) => new()
    {
        ["limitId"] = rate.LimitId, ["limitName"] = rate.LimitName, ["normalModelSlug"] = rate.NormalModelSlug,
        ["primary"] = Window(rate.Primary), ["secondary"] = Window(rate.Secondary),
        ["credits"] = rate.Credits is { } credits ? new JsonObject { ["hasCredits"] = credits.HasCredits, ["unlimited"] = credits.Unlimited, ["balance"] = credits.Balance } : null,
        ["individualLimit"] = Raw(rate.IndividualLimit), ["spendControlReached"] = rate.SpendControlReached,
        ["planType"] = rate.PlanType, ["rateLimitReachedType"] = rate.RateLimitReachedType
    };

    private static JsonObject? Window(CodexRpcRateLimitWindow? window) => window is null ? null :
        new() { ["usedPercent"] = window.UsedPercent, ["windowDurationMins"] = window.WindowDurationMins, ["resetsAt"] = window.ResetsAt };

    private static JsonObject Usage(CodexAccountTokenUsage usage)
    {
        var daily = usage.DailyUsageBuckets is null ? null : new JsonArray(usage.DailyUsageBuckets.Select(bucket => (JsonNode)new JsonObject
        { ["startDate"] = bucket.StartDate, ["tokens"] = bucket.Tokens }).ToArray());
        var summary = usage.Summary;
        var thread = usage.ThreadUsage;
        return new()
        {
            ["summary"] = new JsonObject { ["lifetimeTokens"] = summary.LifetimeTokens, ["peakDailyTokens"] = summary.PeakDailyTokens,
                ["longestRunningTurnSec"] = summary.LongestRunningTurnSec, ["currentStreakDays"] = summary.CurrentStreakDays,
                ["longestStreakDays"] = summary.LongestStreakDays },
            ["dailyUsageBuckets"] = daily,
            ["threadUsage"] = thread is null ? null : new JsonObject
            {
                ["threadId"] = thread.ThreadId, ["estimatedUsageCreditsMicros"] = thread.EstimatedUsageCreditsMicros,
                ["estimatedUsageUsdMicros"] = thread.EstimatedUsageUsdMicros, ["nativeUsageUsdMicros"] = thread.NativeUsageUsdMicros,
                ["groups"] = new JsonArray(thread.Groups.Select(group => (JsonNode)new JsonObject
                {
                    ["model"] = group.Model, ["reasoningEffort"] = group.ReasoningEffort, ["speed"] = group.Speed,
                    ["estimatedUsageCreditsMicros"] = group.EstimatedUsageCreditsMicros, ["netNewInputTokens"] = group.NetNewInputTokens,
                    ["cachedInputTokens"] = group.CachedInputTokens, ["inputTokens"] = group.InputTokens,
                    ["outputTokens"] = group.OutputTokens, ["totalTokens"] = group.TotalTokens
                }).ToArray())
            }
        };
    }

    private static JsonNode? Raw(JsonElement? value) => value is { ValueKind: not JsonValueKind.Undefined } raw ? JsonNode.Parse(raw.GetRawText()) : null;
    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    private static void Fields(JsonElement p, string? allowed)
    {
        if (p.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(p.GetRawText()) > 8192) throw Invalid("Invalid account parameters.");
        var count = 0;
        foreach (var field in p.EnumerateObject())
            if (++count > 1 || field.Name != allowed) throw Invalid("Unknown or duplicate account parameter.");
    }
    private static JsonElement Element(JsonNode value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
    private static CodexThreadRpcException Unavailable() => new(-32000, "Account capability is not registered by the host.");
}
