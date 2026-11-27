using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Account-scoped immutable pages for a native thread journal. The host must provide
/// the same authorization and encryption as its thread storage. A page referenced by any
/// readable manifest must remain available, including during concurrent manifest replacement.
/// Orphan reclamation is a separate, reference-aware host operation.</summary>
public interface ICodexThreadSegmentStorage
{
    /// <summary>Read the exact immutable bytes named by a lower-case SHA-256 digest.</summary>
    Task<ReadOnlyMemory<byte>?> ReadSegmentAsync(Guid threadId, string sha256, CancellationToken cancellationToken);

    /// <summary>Durably create the page, or confirm that identical bytes already exist.
    /// Never overwrite a different page. Returning successfully guarantees that subsequent
    /// readers can retrieve the bytes, even if the caller loses its manifest CAS.</summary>
    Task PutSegmentAsync(Guid threadId, string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}

/// <summary>Opt-in segmented implementation of the existing atomic thread storage contract.
/// The host root store must support both ordinary inline documents and versioned manifest
/// objects with the same revision CAS. Pages are staged first; only the root CAS publishes
/// a new revision. Reads reconstruct and verify a single immutable manifest snapshot.
/// The existing materialized-document bound remains in force until page-aware readers exist.</summary>
public sealed partial class CodexSegmentedThreadStorage : ICodexThreadStorage
{
    private const string ManifestKind = "codexThreadSegments";
    private const int MinimumPageBytes = 32 * 1024;
    private const int MaximumPageBytes = 512 * 1024;
    private const ulong BoundaryMask = (1UL << 17) - 1;
    private const int MaximumPages = CodexThreadStore.MaximumDocumentBytes / MinimumPageBytes + 1;
    private const int MaximumManifestBytes = 128 * 1024;
    private static readonly ulong[] BoundaryTable = CreateBoundaryTable();
    private readonly ICodexThreadStorage roots;
    private readonly ICodexThreadSegmentStorage pages;
    private readonly string accountId;
    private readonly bool indexHistory;

    /// <summary>Bind one account's manifest CAS and immutable page store.
    /// The caller must prevent older inline-only writers from accessing these roots after migration.</summary>
    public CodexSegmentedThreadStorage(ICodexThreadStorage roots, ICodexThreadSegmentStorage pages, string accountId)
        : this(roots, pages, accountId, indexHistory: false) { }

