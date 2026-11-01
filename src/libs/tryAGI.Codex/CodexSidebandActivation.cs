namespace tryAGI.Codex;

public sealed partial class CodexClient
{
    /// <summary>
    /// Join a client-created Instant Connect call while ICE activation is pending. Retries
    /// only HTTP 404 after 100/300/800 ms, with one overall handshake deadline. Never creates
    /// a call or changes its session. Ordinary SDP-call joins use ConnectSidebandAsync.
    /// </summary>
    public async Task<CodexRealtimeConnection> ConnectExistingCallSidebandAsync(string callId,
        string protocol = "v3", CancellationToken cancellationToken = default)
    {
        if (CodexRealtimeProtocol.CallId(callId) != callId)
            throw new ArgumentException("Invalid call ID.", nameof(callId));
        var uri = CodexRealtimeProtocol.SidebandUri(options.SidebandBaseUri, callId, protocol);
        var auth = await credentials.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        return await CodexSidebandActivation.ConnectAsync(
            token => CodexRealtimeConnection.ConnectAsync(uri, auth, options, token, protocol),
            options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }
}

internal static class CodexSidebandActivation
{
    private static readonly int[] DelaysMilliseconds = [100, 300, 800];

    internal static async Task<T> ConnectAsync<T>(Func<CancellationToken, Task<T>> connect,
        TimeSpan timeout, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var activity = CodexClient.ActivitySource.StartActivity("codex.realtime.sideband.activation");
        for (var retry = 0; ; retry++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            activity?.SetTag("codex.sideband.attempt", retry + 1);
            try { return await connect(deadline.Token).ConfigureAwait(false); }
            catch (CodexProtocolException error) when (error.StatusCode == 404 && retry < DelaysMilliseconds.Length)
            {
                // Only a known activation race is retryable. Authentication failures, policy
                // denials, other statuses and exhausted activation retain the original error.
                deadline.Token.ThrowIfCancellationRequested();
                var pause = TimeSpan.FromMilliseconds(DelaysMilliseconds[retry]);
                activity?.AddEvent(new("sideband.activation.pending", tags: new()
                {
                    { "http.status_code", 404 }, { "retry.delay_ms", DelaysMilliseconds[retry] }
                }));
                await (delay is null ? Task.Delay(pause, deadline.Token) : delay(pause, deadline.Token))
                    .ConfigureAwait(false);
            }
        }
    }
}
