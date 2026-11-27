using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex;

/// <summary>Exact immutable local artifact staging authorization, bound to an admitted phase-2 claim.</summary>
/// <param name="Admission">Durable sampling identity.</param>
/// <param name="Revision">Content and provenance digest.</param>
/// <param name="Selected">Exact extraction snapshots used to generate the artifact.</param>
/// <param name="FileCount">Number of regular text files.</param>
/// <param name="TotalBytes">Total UTF-8 file bytes, excluding derived directory entries.</param>
public sealed record CodexMemoryArtifactStage(CodexMemorySamplingAdmission Admission, string Revision,
    IReadOnlyList<CodexMemorySelectedSnapshot> Selected, int FileCount, long TotalBytes);

/// <summary>Local artifact metadata used by the host to evaluate memory readiness without reading file contents.</summary>
/// <param name="ArtifactRevision">Current consolidation pointer, including externally managed revisions.</param>
/// <param name="MaximumConsolidatedThreads">Historical maximum successful selection size.</param>
/// <param name="HasValidV2Summary">Whether the current local manifest contains a validated V2 summary.</param>
public sealed record CodexMemoryLocalArtifactStatus(string? ArtifactRevision,
    uint MaximumConsolidatedThreads, bool HasValidV2Summary);

public sealed partial class CodexMacOsSqliteMemoryJobStorage
{
    private const string LocalArtifactPrefix = "sqlite-memory-v1:";
    private const string NoteDirectory = "extensions/ad_hoc/notes";
    private static readonly byte[] NotesRoot = [0];
    private static readonly byte[] MissingRoot = [255];
    // Encode ordinal UTF-16 paths as big-endian BLOB keys, including a nonempty root sentinel.
    private static readonly UnicodeEncoding PathEncoding = new(true, false, true);

    /// <summary>Maximum regular files in a staged local artifact.</summary>
    public const int MaximumArtifactFiles = 2000;
    /// <summary>Maximum aggregate UTF-8 bytes in a staged local artifact.</summary>
    public const long MaximumArtifactBytes = 32L * 1024 * 1024;
    private const int MaximumArtifactEntries = 10000;

    private const string ArtifactSchema = """
        CREATE TABLE memory_manifest(
            root BLOB PRIMARY KEY NOT NULL,
            claim BLOB NOT NULL,
            worker BLOB NOT NULL,
            attempt BLOB NOT NULL,
            watermark INTEGER NOT NULL,
            selection BLOB NOT NULL,
            file_count INTEGER NOT NULL,
            total_bytes INTEGER NOT NULL,
            valid_summary INTEGER NOT NULL CHECK(valid_summary IN (0,1)),
            created_at INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE UNIQUE INDEX memory_manifest_claim ON memory_manifest(claim) WHERE root<>X'00';
        CREATE INDEX memory_manifest_created ON memory_manifest(created_at,root);
        INSERT INTO memory_manifest VALUES(X'00',X'00',X'00',X'00',0,X'00',0,0,0,0);
        CREATE TABLE memory_entry(
            root BLOB NOT NULL REFERENCES memory_manifest(root) ON DELETE CASCADE,
            path BLOB NOT NULL,
            parent BLOB NOT NULL,
            kind INTEGER NOT NULL CHECK(kind IN (0,1)),
            size INTEGER NOT NULL CHECK(size>=0),
            body BLOB NOT NULL,
            PRIMARY KEY(root,path)
        ) WITHOUT ROWID;
        CREATE INDEX memory_entry_parent ON memory_entry(root,parent,path);
        CREATE INDEX memory_entry_files ON memory_entry(root,kind,path);
        CREATE TABLE memory_artifact_source(
            root BLOB NOT NULL REFERENCES memory_manifest(root) ON DELETE CASCADE,
            thread BLOB NOT NULL,
            watermark INTEGER NOT NULL,
            PRIMARY KEY(root,thread)
        ) WITHOUT ROWID;
        CREATE INDEX memory_artifact_source_thread ON memory_artifact_source(thread,root);
        CREATE TABLE memory_view(
            id INTEGER PRIMARY KEY CHECK(id=1),
            instance BLOB NOT NULL,
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0)
        );
        INSERT INTO memory_view VALUES(1,randomblob(16),0);
        CREATE TRIGGER memory_pointer_changed AFTER UPDATE OF artifact ON progress
        WHEN OLD.artifact IS NOT NEW.artifact BEGIN
            UPDATE memory_view SET revision=revision+1 WHERE id=1;
        END;
        CREATE TABLE memory_io_policy(
            thread BLOB PRIMARY KEY NOT NULL,
            generation INTEGER NOT NULL CHECK(generation>=0),
            flags INTEGER NOT NULL CHECK(flags BETWEEN 0 AND 7)
        ) WITHOUT ROWID;
        """;

