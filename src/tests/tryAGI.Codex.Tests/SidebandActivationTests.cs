namespace tryAGI.Codex.Tests;

public sealed class SidebandActivationTests
{
    [Fact]
    public async Task Activation404UsesExactBoundedDelaysAndReturnsJoinedConnection()
    {
        var attempts = 0; var delays = new List<double>();
        var result = await CodexSidebandActivation.ConnectAsync(_ => ++attempts == 4 ? Task.FromResult("joined") : Task.FromException<string>(new CodexProtocolException("not ready", 404)), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken, (pause, _) => { delays.Add(pause.TotalMilliseconds); return Task.CompletedTask; });
        Assert.Equal("joined", result); Assert.Equal(4, attempts);
        Assert.Equal(new double[] { 100, 300, 800 }, delays);
    }

    [Theory]
    [InlineData(404, 4)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(410, 1)]
    [InlineData(429, 1)]
    [InlineData(500, 1)]
    public async Task ExhaustionOrOtherStatusPreservesOriginalFailure(int status, int expectedAttempts)
    {
        var attempts = 0; var failure = new CodexProtocolException("safe failure", status, "request_id");
        var actual = await Assert.ThrowsAsync<CodexProtocolException>(() => CodexSidebandActivation.ConnectAsync<string>(_ => { attempts++; throw failure; }, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask));
        Assert.Same(failure, actual); Assert.Equal(expectedAttempts, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationOrOverallDeadlineStopsBeforeAnotherHandshake(bool callerCancels)
    {
        using var caller = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodexSidebandActivation.ConnectAsync<string>(
            _ => { attempts++; throw new CodexProtocolException("activation pending", 404); },
            callerCancels ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(40), caller.Token,
            async (_, token) =>
            {
                if (callerCancels) caller.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }));
        Assert.Equal(1, attempts);
    }
}
