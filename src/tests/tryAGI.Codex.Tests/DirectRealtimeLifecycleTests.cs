namespace tryAGI.Codex.Tests;

public sealed class DirectRealtimeLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(RealtimeHistoryTests.Storage Storage, CodexThreadStore Store, CodexThreadDocument Thread)> Setup()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), RealtimeHistoryTests.Json("{}"), Ct);
        thread = await store.CreateDirectRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "v3", "owner", Ct);
        return (storage, store, thread);
    }

    [Fact]
    public async Task CancellationBeforeClaimDoesNotCreateSocketAndAmbiguousCreationIsNeverReplayed()
    {
        var (_, store, thread) = await Setup();
        var calls = 0;
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await using var controller = CodexRealtimeSessionController.CreateDirect(store, thread.Id, "voice", "owner",
            (_, _) => { Interlocked.Increment(ref calls); throw new IOException("Creation outcome unknown."); },
            new() { MaximumAttempts = 4, RetryDelay = TimeSpan.Zero });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.AttachAsync(stop.Token));
        Assert.Equal(0, calls);
        Assert.Null(RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).UsedAttachmentIds);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.AttachAsync(Ct));
        Assert.Equal(1, calls);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.AttachAsync(Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.RecoverAsync(
            (_, _) => Task.FromResult(CodexRealtimeRecoveryDecision.Open), cancellationToken: Ct));
        Assert.Equal(1, calls);
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.RecoverAsync(
            (_, _) => Task.FromResult(CodexRealtimeRecoveryDecision.Ended), cancellationToken: Ct));
        var ended = RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct));
        Assert.Null(ended.CallId);
        Assert.Single(ended.UsedAttachmentIds!);
        Assert.Equal("ended", ended.State);
    }

    [Fact]
    public async Task UnconfirmedAttachmentCommitCannotStartDirectSocket()
    {
        var (storage, store, thread) = await Setup();
        storage.ThrowAfterCommit = true;
        var calls = 0;
        await using var controller = CodexRealtimeSessionController.CreateDirect(store, thread.Id, "voice", "owner",
            (_, _) => { Interlocked.Increment(ref calls); throw new InvalidOperationException("Must not dispatch."); });
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.AttachAsync(Ct));
        Assert.Equal(0, calls);
        Assert.Equal("recoveryRequired", RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).State);
    }

    [Fact]
    public async Task CancelledCreationAfterDispatchRemainsFenced()
    {
        var (_, store, thread) = await Setup();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var controller = CodexRealtimeSessionController.CreateDirect(store, thread.Id, "voice", "owner",
            async (_, ct) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unexpected reconnect.");
            });
        var attaching = controller.AttachAsync(stop.Token);
        await started.Task.WaitAsync(Ct);
        await stop.CancelAsync();
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await attaching);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.AttachAsync(Ct));
        Assert.Equal(1, calls);
        Assert.Equal("recoveryRequired", RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).State);
    }

    [Fact]
    public async Task DirectEnrollmentRejectsExistingCallFactoryBeforeTransportDispatch()
    {
        var (_, store, thread) = await Setup();
        var calls = 0;
        var controller = new CodexRealtimeSessionController(store, thread.Id, "voice", "owner",
            (_, _) => { calls++; throw new InvalidOperationException("Must not join a media call."); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.AttachAsync(Ct));
        Assert.Equal(0, calls);
        Assert.Null(RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).UsedAttachmentIds);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task DirectSocketClosesOnceAfterDrainingFinalSpeech()
    {
        var (_, store, thread) = await Setup();
        using var socket = new DirectRealtimeTestSocket
        {
            FinalObservation = """{"type":"input_transcript.added","item":{"text":"final tail"}}""",
        };
        socket.Incoming.Writer.TryWrite("""{"type":"session.started","session":{"id":"direct-provider"}}""");
        var calls = 0;
        await using var controller = CodexRealtimeSessionController.CreateDirect(store, thread.Id, "voice", "owner",
            (request, ct) =>
            {
                Interlocked.Increment(ref calls);
                return CodexClient.InitializeDirectRealtimeWebSocketAsync(socket,
                    new("test") { Protocol = request.Protocol }, cancellationToken: ct);
            });
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await controller.AttachAsync(Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await controller.AttachAsync(Ct));
        var pump = controller.PumpAsync(cancellationToken: Ct);
        var end = Guid.NewGuid();
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.EndAsync(end, cancellationToken: Ct));
        await pump;
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.EndAsync(end, cancellationToken: Ct));
        Assert.Equal(1, calls);
        Assert.Equal(1, socket.CloseCommands);
        Assert.Equal(1, socket.Disposals);
        var ended = RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct));
        Assert.Null(ended.CallId);
        Assert.Equal("ended", ended.State);
        Assert.Equal("final tail", Assert.Single(ended.History!.Frames).Text);
        Assert.Single(ended.History.Carryover);
        Assert.Equal(end, ended.EndOperationId);
    }
}