    private sealed record ArtifactEntry(string Path, bool IsDirectory, byte[] Body)
    {
        internal int Size => IsDirectory ? 0 : Body.Length - 1;
    }

    /// <summary>
    /// Stages one bounded, immutable text artifact for an already admitted, live phase-2 attempt.
    /// The content, claim, exact selection and scope determine its revision; only an atomic
    /// successful phase-2 settlement can make it visible to retrieval. Repeating identical
    /// staging is idempotent, but different contents under the same claim are refused.
    /// </summary>
    public async Task<CodexMemoryConsolidationOutput> StageMemoryArtifactAsync(CodexMemorySamplingAdmission admission,
        IReadOnlyList<CodexMemorySelectedSnapshot> selected, IReadOnlyList<CodexMemoryFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(files);
        CheckKey(admission.Key);
        admission.Validate();
        if (admission.Key.ThreadId is not null)
            throw new ArgumentException("Artifacts require an admitted phase-2 claim.", nameof(admission));

        var selection = selected.ToArray();
        var selectionHash = ArtifactSelectionHash(selection);
        var entries = PrepareArtifactEntries(files, out var fileCount, out var totalBytes, out var validSummary);
        if (scope.Version == CodexMemoryVersion.V2 && !validSummary)
            throw new InvalidDataException("V2 artifact requires a valid memory_summary.md.");
        var revision = ArtifactRevision(admission, selectionHash, entries);
        var stage = new CodexMemoryArtifactStage(admission, revision, Array.AsReadOnly(selection), fileCount, totalBytes);
        await AuthorizeAsync(new(admission.Key, "stage_artifact", Artifact: stage), cancellationToken).ConfigureAwait(false);

        return await RunAsync(() => Transaction(database, true, cancellationToken, () =>
        {
            var observed = Observe(admission.Key);
            if (!admission.Matches(observed, clock.GetUtcNow()) || !HasSamplingAdmission(admission) ||
                !SelectionIsEligible(selection, cancellationToken))
                throw new InvalidOperationException("Memory artifact claim or source selection is no longer valid.");
            var root = Utf8.GetBytes(revision);
            using (var existing = Prepare(database, "SELECT root FROM memory_manifest WHERE claim=?1 AND root<>X'00';"))
            {
                existing.Bind(1, admission.OwnershipToken.ToByteArray());
                if (existing.Next())
                {
                    var matches = existing.Blob(0).AsSpan().SequenceEqual(root);
                    existing.Done();
                    if (!matches) throw new InvalidOperationException("Another artifact is already staged for this claim.");
                    return new CodexMemoryConsolidationOutput(revision, stage.Selected);
                }
            }

            using (var manifest = Prepare(database, """
                INSERT INTO memory_manifest(root,claim,worker,attempt,watermark,selection,file_count,total_bytes,
                    valid_summary,created_at) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10);
                """))
            {
                manifest.Bind(1, root);
                manifest.Bind(2, admission.OwnershipToken.ToByteArray());
                manifest.Bind(3, admission.WorkerId.ToByteArray());
                manifest.Bind(4, admission.AttemptId.ToByteArray());
                manifest.Bind(5, admission.ClaimedWatermark);
                manifest.Bind(6, selectionHash);
                manifest.Bind(7, fileCount);
                manifest.Bind(8, totalBytes);
                manifest.Bind(9, validSummary ? 1 : 0);
                manifest.Bind(10, clock.GetUtcNow().UtcTicks);
                manifest.Done();
            }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InsertMemoryEntry(root, entry);
            }
            foreach (var item in selection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var statement = Prepare(database, """
                    INSERT INTO memory_artifact_source(root,thread,watermark) VALUES(?1,?2,?3);
                    """);
                statement.Bind(1, root);
                statement.Bind(2, item.ThreadId.ToByteArray());
                statement.Bind(3, item.SourceWatermark);
                statement.Done();
            }
            return new CodexMemoryConsolidationOutput(revision, stage.Selected);
        }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads only current artifact readiness and historical coverage metadata.</summary>
    public async Task<CodexMemoryLocalArtifactStatus> ReadLocalArtifactStatusAsync(CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(new(new(scope, null), "read_artifact_status"), cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            using var query = Prepare(database, """
                SELECT p.artifact,p.max_count,COALESCE(m.valid_summary,0)
                FROM progress p LEFT JOIN memory_manifest m ON m.root=p.artifact WHERE p.id=1;
                """);
            if (!query.Next()) throw new InvalidDataException("Missing memory progress.");
            var result = new CodexMemoryLocalArtifactStatus(query.IsNull(0) ? null : Utf8.GetString(query.Blob(0)),
                checked((uint)query.Number(1)), scope.Version == CodexMemoryVersion.V2 && query.Number(2) == 1);
            query.Done();
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private bool HasSamplingAdmission(CodexMemorySamplingAdmission admission)
    {
        using var query = Prepare(database, """
            SELECT 1 FROM sampling WHERE claim=?1 AND attempt=?2 AND thread=?3 AND worker=?4 AND watermark=?5;
            """);
        query.Bind(1, admission.OwnershipToken.ToByteArray());
        query.Bind(2, admission.AttemptId.ToByteArray());
        query.Bind(3, KeyBytes(admission.Key));
        query.Bind(4, admission.WorkerId.ToByteArray());
        query.Bind(5, admission.ClaimedWatermark);
        if (!query.Next()) return false;
        query.Done();
        return true;
    }

    // External artifact pointers remain a host-authorized capability. The reserved local
    // prefix must resolve to this exact immutable staged claim and input selection.
    private bool CanPublishLocalArtifact(CodexMemoryJobChange change, CodexMemoryJobState prior)
    {
        var output = change.Consolidation!;
        if (!output.ArtifactRevision.StartsWith(LocalArtifactPrefix, StringComparison.Ordinal)) return true;
        using var query = Prepare(database, """
            SELECT claim,worker,watermark,selection FROM memory_manifest WHERE root=?1;
            """);
        query.Bind(1, Utf8.GetBytes(output.ArtifactRevision));
        if (!query.Next()) return false;
        var matches = new Guid(query.Blob(0)) == change.ExpectedOwnershipToken &&
            new Guid(query.Blob(1)) == change.ExpectedWorkerId &&
            query.Number(2) == prior.ClaimedWatermark &&
            CryptographicOperations.FixedTimeEquals(query.Blob(3), ArtifactSelectionHash(output.Selected));
        query.Done();
        return matches;
    }

    private static byte[] ArtifactSelectionHash(IReadOnlyList<CodexMemorySelectedSnapshot> selected)
    {
        if (selected.Count > 10000 || selected.Any(s => s.ThreadId == Guid.Empty || s.SourceWatermark < 0) ||
            selected.Select(s => s.ThreadId).Distinct().Count() != selected.Count)
            throw new ArgumentException("Invalid artifact source selection.", nameof(selected));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashNumber(hash, selected.Count);
        foreach (var source in selected.OrderBy(s => s.ThreadId))
        {
            hash.AppendData(source.ThreadId.ToByteArray());
            HashNumber(hash, source.SourceWatermark);
        }
        return hash.GetHashAndReset();
    }

    private string ArtifactRevision(CodexMemorySamplingAdmission admission, byte[] selectionHash,
        IReadOnlyList<ArtifactEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashBytes(hash, Utf8.GetBytes(LocalArtifactPrefix));
        HashBytes(hash, Utf8.GetBytes(scope.AccountId));
        HashNumber(hash, (int)scope.Version);
        hash.AppendData(admission.OwnershipToken.ToByteArray());
        hash.AppendData(admission.WorkerId.ToByteArray());
        hash.AppendData(admission.AttemptId.ToByteArray());
        HashNumber(hash, admission.ClaimedWatermark);
        hash.AppendData(selectionHash);
        HashNumber(hash, entries.Count);
        foreach (var entry in entries)
        {
            HashBytes(hash, PathKey(entry.Path));
            HashNumber(hash, entry.IsDirectory ? 1 : 0);
            HashBytes(hash, entry.Body);
        }
        return LocalArtifactPrefix + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void HashNumber(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void HashBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        HashNumber(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static ArtifactEntry[] PrepareArtifactEntries(IReadOnlyList<CodexMemoryFile> files,
        out int fileCount, out long totalBytes, out bool validSummary)
    {
        if (files.Count > MaximumArtifactFiles) throw new ArgumentException("Artifact has too many files.", nameof(files));
        var frozen = files.ToArray();
        if (frozen.Length > MaximumArtifactFiles) throw new ArgumentException("Artifact has too many files.", nameof(files));
        var entries = new SortedDictionary<string, ArtifactEntry>(StringComparer.Ordinal);
        totalBytes = 0;
        validSummary = false;
        foreach (var file in frozen)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(file.Content);
            var path = CodexMemoryBackend.ValidatePath(file.Path, false);
            if (path is "extensions" or "extensions/ad_hoc" || path == NoteDirectory ||
                path.StartsWith(NoteDirectory + "/", StringComparison.Ordinal) || path.Count(c => c == '/') > 63)
                throw new ArgumentException("Artifact path overlaps the note namespace or exceeds its depth bound.", nameof(files));
            _ = PathKey(path);
            var size = Utf8.GetByteCount(file.Content);
            if (size > CodexMemoryBackend.MaximumDocumentBytes || checked(totalBytes + size) > MaximumArtifactBytes)
                throw new ArgumentException("Artifact file or aggregate bytes exceed their bound.", nameof(files));
            totalBytes += size;
            if (!entries.TryAdd(path, new(path, false, PackMemoryBody(file.Content, size))))
                throw new ArgumentException("Duplicate artifact path or file/directory collision.", nameof(files));
            if (path == "memory_summary.md") validSummary = CodexMemoryReadiness.IsValidV2Summary(file.Content);

            for (var parent = ParentPath(path); parent.Length != 0; parent = ParentPath(parent))
            {
                if (entries.TryGetValue(parent, out var existing))
                {
                    if (!existing.IsDirectory) throw new ArgumentException("Artifact file/directory collision.", nameof(files));
                }
                else entries.Add(parent, new(parent, true, [0]));
                if (entries.Count > MaximumArtifactEntries)
                    throw new ArgumentException("Artifact has too many directory entries.", nameof(files));
            }
        }
        fileCount = frozen.Length;
        return entries.Values.ToArray();
    }

    private static byte[] PackMemoryBody(string content, int size)
    {
        var packed = new byte[size + 1];
        packed[0] = 1;
        if (Utf8.GetBytes(content.AsSpan(), packed.AsSpan(1)) != size)
            throw new InvalidDataException("Memory document byte count changed.");
        return packed;
    }

    private static byte[] PathKey(string path) => PathEncoding.GetBytes("\0" + path);
    private static string DecodePath(byte[] key)
    {
        var path = PathEncoding.GetString(key);
        if (path.Length == 0 || path[0] != '\0') throw new InvalidDataException("Invalid memory path key.");
        return path[1..];
    }

    private static string ParentPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private void InsertMemoryEntry(byte[] root, ArtifactEntry entry, bool ignoreExisting = false)
    {
        using var statement = Prepare(database, """
            INSERT INTO memory_entry(root,path,parent,kind,size,body) VALUES(?1,?2,?3,?4,?5,?6)
            """ + (ignoreExisting ? " ON CONFLICT(root,path) DO NOTHING;" : ";"));
        statement.Bind(1, root);
        statement.Bind(2, PathKey(entry.Path));
        statement.Bind(3, PathKey(ParentPath(entry.Path)));
        statement.Bind(4, entry.IsDirectory ? 1 : 0);
        statement.Bind(5, entry.Size);
        statement.Bind(6, entry.Body);
        statement.Done();
    }
}
