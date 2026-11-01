using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace tryAGI.Codex.Tests;

public sealed class SidebandRetryTests
{
    [Fact]
    public async Task TemporaryFailureUsesBoundedBackoffThenReturnsTheJoinedConnection()
    {
        var attempts = 0; var delays = new List<double>();
        var result = await CodexSidebandRetry.ConnectAsync(_ => ++attempts == 4 ? Task.FromResult("joined") :
            Task.FromException<string>(new CodexProtocolException("temporary", 503)), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken, (pause, _) => { delays.Add(pause.TotalMilliseconds); return Task.CompletedTask; });
        Assert.Equal("joined", result); Assert.Equal(4, attempts);
        for (var i = 0; i < 3; i++) Assert.InRange(delays[i], 180 * (1 << i), 220 * (1 << i) - 1);
    }

    [Fact]
    public async Task ExhaustionRetainsLastReceiptAndDenialsOrEndedCallsAreNeverRetried()
    {
        foreach (var status in new[] { 503, 401, 403, 404, 410, 429, 501 })
        {
            var attempts = 0; var failure = new CodexProtocolException("safe receipt", status, "request_id");
            var actual = await Assert.ThrowsAsync<CodexProtocolException>(() => CodexSidebandRetry.ConnectAsync<string>(_ => { attempts++; throw failure; }, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask));
            Assert.Same(failure, actual); Assert.Equal(status == 503 ? 4 : 1, attempts);
        }
    }

    [Fact]
    public async Task ClassifiedNetworkFailureRetriesWithoutRetainingPrivateExceptionText()
    {
        var failure = CodexRealtimeConnection.HandshakeFailure(0, "request_id",
            new WebSocketException("private bearer data", new SocketException((int)SocketError.ConnectionReset)));
        Assert.True(failure.TransientSidebandTransport); Assert.DoesNotContain("private", failure.Message);
        var attempts = 0;
        Assert.Equal("joined", await CodexSidebandRetry.ConnectAsync(_ => ++attempts == 1 ? Task.FromException<string>(failure) :
            Task.FromResult("joined"), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask));
        Assert.Equal(2, attempts);
        Assert.False(CodexRealtimeConnection.HandshakeFailure(403, "id",
            new SocketException((int)SocketError.ConnectionReset)).TransientSidebandTransport);
    }

    [Fact]
    public void TlsPolicyAndUnknownFailuresCannotBecomeRetryableNetworkErrors()
    {
        var network = new SocketException((int)SocketError.ConnectionReset);
        Assert.False(CodexSidebandRetry.IsTransientTransport(new AuthenticationException("private", network)));
        Assert.False(CodexSidebandRetry.IsTransientTransport(new UnauthorizedAccessException("private", network)));
        Assert.False(CodexSidebandRetry.IsTransientTransport(new SocketException((int)SocketError.AccessDenied)));
        Assert.False(CodexSidebandRetry.IsTransientTransport(new WebSocketException("unknown")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationOrOverallDeadlineStopsRecoveryBeforeAnotherHandshake(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource(); var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodexSidebandRetry.ConnectAsync<string>(
            _ => { attempts++; throw new CodexProtocolException("temporary", 503); },
            cancelCaller ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(40), caller.Token,
            async (_, token) => { if (cancelCaller) caller.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }));
        Assert.Equal(1, attempts);
    }
}
