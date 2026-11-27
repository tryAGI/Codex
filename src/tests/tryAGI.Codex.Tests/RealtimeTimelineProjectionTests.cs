using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeTimelineProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => RealtimeHistoryTests.Json(text);

    private static CodexRealtimePendingObservation Final(Guid attachment, int index)
        => new(attachment, "final-" + index, RealtimeHistoryTests.Event(new JsonObject
        {
            ["type"] = "turn.done",
            ["turn"] = new JsonObject { ["role"] = "user", ["transcript"] = "message " + index },
        }.ToJsonString()));

    private static string Text(JsonObject item) =>
        item["channels"]!["voice_transcript"]!["text"]!.GetValue<string>();

    private static CodexThreadRpc Rpc(CodexThreadStore store) => new(store,
        _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]), Json("{}"),
        (_, _, _) => throw new InvalidOperationException("No execution expected."), (_, _) => Task.CompletedTask);

    [Fact]
    public async Task RevisionSuffixContainsOnlyNewItemsAndUnpublishedActiveSpeech()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        for (var batch = 0; batch < 8; batch++)
            thread = (await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner",
                attachment, Enumerable.Range(batch * 8, 8).Select(i => Final(attachment, i)).ToArray(), Ct)).Thread;
        var baseline = thread;
        var before = CodexThreadStore.ProjectRealtimeTimeline(baseline, store);
        Assert.Equal(65, before.Count);
        Assert.Equal("session:session:start", before.Rows[0].Id);
        Assert.Equal(Enumerable.Range(0, 64).Select(i => "message " + i), before.Rows.Skip(1).Select(row => Text(row.Materialize())));
        var old = before.Rows.Select(row => row.Materialize().ToJsonString()).ToArray();
        var reduced = store.RealtimeEventsReduced;
        var initial = CodexThreadStore.ProjectRealtimeTimeline(thread, store, thread.Revision, includeActiveFrames: true);
        Assert.Empty(initial.Rows);
        Assert.Empty(initial.ActiveFrames);
        Assert.Equal(65, initial.PrefixCount);
        Assert.Equal(reduced, store.RealtimeEventsReduced);

        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "partial",
            """{"type":"input_transcript.added","item":{"text":"unpublished"}}""");
        var pending = CodexThreadStore.ProjectRealtimeTimeline(thread, store, baseline.Revision, includeActiveFrames: true);
        Assert.Equal(65, pending.PrefixCount);
        Assert.Empty(pending.Rows);
        var active = Assert.Single(pending.ActiveFrames);
        Assert.Equal("unpublished", Text(active));
        var activeId = active["id"]!.GetValue<string>();
        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "final-partial",
            """{"type":"turn.done","turn":{"role":"user","transcript":"unpublished final"}}""");
        var finalized = CodexThreadStore.ProjectRealtimeTimeline(thread, store, baseline.Revision, includeActiveFrames: true);
        Assert.Equal(65, finalized.PrefixCount);
        Assert.Empty(finalized.ActiveFrames);
        var row = Assert.Single(finalized.Rows);
        Assert.Equal(activeId, row.Id);
        Assert.Equal("unpublished final", Text(row.Materialize()));
        Assert.Equal(old, CodexThreadStore.ProjectRealtimeItems(thread, store).Take(65).Select(item => item.ToJsonString()));
        Assert.Equal(CodexThreadStore.ProjectRealtimeItems(thread, store)[^1].ToJsonString(), row.Materialize().ToJsonString());
    }

    [Fact]
    public async Task CursorPageRemainsStableAcrossAppendAndFiltersByMetadataBeforeRendering()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        for (var batch = 0; batch < 4; batch++)
            thread = (await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner",
                attachment, Enumerable.Range(batch * 8, 8).Select(i => Final(attachment, i)).ToArray(), Ct)).Thread;
        var rpc = Rpc(store);
        Task<JsonElement> Page(string? cursor = null, string order = "position", string? eventId = null,
            long? after = null, long? from = null, long? to = null)
        {
            var args = new JsonObject
            {
                ["threadId"] = thread.Id.ToString(), ["order"] = order, ["limit"] = 7, ["sessionId"] = "session",
            };
            if (cursor is not null) args["cursor"] = cursor;
            if (eventId is not null) args["eventId"] = eventId;
            if (after is not null) args["afterPosition"] = after.Value;
            if (from is not null) args["fromObservedAtMs"] = from.Value;
            if (to is not null) args["toObservedAtMs"] = to.Value;
            return rpc.InvokeAsync("thread/realtime/list", Json(args.ToJsonString()), Ct);
        }
        var expected = CodexThreadStore.ProjectRealtimeItems(thread, store).Select(item => item.ToJsonString()).ToArray();
        var page = await Page();
        var snapshot = page.GetProperty("snapshotEnd").GetInt64();
        Assert.Equal(33, snapshot);
        var received = new List<string>();
        void AddPage(JsonElement value)
        {
            Assert.Equal(snapshot, value.GetProperty("snapshotEnd").GetInt64());
            foreach (var entry in value.GetProperty("data").EnumerateArray())
            {
                Assert.Equal(received.Count, entry.GetProperty("position").GetInt32());
                received.Add(entry.GetProperty("item").GetRawText());
            }
        }
        AddPage(page);
        thread = (await store.RecordRealtimeObservationsAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, [Final(attachment, 32), Final(attachment, 33)], Ct)).Thread;
        while (page.GetProperty("nextCursor").ValueKind == JsonValueKind.String)
        {
            page = await Page(page.GetProperty("nextCursor").GetString());
            AddPage(page);
        }
        Assert.Equal(expected, received);
        var rows = CodexThreadStore.ProjectRealtimeTimeline(thread, store).Rows;
        var last = rows[^1];
        var exact = await Page(eventId: last.Id);
        var item = Assert.Single(exact.GetProperty("data").EnumerateArray());
        Assert.Equal(rows.Length - 1, item.GetProperty("position").GetInt32());
        Assert.Equal(last.Materialize().ToJsonString(), item.GetProperty("item").GetRawText());
        var byTime = await Page(order: "observedAt", from: last.ObservedAtMs, to: last.ObservedAtMs + 1);
        Assert.All(byTime.GetProperty("data").EnumerateArray(),
            entry => Assert.Equal(last.ObservedAtMs, entry.GetProperty("item").GetProperty("observedAtMs").GetInt64()));
        var afterPage = await Page(after: rows.Length - 3);
        Assert.Equal(2, afterPage.GetProperty("data").GetArrayLength());
    }
}
