using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>A checkpoint into an immutable logical (uncompressed) Rust JSONL source.</summary>
/// <param name="SourceRevision">Host-verified immutable source revision.</param>
/// <param name="ByteOffset">Logical byte offset of the next record.</param>
/// <param name="NextLineNumber">One-based line number of the next record.</param>
public sealed record CodexRolloutReadCursor(string SourceRevision, long ByteOffset, long NextLineNumber);

/// <summary>Classification of a logical JSONL line. Unknown and damaged records are not replayable.</summary>
public enum CodexRolloutLineStatus
{
    /// <summary>Recognized rollout envelope, not necessarily a validated item payload.</summary>
    Record,
    /// <summary>Whitespace-only line.</summary>
    Blank,
    /// <summary>Unknown rollout discriminator, retained for forward-compatible migration.</summary>
    UnknownType,
    /// <summary>Invalid UTF-8 encoding.</summary>
    InvalidUtf8,
    /// <summary>Invalid JSON or malformed rollout envelope.</summary>
    InvalidJson,
    /// <summary>Line exceeds the configured bound; its bytes remain in the source.</summary>
    Oversized,
}

/// <summary>One classified source line and its precise continuation checkpoint.</summary>
/// <param name="StartOffset">Logical offset of the first byte.</param>
/// <param name="LineNumber">One-based source line number.</param>
/// <param name="NextCursor">Checkpoint after this line, including its newline if present.</param>
/// <param name="HasNewline">Whether an LF terminates the record.</param>
/// <param name="Status">Classification.</param>
/// <param name="RawLine">Exact source bytes excluding LF, or null for oversized lines.</param>
/// <param name="Record">Parsed envelope, if available.</param>
public sealed record CodexRolloutReadEntry(long StartOffset, long LineNumber, CodexRolloutReadCursor NextCursor,
    bool HasNewline, CodexRolloutLineStatus Status, ReadOnlyMemory<byte>? RawLine, CodexRolloutRecord? Record);

/// <summary>
/// Bounded, read-only Rust JSONL scanner. A caller supplies an immutable source revision and
/// a stream from that exact revision. The reader never repairs, truncates, rewrites, or dispatches
/// the source; a migration host may persist validated cursors alongside its own atomic checkpoint.
/// </summary>
public static class CodexRolloutReader
{
    /// <summary>Maximum supported single-line byte budget.</summary>
    public const int MaximumLineBytes = 16_777_216;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Scans one immutable source without materializing its history. A seekable stream supports
    /// restart directly at a validated logical byte offset. A nonseekable stream supports only
    /// the initial scan. The caller owns the stream and must use emitted cursors rather than
    /// its position because the scanner may buffer ahead.
    /// </summary>
    public static async IAsyncEnumerable<CodexRolloutReadEntry> ReadAsync(Stream source, string sourceRevision,
        CodexRolloutReadCursor? cursor = null, int maximumLineBytes = MaximumLineBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLineBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLineBytes, MaximumLineBytes);
        if (!source.CanRead) throw new ArgumentException("Rollout source must be readable.", nameof(source));
        cursor ??= new(sourceRevision, 0, 1);
        if (cursor.SourceRevision != sourceRevision || cursor.ByteOffset < 0 || cursor.NextLineNumber < 1 ||
            ((cursor.ByteOffset == 0) != (cursor.NextLineNumber == 1)))
            throw new ArgumentException("Rollout cursor belongs to a different source or is invalid.", nameof(cursor));
        if (source.CanSeek)
        {
            if (cursor.ByteOffset > source.Length) throw new InvalidDataException("Rollout cursor exceeds source length.");
            if (cursor.ByteOffset > 0 && cursor.ByteOffset < source.Length)
            {
                source.Seek(cursor.ByteOffset - 1, SeekOrigin.Begin);
                var boundary = new byte[1];
                if (await source.ReadAsync(boundary.AsMemory(), cancellationToken).ConfigureAwait(false) != 1 ||
                    boundary[0] != (byte)'\n')
                    throw new InvalidDataException("Rollout cursor is not at a record boundary.");
            }
            source.Seek(cursor.ByteOffset, SeekOrigin.Begin);
        }
        else if (cursor.ByteOffset != 0)
            throw new NotSupportedException("Resuming requires a seekable logical rollout stream.");

