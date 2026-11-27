using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Versioned proof of an exact consumed prefix of one append-only observation
/// collection. The host may persist this with its derived reducer state in one commit.
/// It is not a reducer snapshot, authorization token or dispatch receipt.</summary>
public sealed record CodexRealtimeObservationPrefix(int Version, Guid ThreadId, string SessionId,
    long Revision, string DocumentSha256, int NextOrdinal, string PrefixSha256);

/// <summary>New ordered observation records and the prefix through the last returned record.
/// The checkpoint may be saved only after the consumer has successfully applied these items.</summary>
public sealed record CodexRealtimeObservationDelta(long Revision, int StartOrdinal, int TotalCount,
    JsonElement[] Items, CodexRealtimeObservationPrefix Checkpoint)
{
    /// <summary>Whether the captured revision has additional records.</summary>
    public bool HasMore => Checkpoint.NextOrdinal < TotalCount;
}

public sealed partial class CodexSegmentedThreadStorage
{
    private const int ObservationPrefixVersion = 1;

    /// <summary>Read a bounded suffix of the append-only realtime observation journal.
    /// Unlike an ordinary history cursor, an observation prefix can cross revisions only
    /// if every previously consumed record is unchanged. Fully consumed index blocks are
    /// compared by their committed digests; at most one partial boundary block is re-read.
    /// The root/index must be trusted host-published metadata. This method never restores
    /// or authorizes a reducer or provider action. An absent index or changed prefix
    /// requires an explicit verified full replay by the host.</summary>
    public async Task<CodexRealtimeObservationDelta> ReadRealtimeObservationDeltaAsync(Guid threadId, string sessionId,
        CodexRealtimeObservationPrefix? checkpoint = null, int pageSize = 64,
        CancellationToken cancellationToken = default)
    {
        ValidateObservationDeltaRequest(threadId, sessionId, checkpoint, pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        var root = await roots.ReadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Native thread not found.");
        var (revision, manifest) = ReadRoot(threadId, root);
        return await ReadCapturedObservationDeltaAsync(threadId, sessionId, checkpoint, pageSize, revision,
            manifest, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateObservationDeltaRequest(Guid threadId, string sessionId,
        CodexRealtimeObservationPrefix? checkpoint, int pageSize)
    {
        RequireThreadId(threadId);
        if (!ValidCollection(CodexThreadHistoryCollection.RealtimeObservations, sessionId))
            throw new ArgumentException("Invalid realtime observation scope.", nameof(sessionId));
        if (pageSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (checkpoint is not null && (checkpoint.Version != ObservationPrefixVersion ||
            checkpoint.ThreadId != threadId || checkpoint.SessionId != sessionId ||
            checkpoint.Revision < 1 || checkpoint.NextOrdinal < 0 ||
            !ValidDigest(checkpoint.DocumentSha256) || !ValidDigest(checkpoint.PrefixSha256)))
            throw new ArgumentException("Invalid realtime observation prefix.", nameof(checkpoint));
    }

    private async Task<CodexRealtimeObservationDelta> ReadCapturedObservationDeltaAsync(Guid threadId, string sessionId,
        CodexRealtimeObservationPrefix? checkpoint, int pageSize, long revision, CodexThreadSegmentManifest? manifest,
        CancellationToken cancellationToken)
    {
        if (manifest is not { HistoryIndex: { } index })
            throw new InvalidOperationException("Realtime observation index is unavailable; verified full replay is required.");
        if (checkpoint is not null && (revision < checkpoint.Revision ||
            revision == checkpoint.Revision && checkpoint.DocumentSha256 != manifest.DocumentSha256))
            throw new CodexThreadConflictException();

        var collection = index.SingleOrDefault(e => e.Collection == CodexThreadHistoryCollection.RealtimeObservations &&
            e.SessionId == sessionId);
        var blocks = collection?.Blocks ?? [];
        var total = collection?.Count ?? 0;
        var start = checkpoint?.NextOrdinal ?? 0;
        if (start > total) throw new CodexThreadConflictException();
        // Only the boundary and newly delivered block may need their bytes. Retain the
        // already verified boundary for reuse by the page reader in this same call.
        var loaded = new Dictionary<CodexThreadHistoryBlock, byte[]>();
        if (checkpoint is not null && await PrefixDigestAsync(start).ConfigureAwait(false) != checkpoint.PrefixSha256)
            throw new CodexThreadConflictException();

        JsonElement[] items = [];
        if (start < total)
        {
            var block = blocks.Single(b => start >= b.FirstOrdinal && start < b.FirstOrdinal + b.Count);
            var bytes = await LoadBlockAsync(block).ConfigureAwait(false);
            using var parsed = ParseHistoryRange(bytes, block.Count);
            var offset = start - block.FirstOrdinal;
            items = SelectItems(parsed.RootElement, offset, Math.Min(pageSize, block.Count - offset));
        }
        var next = checked(start + items.Length);
        var proof = await PrefixDigestAsync(next).ConfigureAwait(false);
        return new(revision, start, total, items, new(ObservationPrefixVersion, threadId, sessionId,
            revision, manifest.DocumentSha256, next, proof));

        async Task<byte[]> LoadBlockAsync(CodexThreadHistoryBlock block)
        {
            if (loaded.TryGetValue(block, out var cached)) return cached;
            if (block.Length > MaximumHistoryReadBytes)
                throw new InvalidOperationException("Realtime observation exceeds the selective read budget; verified full replay is required.");
            var bytes = await ReadHistoryRangeAsync(threadId, manifest, block, cancellationToken).ConfigureAwait(false);
            using var parsed = ParseHistoryRange(bytes, block.Count);
            loaded.Add(block, bytes);
            return bytes;
        }

        async Task<string> PrefixDigestAsync(int count)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Scope is unambiguous because account and session identifiers forbid control characters.
            hash.AppendData(Encoding.UTF8.GetBytes("codex-realtime-observation-prefix-v1\n" +
                threadId.ToString("D") + "\n" + accountId + "\n" + sessionId + "\n"));
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.FirstOrdinal >= count) break;
                var included = Math.Min(block.Count, count - block.FirstOrdinal);
                var digest = included == block.Count ? block.Sha256 :
                    PrefixRangeDigest(await LoadBlockAsync(block).ConfigureAwait(false), included);
                AppendPrefixBlock(hash, included, digest);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    private static void AppendPrefixBlock(IncrementalHash hash, int count, string digest)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, count);
        hash.AppendData(length);
        hash.AppendData(Convert.FromHexString(digest));
    }

    private static string PrefixRangeDigest(byte[] arrayBytes, int count)
    {
        var reader = new Utf8JsonReader(arrayBytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("Invalid realtime observation range.");
        for (var i = 0; i < count; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("Incomplete realtime observation prefix.");
            reader.Skip();
        }
        // Include original separators and formatting, but not the synthetic array brackets.
        // The digest equals the original complete block if its prior tail has since grown.
        return Digest(arrayBytes.AsSpan(1, checked((int)reader.BytesConsumed) - 1));
    }
}
