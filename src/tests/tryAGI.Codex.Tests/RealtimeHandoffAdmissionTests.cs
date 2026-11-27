namespace tryAGI.Codex.Tests;

public sealed class RealtimeHandoffAdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<CodexThreadDocument> ObserveHandoff(CodexThreadStore store, CodexThreadDocument thread, Guid attachment, string id)
        => RealtimeHistoryTests.Observe(store, thread, attachment, "obs-" + id,
            $$$"""{"type":"delegation.created","item":{"type":"delegation","target":"client","id":"{{{id}}}","content":[{"type":"input_text","text":"task {{{id}}}"}]}}""");

    private static string Input(CodexThreadDocument document, string id)
        => CodexThreadStore.PrepareRealtimeHandoffInput(RealtimeHistoryTests.Session(document), id)!.Text;

    [Fact]
    public async Task StartThenSteerAreAtomicAndRepeatedReceiptCannotAdmitAgain()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        thread = await ObserveHandoff(store, thread, attachment, "one");
        var startId = Guid.NewGuid();
        var first = Input(thread, "one");
        var admitted = await store.AdmitRealtimeHandoffAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, "one", startId, null, "runner", first, Ct);
        Assert.True(admitted.Admitted);
        Assert.Equal("start", admitted.Receipt.State);
        thread = admitted.Thread;
        var repeated = await store.AdmitRealtimeHandoffAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, "one", startId, null, "runner", first, Ct);
        Assert.False(repeated.Admitted);
        Assert.Equal(thread.Revision, repeated.Thread.Revision);
        thread = await ObserveHandoff(store, thread, attachment, "two");
        var second = Input(thread, "two");
        var steerId = Guid.NewGuid();
        var steered = await store.AdmitRealtimeHandoffAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, "two", steerId, startId, "runner", second, Ct);
        Assert.True(steered.Admitted);
        Assert.Equal("steer", steered.Receipt.State);
        var turn = Assert.Single(steered.Thread.Turns);
        Assert.Equal(startId, turn.Id);
        Assert.Equal("realtime", turn.TurnTrigger);
        Assert.Equal(new[] { first, second }, turn.Input.Select(i => i.GetProperty("content").GetString()));
        Assert.Equal(new[] { startId.ToString(), steerId.ToString() }, turn.ClientUserMessageIds);
        var session = RealtimeHistoryTests.Session(steered.Thread);
        Assert.Equal(2, session.HandoffAdmissions!.Length);
        Assert.All(session.History!.Handoffs, h => Assert.Equal("accepted", h.State));
        Assert.All(session.History.Frames, f => Assert.Equal("accepted", f.Disposition));
    }

    [Fact]
    public async Task AmbiguousCommitIsReconciledWithoutAnotherAdmission()
    {
        var (storage, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        thread = await ObserveHandoff(store, thread, attachment, "one");
        var id = Guid.NewGuid();
        var input = Input(thread, "one");
        storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() => store.AdmitRealtimeHandoffAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, "one", id, null, "runner", input, Ct));
        var recovered = new CodexThreadStore(storage, "account");
        thread = await recovered.ReadAsync(thread.Id, Ct);
        var receipt = await recovered.AdmitRealtimeHandoffAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, "one", id, null, "runner", input, Ct);
        Assert.False(receipt.Admitted);
        Assert.Single(Assert.Single(receipt.Thread.Turns).Input);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.AdmitRealtimeHandoffAsync(thread.Id,
            thread.Revision, "session", "owner", attachment, "one", id, null, "runner", input + "changed", Ct));
    }

    [Fact]
    public async Task DuplicateConcurrentRevisionAndReplacementAreFenced()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        thread = await ObserveHandoff(store, thread, attachment, "one");
        var id = Guid.NewGuid();
        var input = Input(thread, "one");
        var first = await store.AdmitRealtimeHandoffAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, "one", id, null, "runner", input, Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.AdmitRealtimeHandoffAsync(thread.Id,
            thread.Revision, "session", "owner", attachment, "one", id, null, "runner", input, Ct));
        thread = await ObserveHandoff(store, first.Thread, attachment, "late");
        thread = await store.DetachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct);
        var replacement = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", replacement, Ct);
        var late = Input(thread, "late");
        var lateId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdmitRealtimeHandoffAsync(thread.Id,
            thread.Revision, "session", "owner", attachment, "late", lateId, id, "runner", late, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdmitRealtimeHandoffAsync(thread.Id,
            thread.Revision, "session", "owner", replacement, "late", lateId, id, "runner", late, Ct));
        var retired = await store.RejectRealtimeHandoffAsync(thread.Id, thread.Revision, "session", "owner",
            replacement, "late", lateId, late, retired: true, cancellationToken: Ct);
        Assert.Equal("retired", retired.Receipt.State);
        Assert.Single(Assert.Single(retired.Thread.Turns).Input);
        Assert.Equal("rejected", RealtimeHistoryTests.Session(retired.Thread).History!.Handoffs.Single(h => h.HandoffId == "late").State);
    }
}
