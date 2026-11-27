using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex;

/// <summary>Host-selected memory namespace, isolated by account and pinned pipeline version.</summary>
/// <param name="AccountId">Authenticated account identity.</param>
/// <param name="Version">Thread-pinned memory version.</param>
public sealed record CodexMemoryScope(string AccountId, CodexMemoryVersion Version);

/// <summary>Authorization request for a single memory operation.</summary>
/// <param name="Scope">Account and version.</param>
/// <param name="ThreadId">Authenticated logical thread.</param>
/// <param name="Operation">list, read, search, add_ad_hoc_note or summary.</param>
/// <param name="Path">Validated relative path, or empty for the root.</param>
public sealed record CodexMemoryAccess(CodexMemoryScope Scope, Guid ThreadId, string Operation, string Path);

/// <summary>A visible entry in the authorized memory namespace.</summary>
/// <param name="Path">Root-relative slash-separated path.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
public sealed record CodexMemoryEntry(string Path, bool IsDirectory);

/// <summary>One immutable text file from an authorized memory snapshot.</summary>
/// <param name="Path">Root-relative slash-separated path.</param>
/// <param name="Content">Strict UTF-8 decoded text.</param>
public sealed record CodexMemoryFile(string Path, string Content);

/// <summary>A page of memory entries with a revision-bound continuation.</summary>
/// <param name="Path">Requested relative path.</param>
/// <param name="Entries">Ordered visible entries.</param>
/// <param name="NextCursor">Continuation, or null at end.</param>
public sealed record CodexMemoryListResult(string? Path, IReadOnlyList<CodexMemoryEntry> Entries, string? NextCursor)
{
    /// <summary>Whether a continuation is available.</summary>
    public bool Truncated => NextCursor is not null;
}

/// <summary>A line- and token-bounded memory read.</summary>
/// <param name="Path">Requested file.</param>
/// <param name="StartLineNumber">One-based first line.</param>
/// <param name="Content">Read text, preserving original newline bytes.</param>
/// <param name="Truncated">Whether additional text was omitted.</param>
public sealed record CodexMemoryReadResult(string Path, int StartLineNumber, string Content, bool Truncated);

/// <summary>Rust memory search matching modes.</summary>
public enum CodexMemoryMatchMode
{
    /// <summary>Any query on a line.</summary>
    Any,
    /// <summary>All queries on one line.</summary>
    AllOnSameLine,
    /// <summary>All queries within a minimal bounded line window.</summary>
    AllWithinLines,
}

/// <summary>Native memory search input.</summary>
/// <param name="Queries">Nonempty substrings.</param>
/// <param name="MatchMode">Matching mode.</param>
/// <param name="LineCount">Window size for AllWithinLines.</param>
/// <param name="Path">Optional root-relative file or directory.</param>
/// <param name="Cursor">Revision-bound continuation.</param>
/// <param name="ContextLines">Adjacent lines in snippets.</param>
/// <param name="CaseSensitive">Whether case must match.</param>
/// <param name="Normalized">Ignore non-alphanumeric characters.</param>
/// <param name="MaxResults">Page size, clamped to Rust's 200-result cap.</param>
public sealed record CodexMemorySearchRequest(IReadOnlyList<string> Queries,
    CodexMemoryMatchMode MatchMode = CodexMemoryMatchMode.Any, int LineCount = 1,
    string? Path = null, string? Cursor = null, int ContextLines = 0,
    bool CaseSensitive = true, bool Normalized = false, int MaxResults = 200);

/// <summary>A matching line/window and its surrounding context.</summary>
/// <param name="Path">Root-relative file.</param>
/// <param name="MatchLineNumber">One-based first matching line.</param>
/// <param name="ContentStartLineNumber">One-based snippet start.</param>
/// <param name="Content">Snippet with normalized line separators.</param>
/// <param name="MatchedQueries">Matching original query strings in input order.</param>
public sealed record CodexMemorySearchMatch(string Path, int MatchLineNumber,
    int ContentStartLineNumber, string Content, IReadOnlyList<string> MatchedQueries);

