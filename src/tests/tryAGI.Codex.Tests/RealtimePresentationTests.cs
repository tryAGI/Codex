using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class RealtimePresentationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => RealtimeHistoryTests.Json(value);
    private static JsonElement Message(string id, string text) => Json(new JsonObject
    {
        ["type"] = "message", ["role"] = "assistant", ["id"] = id,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
    }.ToJsonString());

    private static JsonObject[] Items(CodexThreadDocument thread) => CodexThreadStore.ProjectRealtimeItems(thread).ToArray();
    private static string Type(JsonObject item) => item["type"]!.GetValue<string>();

    [Fact]
    public async Task PreviewSnapshotFastPathUsesNoReadsAndRejectsConcurrentJournalRevision()
    {
        var (storage, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "request", "inference", Json("{}"), Ct)).Thread;
        var candidate = new CodexRealtimeArtifactCandidate("preview", new("request", "::codex-realtime-inline{}\nStart"));
        var reads = storage.Reads;
        thread = await store.RecordRealtimeArtifactPreviewFromSnapshotAsync(thread, turn, "runner", candidate, Ct);
        for (var i = 0; i < 12; i++)
        {
            candidate = candidate with { Preview = candidate.Preview with { Text = candidate.Preview.Text + "x" } };
            thread = await store.RecordRealtimeArtifactPreviewFromSnapshotAsync(thread, turn, "runner", candidate, Ct);
        }
        Assert.Equal(reads, storage.Reads);
        var stale = thread;
        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "concurrent-voice",
            """{"type":"input_transcript.added","item":{"text":"user"}}""");
        var next = candidate with { Preview = candidate.Preview with { Text = candidate.Preview.Text + "y" } };
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.RecordRealtimeArtifactPreviewFromSnapshotAsync(
            stale, turn, "runner", next, Ct));
        var actual = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(thread.Revision, actual.Revision);
        Assert.Equal(candidate.Preview.Text, Assert.Single(CodexThreadStore.ProjectRealtimeArtifactPreviews(actual, turn))["text"]!.GetValue<string>());
        Assert.Contains(RealtimeHistoryTests.Session(actual).History!.Frames, frame => frame.Text == "user");
        actual = await store.RecordRealtimeArtifactPreviewFromSnapshotAsync(actual, turn, "runner", next, Ct);
        Assert.Equal(next.Preview.Text, Assert.Single(CodexThreadStore.ProjectRealtimeArtifactPreviews(actual, turn))["text"]!.GetValue<string>());
        var promoted = Assert.Single(Items(actual), item => Type(item) == "bemItemPromoted").ToJsonString();
        actual = await store.SynchronizeOutputAsync(actual.Id, actual.Revision, turn, "runner",
            [Message("preview", next.Preview.Text + " final")], Ct);
        Assert.Null(RealtimeHistoryTests.Session(actual).ArtifactPreviewUpdates);
        Assert.Equal(promoted, Assert.Single(Items(actual), item => Type(item) == "bemItemPromoted").ToJsonString());
        Assert.Empty(CodexThreadStore.ProjectRealtimeArtifactPreviews(actual, turn));
    }

    [Fact]
    public async Task DivergentTerminalOutputDoesNotEraseObservedPreviewEvidence()
    {
        var (_, store, thread, _) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "request", "inference", Json("{}"), Ct)).Thread;
        var candidate = new CodexRealtimeArtifactCandidate("preview", new("request", "::codex-realtime-inline{}\nStart"));
        thread = await store.RecordRealtimeArtifactPreviewFromSnapshotAsync(thread, turn, "runner", candidate, Ct);
        var extended = candidate with { Preview = candidate.Preview with { Text = candidate.Preview.Text + " observed" } };
        thread = await store.RecordRealtimeArtifactPreviewFromSnapshotAsync(thread, turn, "runner", extended, Ct);
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner",
            [Message("preview", "::codex-realtime-inline{}\nDifferent terminal")], Ct);
        Assert.Equal(extended.Preview, Assert.Single(RealtimeHistoryTests.Session(thread).ArtifactPreviewUpdates!).Preview);
        Assert.Empty(CodexThreadStore.ProjectRealtimeArtifactPreviews(thread, turn));
    }

    [Fact]
    public async Task PreviewAdmissionRejectsUnknownRequestAndRetainsInterruptedArtifactWithoutModelOutput()
    {
        var (_, store, thread, _) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        var candidate = new CodexRealtimeArtifactCandidate("preview", new("request", "::codex-realtime-inline{}\nObserved"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRealtimeArtifactPreviewAsync(
            thread.Id, thread.Revision, turn, "runner", candidate, Ct));
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "request", "inference", Json("{}"), Ct)).Thread;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordRealtimeArtifactPreviewAsync(
            thread.Id, thread.Revision, turn, "wrong-owner", candidate, Ct));
        thread = await store.RecordRealtimeArtifactPreviewAsync(thread.Id, thread.Revision, turn, "runner", candidate, Ct);
        var replay = await store.RecordRealtimeArtifactPreviewAsync(thread.Id, thread.Revision, turn, "runner", candidate, Ct);
        Assert.Equal(thread.Revision, replay.Revision);
        Assert.Empty(replay.Turns.Single().Output);
        var original = Assert.Single(Items(thread), x => Type(x) == "bemItemPromoted");
        var extension = candidate with { Preview = candidate.Preview with { Text = candidate.Preview.Text + " more public text" } };
        thread = await store.RecordRealtimeArtifactPreviewAsync(thread.Id, thread.Revision, turn, "runner", extension, Ct);
        Assert.Equal(original.ToJsonString(), Assert.Single(Items(thread), x => Type(x) == "bemItemPromoted").ToJsonString());
        Assert.Equal(extension.Preview.Text, Assert.Single(CodexThreadStore.ProjectRealtimeArtifactPreviews(thread, turn))["text"]!.GetValue<string>());
        var update = Assert.Single(RealtimeHistoryTests.Session(thread).ArtifactPreviewUpdates!);
        Assert.Equal(extension.Preview, update.Preview);
        var stale = await store.RecordRealtimeArtifactPreviewAsync(thread.Id, thread.Revision, turn, "runner", candidate, Ct);
        Assert.Equal(thread.Revision, stale.Revision);
        var rewritten = candidate with { Preview = candidate.Preview with { Text = "::codex-realtime-inline{}\nUnobserved rewrite" } };
        var refused = await store.RecordRealtimeArtifactPreviewAsync(thread.Id, thread.Revision, turn, "runner", rewritten, Ct);
        Assert.Equal(thread.Revision, refused.Revision);
        thread = await store.RecoverInterruptedAsync(thread.Id, thread.Revision, turn, "runner", Ct);
        var recovered = await store.ReadAsync(thread.Id, Ct);
        Assert.Empty(recovered.Turns.Single().Output);
        Assert.Equal("unknown", recovered.Turns.Single().Dispatches.Single().State);
        Assert.Equal(original.ToJsonString(), Assert.Single(Items(recovered), x => Type(x) == "bemItemPromoted").ToJsonString());
        var preview = Assert.Single(CodexThreadStore.ProjectRealtimeArtifactPreviews(recovered, turn));
        Assert.Equal("preview", preview["id"]!.GetValue<string>());
        Assert.Equal(extension.Preview.Text, preview["text"]!.GetValue<string>());
    }

    [Fact]
    public void StreamingPresentationRequiresExplicitPublicAssistantItemAndExactRequest()
    {
        var stream = new CodexRealtimeArtifactStream();
        static CodexResponseEvent Event(string request, string wire)
        {
            var json = Json(wire);
            return new(json.GetProperty("type").GetString()!, json, 0) { RequestId = request };
        }
        Assert.Null(stream.Observe(Event("r1", """{"type":"response.output_text.delta","item_id":"unknown","delta":"::codex-realtime-inline{}\nNot a known item"}""")));
        Assert.Null(stream.Observe(Event("r1", """{"type":"response.output_item.added","item":{"id":"private","type":"reasoning","role":"assistant","content":[]}}""")));
        Assert.Null(stream.Observe(Event("r1", """{"type":"response.output_text.delta","item_id":"private","delta":"::codex-realtime-inline{}\nNot public output"}""")));
        Assert.Null(stream.Observe(Event("r1", """{"type":"response.output_item.added","item":{"id":"public","type":"message","role":"assistant","content":[]}}""")));
        Assert.Null(stream.Observe(Event("r1", """{"type":"response.output_text.delta","item_id":"public","delta":"::codex-realtime-"}""")));
        var first = stream.Observe(Event("r1", """{"type":"response.output_text.delta","item_id":"public","delta":"inline{}\nVisible"}"""));
        Assert.NotNull(first);
        Assert.Equal("public", first.ItemId);
        Assert.Equal("r1", first.Preview.RequestId);
        var continuation = stream.Observe(Event("r1", """{"type":"response.output_text.delta","item_id":"public","delta":" more"}"""));
        Assert.NotNull(continuation);
        Assert.Equal(first.Preview.Text + " more", continuation.Preview.Text);
        Assert.Null(stream.Observe(Event("r2", """{"type":"response.output_text.delta","item_id":"public","delta":"::codex-inline-vis{second}"}""")));
    }

    [Fact]
    public async Task PromotesCompletedInlineMarkdownAndDistinctVisualizationsOnce()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [Json("""{"role":"user","content":"question"}""")], cancellationToken: Ct);
        var markdown = Message("markdown", "[NOTE] ::codex-realtime-inline{}\nVisible explanation");
        var visualizations = Message("visuals", "```\n::codex-inline-vis{hidden}\n```\n~~~\n::codex-inline-vis{also-hidden}\n~~~\n::codex-inline-vis{first}\n\uE200visualize\uE202{second}");
        var notReady = Message("no-body", "::codex-realtime-inline{}");
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner", [notReady, markdown, visualizations], Ct);
        var items = Items(thread);
        Assert.Equal(4, items.Length);
        Assert.Equal("inlineMarkdown", items[1]["presentation"]!["type"]!.GetValue<string>());
        Assert.Equal("markdown", items[1]["item_id"]!.GetValue<string>());
        Assert.All(items.Skip(2), item => Assert.Equal("inlineVisualization", item["presentation"]!["type"]!.GetValue<string>()));
        Assert.Equal([0, 1], items.Skip(2).Select(item => item["presentation"]!["index"]!.GetValue<int>()).ToArray());
        thread = await store.CompleteAsync(thread.Id, thread.Revision, turn, "runner", "completed", [notReady, markdown, visualizations], Ct);
        var reloaded = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(items.Select(x => x.ToJsonString()), Items(reloaded).Select(x => x.ToJsonString()));
        Assert.Equal(3, RealtimeHistoryTests.Session(reloaded).History!.Events.Count(e => e.Kind == "promotion"));
        await store.DetachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct);
    }

    [Fact]
    public async Task SuccessfulDynamicToolSplitsSpeechAndDelayedFinalDoesNotDuplicatePrefix()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        var failed = Json("""{"type":"function_call","id":"failed-item","name":"tool","call_id":"failed","arguments":"{}"}""");
        var successful = Json("""{"type":"function_call","id":"successful-item","name":"tool","call_id":"successful","arguments":"{}"}""");
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner", [failed, successful], Ct);
        async Task Observe(string id, string wire) => thread = (await store.RecordRealtimeObservationAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, id, RealtimeHistoryTests.Event(wire), Ct)).Thread;
        await Observe("delta-before", """{"type":"output_transcript.added","item":{"text":"Before tool. "}}""");
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "failed", "tool", failed, Ct)).Thread;
        thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "runner", "failed", "failed", cancellationToken: Ct);
        Assert.Single(Items(thread));
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "successful", "tool", successful, Ct)).Thread;
        thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "runner", "successful", "succeeded",
            Json("""{"type":"function_call_output","call_id":"successful","output":"confirmed"}"""), Ct);
        var projected = Items(thread);
        Assert.Equal(["realtimeSessionStarted", "frame", "bemItemPromoted"], projected.Select(Type).ToArray());
        Assert.Equal("Before tool. ", projected[1]["channels"]!["voice_transcript"]!["text"]!.GetValue<string>());
        Assert.Equal("successful-item", projected[2]["item_id"]!.GetValue<string>());
        await Observe("delta-after", """{"type":"output_transcript.added","item":{"text":"After tool."}}""");
        await Observe("full-final", """{"type":"turn.done","turn":{"role":"assistant","transcript":"Before tool. After tool."}}""");
        thread = (await store.SealRealtimeHistoryAsync(thread.Id, thread.Revision, "session", "owner", attachment, "seal-after", "chunk", Ct)).Thread;
        var frames = RealtimeHistoryTests.Session(await store.ReadAsync(thread.Id, Ct)).History!.Frames;
        Assert.Equal(["Before tool. ", "After tool."], frames.Select(f => f.Text).ToArray());
        Assert.Equal(projected.Select(x => x.ToJsonString()), Items(thread).Take(projected.Length).Select(x => x.ToJsonString()));
    }

    [Fact]
    public async Task SplitUserUtteranceRemainsOneHandoffWithAllSourceFrames()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        async Task Observe(string id, string wire) => thread = (await store.RecordRealtimeObservationAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, id, RealtimeHistoryTests.Event(wire), Ct)).Thread;
        await Observe("first-part", """{"type":"input_transcript.added","item":{"text":"First "}}""");
        var artifact = Message("inline", "::codex-realtime-inline{}\nartifact");
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner", [artifact], Ct);
        await Observe("second-part", """{"type":"input_transcript.added","item":{"text":"part"}}""");
        await Observe("final-user", """{"type":"turn.done","turn":{"role":"user","transcript":"First part"}}""");
        await Observe("handoff", """{"type":"delegation.created","item":{"type":"delegation","target":"client","id":"handoff","content":[{"type":"input_text","text":"First part"}]}}""");
        var session = RealtimeHistoryTests.Session(thread);
        var history = session.History!;
        var capture = Assert.Single(history.Handoffs);
        Assert.Equal(2, capture.FrameIds.Length);
        Assert.Equal(["First ", "part"], history.Frames.Select(f => f.Text).ToArray());
        Assert.All(history.Frames, f => Assert.Equal("handoff", f.HandoffId));
        var input = CodexThreadStore.PrepareRealtimeHandoffInput(session, "handoff")!;
        Assert.Equal(capture.FrameIds, input.SourceIds);
        var admission = await store.AdmitRealtimeHandoffAsync(thread.Id, thread.Revision, "session", "owner",
            attachment, "handoff", Guid.NewGuid(), turn, "runner", input.Text, Ct);
        Assert.True(admission.Admitted);
        Assert.All(RealtimeHistoryTests.Session(admission.Thread).History!.Frames, f => Assert.Equal("accepted", f.Disposition));
    }

    [Fact]
    public async Task InterruptedTurnRetainsOriginalVoiceSessionForLateSuccessfulTool()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        var call = Json("""{"type":"function_call","id":"late-item","name":"tool","call_id":"late","arguments":"{}"}""");
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner", [call], Ct);
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "late", "tool", call, Ct)).Thread;
        thread = await store.RecoverInterruptedAsync(thread.Id, thread.Revision, turn, "runner", Ct);
        var end = Guid.NewGuid();
        thread = (await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", attachment, end, Ct)).Thread;
        thread = await store.CompleteRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", end, Ct);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "second", "rtc_second", "v3", "owner", Ct);
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "second", "owner", Guid.NewGuid(), Ct);
        var previous = Items(thread);
        thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "runner", "late", "succeeded",
            Json("""{"type":"function_call_output","call_id":"late","output":"late result"}"""), Ct);
        var after = Items(await store.ReadAsync(thread.Id, Ct));
        Assert.Equal(previous.Select(x => x.ToJsonString()), after.Take(previous.Length).Select(x => x.ToJsonString()));
        Assert.Equal("bemItemPromoted", Type(after[^1]));
        Assert.Equal("session", after[^1]["realtimeSessionId"]!.GetValue<string>());
        Assert.Equal("late-item", after[^1]["item_id"]!.GetValue<string>());
        Assert.Equal(turn.ToString(), after[^1]["turn_id"]!.GetValue<string>());
    }
}
