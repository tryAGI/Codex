using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Caller-owned ChatGPT OAuth credential snapshot. Never serialized or printed by this SDK.</summary>
public sealed class CodexCredentials(string accessToken, string accountId)
{
    [JsonIgnore] public string AccessToken { get; } = !string.IsNullOrWhiteSpace(accessToken) ? accessToken : throw new ArgumentException("An access token is required.", nameof(accessToken));
    [JsonIgnore] public string AccountId { get; } = !string.IsNullOrWhiteSpace(accountId) ? accountId : throw new ArgumentException("An account ID is required.", nameof(accountId));
    [JsonIgnore] public string? ChatGptUserId { get; init; }
    [JsonIgnore] public bool IsFedRampAccount { get; init; }
    public override string ToString() => "CodexCredentials [redacted]";
}

/// <summary>Refresh and secure storage belong to the host; the SDK asks before each new connection/request.</summary>
public interface ICodexCredentialProvider
{
    ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default);
}

/// <summary>The host no longer has an active login; carries no account or token data.</summary>
public sealed class CodexAuthorizationUnavailableException() : InvalidOperationException("Native Codex requires explicit ChatGPT authorization.");

public sealed record CodexEndpointOptions
{
    public Uri BackendBaseUri { get; init; } = new("https://chatgpt.com/backend-api/codex/");
    public Uri SidebandBaseUri { get; init; } = new("wss://api.openai.com/v1/live/");
    /// <summary>Default <c>originator</c> header value sent by this library.</summary>
    public const string DefaultOriginator = "tryagi_codex";
    public string Originator { get; init; } = DefaultOriginator;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaximumMessageBytes { get; init; } = 1_048_576;
}

public sealed class CodexProtocolException(string message, int? statusCode = null, string? requestId = null) : IOException(message)
{
    public int? StatusCode { get; } = statusCode;
    public string? RequestId { get; } = requestId;
    public CodexRealtimeError? RealtimeError { get; init; }
    // Local classification only; never retain exception bodies, headers or credentials.
    internal bool TransientSidebandTransport { get; init; }
}
