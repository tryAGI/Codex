using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeClosureCapacityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int EncodedBytes(CodexThreadDocument thread)
        => Encoding.UTF8.GetByteCount(CodexThreadStore.Encode(thread).GetRawText());

    [Fact]
    public async Task EscapedSpeechAndPendingHandoffReserveEnoughForTheirActualClosure()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var text = new string('&', 2048) + " <last> 😀";
        var speech = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "input_transcript.added", ["item"] = new System.Text.Json.Nodes.JsonObject { ["text"] = text },
        };
        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "speech", speech.ToJsonString());
        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "handoff",
            """{"type":"delegation.created","item":{"id":"pending","type":"delegation","target":"client","content":[]}}""");
        Assert.True(RealtimeHistoryTests.Session(thread).ClosureByteReserve);
        Assert.Single(RealtimeHistoryTests.Session(thread).History!.Handoffs);

        // The original speech's character count is not a safe bound after XML and JSON escaping.
        Assert.Throws<InvalidOperationException>(() => CodexThreadStore.EnsureRealtimeClosureByteCapacity(
            thread, CodexThreadStore.MaximumDocumentBytes - 4096));
        const int available = 32 * 1024;
        CodexThreadStore.EnsureRealtimeClosureByteCapacity(thread, CodexThreadStore.MaximumDocumentBytes - available);
        var before = EncodedBytes(thread);
        var operation = Guid.NewGuid();
        var admitted = await store.BeginDrainedRealtimeEndAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, operation, Ct);
        Assert.True(admitted.Admitted);
        var ended = await store.CompleteRealtimeEndAsync(thread.Id, admitted.Thread.Revision, "session", "owner", operation, Ct);
        Assert.InRange(EncodedBytes(ended) - before, 1, available);
        var history = RealtimeHistoryTests.Session(ended).History!;
        Assert.Equal("rejected", Assert.Single(history.Handoffs).State);
        Assert.Equal(text, Assert.Single(history.Frames).Text);
        Assert.Single(history.Carryover);
    }

    [Fact]
    public async Task OrdinaryThreadWritesCannotConsumeClosingCapacity()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "speech",
            """{"type":"input_transcript.added","item":{"text":"keep this speech"}}""");
        var baseline = EncodedBytes(thread);
        var padding = CodexThreadStore.MaximumDocumentBytes - baseline - 128;
        var settings = RealtimeHistoryTests.Json("{\"padding\":\"" + new string('x', padding) + "\"}");
        // It fits under the document's ordinary hard bound, but not under the closure reserve.
        Assert.True(EncodedBytes(thread with { Settings = settings }) < CodexThreadStore.MaximumDocumentBytes);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateSettingsAsync(thread.Id, thread.Revision, settings, Ct));
        Assert.Contains("realtime closure", error.Message);
        var unchanged = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(thread.Revision, unchanged.Revision);
        Assert.False(unchanged.Settings.TryGetProperty("padding", out _));
        var detached = await store.DetachRealtimeSessionAsync(thread.Id, unchanged.Revision, "session", "owner", attachment, Ct);
        var end = Guid.NewGuid();
        var admitted = await store.BeginRealtimeEndAsync(thread.Id, detached.Revision, "session", "owner", null, end, Ct);
        var closed = await store.CompleteRealtimeEndAsync(thread.Id, admitted.Thread.Revision, "session", "owner", end, Ct);
        Assert.Equal("keep this speech", Assert.Single(RealtimeHistoryTests.Session(closed).History!.Frames).Text);
        Assert.Single(RealtimeHistoryTests.Session(closed).History!.Carryover);
    }

    [Fact]
    public async Task OldSnapshotsRemainReadableAndNewAttachmentEnrollsInReservation()
    {
        var (storage, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var legacy = thread with
        {
            Revision = thread.Revision + 1,
            RealtimeSessions = [RealtimeHistoryTests.Session(thread) with { ClosureByteReserve = false }],
        };
        var raw = CodexThreadStore.Encode(legacy);
        Assert.False(raw.GetProperty("realtimeSessions")[0].TryGetProperty("closureByteReserve", out _));
        Assert.True(await storage.CompareExchangeAsync(thread.Id, thread.Revision, raw, Ct));
        legacy = await store.ReadAsync(thread.Id, Ct);
        Assert.False(RealtimeHistoryTests.Session(legacy).ClosureByteReserve);
        CodexThreadStore.EnsureRealtimeClosureByteCapacity(legacy, CodexThreadStore.MaximumDocumentBytes);

        var detached = await store.DetachRealtimeSessionAsync(legacy.Id, legacy.Revision, "session", "owner", attachment, Ct);
        var reattached = await store.AttachRealtimeSessionAsync(legacy.Id, detached.Revision, "session", "owner", Guid.NewGuid(), Ct);
        Assert.True(RealtimeHistoryTests.Session(reattached).ClosureByteReserve);
        Assert.Throws<InvalidOperationException>(() => CodexThreadStore.EnsureRealtimeClosureByteCapacity(
            reattached, CodexThreadStore.MaximumDocumentBytes));
    }
}