        var readBuffer = new byte[64 * 1024];
        using var lineBuffer = new MemoryStream();
        var position = cursor.ByteOffset;
        var lineStart = position;
        var lineNumber = cursor.NextLineNumber;
        var oversized = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await source.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var consumed = 0;
            while (consumed < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newline = Array.IndexOf(readBuffer, (byte)'\n', consumed, count - consumed);
                var length = (newline < 0 ? count : newline) - consumed;
                if (!oversized)
                {
                    if (lineBuffer.Length + length > maximumLineBytes)
                    {
                        oversized = true;
                        lineBuffer.SetLength(0);
                    }
                    else lineBuffer.Write(readBuffer, consumed, length);
                }
                position = checked(position + length);
                consumed += length;
                if (newline < 0) break;
                position = checked(position + 1);
                consumed++;
                var next = new CodexRolloutReadCursor(sourceRevision, position, checked(lineNumber + 1));
                var entry = Classify(lineBuffer, oversized, lineStart, lineNumber, next, hasNewline: true);
                lineBuffer.SetLength(0);
                lineStart = position;
                lineNumber = next.NextLineNumber;
                oversized = false;
                yield return entry;
            }
        }

        if (position != lineStart)
        {
            var next = new CodexRolloutReadCursor(sourceRevision, position, checked(lineNumber + 1));
            yield return Classify(lineBuffer, oversized, lineStart, lineNumber, next, hasNewline: false);
        }
    }

    private static CodexRolloutReadEntry Classify(MemoryStream line, bool oversized, long start, long lineNumber,
        CodexRolloutReadCursor next, bool hasNewline)
    {
        if (oversized)
            return new(start, lineNumber, next, hasNewline, CodexRolloutLineStatus.Oversized, null, null);
        var raw = line.ToArray();
        try { _ = StrictUtf8.GetCharCount(raw); }
        catch (DecoderFallbackException)
        {
            return new(start, lineNumber, next, hasNewline, CodexRolloutLineStatus.InvalidUtf8, raw, null);
        }
        if (IsBlank(raw))
            return new(start, lineNumber, next, hasNewline, CodexRolloutLineStatus.Blank, raw, null);

        try
        {
            var record = CodexRolloutRecord.Parse(raw.AsMemory());
            return new(start, lineNumber, next, hasNewline,
                record.IsKnownType ? CodexRolloutLineStatus.Record : CodexRolloutLineStatus.UnknownType, raw, record);
        }
        catch (JsonException)
        {
            return new(start, lineNumber, next, hasNewline, CodexRolloutLineStatus.InvalidJson, raw, null);
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> text)
    {
        // The entire line was UTF-8 validated; ordinary JSON needs no UTF-16 copy.
        while (!text.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(text, out var rune, out var consumed) != System.Buffers.OperationStatus.Done ||
                !Rune.IsWhiteSpace(rune)) return false;
            text = text[consumed..];
        }
        return true;
    }
}

/// <summary>The exclusive inherited prefix reference in a Rust paginated rollout.</summary>
/// <param name="RolloutId">Physical rollout ID, not necessarily the logical thread ID.</param>
/// <param name="EndOrdinalExclusive">First ordinal excluded from the prefix.</param>
/// <param name="EndByteOffset">Logical byte boundary of the prefix.</param>
public sealed record CodexRolloutHistoryBase(Guid RolloutId, ulong EndOrdinalExclusive, ulong EndByteOffset);

/// <summary>Immutable metadata and byte checkpoint for an already verified import prefix.</summary>
/// <param name="ThreadId">Logical thread identity.</param>
/// <param name="SessionId">Root session identity.</param>
/// <param name="IsPaginated">Whether the rollout has paginated history.</param>
/// <param name="HistoryBase">Optional external immutable prefix reference.</param>
/// <param name="SubagentHistoryStartOrdinal">First ordinal belonging to the subagent.</param>
/// <param name="LastOrdinal">Last verified durable ordinal.</param>
/// <param name="Cursor">Continuation in the exact immutable source.</param>
public sealed record CodexRolloutReplayCheckpoint(Guid ThreadId, Guid SessionId, bool IsPaginated,
    CodexRolloutHistoryBase? HistoryBase, ulong? SubagentHistoryStartOrdinal,
    ulong? LastOrdinal, CodexRolloutReadCursor Cursor);

