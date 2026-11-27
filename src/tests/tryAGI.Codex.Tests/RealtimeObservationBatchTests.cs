using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeObservationBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CodexRealtimePendingObservation Delta(Guid attachment, string id, string text) =>
        new(attachment, id, RealtimeHistoryTests.Event(new JsonObject
        {
            ["type"] = "input_transcript.added",
            ["item"] = new JsonObject { ["text"] = text },
        }.ToJsonString()));
    private static string HistoryJson(CodexStoredRealtimeHistory history) =>
        JsonSerializer.Serialize(history, CodexThreadJson.Default.CodexStoredRealtimeHistory);

    [Fact]
    public async Task WarmBatchesReduceOnlyNewEventsAndUseOneReadAndCasPerBatch()
    {
        var (storage, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var startReads = storage.Reads;
        var startWrites = storage.Writes;
        var startReduced = store.RealtimeEventsReduced;
        CodexThreadDocument? earlier = null;
        for (var batch = 0; batch < 8; batch++)
        {
            var observations = Enumerable.Range(batch * 8, 8).Select(i => Delta(attachment, "delta-" + i, "x")).ToArray();
            var result = await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner", attachment,
                observations, Ct);
            Assert.All(result.Admitted, flag => Assert.True(flag));
            Assert.Equal(thread.Revision + 1, result.Thread.Revision);
            thread = result.Thread;
            if (batch == 3) earlier = thread;
        }
        Assert.Equal(8, storage.Reads - startReads);
        Assert.Equal(8, storage.Writes - startWrites);
        Assert.Equal(64, store.RealtimeEventsReduced - startReduced);
        var session = RealtimeHistoryTests.Session(thread);
        Assert.Equal(64, session.History!.Events.Length);
        Assert.Equal(new string('x', 64), Assert.Single(session.History.Frames).Text);
        var final = new CodexRealtimePendingObservation(attachment, "final",
            RealtimeHistoryTests.Event(new JsonObject
            {
                ["type"] = "turn.done",
                ["turn"] = new JsonObject { ["role"] = "user", ["transcript"] = new string('x', 64) + "!" },
            }.ToJsonString()));
        thread = (await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner", attachment, [final], Ct)).Thread;
        var reduced = store.RealtimeEventsReduced;
        var warm = CodexThreadStore.ProjectRealtimeItems(thread, store).Select(item => item.ToJsonString()).ToArray();
        var cold = CodexThreadStore.ProjectRealtimeItems(thread).Select(item => item.ToJsonString()).ToArray();
        Assert.Equal(cold, warm);
        Assert.Equal(reduced, store.RealtimeEventsReduced);
        var reloaded = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(reduced, store.RealtimeEventsReduced);
        session = RealtimeHistoryTests.Session(reloaded);
        Assert.Equal(HistoryJson(session.History!), HistoryJson(CodexThreadStore.RecoverRealtimeHistory(session)));
        Assert.Equal(65, reduced - startReduced);
        Assert.NotNull(earlier);
        _ = store.ValidateSnapshot(CodexThreadStore.Encode(earlier));
        var afterHistoricalRead = store.RealtimeEventsReduced;
        _ = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(afterHistoricalRead, store.RealtimeEventsReduced);

        // Materialized arrays returned to a caller are not cache state or validation authority.
        var frame = Assert.Single(session.History!.Frames);
        frame.SourceObservationIds[0] = "tampered";
        Assert.Throws<InvalidDataException>(() => store.ValidateSnapshot(CodexThreadStore.Encode(reloaded)));
        var good = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal("delta-0", Assert.Single(RealtimeHistoryTests.Session(good).History!.Frames).SourceObservationIds[0]);
    }

    [Fact]
    public async Task BatchRetriesAreAtomicAndDiscardSpeculativeCacheStateAfterFailure()
    {
        var (storage, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var first = Delta(attachment, "a", "a");
        var second = Delta(attachment, "b", "b");
        var writes = storage.Writes;
        storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() => store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, [first, second, first], Ct));
        thread = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(writes + 1, storage.Writes);
        Assert.Equal(["a", "b"], RealtimeHistoryTests.Session(thread).History!.Events.Select(e => e.Id).ToArray());
        var repeat = await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner", attachment,
            [first, second, first], Ct);
        Assert.All(repeat.Admitted, flag => Assert.False(flag));
        Assert.Equal(thread.Revision, repeat.Thread.Revision);
        Assert.Equal(writes + 1, storage.Writes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, [Delta(attachment, "c", "c"), Delta(attachment, "a", "changed")], Ct));
        Assert.Equal(writes + 1, storage.Writes);

        storage.ThrowBeforeCommit = true;
        await Assert.ThrowsAsync<IOException>(() => store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, [Delta(attachment, "c", "never committed")], Ct));
        thread = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal("ab", Assert.Single(RealtimeHistoryTests.Session(thread).History!.Frames).Text);
        var committed = await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner", attachment,
            [first, Delta(attachment, "c", "c"), second, Delta(attachment, "d", "d")], Ct);
        Assert.Equal([false, true, false, true], committed.Admitted);
        var session = RealtimeHistoryTests.Session(committed.Thread);
        Assert.Equal("abcd", Assert.Single(session.History!.Frames).Text);
        Assert.Equal(HistoryJson(session.History), HistoryJson(CodexThreadStore.RecoverRealtimeHistory(session)));
        Assert.Equal(writes + 2, storage.Writes);
    }

    [Fact]
    public async Task CachedPrefixRejectsChangedRawObservationEvenWhenHeaderIsUnchanged()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        thread = (await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner", attachment,
            [Delta(attachment, "a", "observed")], Ct)).Thread;
        var session = RealtimeHistoryTests.Session(thread);
        var history = session.History!;
        var changed = history with { Events = [history.Events[0] with { Payload = Delta(attachment, "a", "different").Event.Payload }] };
        var forged = thread with { RealtimeSessions = [session with { History = changed }] };
        Assert.Throws<InvalidDataException>(() => store.ValidateSnapshot(CodexThreadStore.Encode(forged)));
        var good = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal("observed", Assert.Single(RealtimeHistoryTests.Session(good).History!.Frames).Text);
    }
}
