using System.Net.Sockets;

namespace tryAGI.Codex;

internal static class CodexSidebandRetry
{
    internal static bool IsTransientTransport(Exception? failure)
    {
        var transient = false;
        for (var depth = 0; failure is not null && depth < 8; depth++, failure = failure.InnerException)
        {
            if (failure is System.Security.Authentication.AuthenticationException or UnauthorizedAccessException or System.Security.SecurityException ||
                failure is SocketException { SocketErrorCode: SocketError.AccessDenied }) return false;
            transient |= failure is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded } ||
                failure is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or
                    SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.TimedOut };
        }
        // An unresolved deeper cause cannot establish absence of a policy/TLS denial.
        return failure is null && transient;
    }

    internal static async Task<T> ConnectAsync<T>(Func<CancellationToken, Task<T>> connect,
        TimeSpan timeout, CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var activity = CodexClient.ActivitySource.StartActivity("codex.realtime.sideband.recovery");
        for (var retry = 0; ; retry++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            activity?.SetTag("codex.sideband.attempt", retry + 1);
            try { return await connect(deadline.Token).ConfigureAwait(false); }
            catch (CodexProtocolException failure) when (retry < 3 &&
                (failure.StatusCode is 408 or 500 or 502 or 503 or 504 || failure.TransientSidebandTransport))
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Reviewed Rust backoff: base * 2^(attempt-1), with [0.9,1.1) jitter.
                var jitter = Math.Min(Math.BitDecrement(1.1), 0.9 + Random.Shared.NextDouble() * 0.2);
                var milliseconds = (int)(200 * (1 << retry) * jitter);
                activity?.AddEvent(new("sideband.recovery.pending", tags: new()
                {
                    { "http.status_code", failure.StatusCode }, { "retry.delay_ms", milliseconds }
                }));
                var pause = TimeSpan.FromMilliseconds(milliseconds);
                await (delay is null ? Task.Delay(pause, deadline.Token) : delay(pause, deadline.Token)).ConfigureAwait(false);
            }
        }
    }
}
