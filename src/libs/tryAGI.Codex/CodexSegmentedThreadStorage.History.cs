using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Collections of original native journal records. These are not App Server wire
/// projections, and reading them never grants authority to execute an item.</summary>
public enum CodexThreadHistoryCollection
{
    /// <summary>Backing reasoning turns.</summary>
    Turns,
    /// <summary>Durable raw realtime observations and history boundaries.</summary>
    RealtimeObservations,
    /// <summary>Durable transcript frame projection.</summary>
    RealtimeFrames,
    /// <summary>Captured realtime handoffs.</summary>
    RealtimeHandoffs,
    /// <summary>Saved realtime reasoning inputs.</summary>
    RealtimeCarryover,
    /// <summary>Durable handoff admission receipts.</summary>
    RealtimeAdmissions,
    /// <summary>Saved realtime inputs inherited from a fork.</summary>
    InheritedRealtimeCarryover,
}

/// <summary>Continuation for one collection of one exact committed document.
/// A cursor is not an authorization token.</summary>
public sealed record CodexThreadHistoryCursor(Guid ThreadId, long Revision, string DocumentSha256,
    CodexThreadHistoryCollection Collection, string? SessionId, int NextOrdinal);

/// <summary>Bounded ordered native history records. Ordinals are zero-based.
/// UsedIndex distinguishes selective verified-page reads from legacy full-document reads.</summary>
public sealed record CodexThreadHistoryPage(long Revision, int StartOrdinal, int TotalCount,
    JsonElement[] Items, CodexThreadHistoryCursor? Next, bool UsedIndex);

public sealed partial class CodexSegmentedThreadStorage
{
    private const int MaximumIndexedManifestBytes = 1024 * 1024;
    private const int HistoryBlockTargetBytes = 256 * 1024;
    private const int HistoryBlockItems = 64;
    private const int MaximumIndexEntries = 8192;
    private const int MaximumHistoryReadBytes = 4 * 1024 * 1024;