    /// <summary>Optionally enroll in version-2 semantic history indexes. Upgrade every
    /// manifest reader and writer before enabling this option; version-1 roots remain readable.</summary>
    public CodexSegmentedThreadStorage(ICodexThreadStorage roots, ICodexThreadSegmentStorage pages, string accountId,
        bool indexHistory)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(pages);
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 512 || accountId.Any(char.IsControl))
            throw new ArgumentException("Invalid thread storage account identity.", nameof(accountId));
        this.roots = roots;
        this.pages = pages;
        this.accountId = accountId;
        this.indexHistory = indexHistory;
    }

    /// <inheritdoc />
    public async Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
    {
        RequireThreadId(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        var root = await roots.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (root is null) return null;
        var header = ReadRoot(threadId, root.Value);
        return await ReadCapturedDocumentAsync(threadId, root.Value, header.Manifest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> ReadCapturedDocumentAsync(Guid threadId, JsonElement root,
        CodexThreadSegmentManifest? manifest, CancellationToken cancellationToken)
    {
        if (manifest is null) return root.Clone();
        var bytes = new byte[manifest.Length];
        var offset = 0;
        foreach (var page in manifest.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await pages.ReadSegmentAsync(threadId, page.Sha256, cancellationToken).ConfigureAwait(false);
            if (stored is not { } data || data.Length != page.Length)
                throw new InvalidDataException("Native thread journal page is missing or has a different length.");
            // Verify the copied bytes, not a buffer whose lifetime is controlled by the host.
            data.CopyTo(bytes.AsMemory(offset, page.Length));
            if (Digest(bytes.AsSpan(offset, page.Length)) != page.Sha256)
                throw new InvalidDataException("Native thread journal page digest mismatch.");
            offset += page.Length;
        }
        if (Digest(bytes) != manifest.DocumentSha256)
            throw new InvalidDataException("Native thread journal document digest mismatch.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var value = document.RootElement;
            if (ReadDocumentRevision(threadId, value) != manifest.Revision)
                throw new InvalidDataException("Native thread journal manifest revision does not match its document.");
            if (manifest.HistoryIndex is { } index && !SameHistoryIndex(index, BuildHistoryIndex(bytes)))
                throw new InvalidDataException("Native thread journal history index does not match its document.");
            return value.Clone();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Native thread journal pages do not contain a valid document.", error);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
        CancellationToken cancellationToken)
    {
        RequireThreadId(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedRevision is <= 0 or long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var revision = ReadDocumentRevision(threadId, document);
        if (revision != checked((expectedRevision ?? 0) + 1))
            throw new InvalidOperationException("Native thread journal candidate revision is not the next revision.");
        var raw = document.GetRawText();
        var bytes = Encoding.UTF8.GetBytes(raw);

        // A known conflict must not stage pages. This check is only an optimization;
        // the final root CAS is the sole publication authority.
        var root = await roots.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
        var header = root is { } current ? ReadRoot(threadId, current) : ((long Revision, CodexThreadSegmentManifest? Manifest)?)null;
        if (header?.Revision != expectedRevision) return false;

        var references = new List<CodexThreadSegmentReference>();
        var staged = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        foreach (var (offset, length) in Split(bytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha = Digest(bytes.AsSpan(offset, length));
            staged.TryAdd(sha, (offset, length));
            references.Add(new(sha, length));
        }
        var indexed = indexHistory || header?.Manifest?.Version == 2;
        var index = indexed ? BuildHistoryIndex(bytes) : null;
        var manifest = new CodexThreadSegmentManifest(ManifestKind, indexed ? 2 : 1, threadId, accountId, revision,
            bytes.Length, Digest(bytes), references.ToArray())
        {
            HistoryIndex = index, HistoryIndexSha256 = index is null ? null : HistoryIndexDigest(index),
        };
        var candidate = JsonSerializer.SerializeToElement(manifest, CodexThreadSegmentJson.Default.CodexThreadSegmentManifest);
        if (indexed && Encoding.UTF8.GetByteCount(candidate.GetRawText()) > MaximumIndexedManifestBytes)
        {
            // Indexing must never make an otherwise valid journal impossible to close.
            // Keep v2 enrollment and fall back to an ordinary verified full read.
            manifest = manifest with { HistoryIndex = null, HistoryIndexSha256 = null };
            candidate = JsonSerializer.SerializeToElement(manifest, CodexThreadSegmentJson.Default.CodexThreadSegmentManifest);
        }
        ValidateManifest(threadId, candidate, manifest);
        // Only the currently observed committed manifest proves durable retention.
        // Never infer durability from a failed writer, orphan page or process-local cache.
        var retained = header?.Manifest?.Pages.ToHashSet() ?? [];
        foreach (var (sha, range) in staged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retained.Contains(new(sha, range.Length))) continue;
            await pages.PutSegmentAsync(threadId, sha, bytes.AsMemory(range.Offset, range.Length), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Do not retry uncertain commits. A throw may mean the manifest is already visible.
        return await roots.CompareExchangeAsync(threadId, expectedRevision, candidate, cancellationToken).ConfigureAwait(false);
    }

    private (long Revision, CodexThreadSegmentManifest? Manifest) ReadRoot(Guid threadId, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid native thread journal root.");
        if (!root.TryGetProperty("kind", out _)) return (ReadDocumentRevision(threadId, root), null);
        if (Encoding.UTF8.GetByteCount(root.GetRawText()) > MaximumIndexedManifestBytes)
            throw new InvalidDataException("Native thread journal manifest exceeds its bound.");
        try
        {
            var manifest = root.Deserialize(CodexThreadSegmentJson.Default.CodexThreadSegmentManifest)
                ?? throw new InvalidDataException("Missing native thread journal manifest.");
            ValidateManifest(threadId, root, manifest);
            return (manifest.Revision, manifest);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid native thread journal manifest.", error);
        }
    }

    private void ValidateManifest(Guid threadId, JsonElement raw, CodexThreadSegmentManifest manifest)
    {
        if (manifest.Kind != ManifestKind || manifest.Version is not (1 or 2) ||
            manifest.Version == 1 && manifest.HistoryIndex is not null ||
            (manifest.HistoryIndex is null) != (manifest.HistoryIndexSha256 is null) || manifest.Id != threadId ||
            manifest.AccountId != accountId || manifest.Revision < 1 ||
            manifest.Length is <= 0 or > CodexThreadStore.MaximumDocumentBytes ||
            !ValidDigest(manifest.DocumentSha256) || manifest.Pages is not { Length: > 0 and <= MaximumPages } ||
            Encoding.UTF8.GetByteCount(raw.GetRawText()) > (manifest.Version == 1 ? MaximumManifestBytes : MaximumIndexedManifestBytes))
            throw new InvalidDataException("Native thread journal manifest identity or bounds are invalid.");
        RequireUniqueProperties(raw);
        foreach (var entry in raw.GetProperty("pages").EnumerateArray()) RequireUniqueProperties(entry);
        long length = 0;
        for (var i = 0; i < manifest.Pages.Length; i++)
        {
            var page = manifest.Pages[i];
            if (page is null || !ValidDigest(page.Sha256) || page.Length is <= 0 or > MaximumPageBytes ||
                i + 1 < manifest.Pages.Length && page.Length < MinimumPageBytes)
                throw new InvalidDataException("Native thread journal page reference is invalid.");
            length += page.Length;
        }
        if (length != manifest.Length)
            throw new InvalidDataException("Native thread journal page lengths do not match the manifest.");
        if (manifest.HistoryIndex is { } index)
        {
            ValidateHistoryIndex(raw.GetProperty("historyIndex"), index, manifest.Length);
            if (!ValidDigest(manifest.HistoryIndexSha256) || HistoryIndexDigest(index) != manifest.HistoryIndexSha256)
                throw new InvalidDataException("Native history index digest mismatch.");
        }
    }

    private long ReadDocumentRevision(Guid threadId, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty("kind", out _) ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > CodexThreadStore.MaximumDocumentBytes ||
            !value.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number ||
            !format.TryGetInt32(out var version) || version is not (1 or 2) ||
            !value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !id.TryGetGuid(out var actualId) || actualId != threadId ||
            !value.TryGetProperty("accountId", out var account) || account.ValueKind != JsonValueKind.String ||
            account.GetString() != accountId ||
            !value.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt64(out var number) || number < 1)
            throw new InvalidDataException("Native thread journal document identity or bounds are invalid.");
        RequireUniqueProperties(value);
        return number;
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Native thread journal object is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Native thread journal object contains duplicate fields.");
    }

    private static void RequireThreadId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty native thread ID is required.", nameof(id));
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool ValidDigest(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Content-defined cuts prevent a changed revision or inserted prefix from shifting
    // every subsequent page. The rolling hash chooses boundaries only; SHA-256 identifies
    // and verifies the actual bytes. Hard minimum/maximum cuts bound metadata and I/O.
    private static IEnumerable<(int Offset, int Length)> Split(byte[] bytes)
    {
        var start = 0;
        ulong hash = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            hash = unchecked((hash << 1) + BoundaryTable[bytes[i]]);
            var length = i - start + 1;
            if (length < MinimumPageBytes || length < MaximumPageBytes && (hash & BoundaryMask) != 0) continue;
            yield return (start, length);
            start = i + 1;
        }
        if (start < bytes.Length) yield return (start, bytes.Length - start);
    }

    private static ulong[] CreateBoundaryTable()
    {
        var table = new ulong[256];
        for (var i = 0; i < table.Length; i++)
        {
            // Deterministic SplitMix64 values, independent of runtime hash randomization.
            var value = unchecked(0x9E3779B97F4A7C15UL * (ulong)(i + 1));
            value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
            table[i] = value ^ (value >> 31);
        }
        return table;
    }
}

internal sealed record CodexThreadSegmentReference(
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] int Length);

internal sealed record CodexThreadSegmentManifest(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] int Length,
    [property: JsonRequired] string DocumentSha256,
    [property: JsonRequired] CodexThreadSegmentReference[] Pages)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexThreadHistoryIndexEntry[]? HistoryIndex { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HistoryIndexSha256 { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexThreadSegmentManifest))]
[JsonSerializable(typeof(CodexThreadHistoryIndexEntry[]))]
internal partial class CodexThreadSegmentJson : JsonSerializerContext;
