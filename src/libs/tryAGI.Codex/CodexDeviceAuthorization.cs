using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>A pending device sign-in. Show <see cref="VerificationUri"/> and <see cref="UserCode"/> to the user.</summary>
public sealed class CodexDeviceCode
{
    /// <summary>Page where the user enters the code.</summary>
    public required Uri VerificationUri { get; init; }

    /// <summary>Code the user enters.</summary>
    public required string UserCode { get; init; }

    /// <summary>Minimum delay between <see cref="CodexDeviceAuthorization.PollAsync"/> calls.</summary>
    public required TimeSpan PollInterval { get; init; }

    /// <summary>When the code stops being accepted.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    [JsonIgnore] internal string DeviceAuthId { get; init; } = "";

    /// <inheritdoc />
    public override string ToString() => "CodexDeviceCode [redacted]";
}

/// <summary>ChatGPT OAuth tokens. Store them securely; the refresh token rotates on every refresh.</summary>
public sealed class CodexOAuthTokens
{
    /// <summary>Creates a token set.</summary>
    /// <param name="accessToken">Bearer access token.</param>
    /// <param name="refreshToken">Single-use refresh token.</param>
    /// <param name="accountId">ChatGPT account ID.</param>
    /// <param name="expiresAt">Access token expiry.</param>
    public CodexOAuthTokens(string accessToken, string refreshToken, string accountId, DateTimeOffset expiresAt)
        : this(accessToken, refreshToken, accountId, expiresAt, null, null) { }

    /// <summary>Creates a token set with user identity.</summary>
    /// <param name="accessToken">Bearer access token.</param>
    /// <param name="refreshToken">Single-use refresh token.</param>
    /// <param name="accountId">ChatGPT account ID.</param>
    /// <param name="expiresAt">Access token expiry.</param>
    /// <param name="chatGptUserId">ChatGPT user ID, if known.</param>
    /// <param name="isFedRampAccount">The account is a FedRAMP account.</param>
    public CodexOAuthTokens(string accessToken, string refreshToken, string accountId, DateTimeOffset expiresAt,
        string? chatGptUserId, bool? isFedRampAccount = null)
    {
        AccessToken = accessToken; RefreshToken = refreshToken; AccountId = accountId; ExpiresAt = expiresAt;
        ChatGptUserId = chatGptUserId;
        IsFedRampAccount = isFedRampAccount ?? false;
    }
    /// <summary>Bearer access token.</summary>
    [JsonIgnore] public string AccessToken { get; }

    /// <summary>Single-use refresh token.</summary>
    [JsonIgnore] public string RefreshToken { get; }

    /// <summary>ChatGPT account ID.</summary>
    [JsonIgnore] public string AccountId { get; }

    /// <summary>ChatGPT user ID, if known.</summary>
    [JsonIgnore] public string? ChatGptUserId { get; }

    /// <summary>The account is a FedRAMP account.</summary>
    [JsonIgnore] public bool IsFedRampAccount { get; }

    /// <summary>Access token expiry.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>The credentials to hand to <see cref="ICodexCredentialProvider"/>.</summary>
    public CodexCredentials Credentials => new(AccessToken, AccountId)
    { ChatGptUserId = ChatGptUserId, IsFedRampAccount = IsFedRampAccount };

    /// <inheritdoc />
    public override string ToString() => "CodexOAuthTokens [redacted]";
}

/// <summary>Why a token refresh happens. Sent to the auth service as diagnostics only.</summary>
public enum CodexRefreshReason
{
    /// <summary>The access token is about to expire.</summary>
    ProactiveExpiry,

    /// <summary>A periodic refresh.</summary>
    Scheduled,

    /// <summary>A request was rejected with 401 and is retried with a new token.</summary>
    UnauthorizedRecovery,

    /// <summary>The user or host asked for a refresh.</summary>
    Forced,
}

/// <summary>Explicit ChatGPT device authorization and refresh. No reading or copying a CLI's credential store.</summary>
public sealed class CodexDeviceAuthorization : IDisposable
{
    /// <summary>The public OAuth client ID the open-source Codex CLI uses. Not a secret.</summary>
    public const string OfficialCodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly HttpClient http;
    private readonly string clientId;
    private readonly TimeProvider time;

