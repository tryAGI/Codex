using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Outcome of publishing the optional derived projection cache, independent of
/// the captured authoritative thread revision.</summary>
public enum CodexRealtimeProjectionCacheStatus
{
    /// <summary>The captured checkpoint was durably installed.</summary>
    Saved,
    /// <summary>The same committed checkpoint required no write.</summary>
    Unchanged,
    /// <summary>Another cache writer won the CAS; the returned projection is still from its captured source.</summary>
    Conflict,
    /// <summary>A lost cache write receipt was reconciled against the exact committed root.</summary>
    Reconciled,
}

/// <summary>A read-only projection and the independent outcome of caching it.</summary>
public sealed record CodexCachedRealtimeProjection(CodexRealtimeProjectionRead Projection,
    CodexRealtimeProjectionCacheStatus CacheStatus);

/// <summary>Optional durable read-side cache for the existing segmented realtime reducer.
/// Supply dedicated cache root and page namespaces, not the authoritative thread namespace.
/// Both must use the existing account authorization, immutable-page retention and durability
/// contracts. The host controls encryption, retention and deletion of sensitive checkpoint data.
/// This adapter never writes a thread root or authorizes external execution.</summary>
public sealed class CodexPersistentRealtimeProjectionCache
{
    private const string Kind = "codexRealtimeProjectionCache";
    private const int PageBytes = 512 * 1024;
    private const int MaximumBytes = CodexThreadStore.MaximumDocumentBytes;
    private const int MaximumPages = MaximumBytes / PageBytes;
    private const int MaximumRootBytes = 128 * 1024;
    private readonly ICodexThreadStorage roots;
    private readonly ICodexThreadSegmentStorage pages;
    private readonly string accountId;

