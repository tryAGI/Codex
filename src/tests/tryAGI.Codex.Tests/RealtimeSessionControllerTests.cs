using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeSessionControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CodexRealtimeReconnectOptions Policy => new()
    {
        MaximumAttempts = 3, RetryDelay = TimeSpan.Zero, Timeout = TimeSpan.FromSeconds(5),
        DrainTimeout = TimeSpan.FromSeconds(2),
    };

    private sealed class OAuth : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No network credentials are needed by caller-owned streams.");
    }

    private sealed class Factory : IDisposable
    {
        private readonly CodexClient client = new(new OAuth());
        public List<CodexRealtimeAttachRequest> Requests { get; } = [];
        public Channel<string> Incoming { get; private set; } = Channel.CreateUnbounded<string>();
        public Channel<string> Outgoing { get; private set; } = Channel.CreateUnbounded<string>();
        public Func<CodexRealtimeAttachRequest, CancellationToken, Task>? BeforeConnect { get; set; }

        public async Task<CodexRealtimeAttachedCall> Join(CodexRealtimeAttachRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (BeforeConnect is not null) await BeforeConnect(request, ct);
            Incoming = Channel.CreateUnbounded<string>();
            Outgoing = Channel.CreateUnbounded<string>();
            return await client.AttachExistingRealtimeCallAsync(request.CallId,
                new CodexRealtimeAttachmentStream(Incoming.Reader, Outgoing.Writer), request.Protocol, request.StartBefore, ct);
        }
        public void Dispose() => client.Dispose();
    }

    private static async Task<(RealtimeHistoryTests.Storage Storage, CodexThreadStore Store, CodexThreadDocument Thread)> Detached()
    {
        var (storage, store, thread, old) = await RealtimeHistoryTests.Setup();
        thread = await store.DetachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", old, Ct);
        return (storage, store, thread);
    }

    [Fact]
    public async Task ReconnectSameCallAfterTransportLossAndPreserveVoiceOnlyHistory()
    {
        var (_, store, thread) = await Detached();
        using var factory = new Factory();
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await controller.AttachAsync(Ct));
        var old = Assert.Single(factory.Requests).AttachmentId;
        await factory.Incoming.Writer.WriteAsync("""{"type":"input_transcript.added","item":{"text":"first "}}""", Ct);
        factory.Incoming.Writer.TryComplete(new IOException("connection lost"));
        Assert.Equal(CodexRealtimeLifecycleStatus.Retryable, await controller.PumpAsync(cancellationToken: Ct));
        var fenced = RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct));
        Assert.Equal("recoveryRequired", fenced.State);
        Assert.Equal("first ", Assert.Single(fenced.History!.Frames).Text);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.AttachAsync(Ct));
        Assert.Single(factory.Requests);
        var result = await controller.RecoverAsync((context, _) =>
        {
            Assert.Equal("rtc_test", context.Session.CallId);
            Assert.Equal("first ", Assert.Single(context.Session.History!.Frames).Text);
            return Task.FromResult(CodexRealtimeRecoveryDecision.Open);
        }, cancellationToken: Ct);
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, result);
        Assert.Equal(2, factory.Requests.Count);
        Assert.All(factory.Requests, r => Assert.Equal("rtc_test", r.CallId));
        Assert.NotEqual(old, factory.Requests[1].AttachmentId);

        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = controller.PumpAsync((_, _) => { seen.TrySetResult(); return Task.CompletedTask; }, Ct);
        await factory.Incoming.Writer.WriteAsync("""{"type":"turn.done","turn":{"role":"user","transcript":"first second"}}""", Ct);
        await seen.Task.WaitAsync(Ct);
        Assert.Equal(CodexRealtimeLifecycleStatus.Detached, await controller.DetachAsync(Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.Cancelled, await pump);
        Assert.False(factory.Outgoing.Reader.TryRead(out _));
        var end = Guid.NewGuid();
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.EndAsync(end, cancellationToken: Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.EndAsync(end, cancellationToken: Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.AttachAsync(Ct));
        var finished = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal("first second", Assert.Single(RealtimeHistoryTests.Session(finished).History!.Frames).Text);
        Assert.Single(CodexThreadStore.RealtimeCarryoverBeforeTurn(finished, 0));
        Assert.Empty(finished.Turns);
    }

    [Theory]
    [InlineData(404, CodexRealtimeLifecycleStatus.Ended, 1)]
    [InlineData(410, CodexRealtimeLifecycleStatus.Ended, 1)]
    [InlineData(401, CodexRealtimeLifecycleStatus.AuthenticationRejected, 1)]
    [InlineData(403, CodexRealtimeLifecycleStatus.AuthenticationRejected, 1)]
    [InlineData(429, CodexRealtimeLifecycleStatus.QuotaRejected, 1)]
    [InlineData(500, CodexRealtimeLifecycleStatus.Retryable, 3)]
    public async Task HandshakeClassificationDoesNotCreateReplacementCall(int status, CodexRealtimeLifecycleStatus expected, int attempts)
    {
        var (_, store, thread) = await Detached();
        using var factory = new Factory { BeforeConnect = (_, _) => throw new CodexProtocolException("synthetic failure", status) };
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        Assert.Equal(expected, await controller.AttachAsync(Ct));
        Assert.Equal(attempts, factory.Requests.Count);
        Assert.All(factory.Requests, r => Assert.Equal("rtc_test", r.CallId));
        Assert.Equal(expected == CodexRealtimeLifecycleStatus.Ended ? "ended" : "recoveryRequired",
            RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).State);
        Assert.Null(controller.Connection);
    }

    [Fact]
    public async Task AccountMismatchIsNotRetried()
    {
        var (_, store, thread) = await Detached();
        using var factory = new Factory { BeforeConnect = (_, _) => throw new CodexRealtimeAccountMismatchException() };
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        Assert.Equal(CodexRealtimeLifecycleStatus.AuthenticationRejected, await controller.AttachAsync(Ct));
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task RecoveryDoesNotStealAnotherAttachmentWithoutProof()
    {
        var (_, store, thread, lost) = await RealtimeHistoryTests.Setup();
        using var factory = new Factory();
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        var probes = 0;
        Task<CodexRealtimeRecoveryDecision> Probe(CodexRealtimeRecoveryContext _, CancellationToken __)
        {
            probes++;
            return Task.FromResult(CodexRealtimeRecoveryDecision.Open);
        }
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.RecoverAsync(Probe, cancellationToken: Ct));
        Assert.Equal(0, probes);
        Assert.Empty(factory.Requests);
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await controller.RecoverAsync(Probe,
            provenLostAttachmentId: lost, cancellationToken: Ct));
        Assert.Equal(1, probes);
        Assert.NotEqual(lost, Assert.Single(factory.Requests).AttachmentId);
    }

    [Fact]
    public async Task LostObservationReceiptIsReconciledBeforeObserverAndNeverDuplicated()
    {
        var (storage, store, thread) = await Detached();
        using var factory = new Factory();
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await controller.AttachAsync(Ct));
        storage.ThrowAfterCommit = true;
        const string evt = """{"type":"input_transcript.added","event_id":"one","item":{"text":"once"}}""";
        await factory.Incoming.Writer.WriteAsync(evt, Ct);
        await factory.Incoming.Writer.WriteAsync(evt, Ct);
        factory.Incoming.Writer.TryComplete();
        var deliveries = 0;
        Assert.Equal(CodexRealtimeLifecycleStatus.Retryable, await controller.PumpAsync(async (_, ct) =>
        {
            deliveries++;
            var snapshot = await store.ReadAsync(thread.Id, ct);
            Assert.Equal("once", Assert.Single(RealtimeHistoryTests.Session(snapshot).History!.Frames).Text);
        }, Ct));
        Assert.Equal(1, deliveries);
        var history = RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).History!;
        Assert.Single(history.Events);
        Assert.Single(history.Frames);
    }

    [Fact]
    public async Task CloseDuringJoinCancelsRetryAndDoesNotOpenAnotherTransport()
    {
        var (_, store, thread) = await Detached();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new Factory { BeforeConnect = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        } };
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        var joining = controller.AttachAsync(Ct);
        await entered.Task.WaitAsync(Ct);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.DetachAsync(Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.Cancelled, await joining);
        Assert.Single(factory.Requests);
        Assert.Null(controller.Connection);
    }

    [Fact]
    public async Task AmbiguousEndCommitDoesNotRunProviderEffectAndCannotReopen()
    {
        var (storage, store, thread) = await Detached();
        using var factory = new Factory();
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await controller.AttachAsync(Ct));
        storage.ThrowAfterCommit = true;
        var end = Guid.NewGuid();
        var effects = 0;
        Task Effect(CodexRealtimeConnection? _, CancellationToken __) { effects++; return Task.CompletedTask; }
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.EndAsync(end, Effect, Ct));
        Assert.Equal(0, effects);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.EndAsync(end, Effect, Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired,
            await controller.RecoverAsync((_, _) => Task.FromResult(CodexRealtimeRecoveryDecision.Open), cancellationToken: Ct));
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended,
            await controller.RecoverAsync((_, _) => Task.FromResult(CodexRealtimeRecoveryDecision.Ended), cancellationToken: Ct));
        Assert.Equal(0, effects);
        Assert.Single(factory.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostTransportRetiresUnadmittedHandoffOnlyAfterVerifiedRecovery(bool ended)
    {
        var (_, store, thread, lost) = await RealtimeHistoryTests.Setup();
        thread = await RealtimeHistoryTests.Observe(store, thread, lost, "speech",
            """{"type":"input_transcript.added","item":{"text":"please"}}""");
        thread = await RealtimeHistoryTests.Observe(store, thread, lost, "handoff",
            """{"type":"delegation.created","item":{"id":"orphan","type":"delegation","target":"client","content":[{"type":"input_text","text":"please"}]}}""");
        using var factory = new Factory();
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);

        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired,
            await controller.RecoverAsync((_, _) => Task.FromResult(CodexRealtimeRecoveryDecision.Unknown),
                provenLostAttachmentId: lost, cancellationToken: Ct));
        var fenced = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal("pending", Assert.Single(RealtimeHistoryTests.Session(fenced).History!.Handoffs).State);
        Assert.Empty(fenced.Turns);

        var result = await controller.RecoverAsync((_, _) => Task.FromResult(
            ended ? CodexRealtimeRecoveryDecision.Ended : CodexRealtimeRecoveryDecision.Open), cancellationToken: Ct);
        Assert.Equal(ended ? CodexRealtimeLifecycleStatus.Ended : CodexRealtimeLifecycleStatus.Attached, result);
        var recovered = await store.ReadAsync(thread.Id, Ct);
        var history = RealtimeHistoryTests.Session(recovered).History!;
        Assert.Equal("rejected", Assert.Single(history.Handoffs).State);
        Assert.Single(history.Events, e => e.Reason == "attachmentRetired");
        Assert.Equal("please", Assert.Single(history.Frames).Text);
        Assert.Empty(recovered.Turns);

        if (!ended)
        {
            Assert.Equal(CodexRealtimeLifecycleStatus.Detached, await controller.DetachAsync(Ct));
            Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.EndAsync(Guid.NewGuid(), cancellationToken: Ct));
        }

        var closed = await store.ReadAsync(thread.Id, Ct);
        Assert.Single(RealtimeHistoryTests.Session(closed).History!.Carryover);
        Assert.Contains("please", CodexThreadStore.RealtimeCarryoverBeforeTurn(closed, 0).Single().GetRawText());
        Assert.Empty(closed.Turns);
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.EndAsync(Guid.NewGuid(), cancellationToken: Ct));
        Assert.Equal(closed.Revision, (await store.ReadAsync(thread.Id, Ct)).Revision);
    }

    [Fact]
    public async Task CleanDetachRetiresPendingHandoffWithoutDiscardingSpeech()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "handoff",
            """{"type":"delegation.created","item":{"id":"pending","type":"delegation","target":"client","content":[{"type":"input_text","text":"keep this"}]}}""");
        var detached = await store.DetachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct);
        var session = RealtimeHistoryTests.Session(detached);
        Assert.Equal("detached", session.State);
        Assert.Equal("rejected", Assert.Single(session.History!.Handoffs).State);
        Assert.Equal("pending", Assert.Single(session.History.Frames).Disposition);
        var end = await store.BeginRealtimeEndAsync(thread.Id, detached.Revision, "session", "owner", null, Guid.NewGuid(), Ct);
        Assert.Single(RealtimeHistoryTests.Session(end.Thread).History!.Carryover);
        Assert.Empty(end.Thread.Turns);
    }

    [Fact]
    public async Task ReplacementDuringConnectionFencesOldTransportWithoutTouchingNewOwner()
    {
        var (_, store, thread) = await Detached();
        var replacement = Guid.NewGuid();
        using var factory = new Factory { BeforeConnect = async (request, ct) =>
        {
            var current = await store.ReadAsync(thread.Id, ct);
            current = await store.RequireRealtimeHistoryRecoveryAsync(thread.Id, current.Revision, "session", "owner", request.AttachmentId, ct);
            current = await store.ConfirmRealtimeHistoryRecoveryAsync(thread.Id, current.Revision, "session", "owner", false, ct);
            await store.AttachRealtimeSessionAsync(thread.Id, current.Revision, "session", "owner", replacement, ct);
        } };
        await using var controller = new CodexRealtimeSessionController(store, thread.Id, "session", "owner", factory.Join, Policy);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.AttachAsync(Ct));
        Assert.Equal(replacement, RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).AttachmentId);
        Assert.Null(controller.Connection);
        Assert.Single(factory.Requests);
    }
}
