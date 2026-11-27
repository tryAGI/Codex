using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace tryAGI.Codex;

/// <summary>
/// Host-authoritative, monotonic memory I/O policy, separate from extraction eligibility.
/// A missing policy denies access. The host must publish changes before exposing memory.
/// </summary>
/// <param name="ThreadId">Logical thread.</param>
/// <param name="Generation">Monotonic policy generation.</param>
/// <param name="AllowSummary">Permit the version-pinned summary context.</param>
/// <param name="AllowRetrieval">Permit dedicated list/read/search tools.</param>
/// <param name="AllowNotes">Permit separately authorized ad-hoc note writes.</param>
public sealed record CodexMemoryIoPolicyUpdate(Guid ThreadId, long Generation,
    bool AllowSummary, bool AllowRetrieval, bool AllowNotes)
{
    internal int Flags => (AllowSummary ? 1 : 0) | (AllowRetrieval ? 2 : 0) | (AllowNotes ? 4 : 0);

    internal void Validate()
    {
        if (ThreadId == Guid.Empty || Generation < 0 || AllowRetrieval && !AllowSummary ||
            AllowNotes && !AllowRetrieval)
            throw new ArgumentException("Invalid memory I/O policy.");
    }
}

public sealed partial class CodexMacOsSqliteMemoryJobStorage
{
    private sealed record MemoryView(string Revision, byte[] ArtifactRoot);

