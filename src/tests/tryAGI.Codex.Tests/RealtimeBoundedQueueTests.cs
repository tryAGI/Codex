namespace tryAGI.Codex.Tests;

public sealed class RealtimeBoundedQueueTests
{
    [Fact]
    public async Task SlowReasoningCannotConsumeAudioCapacityAndOverflowIsExplicit()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reasoning = new CodexRealtimeBoundedQueue<int>(2, 8, "reasoning");
        var audio = new CodexRealtimeBoundedQueue<int>(2, 8, "audio");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = reasoning.RunAsync(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); }, stop.Token);
        var a = audio.RunAsync((item, _) => { heard.TrySetResult(item); return Task.CompletedTask; }, stop.Token);
        try
        {
            reasoning.Write(1, 4);
            await entered.Task.WaitAsync(stop.Token);
            Assert.True(reasoning.TryWrite(2, 6));
            Assert.False(reasoning.TryWrite(3, 3));
            reasoning.Write(3, 2);
            Assert.Throws<CodexRealtimeQueueOverflowException>(() => reasoning.Write(4, 0));
            Assert.True(audio.TryWrite(42, 8));
            Assert.Equal(42, await heard.Task.WaitAsync(stop.Token));
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(r, a));
        }
        Assert.False(reasoning.TryWrite(5, 0));
        Assert.False(audio.TryWrite(43, 0));
        Assert.Equal(3, reasoning.Report.Accepted);
        Assert.Equal(3, reasoning.Report.Processed + reasoning.Report.Abandoned);
        Assert.True(reasoning.Report.Abandoned >= 2);
        Assert.Equal(new CodexRealtimeQueueReport("audio", 1, 1, 0), audio.Report);
    }
}
