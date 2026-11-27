using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

// Independent Rust-derived scenarios. Use the native journal, not the presenter in isolation.
public sealed class RustParityPresentationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => RealtimeHistoryTests.Json(value);
    private static JsonObject[] Timeline(CodexThreadDocument document) => CodexThreadStore.ProjectRealtimeItems(document).ToArray();
    private static string? Type(JsonObject item) => (string?)item["type"];

    private static async Task<(CodexThreadStore Store, CodexThreadDocument Thread, Guid Attachment)> StartLegacyAsync()
    {
        var store = new CodexThreadStore(new RealtimeHistoryTests.Storage(), "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice-1", "rtc_voice_1", "v2", "owner", Ct);
        thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, "voice-1", "owner", CodexRealtimeHistoryMode.LegacyFlush, Ct);
        var attachment = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice-1", "owner", attachment, Ct);
        return (store, thread, attachment);
    }

    private static JsonElement Call(string id) => Json(new JsonObject
    {
        ["type"] = "function_call", ["id"] = id + "-item", ["call_id"] = id,
        ["name"] = "lookup", ["arguments"] = "{}",
    }.ToJsonString());

    private static JsonElement McpItem(string id, string server, string status) => Json(new JsonObject
    {
        ["type"] = "mcpToolCall", ["id"] = id + "-item", ["server"] = server,
        ["tool"] = "lookup", ["status"] = status, ["arguments"] = new JsonObject(),
    }.ToJsonString());

    // Rust core/src/realtime_history_tests.rs:495-539. The legacy segment must
    // precede the sole successful codex_app promotion, even after cold replay.
    [Fact]
    public async Task LegacyMcpPresentationSplitsSpeechAndExcludesNonAppOrFailedCalls()
    {
        var (store, thread, attachment) = await StartLegacyAsync();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        var ids = new[] { "other", "failed", "app" };
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner", ids.Select(Call).ToArray(), Ct);
        var initial = Timeline(thread);
        Assert.Single(initial);

        async Task Observe(string id, string wire) =>
            thread = (await store.RecordRealtimeObservationAsync(thread.Id, thread.Revision, "voice-1", "owner",
                attachment, id, RealtimeHistoryTests.Event(wire) with { Protocol = "v2" }, Ct)).Thread;
        await Observe("speech-before", """{"type":"response.output_audio_transcript.delta","delta":"Before tool."}""");
        var observed = Assert.Single(thread.RealtimeSessions!.Single().History!.Frames);

        foreach (var id in ids)
        {
            var server = id == "other" ? "other_server" : "codex_app";
            thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", id, "tool", Call(id), Ct)).Thread;
            thread = (await store.ObserveNativeBackingArtifactAsync(thread.Id, thread.Revision, turn, "runner", id,
                CodexNativeArtifactStage.Started, McpItem(id, server, "inProgress"), Ct)).Thread;
            Assert.Equal(initial.Select(i => i.ToJsonString()), Timeline(thread).Select(i => i.ToJsonString()));
            thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "runner", id, "succeeded",
                Json(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = "fixture" }.ToJsonString()), Ct);
            var terminal = McpItem(id, server, id == "failed" ? "failed" : "completed");
            thread = (await store.ObserveNativeBackingArtifactAsync(thread.Id, thread.Revision, turn, "runner", id,
                CodexNativeArtifactStage.Completed, terminal, Ct)).Thread;
            if (id != "app")
                Assert.Equal(initial.Select(i => i.ToJsonString()), Timeline(thread).Select(i => i.ToJsonString()));
            else
            {
                var beforeReplay = thread.Revision;
                var duplicate = await store.ObserveNativeBackingArtifactAsync(thread.Id, thread.Revision, turn, "runner", id,
                    CodexNativeArtifactStage.Completed, terminal, Ct);
                Assert.False(duplicate.Admitted);
                Assert.Equal(beforeReplay, duplicate.Thread.Revision);
            }
        }
        var items = Timeline(await store.ReadAsync(thread.Id, Ct));
        Assert.Equal(new[] { "realtimeSessionStarted", "transcriptSegment", "bemItemPromoted" }, items.Select(Type));
        Assert.Equal("assistant", (string?)items[1]["role"]);
        Assert.Equal("Before tool.", (string?)items[1]["text"]);
        Assert.Equal(observed.Id, (string?)items[1]["id"]);
        // Rust exposes observation timestamps only when frame enrollment is enabled.
        Assert.Null(items[1]["observedAtMs"]);
        Assert.Equal("voice-1", (string?)items[2]["realtimeSessionId"]);
        Assert.Equal(turn.ToString(), (string?)items[2]["turn_id"]);
        Assert.Equal("app-item", (string?)items[2]["item_id"]);
        Assert.Equal("wholeItem", (string?)items[2]["presentation"]?["type"]);

        await Observe("speech-after", """{"type":"response.output_audio_transcript.delta","delta":"After tool."}""");
        var end = Guid.NewGuid();
        thread = (await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "voice-1", "owner", attachment, end, Ct)).Thread;
        thread = await store.CompleteRealtimeEndAsync(thread.Id, thread.Revision, "voice-1", "owner", end, Ct);
        var closed = Timeline(await store.ReadAsync(thread.Id, Ct));
        Assert.Equal(items.Select(i => i.ToJsonString()), closed.Take(items.Length).Select(i => i.ToJsonString()));
        var tail = Assert.Single(closed, i => Type(i) == "transcriptSegment" && (string?)i["text"] == "After tool.");
        Assert.NotEqual((string?)items[1]["id"], (string?)tail["id"]);
        Assert.Single(closed, i => Type(i) == "bemItemPromoted");
    }

    // Rust core/src/realtime_history_tests.rs:178-208. An interrupted turn
    // remains attributed to its original voice session after a successor starts.
    [Fact]
    public async Task LegacyInterruptedToolReceiptRetainsOriginalSessionAndStablePosition()
    {
        var (store, thread, attachment) = await StartLegacyAsync();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        var call = Call("late");
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner", [call], Ct);
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "late", "tool", call, Ct)).Thread;
        thread = await store.RecoverInterruptedAsync(thread.Id, thread.Revision, turn, "runner", Ct);
        var end = Guid.NewGuid();
        thread = (await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "voice-1", "owner", attachment, end, Ct)).Thread;
        thread = await store.CompleteRealtimeEndAsync(thread.Id, thread.Revision, "voice-1", "owner", end, Ct);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice-2", "rtc_voice_2", "v2", "owner", Ct);
        thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, "voice-2", "owner", CodexRealtimeHistoryMode.LegacyFlush, Ct);
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice-2", "owner", Guid.NewGuid(), Ct);
        var before = Timeline(thread);
        thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "runner", "late", "succeeded",
            Json("""{"type":"function_call_output","call_id":"late","output":"fixture","success":true}"""), Ct);
        var after = Timeline(await store.ReadAsync(thread.Id, Ct));
        Assert.Equal(before.Select(i => i.ToJsonString()), after.Take(before.Length).Select(i => i.ToJsonString()));
        var item = Assert.Single(after.Skip(before.Length));
        Assert.Equal("bemItemPromoted", Type(item));
        Assert.Equal("voice-1", (string?)item["realtimeSessionId"]);
        Assert.Equal(turn.ToString(), (string?)item["turn_id"]);
        Assert.Equal("late-item", (string?)item["item_id"]);
        Assert.Equal("wholeItem", (string?)item["presentation"]?["type"]);
    }
}