    /// <summary>Bind trusted, separately namespaced root CAS and immutable page storage.
    /// Do not use a storage implementation that requires roots to be native thread documents.</summary>
    public CodexPersistentRealtimeProjectionCache(ICodexThreadStorage roots,
        ICodexThreadSegmentStorage pages, string accountId)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(pages);
        if (!Identity(accountId)) throw new ArgumentException("Invalid cache account identity.", nameof(accountId));
        this.roots = roots;
        this.pages = pages;
        this.accountId = accountId;
    }

    internal void RequireSeparateNamespace(ICodexThreadStorage sourceRoots, ICodexThreadSegmentStorage sourcePages)
    {
        if (ReferenceEquals(roots, sourceRoots) || ReferenceEquals(pages, sourcePages))
            throw new ArgumentException("Realtime projection cache requires dedicated root and page namespaces.");
    }

    internal async Task<CodexCachedRealtimeProjection> ReadAsync(CodexSegmentedThreadStorage source,
        string sourceAccountId, Guid threadId, string sessionId, int pageSize, CancellationToken ct)
    {
        if (sourceAccountId != accountId)
            throw new UnauthorizedAccessException("Realtime projection cache account does not match.");
        if (threadId == Guid.Empty || !Identity(sessionId))
            throw new ArgumentException("Invalid realtime projection cache scope.");
        if (pageSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pageSize));
        ct.ThrowIfCancellationRequested();
        var key = CacheKey(threadId, sessionId);
        var prior = await ReadRootAsync(key, threadId, sessionId, ct).ConfigureAwait(false);
        CodexRealtimeProjectionEnvelope? checkpoint = null;
        if (prior is not null)
        {
            try
            {
                checkpoint = await ReadCheckpointAsync(prior, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // A damaged derived payload can be rebuilt. An invalid root identity or
                // generation, in contrast, was rejected before reaching this point.
            }
        }

        CodexRealtimeProjectionRead projection;
        try
        {
            projection = await source.ReadRealtimeProjectionCoreAsync(threadId, sessionId,
                checkpoint, pageSize, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (checkpoint is not null &&
            error is CodexThreadConflictException or InvalidDataException)
        {
            // Rebuild once from the same authorized source API, never from the cache.
            // Source corruption still fails closed; a rollback cannot replace a newer cache.
            checkpoint = null;
            projection = await source.ReadRealtimeProjectionAsync(threadId, sessionId,
                null, pageSize, ct).ConfigureAwait(false);
        }

        var envelope = CodexRealtimeProjectionCodec.Decode(projection.Checkpoint, out var bytes);
        var prefix = envelope.Prefix;
        if (envelope.AccountId != accountId || prefix.ThreadId != threadId ||
            prefix.SessionId != sessionId || prefix.Revision != projection.Revision ||
            prefix.NextOrdinal != projection.ProcessedRecords)
            throw new InvalidDataException("Realtime projection checkpoint does not match its source result.");
        if (prior is not null)
        {
            if (prefix.Revision < prior.SourceRevision ||
                prefix.Revision == prior.SourceRevision && prefix.DocumentSha256 != prior.SourceDocumentSha256)
                throw new CodexThreadConflictException();
            if (checkpoint is not null && prefix.Revision == prior.SourceRevision &&
                projection.AppliedRecords == 0 && prefix.NextOrdinal == prior.ProcessedRecords)
                return new(projection, CodexRealtimeProjectionCacheStatus.Unchanged);
        }

        var references = new List<CodexThreadSegmentReference>();
        var staged = new HashSet<CodexThreadSegmentReference>();
        var retained = prior?.Pages.ToHashSet() ?? [];
        for (var offset = 0; offset < bytes.Length; offset += PageBytes)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = bytes.AsMemory(offset, Math.Min(PageBytes, bytes.Length - offset));
            var reference = new CodexThreadSegmentReference(Digest(chunk.Span), chunk.Length);
            references.Add(reference);
            // Only a verified retained checkpoint proves that its referenced pages
            // exist. A rebuild after corruption must stage even matching digests.
            if (staged.Add(reference) && (checkpoint is null || !retained.Contains(reference)))
                await pages.PutSegmentAsync(key, reference.Sha256, chunk, ct).ConfigureAwait(false);
        }
        var candidate = new CodexRealtimeProjectionCacheRoot(Kind, 1, key, accountId,
            threadId, sessionId, checked((prior?.Revision ?? 0) + 1), prefix.Revision,
            prefix.DocumentSha256, prefix.NextOrdinal, bytes.Length, projection.Checkpoint.StateSha256,
            references.ToArray());
        var raw = JsonSerializer.SerializeToElement(candidate,
            CodexRealtimeProjectionCacheJson.Default.CodexRealtimeProjectionCacheRoot);
        ValidateRoot(raw, candidate, key, threadId, sessionId);
        ct.ThrowIfCancellationRequested();
        try
        {
            var saved = await roots.CompareExchangeAsync(key, prior?.Revision, raw, ct).ConfigureAwait(false);
            return new(projection, saved ? CodexRealtimeProjectionCacheStatus.Saved :
                CodexRealtimeProjectionCacheStatus.Conflict);
        }
        catch (IOException)
        {
            // The commit may have succeeded. Check the exact candidate; never retry
            // the write with a fresh generation or claim a different writer's result.
            var observed = await roots.ReadAsync(key, ct).ConfigureAwait(false);
            if (observed is { } actual && JsonElement.DeepEquals(raw, actual))
                return new(projection, CodexRealtimeProjectionCacheStatus.Reconciled);
            throw;
        }
    }

    private Guid CacheKey(Guid threadId, string sessionId)
    {
        // Domain separation and control-free identities make this encoding unambiguous.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            "codex-realtime-projection-cache-v1\n" + accountId + "\n" + threadId.ToString("D") + "\n" + sessionId));
        var key = new Guid(bytes.AsSpan(0, 16));
        if (key == Guid.Empty || key == threadId)
            throw new InvalidOperationException("Invalid derived cache key.");
        return key;
    }

    private async Task<CodexRealtimeProjectionCacheRoot?> ReadRootAsync(Guid key, Guid threadId,
        string sessionId, CancellationToken ct)
    {
        var raw = await roots.ReadAsync(key, ct).ConfigureAwait(false);
        if (raw is null) return null;
        if (raw.Value.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(raw.Value.GetRawText()) > MaximumRootBytes)
            throw new InvalidDataException("Invalid realtime projection cache root.");
        try
        {
            var root = raw.Value.Deserialize(CodexRealtimeProjectionCacheJson.Default.CodexRealtimeProjectionCacheRoot)
                ?? throw new InvalidDataException("Missing realtime projection cache root.");
            ValidateRoot(raw.Value, root, key, threadId, sessionId);
            return root;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid realtime projection cache root.", error);
        }
    }

    private void ValidateRoot(JsonElement raw, CodexRealtimeProjectionCacheRoot root, Guid key,
        Guid threadId, string sessionId)
    {
        if (root.Kind != Kind || root.Version != 1 || root.Id != key || root.AccountId != accountId ||
            root.ThreadId != threadId || root.SessionId != sessionId || root.Revision <= 0 ||
            root.SourceRevision <= 0 || root.ProcessedRecords is < 0 or > 32768 ||
            !ValidDigest(root.SourceDocumentSha256) || !ValidDigest(root.StateSha256) ||
            root.StateLength is <= 0 or > MaximumBytes || root.Pages is not { Length: > 0 and <= MaximumPages } ||
            Encoding.UTF8.GetByteCount(raw.GetRawText()) > MaximumRootBytes)
            throw new InvalidDataException("Realtime projection cache root scope or bounds are invalid.");
        UniqueProperties(raw);
        foreach (var page in raw.GetProperty("pages").EnumerateArray()) UniqueProperties(page);
        long length = 0;
        for (var i = 0; i < root.Pages.Length; i++)
        {
            var page = root.Pages[i];
            if (page is null || !ValidDigest(page.Sha256) || page.Length is <= 0 or > PageBytes ||
                i + 1 < root.Pages.Length && page.Length != PageBytes)
                throw new InvalidDataException("Invalid realtime projection checkpoint page.");
            length += page.Length;
        }
        if (length != root.StateLength)
            throw new InvalidDataException("Realtime projection checkpoint page lengths do not match.");
    }

    private async Task<CodexRealtimeProjectionEnvelope> ReadCheckpointAsync(
        CodexRealtimeProjectionCacheRoot root, CancellationToken ct)
    {
        var bytes = new byte[root.StateLength];
        var offset = 0;
        foreach (var page in root.Pages)
        {
            ct.ThrowIfCancellationRequested();
            var stored = await pages.ReadSegmentAsync(root.Id, page.Sha256, ct).ConfigureAwait(false);
            if (stored is not { } data || data.Length != page.Length)
                throw new InvalidDataException("Realtime projection checkpoint page is missing or truncated.");
            data.CopyTo(bytes.AsMemory(offset, page.Length));
            if (Digest(bytes.AsSpan(offset, page.Length)) != page.Sha256)
                throw new InvalidDataException("Realtime projection checkpoint page digest mismatch.");
            offset += page.Length;
        }
        var envelope = CodexRealtimeProjectionCodec.DecodeBytes(bytes, root.StateSha256);
        var prefix = envelope.Prefix;
        if (envelope.AccountId != root.AccountId || prefix.ThreadId != root.ThreadId ||
            prefix.SessionId != root.SessionId || envelope.Scope.SessionId != root.SessionId ||
            prefix.Revision != root.SourceRevision || prefix.DocumentSha256 != root.SourceDocumentSha256 ||
            prefix.NextOrdinal != root.ProcessedRecords)
            throw new InvalidDataException("Realtime projection checkpoint does not match its committed root.");
        return envelope;
    }

    private static bool Identity(string? value)
        => value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool ValidDigest(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid realtime projection cache object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate realtime projection cache field.");
    }
}

internal sealed record CodexRealtimeProjectionCacheRoot(string Kind, int Version, Guid Id,
    string AccountId, Guid ThreadId, string SessionId, long Revision, long SourceRevision,
    string SourceDocumentSha256, int ProcessedRecords, int StateLength, string StateSha256,
    CodexThreadSegmentReference[] Pages);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CodexRealtimeProjectionCacheRoot))]
internal partial class CodexRealtimeProjectionCacheJson : JsonSerializerContext;