/// <summary>
/// Validated header and ordinal fence for an immutable rollout import. This is not a payload
/// validator, authorization decision, persistence store, or automatic history reducer.
/// </summary>
public sealed class CodexRolloutReplayFence
{
    /// <summary>Stable logical thread identity.</summary>
    public Guid ThreadId { get; private init; }

    /// <summary>Root session identity (legacy metadata defaults to the thread ID).</summary>
    public Guid SessionId { get; private init; }

    /// <summary>Whether the source uses paginated rather than legacy history.</summary>
    public bool IsPaginated { get; private init; }

    /// <summary>Optional externally referenced immutable prefix.</summary>
    public CodexRolloutHistoryBase? HistoryBase { get; private init; }

    /// <summary>Exclusive end of the inherited subagent prefix, if present.</summary>
    public ulong? SubagentHistoryStartOrdinal { get; private init; }

    /// <summary>Latest observed durable ordinal for a paginated source.</summary>
    public ulong? LastOrdinal { get; private set; }

    /// <summary>Next contiguous logical source position.</summary>
    public CodexRolloutReadCursor Cursor { get; private set; } = null!;

    private CodexRolloutReplayFence() { }

    /// <summary>
    /// Captures the validated prefix. The embedding host must store this atomically with its
    /// imported history and retain the source revision. Capturing does not itself commit anything.
    /// </summary>
    public CodexRolloutReplayCheckpoint CaptureCheckpoint() =>
        new(ThreadId, SessionId, IsPaginated, HistoryBase, SubagentHistoryStartOrdinal, LastOrdinal, Cursor);

