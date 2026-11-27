using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class LegacyRealtimeSegmentLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => RealtimeHistoryTests.Json(text);
    private static string? Text(JsonObject item) => (string?)item["text"];
    private static JsonObject[] Segments(CodexThreadDocument thread) => CodexThreadStore.ProjectRealtimeItems(thread)
        .Where(item => (string?)item["type"] == "transcriptSegment").ToArray();

    [Theory]
    [InlineData("v1", "conversation.input_transcript.delta", "conversation.input_transcript.turn_marked")]
    [InlineData("v2", "conversation.item.input_audio_transcription.delta", "conversation.item.input_audio_transcription.completed")]
    public async Task LegacyFinalPublishesIndependentStableSegmentsWithoutRewritingOrMerging(string protocol, string inputDelta, string inputDone)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_legacy", protocol, "voice-owner", Ct);
        thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, "voice", "voice-owner", CodexRealtimeHistoryMode.LegacyNoFlush, Ct);
        var attachment = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "voice-owner", attachment, Ct);
        var sequence = 0;
        async Task Observe(string wire) => thread = (await store.RecordRealtimeObservationAsync(thread.Id, thread.Revision,
            "voice", "voice-owner", attachment, "legacy-" + ++sequence,
            RealtimeHistoryTests.Event(wire) with { Protocol = protocol }, Ct)).Thread;
        await Observe($$"""{"type":"{{inputDelta}}","delta":"First "}""");
        var firstId = Assert.Single(thread.RealtimeSessions!.Single().History!.Frames).Id;
        await Observe("""{"type":"response.output_audio_transcript.delta","delta":"Answer "}""");
        await Observe($$"""{"type":"{{inputDelta}}","delta":"phrase"}""");
        await Observe($$"""{"type":"{{inputDone}}","event_id":"user-final","transcript":"stale revision"}""");
        var first = Assert.Single(Segments(thread));
        Assert.Equal(firstId, (string?)first["id"]);
        Assert.Equal("First phrase", Text(first));
        Assert.Null(first["observedAtMs"]);
        await Observe("""{"type":"response.output_audio_transcript.done","transcript":"Answer complete"}""");
        Assert.Equal(["First phrase", "Answer complete"], Segments(thread).Select(Text).ToArray());
        await Observe($$"""{"type":"{{inputDone}}","event_id":"user-final","transcript":"stale revision"}""");
        await Observe($$"""{"type":"{{inputDone}}","event_id":"next-final","transcript":"New utterance"}""");
        Assert.Equal(["First phrase", "Answer complete", "New utterance"], Segments(thread).Select(Text).ToArray());
        var prefix = Segments(thread).Select(x => x.ToJsonString()).ToArray();
        thread = await new CodexThreadStore(storage, "account").ReadAsync(thread.Id, Ct);
        Assert.Equal(prefix, Segments(thread).Select(x => x.ToJsonString()).ToArray());
        var ended = await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "voice", "voice-owner", attachment, Guid.NewGuid(), Ct);
        Assert.Equal(prefix, Segments(ended.Thread).Select(x => x.ToJsonString()).ToArray());
        Assert.Empty(ended.Thread.RealtimeSessions!.Single().History!.Carryover);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task LegacyPromotionSplitsSpeechAndLateFinalOnlyAddsUnsealedSuffix(string protocol)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_split", protocol, "voice-owner", Ct);
        thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, "voice", "voice-owner", CodexRealtimeHistoryMode.LegacyFlush, Ct);
        var attachment = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "voice-owner", attachment, Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        async Task Observe(string id, string wire) => thread = (await store.RecordRealtimeObservationAsync(thread.Id, thread.Revision,
            "voice", "voice-owner", attachment, id, RealtimeHistoryTests.Event(wire) with { Protocol = protocol }, Ct)).Thread;
        await Observe("first", """{"type":"response.output_audio_transcript.delta","delta":"Before "}""");
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner",
            [Json("""{"type":"message","role":"assistant","id":"artifact","content":[{"type":"output_text","text":"::codex-realtime-inline{}\nDisplay"}]}""")], Ct);
        var first = Assert.Single(Segments(thread));
        Assert.Equal("Before ", Text(first));
        await Observe("second", """{"type":"response.output_audio_transcript.delta","delta":"after"}""");
        await Observe("done", """{"type":"response.output_audio_transcript.done","transcript":"Before after."}""");
        Assert.Equal(["Before ", "after."], Segments(thread).Select(Text).ToArray());
        Assert.Equal(first.ToJsonString(), Segments(thread)[0].ToJsonString());
        Assert.Equal(["realtimeSessionStarted", "transcriptSegment", "bemItemPromoted", "transcriptSegment"],
            CodexThreadStore.ProjectRealtimeItems(thread).Select(x => (string?)x["type"]).ToArray());
        var ended = await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "voice", "voice-owner", attachment, Guid.NewGuid(), Ct);
        var carry = Assert.Single(ended.Thread.RealtimeSessions!.Single().History!.Carryover);
        Assert.Equal(2, carry.SourceFrameIds.Length);
        Assert.Contains("assistant: Before ", carry.Input.GetProperty("content").GetString());
        Assert.Contains("assistant: after.", carry.Input.GetProperty("content").GetString());
    }
}
