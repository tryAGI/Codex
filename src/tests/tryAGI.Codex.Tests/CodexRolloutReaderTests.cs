using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class CodexRolloutReaderTests
{
    private static readonly Guid Thread = Guid.Parse("c8958e23-460a-4e93-8fc5-e58eea65a630");
    private static readonly Guid Rollout = Guid.Parse("b497ca87-8856-4b2a-a7e8-0ce1cc34dc0c");

    private static async Task<List<CodexRolloutReadEntry>> ScanAsync(Stream source, string revision = "rev",
        CodexRolloutReadCursor? cursor = null, int limit = CodexRolloutReader.MaximumLineBytes)
    {
        var entries = new List<CodexRolloutReadEntry>();
        await foreach (var entry in CodexRolloutReader.ReadAsync(source, revision, cursor, limit, TestContext.Current.CancellationToken))
            entries.Add(entry);
        return entries;
    }

    private static string Header(string extra = "", string ordinal = "") =>
        "{\"timestamp\":\"t\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + Thread + "\"" + extra + "}" + ordinal + "}";

    private static string Event(ulong ordinal) =>
        "{\"timestamp\":\"t\",\"ordinal\":" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}";

    [Fact]
    public async Task ReaderPreservesOffsetsBytesAndUnterminatedTail()
    {
        var header = Header();
        var body = header + "\r\n\n{broken\n" + Event(3);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var entries = await ScanAsync(stream);
        Assert.Equal(4, entries.Count);
        Assert.Equal(CodexRolloutLineStatus.Record, entries[0].Status);
        Assert.Equal(header + "\r", Encoding.UTF8.GetString(entries[0].RawLine!.Value.Span));
        Assert.Equal(header.Length + 2, entries[0].NextCursor.ByteOffset);
        Assert.Equal(CodexRolloutLineStatus.Blank, entries[1].Status);
        Assert.Equal(CodexRolloutLineStatus.InvalidJson, entries[2].Status);
        Assert.False(entries[3].HasNewline);
        Assert.Equal(3UL, entries[3].Record!.Ordinal);
        Assert.Equal(stream.Length, entries[3].NextCursor.ByteOffset);
        Assert.Equal(body, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task ReaderClassifiesInvalidUtf8OversizedAndFutureRecords()
    {
        const string future = "{\"timestamp\":\"t\",\"type\":\"future\",\"payload\":{}}";
        using var stream = new MemoryStream();
        stream.WriteByte(0xff);
        stream.WriteByte((byte)'\n');
        stream.Write(Encoding.UTF8.GetBytes(new string('x', 70_000) + "\n" + future));
        var entries = await ScanAsync(stream, limit: 128);
        Assert.Equal(CodexRolloutLineStatus.InvalidUtf8, entries[0].Status);
        Assert.Equal(CodexRolloutLineStatus.Oversized, entries[1].Status);
        Assert.Null(entries[1].RawLine);
        Assert.Equal(70_003L, entries[1].NextCursor.ByteOffset);
        Assert.Equal(CodexRolloutLineStatus.UnknownType, entries[2].Status);
        Assert.Equal(future, entries[2].Record!.Envelope.GetRawText());
    }

    [Fact]
    public async Task CursorRestartsAtLogicalBoundaryWithoutReadingPrefix()
    {
        var body = Header() + "\n" + Event(1) + "\n" + Event(2);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var entries = await ScanAsync(stream);
        var resumed = await ScanAsync(stream, cursor: entries[0].NextCursor);
        Assert.Equal(new ulong?[] { 1, 2 }, resumed.Select(x => x.Record!.Ordinal).ToArray());
        Assert.Equal(2, resumed[0].LineNumber);
        await Assert.ThrowsAsync<ArgumentException>(() => ScanAsync(stream, "changed", entries[0].NextCursor));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScanAsync(stream,
            cursor: entries[0].NextCursor with { ByteOffset = 3 }));
    }

    [Fact]
    public async Task PaginatedFencePreservesLogicalAndPhysicalIdentityAndOrdinalGaps()
    {
        var meta = ",\"history_mode\":\"paginated\",\"history_base\":{\"thread_id\":\"" + Rollout +
            "\",\"end_ordinal_exclusive\":4,\"end_byte_offset\":123},\"subagent_history_start_ordinal\":7";
        var body = Header(meta, ",\"ordinal\":4") + "\n\n" + Event(6);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var entries = await ScanAsync(stream);
        var fence = CodexRolloutReplayFence.Begin(entries[0], Thread);
        Assert.Equal(Thread, fence.ThreadId);
        Assert.Equal(Thread, fence.SessionId);
        Assert.Equal(Rollout, fence.HistoryBase!.RolloutId);
        Assert.Throws<InvalidDataException>(() => fence.NextOrdinalAtEnd());
        fence.Observe(entries[1]);
        fence.Observe(entries[2]);
        Assert.Equal(7UL, fence.NextOrdinalAtEnd());
        Assert.Equal(entries[2].NextCursor, fence.Cursor);
        Assert.Throws<InvalidDataException>(() => fence.Observe(entries[2]));
        Assert.Equal(entries[2].NextCursor, fence.Cursor);
    }

    [Fact]
    public async Task VerifiedCheckpointRestoresWithoutReprocessingThePrefix()
    {
        var body = Header(",\"history_mode\":\"paginated\"", ",\"ordinal\":0") + "\n" + Event(2) + "\n" + Event(4);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var entries = await ScanAsync(stream);
        var fence = CodexRolloutReplayFence.Begin(entries[0], Thread);
        fence.Observe(entries[1]);
        var checkpoint = fence.CaptureCheckpoint();
        var restored = CodexRolloutReplayFence.RestoreCheckpoint(checkpoint, Thread, "rev");
        Assert.Equal(checkpoint, restored.CaptureCheckpoint());
        var resumed = Assert.Single(await ScanAsync(stream, cursor: restored.Cursor));
        restored.Observe(resumed);
        Assert.Equal(5UL, restored.NextOrdinalAtEnd());
        Assert.Throws<InvalidDataException>(() => CodexRolloutReplayFence.RestoreCheckpoint(checkpoint, Thread, "changed"));
        Assert.Throws<InvalidDataException>(() => CodexRolloutReplayFence.RestoreCheckpoint(checkpoint, Rollout, "rev"));
        Assert.Throws<InvalidDataException>(() => CodexRolloutReplayFence.RestoreCheckpoint(
            checkpoint with { LastOrdinal = null }, Thread, "rev"));
    }

    [Fact]
    public async Task FenceRefusesCorruptionWithoutAdvancingCheckpoint()
    {
        var body = Header(",\"history_mode\":\"paginated\"", ",\"ordinal\":0") + "\n{broken\n" + Event(1);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var entries = await ScanAsync(stream);
        var fence = CodexRolloutReplayFence.Begin(entries[0], Thread);
        Assert.Throws<InvalidDataException>(() => fence.Observe(entries[1]));
        Assert.Throws<InvalidDataException>(() => fence.Observe(entries[2]));
        Assert.Equal(entries[0].NextCursor, fence.Cursor);
        Assert.Equal(0UL, fence.LastOrdinal);
    }

    [Fact]
    public async Task LegacyHeaderDefaultsSessionAndRefusesUnknownModeOrThread()
    {
        using var legacy = new MemoryStream(Encoding.UTF8.GetBytes(Header()));
        var header = Assert.Single(await ScanAsync(legacy));
        var fence = CodexRolloutReplayFence.Begin(header, Thread);
        Assert.False(fence.IsPaginated);
        Assert.Null(fence.NextOrdinalAtEnd());
        Assert.Equal(Thread, fence.SessionId);
        Assert.Throws<InvalidDataException>(() => CodexRolloutReplayFence.Begin(header, Rollout));
        using var unknown = new MemoryStream(Encoding.UTF8.GetBytes(Header(",\"history_mode\":\"future\"")));
        var unknownHeader = Assert.Single(await ScanAsync(unknown));
        Assert.Throws<InvalidDataException>(() => CodexRolloutReplayFence.Begin(unknownHeader, Thread));
    }
}