/// <summary>One page of memory search results.</summary>
/// <param name="Queries">Trimmed original queries.</param>
/// <param name="MatchMode">Effective mode.</param>
/// <param name="Path">Requested relative scope.</param>
/// <param name="Matches">Ordered matches.</param>
/// <param name="NextCursor">Continuation, or null at end.</param>
public sealed record CodexMemorySearchResult(IReadOnlyList<string> Queries, CodexMemoryMatchMode MatchMode,
    string? Path, IReadOnlyList<CodexMemorySearchMatch> Matches, string? NextCursor)
{
    /// <summary>Whether a continuation is available.</summary>
    public bool Truncated => NextCursor is not null;
}

/// <summary>
/// Durable host adapter. All reads and writes must independently enforce the supplied authenticated
/// scope and current access policy; never follow symlinks, hard-link escapes, hidden paths or
/// reinterpret a path as an absolute path. The SDK never falls back to a local filesystem.
/// </summary>
public interface ICodexMemoryStorage
{
    /// <summary>Opens a consistent, revisioned read snapshot under the requested authorization.</summary>
    ValueTask<ICodexMemorySnapshot> OpenReadAsync(CodexMemoryAccess access, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically creates a new note (including required directory entries) without replacing an
    /// existing path. False means an existing note; uncertain commits must surface as errors.
    /// The adapter must authorize and fence policy changes in the same transaction.
    /// </summary>
    ValueTask<bool> CreateNoteAsync(CodexMemoryAccess access, string content, CancellationToken cancellationToken);
}

/// <summary>
/// Immutable authorized snapshot. The revision changes on every relevant namespace mutation.
/// Implementations should use metadata-only indexed list queries and keyset file enumeration;
/// do not load preceding pages or the whole namespace for a single file read.
/// </summary>
public interface ICodexMemorySnapshot : IAsyncDisposable
{
    /// <summary>Opaque, nonempty namespace revision.</summary>
    string Revision { get; }

    /// <summary>
    /// Lists a file itself or immediate visible children of a directory, ordered by ordinal path,
    /// strictly after the optional key. Missing or non-file/non-directory paths must fail.
    /// </summary>
    ValueTask<IReadOnlyList<CodexMemoryEntry>> ListAsync(string path, string? afterPath,
        int limit, CancellationToken cancellationToken);

