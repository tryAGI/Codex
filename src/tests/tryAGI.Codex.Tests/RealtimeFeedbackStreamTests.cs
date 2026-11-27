using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeFeedbackStreamTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "commentary")]
    [InlineData(2, "speakable")]
    public void FlushesAtTwoHundredMillisecondsAndImmediatelyFinishes(int mode, string? channel)
    {
        var clock = new ManualClock();
        var stream = new CodexRealtimeFeedbackStream((CodexRealtimeFeedbackMode)mode, clock);
        var handoff = stream.Activate("delegation");
        Assert.True(stream.Register(handoff, "item", CodexRealtimeMessagePhase.PartialAnswer, "Hello"));
        clock.Advance(199);
        Assert.Empty(stream.DrainDue(handoff));
        stream.Push(handoff, "item", " world");
        clock.Advance(1);
        var chunk = Assert.Single(stream.DrainDue(handoff));
        Assert.Equal("Hello world", chunk.Text);
        Assert.Equal(channel, chunk.Channel);
        Assert.Equal(handoff, chunk.Handoff);
        stream.Push(handoff, "item", "!");
        Assert.Empty(stream.DrainDue(handoff));
        var final = stream.Finish(handoff, "item");
        Assert.True(final.HasOutput);
        Assert.Equal("!", final.Chunk?.Text);
        Assert.False(stream.Finish(handoff, "item").Registered);
    }

    [Theory]
    [InlineData("[ANA", "LYSIS]reasoning", "commentary")]
    [InlineData("[COM", "MENTARY]progress", "commentary")]
    [InlineData("[FI", "NAL]answer", "speakable")]
    public void PreservesSplitBemEnvelopeAndRoutesRecognizedPhase(string first, string second, string channel)
    {
        var clock = new ManualClock();
        var stream = new CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode.BemTags, clock);
        var handoff = stream.Activate("delegation");
        stream.Register(handoff, "item", null, first);
        clock.Advance(200);
        Assert.Empty(stream.DrainDue(handoff));
        stream.Push(handoff, "item", second);
        var chunk = Assert.Single(stream.DrainDue(handoff));
        Assert.Equal(first + second, chunk.Text);
        Assert.Equal(channel, chunk.Channel);
        var wire = Assert.Single(CodexRealtimeProtocol.Context(chunk.Text, chunk.Handoff.Id, chunk.Channel));
        Assert.Equal("delegation.context.append", (string?)wire["type"]);
        Assert.Equal("delegation", (string?)wire["delegation_item_id"]);
        Assert.Equal(channel, (string?)wire["channel"]);
    }

    [Fact]
    public void CustomPrefixesReplaceDefaultsAndUnknownOutputWaitsForCompletion()
    {
        var clock = new ManualClock();
        var stream = new CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode.BemTags, clock,
            channelPrefixes: new Dictionary<string, IReadOnlyList<string>> { ["analysis"] = ["[THINKING]", ""] });
        var handoff = stream.Activate("delegation");
        stream.Register(handoff, "old", null, "[ANALYSIS]old");
        stream.Register(handoff, "new", null, "[THINK");
        clock.Advance(200);
        Assert.Empty(stream.DrainDue(handoff));
        stream.Push(handoff, "new", "ING]new");
        var chunk = Assert.Single(stream.DrainDue(handoff));
        Assert.Equal("commentary", chunk.Channel);
        Assert.Equal("[THINKING]new", chunk.Text);
        var final = stream.Finish(handoff, "old");
        Assert.Equal("speakable", final.Chunk?.Channel);
        Assert.Equal("[ANALYSIS]old", final.Chunk?.Text);
    }

    [Fact]
    public void BoundedHeadAndTailRemainValidUnicodeAcrossManyFlushes()
    {
        var clock = new ManualClock();
        var stream = new CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode.Thinking, clock);
        var handoff = stream.Activate("delegation");
        stream.Register(handoff, "item", null, new string('x', 1800));
        clock.Advance(200);
        var first = Assert.Single(stream.DrainDue(handoff)).Text;
        stream.Push(handoff, "item", string.Concat(Enumerable.Repeat("🧪", 3000)));
        clock.Advance(200);
        var second = Assert.Single(stream.DrainDue(handoff)).Text;
        stream.Push(handoff, "item", "END");
        var final = stream.Finish(handoff, "item");
        var result = first + second + final.Chunk?.Text;
        Assert.InRange(Encoding.UTF8.GetByteCount(result), 3990, 4000);
        Assert.StartsWith(new string('x', 1800), result);
        Assert.EndsWith("END", result);
        Assert.Contains(CodexRealtimeFeedbackStream.TruncationMarker, result);
        Assert.DoesNotContain("\uFFFD", result);
    }

    [Fact]
    public void UnrecognizedBemOutputIsBoundedUntilFinal()
    {
        var clock = new ManualClock();
        var stream = new CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode.BemTags, clock);
        var handoff = stream.Activate("delegation");
        stream.Register(handoff, "item", null, new string('a', 100_000));
        stream.Push(handoff, "item", "last");
        clock.Advance(200);
        Assert.Empty(stream.DrainDue(handoff));
        var result = stream.Finish(handoff, "item").Chunk;
        Assert.NotNull(result);
        Assert.Equal(4000, Encoding.UTF8.GetByteCount(result.Text));
        Assert.Equal("speakable", result.Channel);
        Assert.EndsWith("last", result.Text);
    }

    [Fact]
    public void SupersededGenerationCannotSendProgressOrCompleteEvenWithReusedIds()
    {
        var clock = new ManualClock();
        var stream = new CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode.Thinking, clock,
            backendReasoningStatus: true);
        var old = stream.Activate("delegation");
        stream.Register(old, "item", null, "old");
        clock.Advance(200);
        var queued = Assert.Single(stream.DrainDue(old));
        var current = stream.Activate("delegation");
        Assert.False(stream.IsCurrent(queued.Handoff));
        Assert.True(stream.Register(current, "item", null, "new"));
        Assert.False(stream.Push(old, "item", "stale"));
        Assert.False(stream.Finish(old, "item").Registered);
        Assert.Null(stream.Status(old, "old status"));
        Assert.False(stream.Register(current, "item", null));
        stream.Retire(old);
        Assert.Equal("new", stream.Finish(current, "item").Chunk?.Text);
        stream.Retire(current);
        Assert.False(stream.IsCurrent(current));
    }

    [Fact]
    public void QuietStatusDoesNotUseSpeakableChannelAndHonorsByteLimit()
    {
        var stream = new CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode.BemTags,
            backendReasoningStatus: true);
        var handoff = stream.Activate("delegation");
        var status = stream.Status(handoff, string.Concat(Enumerable.Repeat("🧪", 100)));
        Assert.NotNull(status);
        Assert.Equal("commentary", status.Channel);
        Assert.StartsWith("[STATUS] ", status.Text);
        Assert.Equal(265, Encoding.UTF8.GetByteCount(status.Text));
        Assert.DoesNotContain("\uFFFD", status.Text);
        var disabled = new CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode.Thinking, clientManagedHandoffs: true,
            backendReasoningStatus: true);
        var other = disabled.Activate("handoff");
        Assert.False(disabled.Register(other, "item", null));
        Assert.Null(disabled.Status(other, "status"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public void Advance(long milliseconds) => ticks += milliseconds;
    }
}