    /// <summary>
    /// Publishes the exact thread I/O policy. Older generations and conflicting equal
    /// generations cannot reopen access. This does not change extraction eligibility.
    /// </summary>
    public async Task<bool> PublishMemoryIoPolicyAsync(CodexMemoryIoPolicyUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        update.Validate();
        var key = new CodexMemoryJobKey(scope, update.ThreadId);
        await AuthorizeAsync(new(key, "publish_memory_io_policy", IoPolicy: update), cancellationToken).ConfigureAwait(false);
        return await RunAsync(() => Transaction(database, true, cancellationToken, () =>
        {
            var thread = update.ThreadId.ToByteArray();
            using (var prior = Prepare(database, "SELECT generation,flags FROM memory_io_policy WHERE thread=?1;"))
            {
                prior.Bind(1, thread);
                if (prior.Next())
                {
                    var generation = prior.Number(0);
                    var flags = prior.Number(1);
                    prior.Done();
                    if (generation > update.Generation) return false;
                    if (generation == update.Generation) return flags == update.Flags;
                }
            }
            using var statement = Prepare(database, """
                INSERT INTO memory_io_policy(thread,generation,flags) VALUES(?1,?2,?3)
                ON CONFLICT(thread) DO UPDATE SET generation=excluded.generation,flags=excluded.flags;
                """);
            statement.Bind(1, thread);
            statement.Bind(2, update.Generation);
            statement.Bind(3, update.Flags);
            statement.Done();
            return true;
        }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the published local artifact and append-only notes at a revision-bound view.
    /// Each subsequent read rechecks the authorized operation, policy generation and namespace
    /// revision; it fails closed if the view changed. No long-running database transaction is held.
    /// Externally managed consolidation pointers require their own host storage adapter.
    /// </summary>
    public async ValueTask<ICodexMemorySnapshot> OpenReadAsync(CodexMemoryAccess access,
        CancellationToken cancellationToken)
    {
        var key = ValidateMemoryAccess(access, write: false);
        await AuthorizeAsync(new(key, "memory_open_read", MemoryAccess: access), cancellationToken).ConfigureAwait(false);
        var view = await RunAsync(() => Transaction(database, false, cancellationToken, () => ReadMemoryView(access)),
            cancellationToken).ConfigureAwait(false);
        return new LocalMemorySnapshot(this, key, access, view);
    }

    /// <summary>
    /// Creates an exactly authorized note under the reserved namespace, with its directory
    /// entries and view revision in one transaction. A duplicate never overwrites the original.
    /// </summary>
    public async ValueTask<bool> CreateNoteAsync(CodexMemoryAccess access, string content,
        CancellationToken cancellationToken)
    {
        var key = ValidateMemoryAccess(access, write: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var size = Utf8.GetByteCount(content);
        if (size > CodexMemoryBackend.MaximumDocumentBytes)
            throw new ArgumentException("Memory note exceeds its size bound.", nameof(content));
        var packed = PackMemoryBody(content, size);
        var digest = Convert.ToHexString(SHA256.HashData(packed.AsSpan(1)));
        await AuthorizeAsync(new(key, "memory_create_note", MemoryAccess: access, NoteSha256: digest), cancellationToken)
            .ConfigureAwait(false);
        return await RunAsync(() => Transaction(database, true, cancellationToken, () =>
        {
            _ = ReadMemoryView(access);
            using (var existing = Prepare(database, "SELECT 1 FROM memory_entry WHERE root=?1 AND path=?2;"))
            {
                existing.Bind(1, NotesRoot);
                existing.Bind(2, PathKey(access.Path));
                if (existing.Next()) { existing.Done(); return false; }
            }
            for (var parent = ParentPath(access.Path); parent.Length != 0; parent = ParentPath(parent))
                InsertMemoryEntry(NotesRoot, new(parent, true, [0]), ignoreExisting: true);
            InsertMemoryEntry(NotesRoot, new(access.Path, false, packed));
            Execute(database, "UPDATE memory_view SET revision=revision+1 WHERE id=1;");
            if (Changes(database) != 1) throw new InvalidDataException("Missing memory view.");
            return true;
        }), cancellationToken).ConfigureAwait(false);
    }

    private CodexMemoryJobKey ValidateMemoryAccess(CodexMemoryAccess access, bool write)
    {
        ArgumentNullException.ThrowIfNull(access);
        var key = new CodexMemoryJobKey(access.Scope, access.ThreadId);
        CheckKey(key);
        if (access.ThreadId == Guid.Empty || access.Path is null)
            throw new ArgumentException("Memory I/O requires an exact logical thread and relative path.", nameof(access));
        _ = CodexMemoryBackend.ValidatePath(access.Path, access.Operation is "list" or "search");
        if (write)
        {
            if (access.Operation != "add_ad_hoc_note" ||
                !access.Path.StartsWith(NoteDirectory + "/", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Only authorized ad-hoc note creation is supported.");
            CodexMemoryBackend.ValidateAdHocFilename(access.Path[(NoteDirectory.Length + 1)..]);
        }
        else if (access.Operation is not ("list" or "read" or "search" or "summary") ||
            access.Operation == "summary" && access.Path != "memory_summary.md")
            throw new UnauthorizedAccessException("Invalid memory read operation.");
        return key;
    }

    private long RequireMemoryPermission(CodexMemoryAccess access)
    {
        var required = access.Operation switch
        {
            "summary" => 1,
            "list" or "read" or "search" => 2,
            "add_ad_hoc_note" => 4,
            _ => throw new UnauthorizedAccessException("Unknown memory I/O operation."),
        };
        using var policy = Prepare(database, "SELECT generation,flags FROM memory_io_policy WHERE thread=?1;");
        policy.Bind(1, access.ThreadId.ToByteArray());
        if (!policy.Next()) throw new UnauthorizedAccessException("No current memory I/O permission.");
        var generation = policy.Number(0);
        var flags = policy.Number(1);
        policy.Done();
        if ((flags & required) == 0) throw new UnauthorizedAccessException("Current memory I/O policy denies this operation.");
        return generation;
    }

    private MemoryView ReadMemoryView(CodexMemoryAccess access)
    {
        var generation = RequireMemoryPermission(access);
        using var view = Prepare(database, """
            SELECT v.instance,v.revision,p.artifact,m.root FROM memory_view v CROSS JOIN progress p
            LEFT JOIN memory_manifest m ON m.root=p.artifact WHERE v.id=1 AND p.id=1;
            """);
        if (!view.Next()) throw new InvalidDataException("Missing memory namespace view.");
        var revision = new Guid(view.Blob(0)).ToString("N") + ":" + view.Number(1).ToString(CultureInfo.InvariantCulture) +
            ":" + generation.ToString(CultureInfo.InvariantCulture);
        byte[] artifactRoot;
        if (view.IsNull(2)) artifactRoot = MissingRoot;
        else
        {
            artifactRoot = view.Blob(2);
            if (!Utf8.GetString(artifactRoot).StartsWith(LocalArtifactPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("The published memory artifact requires its external storage adapter.");
            if (view.IsNull(3) || !artifactRoot.AsSpan().SequenceEqual(view.Blob(3)))
                throw new InvalidDataException("Published local memory artifact is missing.");
        }
        view.Done();
        return new(revision, artifactRoot);
    }

    private int? MemoryEntryKind(byte[] artifact, string path)
    {
        if (path.Length == 0) return 1;
        using var statement = Prepare(database, """
            SELECT DISTINCT kind FROM memory_entry WHERE root IN (?1,?2) AND path=?3;
            """);
        statement.Bind(1, artifact);
        statement.Bind(2, NotesRoot);
        statement.Bind(3, PathKey(path));
        if (!statement.Next()) return null;
        var kind = checked((int)statement.Number(0));
        if (statement.Next()) throw new InvalidDataException("Memory path has conflicting entry kinds.");
        return kind;
    }

    private IReadOnlyList<CodexMemoryEntry> ListMemoryEntries(byte[] artifact, string path,
        string? afterPath, int limit)
    {
        var kind = MemoryEntryKind(artifact, path) ?? throw new KeyNotFoundException("Memory path not found.");
        if (kind == 0)
            return afterPath is null || string.CompareOrdinal(path, afterPath) > 0 ? [new(path, false)] : [];

        using var statement = Prepare(database, """
            SELECT DISTINCT path,kind FROM memory_entry
            WHERE root IN (?1,?2) AND parent=?3 AND path>?4 ORDER BY path LIMIT ?5;
            """);
        statement.Bind(1, artifact);
        statement.Bind(2, NotesRoot);
        statement.Bind(3, PathKey(path));
        statement.Bind(4, PathKey(afterPath ?? ""));
        statement.Bind(5, limit);
        var results = new List<CodexMemoryEntry>();
        while (statement.Next())
            results.Add(new(DecodePath(statement.Blob(0)), statement.Number(1) == 1));
        return results;
    }

    private CodexMemoryFile ReadMemoryFile(byte[] artifact, string path, int maxBytes)
    {
        // Do not fetch oversized bodies. Directory metadata and preceding files are not read.
        using var statement = Prepare(database, """
            SELECT size,CASE WHEN size<=?4 THEN body ELSE NULL END FROM memory_entry
            WHERE root IN (?1,?2) AND path=?3 AND kind=0;
            """);
        statement.Bind(1, artifact);
        statement.Bind(2, NotesRoot);
        statement.Bind(3, PathKey(path));
        statement.Bind(4, maxBytes);
        if (!statement.Next()) throw new KeyNotFoundException("Memory file not found.");
        var size = statement.Number(0);
        if (size < 0 || size > maxBytes) throw new InvalidDataException("Memory file exceeds the read bound.");
        var packed = statement.Blob(1);
        if (packed.Length != size + 1 || packed[0] != 1)
            throw new InvalidDataException("Invalid memory file body.");
        var text = Utf8.GetString(packed.AsSpan(1));
        if (statement.Next()) throw new InvalidDataException("Conflicting memory file bodies.");
        return new(path, text);
    }

    private CodexMemoryFile? NextMemoryFile(byte[] artifact, string path, string? afterPath, bool inclusive, int maxBytes)
    {
        // Select only the next key and size using the range index; do not prefetch content
        // for earlier keys, later matches, or files that will never be consumed.
        var lower = path.Length == 0 ? "" : path + "/";
        var start = afterPath is not null && string.CompareOrdinal(afterPath, lower) >= 0 ? afterPath : lower;
        var upper = path.Length == 0 ? new byte[] { 0, 1 } : PathKey(path + "0");
        string? next;
        using (var statement = Prepare(database, """
            SELECT path,size FROM memory_entry WHERE root IN (?1,?2) AND kind=0
                AND path>=?3 AND path<?4 AND (?5=1 OR path>?3)
            ORDER BY path LIMIT 1;
            """))
        {
            statement.Bind(1, artifact);
            statement.Bind(2, NotesRoot);
            statement.Bind(3, PathKey(start));
            statement.Bind(4, upper);
            statement.Bind(5, inclusive || start != afterPath ? 1 : 0);
            if (!statement.Next()) return null;
            if (statement.Number(1) > maxBytes) throw new InvalidDataException("Memory file exceeds the search bound.");
            next = DecodePath(statement.Blob(0));
            statement.Done();
        }
        return ReadMemoryFile(artifact, next, maxBytes);
    }

    private sealed class LocalMemorySnapshot(CodexMacOsSqliteMemoryJobStorage owner, CodexMemoryJobKey key,
        CodexMemoryAccess access, MemoryView opened) : ICodexMemorySnapshot
    {
        private int closed;
        public string Revision => opened.Revision;

        public async ValueTask<IReadOnlyList<CodexMemoryEntry>> ListAsync(string path, string? afterPath,
            int limit, CancellationToken cancellationToken)
        {
            ValidateRequest(path, "list", allowRoot: true);
            if (limit is < 1 or > 2001) throw new ArgumentOutOfRangeException(nameof(limit));
            if (afterPath is not null)
            {
                _ = CodexMemoryBackend.ValidatePath(afterPath, false);
                if (afterPath != path && ParentPath(afterPath) != path)
                    throw new UnauthorizedAccessException("Memory list cursor exceeds its authorized directory.");
            }
            return await QueryAsync(view => owner.ListMemoryEntries(view.ArtifactRoot, path, afterPath, limit),
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<CodexMemoryFile> ReadAsync(string path, int maxBytes, CancellationToken cancellationToken)
        {
            ValidateRequest(path, access.Operation == "summary" ? "summary" : "read", allowRoot: false);
            ValidateByteLimit(maxBytes);
            return await QueryAsync(view => owner.ReadMemoryFile(view.ArtifactRoot, path, maxBytes),
                cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<CodexMemoryFile> EnumerateFilesAsync(string path, string? atOrAfterPath,
            int maxBytes, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ValidateRequest(path, "search", allowRoot: true);
            ValidateByteLimit(maxBytes);
            if (atOrAfterPath is not null)
            {
                _ = CodexMemoryBackend.ValidatePath(atOrAfterPath, false);
                if (path.Length != 0 && atOrAfterPath != path &&
                    !atOrAfterPath.StartsWith(path + "/", StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Memory search cursor exceeds its authorized subtree.");
            }

            var first = true;
            var after = atOrAfterPath;
            while (true)
            {
                var next = await QueryAsync<CodexMemoryFile?>(view =>
                {
                    if (first)
                    {
                        var kind = owner.MemoryEntryKind(view.ArtifactRoot, path) ??
                            throw new KeyNotFoundException("Memory search path not found.");
                        if (kind == 0)
                            return after is null || string.CompareOrdinal(path, after) >= 0
                                ? owner.ReadMemoryFile(view.ArtifactRoot, path, maxBytes) : null;
                    }
                    return owner.NextMemoryFile(view.ArtifactRoot, path, after, first, maxBytes);
                }, cancellationToken).ConfigureAwait(false);
                if (next is null) yield break;
                yield return next;
                if (next.Path == path) yield break;
                first = false;
                after = next.Path;
            }
        }

        private async Task<T> QueryAsync<T>(Func<MemoryView, T> query, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
            await owner.AuthorizeAsync(new(key, "memory_read_snapshot", MemoryAccess: access), cancellationToken)
                .ConfigureAwait(false);
            return await owner.RunAsync(() => Transaction(owner.database, false, cancellationToken, () =>
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
                var current = owner.ReadMemoryView(access);
                if (current.Revision != opened.Revision)
                    throw new InvalidOperationException("Memory namespace or permission changed; reopen the snapshot.");
                return query(current);
            }), cancellationToken).ConfigureAwait(false);
        }

        private void ValidateRequest(string path, string operation, bool allowRoot)
        {
            _ = CodexMemoryBackend.ValidatePath(path, allowRoot);
            if (access.Operation != operation || access.Path != path)
                throw new UnauthorizedAccessException("Memory snapshot cannot widen its authorized path or operation.");
        }

        private static void ValidateByteLimit(int maxBytes)
        {
            if (maxBytes is < 1 or > CodexMemoryBackend.MaximumDocumentBytes)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref closed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
