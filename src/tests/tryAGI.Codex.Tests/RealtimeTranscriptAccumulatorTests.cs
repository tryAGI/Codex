using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeTranscriptAccumulatorTests
{
    private static CodexRealtimeEvent Event(string json, double ms = 0)
    {
        using var doc = JsonDocument.Parse(json);
        var raw = doc.RootElement.Clone();
        return new(raw.GetProperty("type").GetString()!, raw, ms);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V3InterleavedSpeechReconcilesInEitherFinalOrder(bool assistantFirst)
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3");
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"id":"f1","text":"What can "},"start_ms":10,"end_ms":30}""", 40));
        accumulator.Observe(Event("""{"type":"output_transcript.added","item":{"text":"A bunch "}}"""));
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"id":"f2","text":"you do?"}}"""));
        accumulator.Observe(Event("""{"type":"output_transcript.added","item":{"text":"of stuff"}}"""));

        var user = Event("""{"type":"turn.done","event_id":"u-final","turn":{"role":"user","transcript":"What can you do?"}}""");
        var assistant = Event("""{"type":"turn.done","event_id":"a-final","turn":{"role":"assistant","transcript":"A bunch of stuff, including coding."}}""");
        accumulator.Observe(assistantFirst ? assistant : user);
        accumulator.Observe(assistantFirst ? user : assistant);
        accumulator.Observe(user); // Stable event ID replay after reconnect.
        var tail = accumulator.TakeTail();
        Assert.Equal(new[] { "user", "assistant" }, tail.Select(e => e.Role));
        Assert.Equal(new[] { "What can you do?", "A bunch of stuff, including coding." }, tail.Select(e => e.Text));
        Assert.All(tail, e => Assert.True(e.Final));
        var fragment = tail[0].Fragments[0];
        Assert.Equal("f1", fragment.InputFragmentId);
        Assert.Equal(10, fragment.InputStartMs);
        Assert.Equal(30, fragment.InputEndMs);
        Assert.Equal(40, fragment.ReceivedAfterConnectMs);
        Assert.Equal("What can ", fragment.Payload.GetProperty("item").GetProperty("text").GetString());
        Assert.Equal(3, tail[0].Fragments.Length);
        Assert.Empty(accumulator.TakeTail());
    }

    [Fact]
    public void V3LateFinalPreservesNewDeltasAndRepeatedSpeechWithoutIdentityIsDistinct()
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3");
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"text":"new speech"}}"""));
        accumulator.Observe(Event("""{"type":"turn.done","turn":{"role":"user","transcript":"old speech"}}"""));
        Assert.Equal("new speech", Assert.Single(accumulator.Snapshot()).Text);

        for (var i = 0; i < 3; i++)
        {
            if (i < 2) accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"text":"Again"}}"""));
            accumulator.Observe(Event("""{"type":"turn.done","turn":{"role":"user","transcript":"Again."}}"""));
            accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"text":""}}"""));
        }
        Assert.Equal(new[] { "new speech", "Again.", "Again.", "Again." }, accumulator.TakeTail().Select(e => e.Text));
    }

    [Fact]
    public void HandoffBetweenDeltasCapturesOnlyOnceAndRetainsNextUtterance()
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3");
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"id":"one","text":"ask"}}"""));
        var handoff = Event("""{"type":"delegation.created","item":{"id":"d1","type":"delegation","target":"client","content":[{"type":"input_text","text":"ask"}]}}""");
        var first = accumulator.Observe(handoff);
        Assert.NotNull(first);
        Assert.True(first.IsNew);
        Assert.Equal("ask", Assert.Single(first.Entries).Text);
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"id":"two","text":"next"}}"""));
        var replay = accumulator.Observe(handoff);
        Assert.NotNull(replay);
        Assert.False(replay.IsNew);
        Assert.Equal("ask", Assert.Single(replay.Entries).Text);
        Assert.Equal("next", Assert.Single(accumulator.TakeTail()).Text);
        var conflict = Event("""{"type":"delegation.created","item":{"id":"d1","type":"delegation","target":"client","content":[{"type":"input_text","text":"different"}]}}""");
        Assert.Throws<InvalidOperationException>(() => accumulator.Observe(conflict));
    }

    [Fact]
    public void CompletedPrefixUsesUtf8AndKeepsOtherSpeakerAndHandoffDoesNotEchoFlushedUserInput()
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3");
        accumulator.Observe(Event("""{"type":"turn.done","turn":{"role":"user","transcript":"😀"}}"""));
        Assert.Empty(accumulator.TakeCompleted(11)); // user (4) + emoji (4) + framing (3)
        Assert.Equal("😀", Assert.Single(accumulator.TakeCompleted(10)).Text);

        accumulator.Observe(Event("""{"type":"output_transcript.added","item":{"text":"ongoing"}}"""));
        accumulator.Observe(Event("""{"type":"turn.done","turn":{"role":"user","transcript":"next"}}"""));
        Assert.Empty(accumulator.TakeCompleted(0)); // The assistant precedes the completed user turn.
        var handoff = accumulator.Observe(Event("""{"type":"delegation.created","item":{"id":"d2","type":"delegation","target":"client","content":[{"type":"input_text","text":"😀"}]}}"""));
        Assert.NotNull(handoff);
        Assert.Equal(new[] { "ongoing", "next" }, handoff.Entries.Select(e => e.Text));
        Assert.Empty(accumulator.Snapshot());
    }

    [Theory]
    [InlineData("v1", "conversation.input_transcript.delta", "conversation.input_transcript.turn_marked")]
    [InlineData("v2", "conversation.item.input_audio_transcription.delta", "conversation.item.input_audio_transcription.completed")]
    public void LegacyAdjacentRunsUseFinalReplacement(string protocol, string delta, string done)
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator(protocol);
        accumulator.Observe(Event($$"""{"type":"{{delta}}","delta":"old "}"""));
        accumulator.Observe(Event($$"""{"type":"{{delta}}","delta":"words"}"""));
        accumulator.Observe(Event($$"""{"type":"{{done}}","item_id":"user-item","transcript":"corrected"}"""));
        accumulator.Observe(Event($$"""{"type":"{{done}}","item_id":"user-item","transcript":"corrected"}"""));
        accumulator.Observe(Event("""{"type":"response.output_audio_transcript.delta","delta":"answer"}"""));
        accumulator.Observe(Event("""{"type":"response.output_audio_transcript.done","item_id":"assistant-item","transcript":"answer."}"""));
        Assert.Empty(accumulator.TakeCompleted(0)); // Only V3 flushes the completed prefix.
        var tail = accumulator.TakeTail();
        Assert.Equal(new[] { "corrected", "answer." }, tail.Select(e => e.Text));
        Assert.Equal(3, tail[0].Fragments.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyEmptyDeltaResetsSpeechAndResponseBoundariesWithoutChangingOldJournalReplay(bool replayOldJournal)
    {
        const string protocol = "v2";
        const string inputDelta = "conversation.item.input_audio_transcription.delta";
        var accumulator = replayOldJournal
            ? new CodexRealtimeTranscriptAccumulator(protocol, 4_194_304, canonicalLegacySegments: false)
            : new CodexRealtimeTranscriptAccumulator(protocol);
        accumulator.Observe(Event($$"""{"type":"{{inputDelta}}","delta":"first"}""") with { Protocol = protocol });
        accumulator.Observe(Event("""{"type":"input_audio_buffer.speech_started"}""") with { Protocol = protocol });
        accumulator.Observe(Event($$"""{"type":"{{inputDelta}}","delta":""}""") with { Protocol = protocol });
        accumulator.Observe(Event($$"""{"type":"{{inputDelta}}","delta":" second"}""") with { Protocol = protocol });
        accumulator.Observe(Event("""{"type":"response.output_audio_transcript.delta","delta":"answer"}""") with { Protocol = protocol });
        accumulator.Observe(Event("""{"type":"response.created","response":{"id":"next"}}""") with { Protocol = protocol });
        accumulator.Observe(Event("""{"type":"response.output_audio_transcript.delta","delta":""}""") with { Protocol = protocol });
        accumulator.Observe(Event("""{"type":"response.output_audio_transcript.delta","delta":" continued"}""") with { Protocol = protocol });
        Assert.Equal(replayOldJournal ? new[] { "first", " second", "answer", " continued" } :
            new[] { "first second", "answer continued" }, accumulator.TakeTail().Select(e => e.Text));
    }

    [Theory]
    [InlineData("user", "input_transcript.added")]
    [InlineData("assistant", "output_transcript.added")]
    public void V3RepeatedCompletedUtterancesIncludingFinalOnlyRemainDistinct(string role, string delta)
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3");
        for (var i = 0; i < 3; i++)
        {
            if (i < 2)
                accumulator.Observe(Event($$$"""{"type":"{{{delta}}}","item":{"text":"Again"}}""") with { Protocol = "v3" });
            accumulator.Observe(Event($$$"""{"type":"turn.done","turn":{"role":"{{{role}}}","transcript":"Again."}}""") with { Protocol = "v3" });
            accumulator.Observe(Event($$$"""{"type":"{{{delta}}}","item":{"text":""}}""") with { Protocol = "v3" });
        }
        var tail = accumulator.TakeTail();
        Assert.Equal(3, tail.Length);
        Assert.All(tail, e =>
        {
            Assert.Equal(role, e.Role);
            Assert.Equal("Again.", e.Text);
            Assert.True(e.Final);
        });
        Assert.Empty(accumulator.TakeTail());
    }

    [Fact]
    public void V1HandoffConsumesOnlyTheCoveredTranscriptAndLeavesTheTail()
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v1");
        Assert.Empty(accumulator.TakeTail());
        accumulator.Observe(Event("""{"type":"conversation.input_transcript.delta","delta":"already handed off"}""") with { Protocol = "v1" });
        var handoff = accumulator.Observe(Event("""{"type":"conversation.handoff.requested","handoff_id":"handoff_1","item_id":"item_1","input_transcript":"already handed off"}""") with { Protocol = "v1" });
        Assert.NotNull(handoff);
        Assert.Equal("handoff_1", handoff.Id);
        Assert.Equal("already handed off", handoff.InputTranscript);
        var covered = Assert.Single(handoff.Entries);
        Assert.Equal("user", covered.Role);
        Assert.Equal("already handed off", covered.Text);
        accumulator.Observe(Event("""{"type":"response.output_audio_transcript.delta","delta":"tail"}""") with { Protocol = "v1" });
        var tail = Assert.Single(accumulator.TakeTail());
        Assert.Equal("assistant", tail.Role);
        Assert.Equal("tail", tail.Text);
        Assert.Empty(accumulator.TakeTail());
    }

    [Fact]
    public void RejectedOversizedFinalDoesNotConsumePresentedPrefixAndRetainsOriginalFragment()
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3", 300);
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"text":"prefix"}}"""), "prefix");
        Assert.Equal("prefix", Assert.Single(accumulator.TakePresentationBoundary()).Text);
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"text":"suffix"}}"""), "suffix");
        var oversized = Event($$$"""{"type":"turn.done","turn":{"role":"user","transcript":"prefixsuffix{{{new string('x', 400)}}}"}}""");
        Assert.Throws<InvalidOperationException>(() => accumulator.Observe(oversized, "final"));
        Assert.Equal("suffix", Assert.Single(accumulator.Snapshot()).Text);
        accumulator.Observe(Event("""{"type":"turn.done","turn":{"role":"user","transcript":"prefixsuffix!"}}"""), "final");
        var tail = Assert.Single(accumulator.TakeTail());
        Assert.Equal("suffix!", tail.Text);
        Assert.True(tail.Final);
        Assert.Equal("prefixsuffix!", tail.Fragments[^1].Text);
        Assert.Equal("final", tail.Fragments[^1].ObservationId);
        Assert.Equal("prefixsuffix!", tail.Fragments[^1].Payload.GetProperty("turn").GetProperty("transcript").GetString());
    }

    [Fact]
    public void StableObservationIdsSurviveReplayAndRejectConflictsWithoutMutatingState()
    {
        var source = Event("""{"type":"input_transcript.added","item":{"text":"same"}}""");
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3");
        accumulator.Observe(source, "observation-1");
        accumulator.Observe(source, "observation-1");
        Assert.Equal("same", Assert.Single(accumulator.Snapshot()).Text);
        Assert.Throws<InvalidOperationException>(() => accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"text":"conflict"}}"""), "observation-1"));
        Assert.Equal("same", Assert.Single(accumulator.Snapshot()).Text);
        var restarted = new CodexRealtimeTranscriptAccumulator("v3");
        restarted.Observe(source, "observation-1");
        Assert.Equal(Assert.Single(accumulator.Snapshot()).Id, Assert.Single(restarted.Snapshot()).Id);
    }

    [Fact]
    public void BoundsFailBeforeMutationAndDoNotConsumeIdentity()
    {
        var accumulator = new CodexRealtimeTranscriptAccumulator("v3", 220);
        var oversized = Event($$$"""{"type":"input_transcript.added","item":{"text":"{{{new string('a', 400)}}}"}}""");
        Assert.Throws<InvalidOperationException>(() => accumulator.Observe(oversized, "retry"));
        Assert.Empty(accumulator.Snapshot());
        accumulator.Observe(Event("""{"type":"input_transcript.added","item":{"text":"ok"}}"""), "retry");
        Assert.Equal("ok", Assert.Single(accumulator.Snapshot()).Text);
    }
}