    /// <summary>Reads a single regular text file; reject files exceeding maxBytes before returning.</summary>
    ValueTask<CodexMemoryFile> ReadAsync(string path, int maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Lazily enumerates regular UTF-8 text files in the requested file or directory subtree, ordered
    /// by ordinal path, starting at the inclusive key. Reject oversized files; skip non-text files.
    /// Missing scopes must fail. Do not fetch contents of preceding files.
    /// </summary>
    IAsyncEnumerable<CodexMemoryFile> EnumerateFilesAsync(string path, string? atOrAfterPath,
        int maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Account-bound native memory operations. Authorization and effective policy are checked before
/// storage access on every invocation; durable I/O is delegated to the embedding host.
/// </summary>
public sealed class CodexMemoryBackend
{
    /// <summary>Maximum size of an individual memory document accepted by this SDK.</summary>
    public const int MaximumDocumentBytes = 1_048_576;
    private const int MaximumResultBytes = 2 * MaximumDocumentBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ICodexMemoryStorage storage;
    private readonly CodexMemoryScope scope;
    private readonly Guid threadId;
    private readonly Func<CodexMemoryPolicy> policy;
    private readonly Func<CodexMemoryAccess, CancellationToken, ValueTask> authorize;

    /// <summary>Creates an explicitly host-authorized backend for one thread.</summary>
    public CodexMemoryBackend(ICodexMemoryStorage storage, CodexMemoryScope scope, Guid threadId,
        Func<CodexMemoryPolicy> policy, Func<CodexMemoryAccess, CancellationToken, ValueTask> authorize)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AccountId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorize);
        if (threadId == Guid.Empty || scope.Version is not (CodexMemoryVersion.V1 or CodexMemoryVersion.V2))
            throw new ArgumentException("Invalid memory authority scope.");
        this.storage = storage;
        this.scope = scope;
        this.threadId = threadId;
        this.policy = policy;
        this.authorize = authorize;
    }

    /// <summary>Whether the current pinned policy allows dedicated model tools.</summary>
    public bool DedicatedToolsEnabled
    {
        get { var effective = policy(); return effective.Version == scope.Version && effective.AreDedicatedToolsEnabled; }
    }

    /// <summary>Immutable pipeline namespace selected by the host for this backend.</summary>
    public CodexMemoryVersion Version => scope.Version;

    internal bool IsBoundTo(CodexThreadDocument thread) => thread.Id == threadId && thread.AccountId == scope.AccountId;

    /// <summary>
    /// Reads the generated summary once, without listing the namespace. Disabled memory
    /// performs no storage or authorization calls. Missing or empty summary is normal.
    /// Permission failures and malformed storage responses are not silently ignored.
    /// </summary>
    public async Task<string?> ReadSummaryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effective = policy();
        if (!effective.IsReadPathEnabled || effective.Version != scope.Version) return null;
        const string path = "memory_summary.md";
        var access = new CodexMemoryAccess(scope, threadId, "summary", path);
        await authorize(access, cancellationToken).ConfigureAwait(false);
        CheckPolicy(dedicated: false);
        try
        {
            await using var snapshot = await storage.OpenReadAsync(access, cancellationToken).ConfigureAwait(false);
            var file = await snapshot.ReadAsync(path, MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
            if (file.Path != path) throw new InvalidDataException("Memory summary identity mismatch.");
            CheckFile(file);
            CheckPolicy(dedicated: false);
            var summary = file.Content.Trim();
            return summary.Length == 0 ? null : Truncate(summary, 2500);
        }
        catch (KeyNotFoundException) { return null; }
    }

    /// <summary>Lists a file or immediate directory children without loading their contents.</summary>
    public async Task<CodexMemoryListResult> ListAsync(string? path = null, string? cursor = null,
        int maxResults = 2000, CancellationToken cancellationToken = default)
    {
        var normalized = ValidatePath(path, true);
        var access = await AuthorizeAsync("list", normalized, cancellationToken).ConfigureAwait(false);
        await using var snapshot = await storage.OpenReadAsync(access, cancellationToken).ConfigureAwait(false);
        var binding = Binding(snapshot, "list", normalized);
        var (after, line) = DecodeCursor(cursor, binding);
        if (line != 0 || (after is not null && !IsListEntry(normalized, after)))
            throw new ArgumentException("Invalid memory list cursor.", nameof(cursor));
        var limit = Math.Clamp(maxResults, 1, 2000);
        var entries = await snapshot.ListAsync(normalized, after, limit + 1, cancellationToken).ConfigureAwait(false);
        if (entries.Count > limit + 1) throw new InvalidDataException("Memory list exceeded its bound.");
        var prior = after;
        foreach (var entry in entries)
        {
            ValidatePath(entry.Path, false);
            if (!IsListEntry(normalized, entry.Path) ||
                (prior is not null && string.CompareOrdinal(prior, entry.Path) >= 0))
                throw new InvalidDataException("Memory list returned an invalid or unordered entry.");
            prior = entry.Path;
        }
        CheckPolicy();
        var page = entries.Take(limit).ToArray();
        var next = entries.Count > limit ? EncodeCursor(binding, page[^1].Path, 0) : null;
        return new(path, page, next);
    }

    /// <summary>Reads one file with Rust's one-based line range and UTF-8 token truncation.</summary>
    public async Task<CodexMemoryReadResult> ReadAsync(string path, int lineOffset = 1,
        int? maxLines = null, int maxTokens = 20_000, CancellationToken cancellationToken = default)
    {
        var normalized = ValidatePath(path, false);
        ArgumentOutOfRangeException.ThrowIfLessThan(lineOffset, 1);
        if (maxLines is <= 0) throw new ArgumentOutOfRangeException(nameof(maxLines));
        ArgumentOutOfRangeException.ThrowIfNegative(maxTokens);
        var access = await AuthorizeAsync("read", normalized, cancellationToken).ConfigureAwait(false);
        await using var snapshot = await storage.OpenReadAsync(access, cancellationToken).ConfigureAwait(false);
        var file = await snapshot.ReadAsync(normalized, MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
        if (file.Path != normalized) throw new InvalidDataException("Memory file identity mismatch.");
        CheckFile(file);
        var start = 0;
        for (var n = 1; n < lineOffset; n++)
        {
            var newline = file.Content.IndexOf('\n', start);
            if (newline < 0) throw new ArgumentOutOfRangeException(nameof(lineOffset), "Offset exceeds file length.");
            start = newline + 1;
        }
        var end = file.Content.Length;
        if (maxLines is { } count)
        {
            var position = start;
            for (var n = 0; n < count; n++)
            {
                var newline = file.Content.IndexOf('\n', position);
                if (newline < 0) { position = end; break; }
                position = newline + 1;
            }
            end = position;
        }
        var slice = file.Content[start..end];
        var content = Truncate(slice, maxTokens == 0 ? 20_000 : maxTokens);
        CheckPolicy();
        return new(path, lineOffset, content, end < file.Content.Length || content != slice);
    }

    /// <summary>Searches bounded text files with minimal matching windows and keyset pagination.</summary>
    public async Task<CodexMemorySearchResult> SearchAsync(CodexMemorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Queries);
        var path = ValidatePath(request.Path, true);
        if (request.Queries.Count is < 1 or > 32 ||
            request.MatchMode is not (CodexMemoryMatchMode.Any or CodexMemoryMatchMode.AllOnSameLine or CodexMemoryMatchMode.AllWithinLines) ||
            request.ContextLines is < 0 or > 64 ||
            (request.MatchMode == CodexMemoryMatchMode.AllWithinLines && request.LineCount is < 1 or > 512))
            throw new ArgumentException("Invalid or oversized memory search.", nameof(request));
        var queries = request.Queries.Select(query => query?.Trim() ?? "").ToArray();
        if (queries.Any(query => query.Length is < 1 or > 4096))
            throw new ArgumentException("Memory search queries must be nonempty and bounded.", nameof(request));
        var prepared = queries.Select(q => Prepare(q, request)).ToArray();
        if (prepared.Any(string.IsNullOrEmpty))
            throw new ArgumentException("Normalized memory search queries must be nonempty.", nameof(request));
        var access = await AuthorizeAsync("search", path, cancellationToken).ConfigureAwait(false);
        await using var snapshot = await storage.OpenReadAsync(access, cancellationToken).ConfigureAwait(false);
        var binding = Binding(snapshot, "search", path,
            [((int)request.MatchMode).ToString(CultureInfo.InvariantCulture),
            request.LineCount.ToString(CultureInfo.InvariantCulture),
            request.ContextLines.ToString(CultureInfo.InvariantCulture),
            request.CaseSensitive.ToString(), request.Normalized.ToString(), ..queries]);
        var (after, line) = DecodeCursor(request.Cursor, binding);
        if (after is not null && (line < 1 || !IsWithin(path, after)))
            throw new ArgumentException("Invalid memory search cursor.", nameof(request));
        var limit = Math.Clamp(request.MaxResults, 1, 200);
        var matches = new List<CodexMemorySearchMatch>();
        string? prior = null;
        var resultBytes = 0;
        var more = false;
        await foreach (var file in snapshot.EnumerateFilesAsync(path, after, MaximumDocumentBytes, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ValidatePath(file.Path, false);
            if (!IsWithin(path, file.Path) || (after is not null && string.CompareOrdinal(file.Path, after) < 0) ||
                (prior is not null && string.CompareOrdinal(file.Path, prior) <= 0))
                throw new InvalidDataException("Memory search returned an invalid or unordered file.");
            prior = file.Path;
            CheckFile(file);
            foreach (var match in MatchFile(file, request, queries, prepared, cancellationToken))
            {
                if (file.Path == after && match.MatchLineNumber <= line) continue;
                var bytes = Utf8.GetByteCount(match.Content);
                if (matches.Count == limit || (matches.Count > 0 && resultBytes + bytes > MaximumResultBytes))
                {
                    more = true;
                    break;
                }
                matches.Add(match);
                resultBytes += bytes;
            }
            if (more) break;
            CheckPolicy();
        }
        CheckPolicy();
        var next = more ? EncodeCursor(binding, matches[^1].Path, matches[^1].MatchLineNumber) : null;
        return new(queries, request.MatchMode, request.Path, matches, next);
    }

    /// <summary>Creates a timestamp-slug Markdown note without replacing any existing content.</summary>
    public async Task AddAdHocNoteAsync(string filename, string note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        if (filename.Length is < 24 or > 103 || !filename.EndsWith(".md", StringComparison.Ordinal) ||
            !ValidFilename(filename))
            throw new ArgumentException("Expected YYYY-MM-DDTHH-MM-SS-slug.md.", nameof(filename));
        if (Utf8.GetByteCount(note) > MaximumDocumentBytes)
            throw new ArgumentException("Memory note exceeds its size bound.", nameof(note));
        var path = "extensions/ad_hoc/notes/" + filename;
        var access = await AuthorizeAsync("add_ad_hoc_note", path, cancellationToken).ConfigureAwait(false);
        if (!await storage.CreateNoteAsync(access, note, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Memory note already exists.");
    }

    private void CheckPolicy(bool dedicated = true)
    {
        var effective = policy();
        if (!(dedicated ? effective.AreDedicatedToolsEnabled : effective.IsReadPathEnabled) || effective.Version != scope.Version)
            throw new UnauthorizedAccessException("Memory access is not enabled for this thread.");
    }

    private async ValueTask<CodexMemoryAccess> AuthorizeAsync(string operation, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckPolicy();
        var access = new CodexMemoryAccess(scope, threadId, operation, path);
        await authorize(access, ct).ConfigureAwait(false);
        CheckPolicy();
        return access;
    }

    private static string ValidatePath(string? path, bool allowRoot)
    {
        if (string.IsNullOrEmpty(path) && allowRoot) return "";
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl) || path.Split('/').Any(part => part.Length == 0 || part[0] == '.'))
            throw new ArgumentException("Expected a visible root-relative memory path.", nameof(path));
        return path;
    }

    private static bool IsWithin(string path, string candidate) => path.Length == 0 || candidate == path ||
        candidate.StartsWith(path + "/", StringComparison.Ordinal);

    private static bool IsListEntry(string path, string candidate) => candidate == path ||
        (IsWithin(path, candidate) && !candidate.AsSpan(path.Length == 0 ? 0 : path.Length + 1).Contains('/'));

    private static void CheckFile(CodexMemoryFile file)
    {
        if (Utf8.GetByteCount(file.Content) > MaximumDocumentBytes)
            throw new InvalidDataException("Memory file exceeded its bound.");
    }

    private string Binding(ICodexMemorySnapshot snapshot, string operation, string path, params string[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Revision);
        string[] fields = [scope.AccountId, scope.Version.ToString(), threadId.ToString("D"),
            snapshot.Revision, operation, path, ..values];
        var material = string.Concat(fields.Select(field => field.Length.ToString(CultureInfo.InvariantCulture) + ":" + field));
        return Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(material)));
    }

