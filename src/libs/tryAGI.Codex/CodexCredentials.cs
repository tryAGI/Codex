using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Caller-owned ChatGPT OAuth credential snapshot. Never serialized or printed by this SDK.</summary>
/// <param name="accessToken">Bearer access token.</param>
/// <param name="accountId">ChatGPT account ID.</param>
public sealed class CodexCredentials(string accessToken, string accountId)
{
    /// <summary>Bearer access token.</summary>
    [JsonIgnore] public string AccessToken { get; } = !string.IsNullOrWhiteSpace(accessToken) ? accessToken : throw new ArgumentException("An access token is required.", nameof(accessToken));
    /// <summary>ChatGPT account ID, sent as <c>ChatGPT-Account-Id</c>.</summary>
    [JsonIgnore] public string AccountId { get; } = !string.IsNullOrWhiteSpace(accountId) ? accountId : throw new ArgumentException("An account ID is required.", nameof(accountId));
    /// <summary>ChatGPT user ID, if known.</summary>
    [JsonIgnore] public string? ChatGptUserId { get; init; }
    /// <summary>The account is a FedRAMP account.</summary>
    [JsonIgnore] public bool IsFedRampAccount { get; init; }
    /// <inheritdoc />
    public override string ToString() => "CodexCredentials [redacted]";
}

/// <summary>Refresh and secure storage belong to the host; the SDK asks before each new connection/request.</summary>
public interface ICodexCredentialProvider
{
    /// <summary>Returns current credentials, refreshing them first if needed.</summary>
    ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default);
}

/// <summary>The host no longer has an active login; carries no account or token data.</summary>
public sealed class CodexAuthorizationUnavailableException() : InvalidOperationException("Native Codex requires explicit ChatGPT authorization.");

/// <summary>Endpoints, timeouts and size bounds for <see cref="CodexClient"/>.</summary>
public sealed record CodexEndpointOptions
{
    /// <summary>ChatGPT Codex backend base URI (HTTPS).</summary>
    public Uri BackendBaseUri { get; init; } = new("https://chatgpt.com/backend-api/codex/");
    /// <summary>Realtime sideband base URI (WSS).</summary>
    public Uri SidebandBaseUri { get; init; } = new("wss://api.openai.com/v1/live/");
    /// <summary>Default <c>originator</c> header value sent by this library.</summary>
    public const string DefaultOriginator = "tryagi_codex";
    /// <summary>Value of the <c>originator</c> header.</summary>
    public string Originator { get; init; } = DefaultOriginator;
    /// <summary>Deadline for each HTTP request and connection attempt.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Largest response body or WebSocket message accepted, 1 KiB–16 MiB.</summary>
    public int MaximumMessageBytes { get; init; } = 1_048_576;
}

/// <summary>The provider returned an error or an unexpected response. Carries no response body or credentials.</summary>
/// <param name="message">Error description.</param>
/// <param name="statusCode">HTTP status code, if any.</param>
/// <param name="requestId">Provider request ID, if returned.</param>
public sealed class CodexProtocolException(string message, int? statusCode = null, string? requestId = null) : IOException(message)
{
    /// <summary>HTTP status code, if any.</summary>
    public int? StatusCode { get; } = statusCode;
    /// <summary>Provider request ID, if returned.</summary>
    public string? RequestId { get; } = requestId;
    /// <summary>The realtime error event that caused this exception, if any.</summary>
    public CodexRealtimeError? RealtimeError { get; init; }
    // Local classification only; never retain exception bodies, headers or credentials.
    internal bool TransientSidebandTransport { get; init; }
}
