using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexRealtimeWorldStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [Theory]
    [InlineData("attached", true)]
    [InlineData("detached", true)]
    [InlineData("ended", false)]
    [InlineData("ending", false)]
    [InlineData("recoveryRequired", false)]
    public async Task MissingRetainedFragmentRendersStartOnlyForKnownActiveState(string state, bool emitsStart)
    {
        var (_, _, thread, _) = await RealtimeHistoryTests.Setup();
        var session = RealtimeHistoryTests.Session(thread) with
        {
            State = state, ModeInstructions = new("custom start", "custom end"),
        };
        var compacted = Json("""{"type":"compaction","encrypted_content":"opaque"}""");
        var capture = CodexRealtimeModeContext.CaptureProjected(thread, session, [compacted], 17);
        if (!emitsStart)
        {
            Assert.Null(capture);
            return;
        }
        Assert.True(capture!.Transition.Active);
        Assert.Equal(17, capture.Transition.OutputIndex);
        Assert.Equal("session", capture.Transition.SessionId);
        Assert.Equal("<realtime_conversation>\ncustom start\n</realtime_conversation>",
            capture.Message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task LatestRetainedFragmentDeterminesModeNotLatestMatchingRawOccurrence()
    {
        var (_, store, thread, _) = await RealtimeHistoryTests.Setup();
        var active = RealtimeHistoryTests.Session(thread) with
        {
            ModeInstructions = new("custom start", "custom end"),
        };
        var ended = active with { State = "ended" };
        var input = Json("""{"role":"user","content":"hello"}""");
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [input], cancellationToken: Ct);
        var start = CodexRealtimeModeContext.CaptureProjected(thread, active, [], 0)!;
        thread = await store.SynchronizeOutputCoreAsync(thread.Id, thread.Revision, turn, "runner",
            [start.Message], start, Ct);
        var end = CodexRealtimeModeContext.CaptureProjected(thread, ended, [start.Message], 1)!;
        thread = await store.SynchronizeOutputCoreAsync(thread.Id, thread.Revision, turn, "runner",
            [start.Message, end.Message], end, Ct);
        var restarted = CodexRealtimeModeContext.CaptureProjected(thread, active, [start.Message, end.Message], 2)!;
        thread = await store.SynchronizeOutputCoreAsync(thread.Id, thread.Revision, turn, "runner",
            [start.Message, end.Message, restarted.Message], restarted, Ct);

        // The same start text occurs later in the raw journal, but the projection
        // contains the older start followed by end. It is visibly inactive.
        var retained = new[] { start.Message, end.Message };
        var restored = CodexRealtimeModeContext.CaptureProjected(thread, active, retained, 3)!;
        Assert.True(restored.Transition.Active);
        Assert.Equal(3, restored.Transition.OutputIndex);
        Assert.True(JsonElement.DeepEquals(start.Message, restored.Message));
        Assert.Null(CodexRealtimeModeContext.CaptureProjected(thread, ended, retained, 3));
        Assert.Null(CodexRealtimeModeContext.CaptureProjected(thread, active, [restarted.Message], 3));
        Assert.False(CodexRealtimeModeContext.CaptureProjected(thread, ended, [restarted.Message], 3)!.Transition.Active);
        Assert.Null(CodexRealtimeModeContext.CaptureProjected(thread, ended, [], 3));
    }
}
