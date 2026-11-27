using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeSessionJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        public bool ThrowAfterCommit { get; set; }
        public bool ThrowBeforeCommit { get; set; }

        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                return Task.FromResult(rows.TryGetValue(id, out var row) ? (JsonElement?)row.Clone() : null);
        }

        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? prior = rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (prior != revision) return Task.FromResult(false);
                if (ThrowBeforeCommit) { ThrowBeforeCommit = false; throw new IOException("Write failed before commit."); }
                rows[id] = body.Clone();
                if (ThrowAfterCommit) { ThrowAfterCommit = false; throw new IOException("Commit receipt lost."); }
                return Task.FromResult(true);
            }
        }
    }

    private static async Task<(Storage Storage, CodexThreadStore Store, CodexThreadDocument Thread)> SetupAsync()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        using var settings = JsonDocument.Parse("{}");
        var original = await store.CreateAsync(Guid.NewGuid(), settings.RootElement, Ct);
        Assert.Null(original.RealtimeSessions);
        var thread = await store.CreateRealtimeSessionAsync(original.Id, original.Revision, "session", "call", "v3", "owner", Ct);
        return (storage, store, thread);
    }

    private static CodexStoredRealtimeSession Session(CodexThreadDocument thread)
        => Assert.Single(thread.RealtimeSessions ?? []);

    [Fact]
    public async Task TwoAttachersAreFencedAndOldAttachmentCannotBeReused()
    {
        var (storage, store, thread) = await SetupAsync();
        var other = new CodexThreadStore(storage, "account");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        async Task<CodexThreadDocument?> TryAttach(CodexThreadStore instance, Guid attachment)
        {
            try { return await instance.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct); }
            catch (CodexThreadConflictException) { return null; }
        }

        var attempts = await Task.WhenAll(Task.Run(() => TryAttach(store, first), Ct),
            Task.Run(() => TryAttach(other, second), Ct));
        var winner = Assert.Single(attempts.OfType<CodexThreadDocument>());
        var attachment = Session(winner).AttachmentId!.Value;
        var loser = attachment == first ? second : first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DetachRealtimeSessionAsync(thread.Id, winner.Revision, "session", "owner", loser, Ct));
        var detached = await store.DetachRealtimeSessionAsync(thread.Id, winner.Revision, "session", "owner", attachment, Ct);
        Assert.Equal("detached", Session(detached).State);
        Assert.Equal("call", Session(detached).CallId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AttachRealtimeSessionAsync(thread.Id, detached.Revision, "session", "owner", attachment, Ct));
        var reattached = await other.AttachRealtimeSessionAsync(thread.Id, detached.Revision, "session", "owner", loser, Ct);
        Assert.Equal(loser, Session(reattached).AttachmentId);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.DetachRealtimeSessionAsync(thread.Id, winner.Revision, "session", "owner", attachment, Ct));
    }

    [Fact]
    public async Task TransportLossRequiresVerifiedHistoryBeforeReattachOrEnd()
    {
        var (storage, store, thread) = await SetupAsync();
        var attachment = Guid.NewGuid();
        var attached = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct);
        var restarted = new CodexThreadStore(storage, "account");
        var recovered = await restarted.RequireRealtimeHistoryRecoveryAsync(thread.Id, attached.Revision, "session", "owner", attachment, Ct);
        Assert.Equal("recoveryRequired", Session(recovered).State);
        Assert.Null(Session(recovered).AttachmentId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AttachRealtimeSessionAsync(thread.Id, recovered.Revision, "session", "owner", Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.BeginRealtimeEndAsync(thread.Id, recovered.Revision, "session", "owner", null, Guid.NewGuid(), Ct));
        var detached = await restarted.ConfirmRealtimeHistoryRecoveryAsync(thread.Id, recovered.Revision, "session", "owner", false, Ct);
        var endId = Guid.NewGuid();
        var end = await restarted.BeginRealtimeEndAsync(thread.Id, detached.Revision, "session", "owner", null, endId, Ct);
        Assert.True(end.Admitted);
        var complete = await restarted.CompleteRealtimeEndAsync(thread.Id, end.Thread.Revision, "session", "owner", endId, Ct);
        Assert.Equal("ended", Session(complete).State);
        Assert.False((await restarted.BeginRealtimeEndAsync(thread.Id, complete.Revision, "session", "owner", null, Guid.NewGuid(), Ct)).Admitted);
        Assert.Equal(complete.Revision, (await restarted.CompleteRealtimeEndAsync(thread.Id, complete.Revision, "session", "owner", endId, Ct)).Revision);
        var next = await restarted.CreateRealtimeSessionAsync(thread.Id, complete.Revision, "new-session", "new-call", "v3", "owner", Ct);
        Assert.Equal(2, next.RealtimeSessions!.Length);
    }

    [Fact]
    public async Task LostEndReceiptDoesNotAdmitDuplicateEffect()
    {
        var (storage, store, thread) = await SetupAsync();
        var operation = Guid.NewGuid();
        storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() => store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", null, operation, Ct));
        var restarted = new CodexThreadStore(storage, "account");
        var current = await restarted.ReadAsync(thread.Id, Ct);
        Assert.Equal("ending", Session(current).State);
        Assert.Equal(operation, Session(current).EndOperationId);
        Assert.False((await restarted.BeginRealtimeEndAsync(thread.Id, current.Revision, "session", "owner", null, operation, Ct)).Admitted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.BeginRealtimeEndAsync(thread.Id, current.Revision, "session", "owner", null, Guid.NewGuid(), Ct));
        var fence = await restarted.RequireRealtimeHistoryRecoveryAsync(thread.Id, current.Revision, "session", "owner", null, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ConfirmRealtimeHistoryRecoveryAsync(thread.Id, fence.Revision, "session", "owner", false, Ct));
        var ended = await restarted.ConfirmRealtimeHistoryRecoveryAsync(thread.Id, fence.Revision, "session", "owner", true, Ct);
        Assert.Equal("ended", Session(ended).State);
    }

    [Fact]
    public async Task FailedWriteAndForkDoNotInventAnAttachment()
    {
        var (storage, store, thread) = await SetupAsync();
        storage.ThrowBeforeCommit = true;
        await Assert.ThrowsAsync<IOException>(() => store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", Guid.NewGuid(), Ct));
        var read = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(thread.Revision, read.Revision);
        Assert.Equal("detached", Session(read).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AttachRealtimeSessionAsync(thread.Id, read.Revision, "session", "other-owner", Guid.NewGuid(), Ct));
        var fork = await store.ForkAsync(thread.Id, read.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.RealtimeSessions);
        Assert.Equal("detached", Session(await store.ReadAsync(thread.Id, Ct)).State);
    }
}