    /// <summary>Creates a device authorization client.</summary>
    /// <param name="clientId">OAuth client ID.</param>
    /// <param name="handler">Optional HTTP handler, for proxies or tests.</param>
    /// <param name="timeProvider">Clock used for expiry.</param>
    public CodexDeviceAuthorization(string clientId = OfficialCodexClientId, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        this.clientId = clientId; time = timeProvider ?? TimeProvider.System;
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
        { BaseAddress = new Uri("https://auth.openai.com/"), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Requests a user code. The code expires after 15 minutes.</summary>
    public async Task<CodexDeviceCode> BeginAsync(CancellationToken cancellationToken = default)
    {
        using var content = JsonContent(new JsonObject { ["client_id"] = clientId });
        using var response = await http.PostAsync("api/accounts/deviceauth/usercode", content, cancellationToken).ConfigureAwait(false);
        CodexClient.EnsureSuccess(response);
        using var document = JsonDocument.Parse(await CodexClient.ReadBoundedAsync(response.Content, 65536, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var interval = root.TryGetProperty("interval", out var item) && double.TryParse(item.ToString(), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? Math.Clamp(value, 1, 60) : 5;
        return new()
        {
            VerificationUri = new Uri("https://auth.openai.com/codex/device"),
            UserCode = CodexRealtimeProtocol.Text(root, "user_code") ?? Required(root, "usercode"),
            DeviceAuthId = Required(root, "device_auth_id"), PollInterval = TimeSpan.FromSeconds(interval),
            ExpiresAt = time.GetUtcNow().AddMinutes(15),
        };
    }

    /// <summary>One poll. Null means pending; the host must honor PollInterval. Approval is always performed by the user.</summary>
    public async Task<CodexOAuthTokens?> PollAsync(CodexDeviceCode code, CancellationToken cancellationToken = default)
    {
        if (time.GetUtcNow() >= code.ExpiresAt) throw new CodexProtocolException("Device authorization expired.");
        if (string.IsNullOrEmpty(code.DeviceAuthId)) throw new ArgumentException("Use a code returned by BeginAsync.", nameof(code));
        using var content = JsonContent(new JsonObject { ["device_auth_id"] = code.DeviceAuthId, ["user_code"] = code.UserCode });
        using var response = await http.PostAsync("api/accounts/deviceauth/token", content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        CodexClient.EnsureSuccess(response);
        using var document = JsonDocument.Parse(await CodexClient.ReadBoundedAsync(response.Content, 65536, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        return await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = Required(root, "authorization_code"),
            ["redirect_uri"] = "https://auth.openai.com/deviceauth/callback", ["code_verifier"] = Required(root, "code_verifier"),
        }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The host must serialize refreshes and atomically persist the returned rotating token.</summary>
    public Task<CodexOAuthTokens> RefreshAsync(CodexOAuthTokens previous, CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(previous, null, cancellationToken);

    /// <summary>Refreshes and tells the auth service why, as diagnostics only; it never affects token validity.
    /// The host must serialize refreshes and atomically persist the returned rotating token.</summary>
    public Task<CodexOAuthTokens> RefreshAsync(CodexOAuthTokens previous, CodexRefreshReason reason, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return RefreshCoreAsync(previous, reason, cancellationToken);
    }

    private Task<CodexOAuthTokens> RefreshCoreAsync(CodexOAuthTokens previous, CodexRefreshReason? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var fields = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = previous.RefreshToken };
        if (reason is { } value) fields["refresh_reason"] = value switch
        {
            CodexRefreshReason.ProactiveExpiry => "proactive_expiry",
            CodexRefreshReason.Scheduled => "scheduled",
            CodexRefreshReason.UnauthorizedRecovery => "unauthorized_recovery",
            _ => "forced",
        };
        return ExchangeAsync(fields, previous, cancellationToken);
    }

    private async Task<CodexOAuthTokens> ExchangeAsync(Dictionary<string, string> fields, CodexOAuthTokens? previous, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync("oauth/token", content, ct).ConfigureAwait(false);
        CodexClient.EnsureSuccess(response);
        using var document = JsonDocument.Parse(await CodexClient.ReadBoundedAsync(response.Content, 131072, ct).ConfigureAwait(false));
        var root = document.RootElement; var access = Required(root, "access_token");
        var idToken = CodexRealtimeProtocol.Text(root, "id_token");
        var identity = CodexIdentityClaims.Read(idToken ?? access);
        var account = identity?.AccountId ?? previous?.AccountId
            ?? throw new CodexProtocolException("OAuth response has no ChatGPT account ID.");
        if (previous is not null && !string.Equals(account, previous.AccountId, StringComparison.Ordinal))
            throw new CodexProtocolException("OAuth refresh changed account identity; refusing replacement.");
        var refresh = CodexRealtimeProtocol.Text(root, "refresh_token") ?? previous?.RefreshToken
            ?? throw new CodexProtocolException("OAuth response has no refresh token.");
        var seconds = root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetDouble(out var n) && double.IsFinite(n) && n > 0 ? Math.Min(n, 86400) : 3600;
        var user = idToken is null && previous is not null ? previous.ChatGptUserId : identity?.UserId;
        if (previous?.ChatGptUserId is { } previousUser && user is { } nextUser &&
            !string.Equals(previousUser, nextUser, StringComparison.Ordinal))
            throw new CodexProtocolException("OAuth refresh changed user identity; refusing replacement.");
        return new(access, refresh, account, time.GetUtcNow().AddSeconds(seconds), user,
            idToken is null && previous is not null ? previous.IsFedRampAccount : identity?.IsFedRampAccount);
    }

    internal static string? ReadAccountId(string jwt)
    {
        // This extracts identity only from a token just received over the fixed OAuth TLS endpoint.
        // It is not a JWT validation API and must never authorize arbitrary caller-provided tokens.
        return CodexIdentityClaims.Read(jwt)?.AccountId;
    }

    private static string Required(JsonElement root, string key) => CodexRealtimeProtocol.Text(root, key) is { Length: > 0 } value ? value : throw new CodexProtocolException($"OAuth response omitted {key}.");
    private static StringContent JsonContent(JsonObject value) => new(value.ToJsonString(), Encoding.UTF8, "application/json");
    /// <inheritdoc />
    public void Dispose() => http.Dispose();
}
