using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Native network client. Does not launch processes, read Codex files, or invoke an MCP connector.</summary>
public sealed partial class CodexClient : IDisposable
{
    /// <summary>Source of the OpenTelemetry activities this library starts (name <c>tryAGI.Codex</c>).</summary>
    public static readonly ActivitySource ActivitySource = new("tryAGI.Codex");
    private readonly HttpClient http;
    private readonly ICodexCredentialProvider credentials;
    private readonly CodexEndpointOptions options;
    private readonly ICodexUsageObserver? usageObserver;
    internal bool IsBoundTo(ICodexCredentialProvider expectedCredentials, ICodexUsageObserver expectedObserver) =>
        ReferenceEquals(credentials, expectedCredentials) && ReferenceEquals(usageObserver, expectedObserver);

    /// <summary>Creates a client.</summary>
    /// <param name="credentials">Supplies ChatGPT OAuth credentials before every request.</param>
    /// <param name="options">Endpoints, timeouts and size bounds.</param>
    /// <param name="handler">Optional HTTP handler, for proxies or tests.</param>
    /// <param name="usageObserver">Optional admission and accounting hooks for every model request.</param>
    public CodexClient(ICodexCredentialProvider credentials, CodexEndpointOptions? options = null, HttpMessageHandler? handler = null,
        ICodexUsageObserver? usageObserver = null)
    {
        this.credentials = credentials;
        this.options = options ?? new();
        this.usageObserver = usageObserver;
        if (this.options.BackendBaseUri.Scheme != "https" || this.options.SidebandBaseUri.Scheme != "wss" ||
            this.options.MaximumMessageBytes is < 1024 or > 16_777_216 || this.options.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Use HTTPS/WSS endpoints, a positive timeout, and a bounded message size.", nameof(options));
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate }, disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Creates a WebRTC realtime call from an SDP offer. Never retried: a lost response may still have created a call.</summary>
    /// <param name="offerSdp">SDP offer from the caller's peer connection.</param>
    /// <param name="session">Session settings.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<CodexRealtimeCall> CreateRealtimeCallAsync(string offerSdp, CodexRealtimeSessionOptions session, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("codex.realtime.call.create");
        ArgumentException.ThrowIfNullOrWhiteSpace(offerSdp);
        var body = new JsonObject { ["sdp"] = offerSdp, ["session"] = CodexRealtimeProtocol.Session(session) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        using var request = await RequestAsync("realtime/calls?intent=quicksilver&architecture=avas", body, deadline.Token).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation("openai-alpha", session.Protocol == "v1" ? "quicksilver=v1" : "quicksilver=v2");
        // Creation is deliberately not retried: a lost response may still have created a call.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        await EnsureCallSuccessAsync(response, deadline.Token).ConfigureAwait(false);
        var answer = await ReadBoundedAsync(response.Content, options.MaximumMessageBytes, deadline.Token).ConfigureAwait(false);
        if (!answer.StartsWith("v=0", StringComparison.Ordinal)) throw new CodexProtocolException("Call response is not SDP.");
        if (!response.Headers.TryGetValues("Location", out var values)) throw new CodexProtocolException("Call response omitted Location.");
        return new(CodexRealtimeProtocol.CallId(values.First()), answer);
    }

    /// <summary>Connects the V3 sideband WebSocket of a call.</summary>
    /// <param name="callId">ID from <see cref="CreateRealtimeCallAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    public async Task<CodexRealtimeConnection> ConnectSidebandAsync(string callId, CancellationToken cancellationToken = default)
        => await ConnectSidebandAsync(callId, "v3", cancellationToken).ConfigureAwait(false);

    /// <summary>Connects the sideband WebSocket of a call for a specific protocol.</summary>
    /// <param name="callId">ID from <see cref="CreateRealtimeCallAsync"/>.</param>
    /// <param name="protocol"><c>v3</c>, <c>v2</c> or <c>v1</c>.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    public async Task<CodexRealtimeConnection> ConnectSidebandAsync(string callId, string protocol, CancellationToken cancellationToken = default)
    {
        if (CodexRealtimeProtocol.CallId(callId) != callId) throw new ArgumentException("Invalid call ID.", nameof(callId));
        var uri = CodexRealtimeProtocol.SidebandUri(options.SidebandBaseUri, callId, protocol);
        var auth = await credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        return await CodexSidebandRetry.ConnectAsync(
            token => CodexRealtimeConnection.ConnectAsync(uri, auth, options, token, protocol),
            options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Native V3 audio WebSocket, using the reviewed Live provider URI.
    /// This creates a new session; it does not duplicate an existing WebRTC call.
    /// The host owns usage reservations, PCM input/output and reconnection.</summary>
    public async Task<CodexRealtimeConnection> ConnectRealtimeWebSocketAsync(CodexRealtimeSessionOptions session, CancellationToken cancellationToken = default)
    {
        if (session.Protocol == "v2") return await ConnectGaRealtimeAsync(session, cancellationToken).ConfigureAwait(false);
        if (session.Protocol != "v3") throw new ArgumentException("The direct audio WebSocket host supports V3 only; use the reviewed existing-call V1 sideband diagnostic.", nameof(session));
        _ = CodexRealtimeProtocol.SessionUpdate(session);
        var auth = await credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        // ChatGPT OAuth credentials belong to the ChatGPT backend. The API Live URI is a
        // different authorization surface and can accept the socket before rejecting session setup.
        var uri = DirectRealtimeUri(options.BackendBaseUri, session.Model);
        var connection = await CodexRealtimeConnection.ConnectAsync(uri, auth, options, cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.UpdateSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await connection.WaitForSessionStartedAsync(options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private async Task<CodexRealtimeConnection> ConnectGaRealtimeAsync(CodexRealtimeSessionOptions session, CancellationToken ct)
    {
        _ = CodexRealtimeGaProtocol.Session(session);
        var auth = await credentials.GetCredentialsAsync(ct).ConfigureAwait(false);
        // Explicit trusted GA route, same secure OAuth provider; never fall back to an API key.
        var uri = new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(session.Model));
        var connection = await CodexRealtimeConnection.ConnectAsync(uri, auth, options, ct, "v2").ConfigureAwait(false);
        try
        {
            await connection.WaitForSessionAsync("session.created", options.RequestTimeout, ct, session.Model).ConfigureAwait(false);
            await connection.UpdateSessionAsync(session, ct).ConfigureAwait(false);
            await connection.WaitForSessionAsync("session.updated", options.RequestTimeout, ct, session.Model).ConfigureAwait(false);
            foreach (var item in session.InitialItems)
                await connection.SendEventAsync(CodexRealtimeGaProtocol.Message(item.Role, item.Text), ct).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal static Uri DirectRealtimeUri(Uri backend, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 128 || model.Any(char.IsControl)) throw new ArgumentException("Invalid realtime model.");
        var path = backend.AbsolutePath.TrimEnd('/');
        if (path is "" or "/v1") path = "/v1/live";
        else if (path.EndsWith("/realtime", StringComparison.Ordinal)) path = path[..^9] + "/live";
        return new UriBuilder(backend) { Scheme = "wss", Port = -1, Path = path,
            Query = "model=" + Uri.EscapeDataString(model) }.Uri;
    }

    private async Task<HttpRequestMessage> RequestAsync(string path, JsonObject body, CancellationToken ct)
    {
        var auth = await credentials.GetCredentialsAsync(ct).ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BackendBaseUri.AbsoluteUri.TrimEnd('/') + "/" + path))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.Add("originator", options.Originator);
        return request;
    }

    internal static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new CodexProtocolException($"Codex endpoint returned HTTP {(int)response.StatusCode}; response bodies and credentials are omitted.", (int)response.StatusCode);
    }

    private async Task EnsureCallSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        CodexRealtimeError? error = null;
        CodexUsageLimitException? usageLimit = null;
        try
        {
            using var body = JsonDocument.Parse(await ReadBoundedAsync(response.Content, Math.Min(options.MaximumMessageBytes, 65536), ct).ConfigureAwait(false));
            if (body.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.Object)
            {
                var limitHeader = response.Headers.TryGetValues("x-codex-active-limit", out var limits) ? limits.FirstOrDefault() : null;
                usageLimit = CodexUsageLimitException.TryRead((int)response.StatusCode, value, limitHeader);
                var envelope = new JsonObject { ["type"] = "error", ["error"] = JsonNode.Parse(value.GetRawText()) };
                using var eventBody = JsonDocument.Parse(envelope.ToJsonString());
                error = CodexRealtimeProtocol.ReadError(eventBody.RootElement);
                if (error is not null && value.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String &&
                    param.GetString() is { } path && SafeResponseParameter(path)) error = error with { Parameter = path };
            }
        }
        catch (Exception e) when (e is JsonException or CodexProtocolException or DecoderFallbackException) { }
        if (usageLimit is not null) throw usageLimit;
        throw new CodexProtocolException($"Codex call returned HTTP {(int)response.StatusCode}; code={error?.Code ?? "unknown"}; type={error?.Kind ?? "unknown"}; parameter={error?.Parameter ?? "unknown"}; provider text is omitted.", (int)response.StatusCode)
        { RealtimeError = error };
    }

    private static bool SafeResponseParameter(string path)
    {
        var close = path.IndexOf(']');
        return path.Length <= 48 && path.StartsWith("input[", StringComparison.Ordinal) && close is > 6 and <= 12 &&
            path[6..close].All(char.IsAsciiDigit) && path[(close + 1)..] is ".call_id" or ".name" or ".type" or ".arguments" or ".output";
    }

    internal static async Task<string> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximumBytes) throw new CodexProtocolException("Codex response exceeded the configured bound.");
            output.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();
}