    /// <summary>
    /// Restores an integrity-checked checkpoint retrieved from trusted host storage, not from a
    /// client/model. The host verifies that its import and this checkpoint committed atomically
    /// against the exact source revision before resuming. No previous source records are read.
    /// </summary>
    public static CodexRolloutReplayFence RestoreCheckpoint(CodexRolloutReplayCheckpoint checkpoint,
        Guid expectedThreadId, string verifiedSourceRevision)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedSourceRevision);
        if (expectedThreadId == Guid.Empty || checkpoint.ThreadId != expectedThreadId ||
            checkpoint.SessionId == Guid.Empty || checkpoint.Cursor is null ||
            checkpoint.Cursor.SourceRevision != verifiedSourceRevision ||
            checkpoint.Cursor.ByteOffset <= 0 || checkpoint.Cursor.NextLineNumber < 2 ||
            checkpoint.IsPaginated != checkpoint.LastOrdinal.HasValue ||
            (!checkpoint.IsPaginated && checkpoint.HistoryBase is not null) ||
            (checkpoint.HistoryBase is { } prefix &&
                (prefix.RolloutId == Guid.Empty || checkpoint.LastOrdinal!.Value < prefix.EndOrdinalExclusive)))
            throw new InvalidDataException("Invalid rollout checkpoint or source identity.");
        return new()
        {
            ThreadId = checkpoint.ThreadId, SessionId = checkpoint.SessionId, IsPaginated = checkpoint.IsPaginated,
            HistoryBase = checkpoint.HistoryBase, SubagentHistoryStartOrdinal = checkpoint.SubagentHistoryStartOrdinal,
            LastOrdinal = checkpoint.LastOrdinal, Cursor = checkpoint.Cursor,
        };
    }

    /// <summary>
    /// Starts from the first nonblank source line. The caller must obtain it from the beginning
    /// of the authorized immutable source, without skipping any nonblank records.
    /// </summary>
    public static CodexRolloutReplayFence Begin(CodexRolloutReadEntry header, Guid expectedThreadId)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (expectedThreadId == Guid.Empty || header.Status != CodexRolloutLineStatus.Record ||
            header.Record is not { Type: "session_meta" } record || record.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Rollout must start with valid session metadata.");
        var meta = record.Payload;
        var thread = RequiredId(meta, "id");
        if (thread != expectedThreadId) throw new InvalidDataException("Rollout logical thread mismatch.");
        var session = meta.TryGetProperty("session_id", out _) ? RequiredId(meta, "session_id") : thread;
        var paginated = false;
        if (meta.TryGetProperty("history_mode", out var mode))
        {
            if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("legacy" or "paginated"))
                throw new InvalidDataException("Unknown rollout history mode.");
            paginated = mode.GetString() == "paginated";
        }

        CodexRolloutHistoryBase? historyBase = null;
        if (meta.TryGetProperty("history_base", out var prefix) && prefix.ValueKind != JsonValueKind.Null)
        {
            if (!paginated || prefix.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid inherited rollout history base.");
            historyBase = new(RequiredId(prefix, "thread_id"), RequiredOrdinal(prefix, "end_ordinal_exclusive"),
                RequiredOrdinal(prefix, "end_byte_offset"));
        }
        var subagentStart = meta.TryGetProperty("subagent_history_start_ordinal", out var start) &&
            start.ValueKind != JsonValueKind.Null ? RequiredOrdinal(meta, "subagent_history_start_ordinal") : (ulong?)null;
        if (paginated)
        {
            if (record.Ordinal != (historyBase?.EndOrdinalExclusive ?? 0))
                throw new InvalidDataException("Unexpected initial paginated rollout ordinal.");
        }
        else if (record.Ordinal is not null)
            throw new InvalidDataException("Legacy rollout contains a paginated ordinal.");

        return new()
        {
            ThreadId = thread, SessionId = session, IsPaginated = paginated, HistoryBase = historyBase,
            SubagentHistoryStartOrdinal = subagentStart, LastOrdinal = record.Ordinal, Cursor = header.NextCursor,
        };
    }

    /// <summary>
    /// Observes a contiguous source entry. Gaps in paginated ordinals are allowed, but duplicates,
    /// reversal, damaged records and unknown types cannot advance the verified checkpoint.
    /// </summary>
    public void Observe(CodexRolloutReadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.NextCursor.SourceRevision != Cursor.SourceRevision || entry.StartOffset != Cursor.ByteOffset ||
            entry.LineNumber != Cursor.NextLineNumber || entry.NextCursor.ByteOffset <= Cursor.ByteOffset ||
            entry.NextCursor.NextLineNumber != checked(Cursor.NextLineNumber + 1))
            throw new InvalidDataException("Rollout entry does not continue the verified source checkpoint.");

        if (entry.Status != CodexRolloutLineStatus.Blank)
        {
            if (entry.Status != CodexRolloutLineStatus.Record || entry.Record is not { } record ||
                record.Type == "session_meta")
                throw new InvalidDataException("Rollout entry requires explicit compatibility review before import.");
            if (IsPaginated)
            {
                if (record.Ordinal is not { } ordinal || ordinal <= LastOrdinal!.Value)
                    throw new InvalidDataException("Paginated rollout ordinal did not advance.");
                LastOrdinal = ordinal;
            }
            else if (record.Ordinal is not null)
                throw new InvalidDataException("Legacy rollout contains a paginated ordinal.");
        }
        Cursor = entry.NextCursor;
    }

    /// <summary>
    /// Computes the next local ordinal after EOF, rejecting incomplete inherited prefixes or
    /// overflow. A referenced prefix still requires independent host identity/digest verification.
    /// </summary>
    public ulong? NextOrdinalAtEnd()
    {
        if (!IsPaginated) return null;
        var ordinal = LastOrdinal!.Value;
        if (SubagentHistoryStartOrdinal is > 0 && ordinal < SubagentHistoryStartOrdinal.Value - 1)
            throw new InvalidDataException("Inherited subagent rollout prefix is incomplete.");
        if (ordinal == ulong.MaxValue) throw new InvalidDataException("Paginated rollout ordinal overflow.");
        return ordinal + 1;
    }

    private static Guid RequiredId(JsonElement json, string property)
    {
        if (!json.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            !value.TryGetGuid(out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid rollout identity: " + property);
        return id;
    }

    private static ulong RequiredOrdinal(JsonElement json, string property)
    {
        if (!json.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetUInt64(out var ordinal))
            throw new InvalidDataException("Invalid rollout history ordinal: " + property);
        return ordinal;
    }
}