    private static string EncodeCursor(string binding, string path, int line) =>
        binding + ":" + line.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(Utf8.GetBytes(path));

    private static (string? Path, int Line) DecodeCursor(string? cursor, string binding)
    {
        if (cursor is null) return (null, 0);
        if (cursor.Length > 25_000) throw new ArgumentException("Memory cursor exceeds its bound.", nameof(cursor));
        var parts = cursor.Split(':', 3);
        if (parts.Length != 3 || parts[0] != binding ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var line))
            throw new ArgumentException("Memory cursor belongs to a different query or revision.", nameof(cursor));
        try { return (ValidatePath(Utf8.GetString(Convert.FromBase64String(parts[2])), false), line); }
        catch (FormatException ex) { throw new ArgumentException("Malformed memory cursor.", nameof(cursor), ex); }
        catch (DecoderFallbackException ex) { throw new ArgumentException("Malformed memory cursor.", nameof(cursor), ex); }
    }

    private static bool ValidFilename(string value)
    {
        for (var i = 0; i < 20; i++)
        {
            var expected = i switch { 4 or 7 or 13 or 16 or 19 => '-', 10 => 'T', _ => '\0' };
            if (expected == '\0' ? !char.IsAsciiDigit(value[i]) : value[i] != expected) return false;
        }
        var slug = value.AsSpan(20, value.Length - 23);
        foreach (var ch in slug)
            if (!(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-')) return false;
        return slug.Length is >= 1 and <= 80;
    }

    private static string Truncate(string text, int tokens)
    {
        var bytes = Utf8.GetBytes(text);
        var budget = (long)tokens * 4;
        if (bytes.Length <= budget) return text;
        var left = (int)(budget / 2);
        var right = bytes.Length - (int)(budget - left);
        while (left > 0 && (bytes[left] & 0xC0) == 0x80) left--;
        while (right < bytes.Length && (bytes[right] & 0xC0) == 0x80) right++;
        var removed = (bytes.Length - budget + 3) / 4;
        return Utf8.GetString(bytes, 0, left) + "…" + removed.ToString(CultureInfo.InvariantCulture) +
            " tokens truncated…" + Utf8.GetString(bytes, right, bytes.Length - right);
    }

    private static string Prepare(string value, CodexMemorySearchRequest request)
    {
        if (!request.CaseSensitive) value = value.ToLowerInvariant();
        if (!request.Normalized) return value;
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) result.Append(rune.ToString());
        return result.ToString();
    }

    private static IEnumerable<CodexMemorySearchMatch> MatchFile(CodexMemoryFile file,
        CodexMemorySearchRequest request, string[] queries, string[] prepared, CancellationToken ct)
    {
        if (file.Content.Length == 0) yield break;
        var lines = file.Content.Split('\n');
        var count = lines.Length - (lines[^1].Length == 0 ? 1 : 0);
        for (var i = 0; i < count; i++)
            if (i < lines.Length - 1 && lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];

        var positions = Enumerable.Repeat(-1, queries.Length).ToArray();
        var lastWindowStart = -1;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var value = Prepare(lines[i], request);
            var matched = new List<string>();
            for (var j = 0; j < prepared.Length; j++)
                if (value.Contains(prepared[j], StringComparison.Ordinal))
                {
                    positions[j] = i;
                    matched.Add(queries[j]);
                }

            var start = i;
            switch (request.MatchMode)
            {
                case CodexMemoryMatchMode.Any:
                    if (matched.Count == 0) continue;
                    break;
                case CodexMemoryMatchMode.AllOnSameLine:
                    if (matched.Count != queries.Length) continue;
                    break;
                case CodexMemoryMatchMode.AllWithinLines:
                    if (matched.Count == 0) continue;
                    start = positions.Min();
                    if (start < 0 || i - start >= request.LineCount || start == lastWindowStart) continue;
                    lastWindowStart = start;
                    matched = [..queries];
                    break;
            }
            var contextStart = Math.Max(0, start - request.ContextLines);
            var contextEnd = Math.Min(count, i + request.ContextLines + 1);
            yield return new(file.Path, start + 1, contextStart + 1,
                string.Join("\n", lines, contextStart, contextEnd - contextStart), matched);
        }
    }
}