    /// <summary>Read at most 256 original records from one committed collection, using only
    /// the physical pages covering its indexed range. Missing collections are empty.
    /// Old inline/v1 roots and v2 roots without a bounded index use a verified full read.
    /// Stale cursors fail before reading segments. A record larger than the bounded
    /// read budget requires the existing full-document API.</summary>
    public async Task<CodexThreadHistoryPage> ReadHistoryPageAsync(Guid threadId,
        CodexThreadHistoryCollection collection, string? sessionId = null, int pageSize = 64,
        CodexThreadHistoryCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        RequireThreadId(threadId);
        if (!ValidCollection(collection, sessionId)) throw new ArgumentException("Invalid native history collection scope.");
        if (pageSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (cursor is not null && (cursor.ThreadId != threadId || cursor.Collection != collection ||
            cursor.SessionId != sessionId || cursor.Revision < 1 || cursor.NextOrdinal < 0 ||
            !ValidDigest(cursor.DocumentSha256)))
            throw new ArgumentException("Invalid native history cursor.", nameof(cursor));
        cancellationToken.ThrowIfCancellationRequested();
        var root = await roots.ReadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Native thread not found.");
        var (revision, manifest) = ReadRoot(threadId, root);
        var digest = manifest?.DocumentSha256 ?? Digest(Encoding.UTF8.GetBytes(root.GetRawText()));
        if (cursor is not null && (cursor.Revision != revision || cursor.DocumentSha256 != digest))
            throw new CodexThreadConflictException();
        var start = cursor?.NextOrdinal ?? 0;
        if (manifest is { HistoryIndex: { } index })
        {
            var entry = index.SingleOrDefault(e => e.Collection == collection && e.SessionId == sessionId);
            var total = entry?.Count ?? 0;
            if (start > total) throw new ArgumentException("Native history cursor exceeds its collection.", nameof(cursor));
            if (start == total) return Result(total, [], true);
            var block = entry!.Blocks.Single(b => start >= b.FirstOrdinal && start < b.FirstOrdinal + b.Count);
            if (block.Length > MaximumHistoryReadBytes)
                throw new InvalidOperationException("Native history record exceeds the selective read budget; use the full-document API.");
            var bytes = await ReadHistoryRangeAsync(threadId, manifest, block, cancellationToken).ConfigureAwait(false);
            using var parsed = ParseHistoryRange(bytes, block.Count);
            var offset = start - block.FirstOrdinal;
            var items = SelectItems(parsed.RootElement, offset, Math.Min(pageSize, block.Count - offset));
            return Result(total, items, true);
        }

        var document = await ReadCapturedDocumentAsync(threadId, root, manifest, cancellationToken).ConfigureAwait(false);
        var values = FindHistoryCollection(document, collection, sessionId);
        var count = values.ValueKind == JsonValueKind.Array ? values.GetArrayLength() : 0;
        if (start > count) throw new ArgumentException("Native history cursor exceeds its collection.", nameof(cursor));
        return Result(count, count == start ? [] : SelectItems(values, start, Math.Min(pageSize, count - start)), false);

        CodexThreadHistoryPage Result(int total, JsonElement[] items, bool usedIndex)
        {
            var next = checked(start + items.Length);
            return new(revision, start, total, items,
                next < total ? new(threadId, revision, digest, collection, sessionId, next) : null, usedIndex);
        }
    }

    private static JsonElement[] SelectItems(JsonElement array, int offset, int count)
    {
        var result = new List<JsonElement>(count);
        var bytes = 0;
        for (var i = 0; i < count; i++)
        {
            var item = array[offset + i];
            var size = Encoding.UTF8.GetByteCount(item.GetRawText());
            if (size > MaximumHistoryReadBytes - bytes)
            {
                if (result.Count == 0)
                    throw new InvalidOperationException("Native history record exceeds the selective read budget; use the full-document API.");
                break;
            }
            bytes += size;
            result.Add(item.Clone());
        }
        return result.ToArray();
    }

    private async Task<byte[]> ReadHistoryRangeAsync(Guid threadId, CodexThreadSegmentManifest manifest,
        CodexThreadHistoryBlock block, CancellationToken ct)
    {
        var result = new byte[block.Length + 2];
        result[0] = (byte)'[';
        result[^1] = (byte)']';
        var end = block.Offset + block.Length;
        var pageOffset = 0;
        foreach (var page in manifest.Pages)
        {
            var pageEnd = pageOffset + page.Length;
            if (pageOffset >= end) break;
            if (pageEnd > block.Offset)
            {
                ct.ThrowIfCancellationRequested();
                var stored = await pages.ReadSegmentAsync(threadId, page.Sha256, ct).ConfigureAwait(false);
                if (stored is not { } data || data.Length != page.Length)
                    throw new InvalidDataException("Native thread journal page is missing or has a different length.");
                var owned = data.ToArray();
                if (Digest(owned) != page.Sha256)
                    throw new InvalidDataException("Native thread journal page digest mismatch.");
                var start = Math.Max(pageOffset, block.Offset);
                var stop = Math.Min(pageEnd, end);
                owned.AsSpan(start - pageOffset, stop - start).CopyTo(result.AsSpan(1 + start - block.Offset));
            }
            pageOffset = pageEnd;
        }
        if (Digest(result.AsSpan(1, block.Length)) != block.Sha256)
            throw new InvalidDataException("Native history range digest mismatch.");
        return result;
    }

    private static JsonDocument ParseHistoryRange(byte[] bytes, int count)
    {
        try
        {
            var result = JsonDocument.Parse(bytes);
            if (result.RootElement.GetArrayLength() == count &&
                result.RootElement.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Object)) return result;
            result.Dispose();
            throw new InvalidDataException("Native history range item count or type mismatch.");
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid indexed native history range.", error); }
    }

    private static bool ValidCollection(CodexThreadHistoryCollection collection, string? sessionId)
        => Enum.IsDefined(collection) && (collection is CodexThreadHistoryCollection.Turns or
            CodexThreadHistoryCollection.InheritedRealtimeCarryover
            ? sessionId is null : sessionId is { Length: > 0 and <= 512 } &&
                !string.IsNullOrWhiteSpace(sessionId) && !sessionId.Any(char.IsControl));

    private static string? HistoryProperty(CodexThreadHistoryCollection collection) => collection switch
    {
        CodexThreadHistoryCollection.RealtimeObservations => "events",
        CodexThreadHistoryCollection.RealtimeFrames => "frames",
        CodexThreadHistoryCollection.RealtimeHandoffs => "handoffs",
        CodexThreadHistoryCollection.RealtimeCarryover => "carryover",
        _ => null,
    };

    private static JsonElement FindHistoryCollection(JsonElement document, CodexThreadHistoryCollection collection, string? sessionId)
    {
        if (sessionId is null)
            return document.TryGetProperty(collection == CodexThreadHistoryCollection.Turns ? "turns" : "inheritedRealtimeCarryover", out var top)
                ? top : default;
        if (!document.TryGetProperty("realtimeSessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array) return default;
        foreach (var session in sessions.EnumerateArray())
        {
            if (!session.TryGetProperty("sessionId", out var id) || id.GetString() != sessionId) continue;
            if (collection == CodexThreadHistoryCollection.RealtimeAdmissions)
                return session.TryGetProperty("handoffAdmissions", out var admissions) ? admissions : default;
            if (session.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Object &&
                HistoryProperty(collection) is { } name && history.TryGetProperty(name, out var value)) return value;
        }
        return default;
    }

    private static CodexThreadHistoryIndexEntry[]? BuildHistoryIndex(byte[] bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Invalid native history document.");
            var result = new List<CodexThreadHistoryIndexEntry>();
            while (ReadProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "turns": AddHistoryArray(ref reader, bytes, CodexThreadHistoryCollection.Turns, result); break;
                    case "inheritedRealtimeCarryover":
                        AddHistoryArray(ref reader, bytes, CodexThreadHistoryCollection.InheritedRealtimeCarryover, result); break;
                    case "realtimeSessions": IndexRealtimeSessions(ref reader, bytes, result); break;
                    default: reader.Skip(); break;
                }
            }
            if (result.Count > MaximumIndexEntries || result.Sum(e => (long)e.Blocks.Length) > MaximumIndexEntries) return null;
            return result.ToArray();
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid native history document.", error); }
    }

    private static bool ReadProperty(ref Utf8JsonReader reader, out string name)
    {
        if (!reader.Read()) throw new InvalidDataException("Incomplete native history object.");
        if (reader.TokenType == JsonTokenType.EndObject) { name = ""; return false; }
        if (reader.TokenType != JsonTokenType.PropertyName) throw new InvalidDataException("Invalid native history property.");
        name = reader.GetString()!;
        if (!reader.Read()) throw new InvalidDataException("Missing native history property value.");
        return true;
    }

    private static void IndexRealtimeSessions(ref Utf8JsonReader reader, byte[] bytes, List<CodexThreadHistoryIndexEntry> result)
    {
        if (reader.TokenType == JsonTokenType.Null) return;
        if (reader.TokenType != JsonTokenType.StartArray) throw new InvalidDataException("Invalid native realtime session array.");
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Invalid native realtime session.");
            var local = new List<CodexThreadHistoryIndexEntry>();
            var properties = new HashSet<string>(StringComparer.Ordinal);
            string? sessionId = null;
            while (ReadProperty(ref reader, out var name))
            {
                if (!properties.Add(name)) throw new InvalidDataException("Duplicate native realtime session property.");
                switch (name)
                {
                    case "sessionId":
                        sessionId = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                    case "handoffAdmissions":
                        AddHistoryArray(ref reader, bytes, CodexThreadHistoryCollection.RealtimeAdmissions, local); break;
                    case "history":
                        if (reader.TokenType == JsonTokenType.Null) break;
                        if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Invalid native realtime history.");
                        var historyProperties = new HashSet<string>(StringComparer.Ordinal);
                        while (ReadProperty(ref reader, out var field))
                        {
                            if (!historyProperties.Add(field)) throw new InvalidDataException("Duplicate native realtime history property.");
                            var kind = field switch
                            {
                                "events" => CodexThreadHistoryCollection.RealtimeObservations,
                                "frames" => CodexThreadHistoryCollection.RealtimeFrames,
                                "handoffs" => CodexThreadHistoryCollection.RealtimeHandoffs,
                                "carryover" => CodexThreadHistoryCollection.RealtimeCarryover,
                                _ => (CodexThreadHistoryCollection?)null,
                            };
                            if (kind is { } collection) AddHistoryArray(ref reader, bytes, collection, local);
                            else reader.Skip();
                        }
                        break;
                    default: reader.Skip(); break;
                }
            }
            if (!ValidCollection(CodexThreadHistoryCollection.RealtimeObservations, sessionId) || !sessions.Add(sessionId!))
                throw new InvalidDataException("Invalid or duplicate native realtime session identity.");
            result.AddRange(local.Select(e => e with { SessionId = sessionId }));
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new InvalidDataException("Incomplete native realtime session array.");
    }

    private static void AddHistoryArray(ref Utf8JsonReader reader, byte[] bytes, CodexThreadHistoryCollection collection,
        List<CodexThreadHistoryIndexEntry> result)
    {
        if (reader.TokenType == JsonTokenType.Null) return;
        if (reader.TokenType != JsonTokenType.StartArray) throw new InvalidDataException("Invalid native history collection.");
        var blocks = new List<CodexThreadHistoryBlock>();
        var count = 0;
        var first = 0;
        var start = 0;
        var end = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Invalid native history item.");
            var itemStart = checked((int)reader.TokenStartIndex);
            reader.Skip();
            var itemEnd = checked((int)reader.BytesConsumed);
            if (count > first && (count - first == HistoryBlockItems || itemEnd - start > HistoryBlockTargetBytes)) Flush();
            if (count == first) start = itemStart;
            end = itemEnd;
            count++;
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new InvalidDataException("Incomplete native history collection.");
        Flush();
        if (count != 0) result.Add(new(collection, null, count, blocks.ToArray()));

        void Flush()
        {
            if (count == first) return;
            blocks.Add(new(first, count - first, start, end - start, Digest(bytes.AsSpan(start, end - start))));
            first = count;
        }
    }

    private static void ValidateHistoryIndex(JsonElement raw, CodexThreadHistoryIndexEntry[] index, int documentLength)
    {
        if (index.Length > MaximumIndexEntries) throw new InvalidDataException("Native history index exceeds its bound.");
        var keys = new HashSet<(CodexThreadHistoryCollection, string?)>();
        var ranges = new List<CodexThreadHistoryBlock>();
        foreach (var entry in index)
        {
            if (entry is null || !ValidCollection(entry.Collection, entry.SessionId) || !keys.Add((entry.Collection, entry.SessionId)) ||
                entry.Count <= 0 || entry.Blocks is not { Length: > 0 and <= MaximumIndexEntries })
                throw new InvalidDataException("Invalid native history index entry.");
            var count = 0;
            var end = 0;
            foreach (var block in entry.Blocks)
            {
                if (block is null || block.FirstOrdinal != count || block.Count is <= 0 or > HistoryBlockItems ||
                    block.Length <= 0 || block.Offset < end || block.Offset > documentLength - block.Length ||
                    block.Length > HistoryBlockTargetBytes && block.Count != 1 || !ValidDigest(block.Sha256))
                    throw new InvalidDataException("Invalid native history index range.");
                count = checked(count + block.Count);
                end = block.Offset + block.Length;
                ranges.Add(block);
                if (ranges.Count > MaximumIndexEntries) throw new InvalidDataException("Native history index exceeds its range bound.");
            }
            if (count != entry.Count) throw new InvalidDataException("Native history index ordinal count mismatch.");
        }
        var priorEnd = 0;
        foreach (var block in ranges.OrderBy(b => b.Offset))
        {
            if (block.Offset < priorEnd) throw new InvalidDataException("Native history ranges overlap.");
            priorEnd = block.Offset + block.Length;
        }
        foreach (var entry in raw.EnumerateArray())
        {
            RequireUniqueProperties(entry);
            foreach (var block in entry.GetProperty("blocks").EnumerateArray()) RequireUniqueProperties(block);
        }
    }

    private static string HistoryIndexDigest(CodexThreadHistoryIndexEntry[] index)
        => Digest(JsonSerializer.SerializeToUtf8Bytes(index, CodexThreadSegmentJson.Default.CodexThreadHistoryIndexEntryArray));

    private static bool SameHistoryIndex(CodexThreadHistoryIndexEntry[] first, CodexThreadHistoryIndexEntry[]? second)
        => second is not null && first.Length == second.Length &&
            first.Zip(second).All(p => p.First.Collection == p.Second.Collection && p.First.SessionId == p.Second.SessionId &&
                p.First.Count == p.Second.Count && p.First.Blocks.SequenceEqual(p.Second.Blocks));
}

internal sealed record CodexThreadHistoryIndexEntry(
    [property: JsonRequired] CodexThreadHistoryCollection Collection,
    [property: JsonRequired] string? SessionId,
    [property: JsonRequired] int Count,
    [property: JsonRequired] CodexThreadHistoryBlock[] Blocks);

internal sealed record CodexThreadHistoryBlock(
    [property: JsonRequired] int FirstOrdinal,
    [property: JsonRequired] int Count,
    [property: JsonRequired] int Offset,
    [property: JsonRequired] int Length,
    [property: JsonRequired] string Sha256);
