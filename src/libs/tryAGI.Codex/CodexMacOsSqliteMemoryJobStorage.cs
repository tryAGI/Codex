using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Host-authoritative memory eligibility and monotonic source generation.</summary>
/// <param name="ThreadId">Logical native thread.</param>
/// <param name="Watermark">Monotonic source generation, including permission changes.</param>
/// <param name="Enabled">Whether extraction is permitted and the source is not polluted.</param>
public sealed record CodexMemorySourceUpdate(Guid ThreadId, long Watermark, bool Enabled);

/// <summary>Authorization context for one local memory job database operation.</summary>
/// <param name="Key">Authenticated account/version and job identity.</param>
/// <param name="Operation">Exact storage operation.</param>
/// <param name="Change">Frozen mutation, including its worker and fencing token.</param>
/// <param name="Source">Source publication, when applicable.</param>
/// <param name="OperationId">Receipt lookup identity, when applicable.</param>
/// <param name="Watermark">Exact requested extraction snapshot, when applicable.</param>
public sealed record CodexMemoryJobStorageAccess(CodexMemoryJobKey Key, string Operation,
    CodexMemoryJobChange? Change = null, CodexMemorySourceUpdate? Source = null,
    Guid? OperationId = null, long? Watermark = null);

/// <summary>Current verified consolidation pointer and historical maximum successful coverage.</summary>
/// <param name="ArtifactRevision">Immutable artifact revision, or null if no safe artifact is published.</param>
/// <param name="MaximumConsolidatedThreads">Largest successful exact selection since initialization.</param>
public sealed record CodexMemoryConsolidationState(string? ArtifactRevision, uint MaximumConsolidatedThreads);

/// <summary>
/// Local, account/version-bound SQLite implementation of ICodexMemoryJobStorage. Uses the macOS
/// system SQLite library with durable rollback transactions; it never reads or rewrites reasoning
/// history. The trusted host must supply a private stable directory, exclude external writers,
/// links and database replacement, and provide any required encryption at rest. It must publish
/// monotonic source generations and permission revocations before admitting extraction for them.
/// A distinct generation is required when eligibility changes, including re-enabling a source.
/// The authorization callback authenticates the exact worker/mutation and verifies any immutable
/// consolidation artifact before commit. It must not treat a model claim as authorization.
/// </summary>
public sealed partial class CodexMacOsSqliteMemoryJobStorage : ICodexMemoryJobStorage, IAsyncDisposable
{
    private const int ApplicationId = 1129137485;
    private const int SchemaVersion = 1;
    private const int MaximumBlobBytes = 8 * 1024 * 1024;
    private const int Ok = 0, Row = 100, Done = 101;
    private const int OpenReadWrite = 0x2, OpenFullMutex = 0x10000, OpenNoFollow = 0x01000000;
    private const string Library = "/usr/lib/libsqlite3.dylib";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Global = Guid.Empty.ToByteArray();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CodexMemoryScope scope;
    private readonly Func<CodexMemoryJobStorageAccess, CancellationToken, ValueTask> authorize;
    private readonly TimeProvider clock;
    private IntPtr database;
    private bool disposed;

    private CodexMacOsSqliteMemoryJobStorage(IntPtr database, CodexMemoryScope scope,
        Func<CodexMemoryJobStorageAccess, CancellationToken, ValueTask> authorize, TimeProvider clock)
    {
        this.database = database;
        this.scope = scope;
        this.authorize = authorize;
        this.clock = clock;
    }

    /// <summary>
    /// Creates a new private database exclusively, or opens an existing initialized database.
    /// Reopening a different account/version or an unknown schema fails closed.
    /// </summary>
    public static Task<CodexMacOsSqliteMemoryJobStorage> OpenAsync(string path, CodexMemoryScope scope,
        Func<CodexMemoryJobStorageAccess, CancellationToken, ValueTask> authorize,
        bool createNew = false, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Local memory job storage requires macOS.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AccountId);
        ArgumentNullException.ThrowIfNull(authorize);
        if (scope.AccountId.Length > 512 || scope.AccountId.Any(char.IsControl) ||
            scope.Version is not (CodexMemoryVersion.V1 or CodexMemoryVersion.V2))
            throw new ArgumentException("Invalid memory database scope.", nameof(scope));
        if (!Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path) || path.Any(char.IsControl) ||
            Path.GetDirectoryName(path) is not { } directory || directory == "/")
            throw new ArgumentException("Memory database requires a normalized private path.", nameof(path));
        return Task.Run(() => OpenCore(path, directory, scope, authorize, createNew, timeProvider ?? TimeProvider.System,
            cancellationToken), cancellationToken);
    }

    private static CodexMacOsSqliteMemoryJobStorage OpenCore(string path, string directory, CodexMemoryScope scope,
        Func<CodexMemoryJobStorageAccess, CancellationToken, ValueTask> authorize, bool createNew,
        TimeProvider clock, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (!Directory.Exists(directory) || CodexFileSystem.Canonical(directory) != directory ||
            (File.GetUnixFileMode(directory) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0)
            throw new UnauthorizedAccessException("Memory database directory must be private and canonical.");
        if (LibVersion() < 3031000) throw new PlatformNotSupportedException("SQLite 3.31 or later is required.");
        if (createNew)
        {
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            file.Flush(flushToDisk: true);
        }
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null ||
            CodexFileSystemNative.Kind(path, false) != CodexFileSystemNative.Regular ||
            (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw new UnauthorizedAccessException("Memory database must be a private existing regular file.");

        var code = OpenV2(path, out var db, OpenReadWrite | OpenFullMutex | OpenNoFollow, IntPtr.Zero);
        try
        {
            Check(code);
            Check(BusyTimeout(db, 5000));
            _ = Limit(db, 0, MaximumBlobBytes + 65536);
            Execute(db, "PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;");
            if (!createNew) Verify(db, scope);
            if (ScalarText(db, "PRAGMA journal_mode=DELETE;") != "delete")
                throw new IOException("Could not establish SQLite rollback journal mode.");
            Execute(db, "PRAGMA synchronous=EXTRA; PRAGMA fullfsync=ON;");
            if (ScalarNumber(db, "PRAGMA synchronous;") != 3 || ScalarNumber(db, "PRAGMA fullfsync;") != 1)
                throw new IOException("Could not establish durable SQLite synchronization.");
            if (createNew)
                Transaction(db, true, ct, () =>
                {
                    Execute(db, """
                        CREATE TABLE identity(id INTEGER PRIMARY KEY CHECK(id=1), body BLOB NOT NULL);
                        CREATE TABLE source(thread BLOB PRIMARY KEY NOT NULL, watermark INTEGER NOT NULL CHECK(watermark>=0),
                            enabled INTEGER NOT NULL CHECK(enabled IN (0,1))) WITHOUT ROWID;
                        CREATE TABLE job(thread BLOB PRIMARY KEY NOT NULL, revision INTEGER NOT NULL CHECK(revision>=1),
                            status INTEGER NOT NULL, lease_until INTEGER NOT NULL, body BLOB NOT NULL) WITHOUT ROWID;
                        CREATE INDEX job_running ON job(status,lease_until);
                        CREATE TABLE extraction(thread BLOB PRIMARY KEY NOT NULL, watermark INTEGER NOT NULL CHECK(watermark>=0),
                            body BLOB NOT NULL) WITHOUT ROWID;
                        CREATE INDEX extraction_order ON extraction(watermark DESC,thread);
                        CREATE TABLE receipt(operation BLOB PRIMARY KEY NOT NULL, thread BLOB NOT NULL, fingerprint BLOB NOT NULL,
                            settlement INTEGER NOT NULL, revision INTEGER NOT NULL) WITHOUT ROWID;
                        CREATE TABLE selected(thread BLOB PRIMARY KEY NOT NULL, watermark INTEGER NOT NULL) WITHOUT ROWID;
                        CREATE TABLE progress(id INTEGER PRIMARY KEY CHECK(id=1), max_count INTEGER NOT NULL,
                            artifact BLOB);
                        INSERT INTO progress(id,max_count,artifact) VALUES(1,0,NULL);
                        PRAGMA application_id=1129137485;
                        PRAGMA user_version=1;
                        """);
                    using var identity = Prepare(db, "INSERT INTO identity(id,body) VALUES(1,?1);");
                    identity.Bind(1, Serialize(scope, CodexMemoryStorageJson.Default.CodexMemoryScope));
                    identity.Done();
                    return true;
                });
            Verify(db, scope);
            ct.ThrowIfCancellationRequested();
            return new(db, scope, authorize, clock);
        }
        catch
        {
            if (db != IntPtr.Zero) _ = CloseV2(db);
            throw;
        }
    }

    private static void Verify(IntPtr db, CodexMemoryScope scope)
    {
        if (ScalarNumber(db, "PRAGMA application_id;") != ApplicationId ||
            ScalarNumber(db, "PRAGMA user_version;") != SchemaVersion)
            throw new InvalidDataException("Unknown or uninitialized memory job database.");
        using (var identity = Prepare(db, "SELECT body FROM identity WHERE id=1;"))
        {
            if (!identity.Next() || Deserialize(identity.Blob(0), CodexMemoryStorageJson.Default.CodexMemoryScope) != scope)
                throw new UnauthorizedAccessException("Memory database belongs to another namespace.");
            identity.Done();
        }
        using var schema = Prepare(db, """
            SELECT s.thread,s.watermark,s.enabled,j.revision,j.status,j.lease_until,j.body,e.body,r.operation,
                r.fingerprint,r.settlement,r.revision,k.watermark,p.max_count,p.artifact
            FROM source s,job j,extraction e,receipt r,selected k,progress p LIMIT 0;
            """);
        schema.Done();
    }

    /// <inheritdoc />
    public async ValueTask<CodexMemoryJobObservation> ReadAsync(CodexMemoryJobKey key, CancellationToken cancellationToken)
    {
        CheckKey(key);
        await AuthorizeAsync(new(key, "read"), cancellationToken).ConfigureAwait(false);
        return await RunAsync(() => Transaction(database, false, cancellationToken, () => Observe(key)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes authoritative source metadata without reading the source body. Equal generations
    /// are idempotent only when eligibility is identical. Older or conflicting generations are refused.
    /// Disabling a source removes its extraction, hides any affected consolidated pointer and
    /// enqueues phase 2 in the same transaction. No original history is deleted.
    /// </summary>
    public async Task<bool> PublishSourceAsync(CodexMemorySourceUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var key = new CodexMemoryJobKey(scope, update.ThreadId);
        CheckKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(update.Watermark);
        await AuthorizeAsync(new(key, "publish_source", Source: update), cancellationToken).ConfigureAwait(false);
        return await RunAsync(() => Transaction(database, true, cancellationToken, () =>
        {
            var before = ReadSource(update.ThreadId.ToByteArray());
            if (before is { } prior && (update.Watermark < prior.Watermark ||
                update.Watermark == prior.Watermark && update.Enabled != prior.Enabled))
                return false;
            if (before is { } same && update.Watermark == same.Watermark) return true;
            var thread = update.ThreadId.ToByteArray();
            using (var statement = Prepare(database, """
                INSERT INTO source(thread,watermark,enabled) VALUES(?1,?2,?3)
                ON CONFLICT(thread) DO UPDATE SET watermark=excluded.watermark,enabled=excluded.enabled;
                """))
            {
                statement.Bind(1, thread);
                statement.Bind(2, update.Watermark);
                statement.Bind(3, update.Enabled ? 1L : 0L);
                statement.Done();
            }
            if (!update.Enabled)
            {
                var changed = DeleteExtraction(thread);
                var selected = InvalidateSelected(thread);
                if (changed || selected) Enqueue(update.Watermark);
            }
            return true;
        }), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<CodexMemoryJobReceipt> ApplyAsync(CodexMemoryJobChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        CheckKey(change.Key);
        if (change.OperationId == Guid.Empty) throw new ArgumentException("Memory operation identity is empty.", nameof(change));
        // Freeze the exact operation before authorization, including caller-owned collections.
        var body = Serialize(change, CodexMemoryStorageJson.Default.CodexMemoryJobChange);
        var frozen = Deserialize(body, CodexMemoryStorageJson.Default.CodexMemoryJobChange);
        var fingerprint = SHA256.HashData(body);
        await AuthorizeAsync(new(frozen.Key, "apply", Change: frozen), cancellationToken).ConfigureAwait(false);
        // Do not allow a callback to modify a selected array after authorizing its contents.
        if (!Serialize(frozen, CodexMemoryStorageJson.Default.CodexMemoryJobChange).AsSpan().SequenceEqual(body))
            throw new InvalidDataException("Memory mutation changed during authorization.");
        return await RunAsync(() => Transaction(database, true, cancellationToken,
            () => Apply(frozen, fingerprint, body, cancellationToken)), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<CodexMemoryJobReceipt?> ReadReceiptAsync(CodexMemoryJobKey key, Guid operationId,
        CancellationToken cancellationToken)
    {
        CheckKey(key);
        if (operationId == Guid.Empty) throw new ArgumentException("Memory operation identity is empty.", nameof(operationId));
        await AuthorizeAsync(new(key, "receipt", OperationId: operationId), cancellationToken).ConfigureAwait(false);
        return await RunAsync(() => ReadReceipt(key, operationId, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists only bounded, eligible extraction identities; no output body is loaded.</summary>
    public async Task<IReadOnlyList<CodexMemorySelectedSnapshot>> ListEligibleOutputsAsync(int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 1000);
        await AuthorizeAsync(new(new(scope, null), "list_outputs"), cancellationToken).ConfigureAwait(false);
        return await RunAsync<IReadOnlyList<CodexMemorySelectedSnapshot>>(() =>
        {
            using var statement = Prepare(database, """
                SELECT e.thread,e.watermark FROM extraction e JOIN source s ON s.thread=e.thread
                WHERE s.enabled=1 AND e.watermark<=s.watermark ORDER BY e.watermark DESC,e.thread LIMIT ?1;
                """);
            statement.Bind(1, maximumResults);
            var result = new List<CodexMemorySelectedSnapshot>();
            while (statement.Next())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(new Guid(statement.Blob(0)), statement.Number(1)));
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads only the requested still-eligible extraction generation, or null if stale.</summary>
    public async Task<CodexMemoryExtractionOutput?> ReadExtractionAsync(CodexMemorySelectedSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var key = new CodexMemoryJobKey(scope, snapshot.ThreadId);
        CheckKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.SourceWatermark);
        await AuthorizeAsync(new(key, "read_output", Watermark: snapshot.SourceWatermark), cancellationToken).ConfigureAwait(false);
        return await RunAsync<CodexMemoryExtractionOutput?>(() =>
        {
            using var statement = Prepare(database, """
                SELECT e.body FROM extraction e JOIN source s ON s.thread=e.thread
                WHERE e.thread=?1 AND e.watermark=?2 AND s.enabled=1 AND e.watermark<=s.watermark;
                """);
            statement.Bind(1, snapshot.ThreadId.ToByteArray());
            statement.Bind(2, snapshot.SourceWatermark);
            if (!statement.Next()) return null;
            var output = Deserialize(statement.Blob(0), CodexMemoryStorageJson.Default.CodexMemoryExtractionOutput);
            if (output.SourceWatermark != snapshot.SourceWatermark) throw new InvalidDataException("Extraction generation mismatch.");
            statement.Done();
            return output;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads only current consolidation pointer and historical coverage metadata.</summary>
    public async Task<CodexMemoryConsolidationState> ReadConsolidationAsync(CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(new(new(scope, null), "read_consolidation"), cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            using var statement = Prepare(database, "SELECT artifact,max_count FROM progress WHERE id=1;");
            if (!statement.Next()) throw new InvalidDataException("Missing memory progress.");
            var result = new CodexMemoryConsolidationState(statement.IsNull(0) ? null : Utf8.GetString(statement.Blob(0)),
                checked((uint)statement.Number(1)));
            statement.Done();
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private CodexMemoryJobReceipt Apply(CodexMemoryJobChange change, byte[] fingerprint, byte[] body, CancellationToken ct)
    {
        var duplicate = ReadReceipt(change.Key, change.OperationId, fingerprint);
        if (duplicate is not null) return duplicate;
        var observed = Observe(change.Key);
        var prior = observed.State;
        var now = clock.GetUtcNow();
        var outcome = CodexMemoryJobSettlement.Applied;
        if (prior?.Revision != change.ExpectedRevision)
            outcome = CodexMemoryJobSettlement.Conflict;
        else if (change.Key.ThreadId is not null && change.Kind is (CodexMemoryJobChangeKind.Claim or CodexMemoryJobChangeKind.CompleteStage1) &&
            (!observed.SourceEnabled || observed.SourceWatermark != change.ExpectedSourceWatermark ||
                observed.OutputWatermark > change.ExpectedSourceWatermark))
            outcome = CodexMemoryJobSettlement.Denied;
        else if (IsOwned(change.Kind) &&
            (prior is not { Status: CodexMemoryJobStatus.Running } || prior.LeaseUntil <= now ||
                prior.WorkerId != change.ExpectedWorkerId || prior.OwnershipToken != change.ExpectedOwnershipToken))
            outcome = CodexMemoryJobSettlement.Conflict;
        else if (!IsExactPlan(change, observed, now, body))
            outcome = CodexMemoryJobSettlement.Conflict;
        else if (change.Kind == CodexMemoryJobChangeKind.Claim && change.Key.ThreadId is not null &&
            RunningStage1Count(now) >= change.MaximumRunningClaims!.Value)
            outcome = CodexMemoryJobSettlement.Capacity;
        else if (change.Kind == CodexMemoryJobChangeKind.CompletePhase2 &&
            !SelectionIsEligible(change.Consolidation!.Selected, ct))
            outcome = CodexMemoryJobSettlement.Conflict;

        if (outcome == CodexMemoryJobSettlement.Applied)
        {
            WriteJob(KeyBytes(change.Key), change.Next);
            if (change.Kind == CodexMemoryJobChangeKind.CompleteStage1)
                CompleteExtraction(change);
            else if (change.Kind == CodexMemoryJobChangeKind.CompletePhase2)
                CompleteConsolidation(change.Consolidation!, ct);
        }
        using (var statement = Prepare(database, "INSERT INTO receipt(operation,thread,fingerprint,settlement,revision) VALUES(?1,?2,?3,?4,?5);"))
        {
            statement.Bind(1, change.OperationId.ToByteArray());
            statement.Bind(2, KeyBytes(change.Key));
            statement.Bind(3, fingerprint);
            statement.Bind(4, (long)outcome);
            statement.Bind(5, outcome == CodexMemoryJobSettlement.Applied ? change.Next.Revision : 0);
            statement.Done();
        }
        return new(change.Key, change.OperationId, outcome, outcome == CodexMemoryJobSettlement.Applied ? change.Next.Revision : null);
    }

    private bool IsExactPlan(CodexMemoryJobChange change, CodexMemoryJobObservation observed, DateTimeOffset now, byte[] body)
    {
        // Reuse the native planner over the transactional observation. Never accept arbitrary
        // next-state JSON simply because a caller supplied the current revision.
        var plannedAt = IsOwned(change.Kind) && change.Kind != CodexMemoryJobChangeKind.Heartbeat
            ? change.Next.FinishedAt : now;
        if (plannedAt is null || plannedAt > now) return false;
        var planner = new CodexMemoryJobs(scope, this, (_, _) => ValueTask.CompletedTask, new FixedClock(plannedAt.Value));
        CodexMemoryJobChange? expected;
        try
        {
            expected = change.Kind switch
            {
                CodexMemoryJobChangeKind.Claim when change.Next.WorkerId is { } worker && change.Next.LeaseUntil is { } expires =>
                    planner.PrepareClaim(observed, worker, change.OperationId, expires - now, change.MaximumRunningClaims ?? 10).Change,
                CodexMemoryJobChangeKind.Heartbeat when change.Next.LeaseUntil is { } expires =>
                    planner.PrepareHeartbeat(observed, change.ExpectedWorkerId!.Value, change.ExpectedOwnershipToken!.Value,
                        change.OperationId, expires - now),
                CodexMemoryJobChangeKind.Fail when change.Next.RetryAt is { } retry && change.Next.LastError is { } error =>
                    planner.PrepareFailure(observed, change.ExpectedWorkerId!.Value, change.ExpectedOwnershipToken!.Value,
                        change.OperationId, error, retry - plannedAt.Value),
                CodexMemoryJobChangeKind.CompleteStage1 =>
                    planner.PrepareStage1Success(observed, change.ExpectedWorkerId!.Value, change.ExpectedOwnershipToken!.Value,
                        change.OperationId, change.Extraction),
                CodexMemoryJobChangeKind.CompletePhase2 when change.Consolidation is not null =>
                    planner.PreparePhase2Success(observed, change.ExpectedWorkerId!.Value, change.ExpectedOwnershipToken!.Value,
                        change.OperationId, change.Consolidation),
                CodexMemoryJobChangeKind.EnqueuePhase2 =>
                    planner.PrepareEnqueue(observed, change.OperationId, change.Next.InputWatermark),
                _ => null,
            };
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OverflowException) { return false; }
        return expected is not null && Serialize(expected, CodexMemoryStorageJson.Default.CodexMemoryJobChange).AsSpan().SequenceEqual(body);
    }

    private static bool IsOwned(CodexMemoryJobChangeKind kind) =>
        kind is CodexMemoryJobChangeKind.Heartbeat or CodexMemoryJobChangeKind.Fail or
            CodexMemoryJobChangeKind.CompleteStage1 or CodexMemoryJobChangeKind.CompletePhase2;

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private long RunningStage1Count(DateTimeOffset now)
    {
        using var statement = Prepare(database, "SELECT COUNT(*) FROM job WHERE status=?1 AND lease_until>?2 AND thread<>?3;");
        statement.Bind(1, (long)CodexMemoryJobStatus.Running);
        statement.Bind(2, now.UtcTicks);
        statement.Bind(3, Global);
        if (!statement.Next()) throw new InvalidDataException("Missing running count.");
        var count = statement.Number(0);
        statement.Done();
        return count;
    }

    private bool SelectionIsEligible(IReadOnlyList<CodexMemorySelectedSnapshot> selected, CancellationToken ct)
    {
        foreach (var snapshot in selected)
        {
            ct.ThrowIfCancellationRequested();
            var thread = snapshot.ThreadId.ToByteArray();
            if (ReadSource(thread) is not { Enabled: true } source || source.Watermark < snapshot.SourceWatermark ||
                ReadOutputWatermark(thread) != snapshot.SourceWatermark)
                return false;
        }
        return true;
    }

    private void CompleteExtraction(CodexMemoryJobChange change)
    {
        var thread = KeyBytes(change.Key);
        var watermark = change.ExpectedSourceWatermark!.Value;
        if (change.Extraction is { } output)
        {
            using var statement = Prepare(database, """
                INSERT INTO extraction(thread,watermark,body) VALUES(?1,?2,?3)
                ON CONFLICT(thread) DO UPDATE SET watermark=excluded.watermark,body=excluded.body
                WHERE excluded.watermark>=extraction.watermark;
                """);
            statement.Bind(1, thread);
            statement.Bind(2, watermark);
            statement.Bind(3, Serialize(output, CodexMemoryStorageJson.Default.CodexMemoryExtractionOutput));
            statement.Done();
            if (Changes(database) != 1) throw new InvalidDataException("Extraction update did not settle.");
            Enqueue(watermark);
        }
        else
        {
            var changed = DeleteExtraction(thread);
            var selected = InvalidateSelected(thread);
            if (changed || selected) Enqueue(watermark);
        }
    }

    private bool DeleteExtraction(byte[] thread)
    {
        using var statement = Prepare(database, "DELETE FROM extraction WHERE thread=?1;");
        statement.Bind(1, thread);
        statement.Done();
        return Changes(database) != 0;
    }

    private bool InvalidateSelected(byte[] thread)
    {
        using var statement = Prepare(database, "DELETE FROM selected WHERE thread=?1;");
        statement.Bind(1, thread);
        statement.Done();
        if (Changes(database) == 0) return false;
        Execute(database, "UPDATE progress SET artifact=NULL WHERE id=1;");
        return true;
    }

    private void Enqueue(long watermark)
    {
        var key = new CodexMemoryJobKey(scope, null);
        var observed = Observe(key);
        var planner = new CodexMemoryJobs(scope, this, (_, _) => ValueTask.CompletedTask, clock);
        var next = planner.PrepareEnqueue(observed, Guid.NewGuid(), watermark).Next;
        WriteJob(Global, next);
    }

    private void CompleteConsolidation(CodexMemoryConsolidationOutput output, CancellationToken ct)
    {
        Execute(database, "DELETE FROM selected;");
        foreach (var item in output.Selected)
        {
            ct.ThrowIfCancellationRequested();
            using var statement = Prepare(database, "INSERT INTO selected(thread,watermark) VALUES(?1,?2);");
            statement.Bind(1, item.ThreadId.ToByteArray());
            statement.Bind(2, item.SourceWatermark);
            statement.Done();
        }
        using var progress = Prepare(database, "UPDATE progress SET artifact=?1,max_count=MAX(max_count,?2) WHERE id=1;");
        progress.Bind(1, Utf8.GetBytes(output.ArtifactRevision));
        progress.Bind(2, output.Selected.Count);
        progress.Done();
        if (Changes(database) != 1) throw new InvalidDataException("Missing consolidation progress.");
    }

    private CodexMemoryJobObservation Observe(CodexMemoryJobKey key)
    {
        var thread = KeyBytes(key);
        var state = ReadJob(thread);
        if (key.ThreadId is null) return new(key, state, false, null, null);
        var source = ReadSource(thread);
        return new(key, state, source?.Enabled == true, source?.Watermark, ReadOutputWatermark(thread));
    }

    private (long Watermark, bool Enabled)? ReadSource(byte[] thread)
    {
        using var statement = Prepare(database, "SELECT watermark,enabled FROM source WHERE thread=?1;");
        statement.Bind(1, thread);
        if (!statement.Next()) return null;
        var result = (statement.Number(0), statement.Number(1) == 1);
        statement.Done();
        return result;
    }

    private long? ReadOutputWatermark(byte[] thread)
    {
        using var statement = Prepare(database, "SELECT watermark FROM extraction WHERE thread=?1;");
        statement.Bind(1, thread);
        if (!statement.Next()) return null;
        var watermark = statement.Number(0);
        statement.Done();
        return watermark;
    }

    private CodexMemoryJobState? ReadJob(byte[] thread)
    {
        using var statement = Prepare(database, "SELECT revision,status,lease_until,body FROM job WHERE thread=?1;");
        statement.Bind(1, thread);
        if (!statement.Next()) return null;
        var state = Deserialize(statement.Blob(3), CodexMemoryStorageJson.Default.CodexMemoryJobState);
        if (state.Revision != statement.Number(0) || (long)state.Status != statement.Number(1) ||
            (state.LeaseUntil?.UtcTicks ?? 0) != statement.Number(2))
            throw new InvalidDataException("Memory job index does not match its durable body.");
        statement.Done();
        return state;
    }

    private void WriteJob(byte[] thread, CodexMemoryJobState state)
    {
        using var statement = Prepare(database, """
            INSERT INTO job(thread,revision,status,lease_until,body) VALUES(?1,?2,?3,?4,?5)
            ON CONFLICT(thread) DO UPDATE SET revision=excluded.revision,status=excluded.status,
                lease_until=excluded.lease_until,body=excluded.body;
            """);
        statement.Bind(1, thread);
        statement.Bind(2, state.Revision);
        statement.Bind(3, (long)state.Status);
        statement.Bind(4, state.LeaseUntil?.UtcTicks ?? 0);
        statement.Bind(5, Serialize(state, CodexMemoryStorageJson.Default.CodexMemoryJobState));
        statement.Done();
        if (Changes(database) != 1) throw new InvalidDataException("Memory job update did not settle.");
    }

    private CodexMemoryJobReceipt? ReadReceipt(CodexMemoryJobKey key, Guid operationId, byte[]? fingerprint)
    {
        using var statement = Prepare(database, "SELECT thread,fingerprint,settlement,revision FROM receipt WHERE operation=?1;");
        statement.Bind(1, operationId.ToByteArray());
        if (!statement.Next()) return null;
        if (!statement.Blob(0).AsSpan().SequenceEqual(KeyBytes(key)) ||
            (fingerprint is not null && !CryptographicOperations.FixedTimeEquals(fingerprint, statement.Blob(1))))
            throw new InvalidOperationException("Memory operation identity was reused with different contents.");
        var settlement = (CodexMemoryJobSettlement)statement.Number(2);
        var revision = statement.Number(3);
        if (settlement is not (CodexMemoryJobSettlement.Applied or CodexMemoryJobSettlement.Conflict or
            CodexMemoryJobSettlement.Capacity or CodexMemoryJobSettlement.Denied) ||
            (settlement == CodexMemoryJobSettlement.Applied ? revision <= 0 : revision != 0))
            throw new InvalidDataException("Invalid memory job receipt.");
        statement.Done();
        return new(key, operationId, settlement, revision == 0 ? null : revision);
    }

    private void CheckKey(CodexMemoryJobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Scope != scope || key.ThreadId == Guid.Empty)
            throw new UnauthorizedAccessException("Memory job belongs to another namespace.");
    }

    private static byte[] KeyBytes(CodexMemoryJobKey key) => key.ThreadId?.ToByteArray() ?? Global;

    private async ValueTask AuthorizeAsync(CodexMemoryJobStorageAccess access, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await authorize(access, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private async Task<T> RunAsync<T>(Func<T> run, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return run();
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Closes the independent SQLite connection after its pending operation settles.</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            var db = database;
            database = IntPtr.Zero;
            Check(CloseV2(db));
        }
        finally { gate.Release(); }
    }

    private static T Transaction<T>(IntPtr db, bool write, CancellationToken ct, Func<T> action)
    {
        ct.ThrowIfCancellationRequested();
        Execute(db, write ? "BEGIN IMMEDIATE;" : "BEGIN;");
        try
        {
            var result = action();
            ct.ThrowIfCancellationRequested();
            Execute(db, "COMMIT;");
            return result;
        }
        catch
        {
            // A failed COMMIT may be ambiguous. Roll back only any still-open transaction;
            // preserve the original error and never repeat the operation automatically.
            _ = Exec(db, "ROLLBACK;", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            throw;
        }
    }

    private static byte[] Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (body.Length > MaximumBlobBytes) throw new InvalidDataException("Memory database record exceeds its bound.");
        return body;
    }

    private static T Deserialize<T>(byte[] body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(body, type) ?? throw new InvalidDataException("Empty memory database record.");

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        internal bool Next()
        {
            var result = Step(handle);
            if (result == CodexMacOsSqliteMemoryJobStorage.Done) return false;
            Check(result, Row);
            return true;
        }
        internal void Done() => Check(Step(handle), CodexMacOsSqliteMemoryJobStorage.Done);
        internal void Bind(int index, long value) => Check(BindInt64(handle, index, value));
        internal unsafe void Bind(int index, byte[] bytes)
        {
            if (bytes.Length is < 1 or > MaximumBlobBytes) throw new InvalidDataException("Invalid memory database blob size.");
            fixed (byte* pointer = bytes) Check(BindBlob(handle, index, pointer, bytes.Length, new IntPtr(-1)));
        }
        internal long Number(int column) => ColumnInt64(handle, column);
        internal bool IsNull(int column) => ColumnType(handle, column) == 5;
        internal byte[] Blob(int column)
        {
            var length = ColumnBytes(handle, column);
            if (length is < 1 or > MaximumBlobBytes) throw new InvalidDataException("Invalid memory database blob size.");
            var pointer = ColumnBlob(handle, column);
            if (pointer == IntPtr.Zero) throw new InvalidDataException("Missing memory database blob.");
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }
        internal string Text(int column) => Marshal.PtrToStringUTF8(ColumnText(handle, column))
            ?? throw new InvalidDataException("Missing SQLite value.");
        public void Dispose() { _ = FinalizeStatement(handle); }
    }

    private static Statement Prepare(IntPtr db, string sql)
    {
        var result = PrepareV2(db, sql, -1, out var statement, IntPtr.Zero);
        if (result != Ok)
        {
            if (statement != IntPtr.Zero) _ = FinalizeStatement(statement);
            Check(result);
        }
        if (statement == IntPtr.Zero) throw new InvalidDataException("Empty SQLite statement.");
        return new(statement);
    }
    private static void Execute(IntPtr db, string sql) => Check(Exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    private static long ScalarNumber(IntPtr db, string sql)
    {
        using var statement = Prepare(db, sql);
        if (!statement.Next()) throw new InvalidDataException("Missing SQLite value.");
        var result = statement.Number(0);
        statement.Done();
        return result;
    }
    private static string ScalarText(IntPtr db, string sql)
    {
        using var statement = Prepare(db, sql);
        if (!statement.Next()) throw new InvalidDataException("Missing SQLite value.");
        var result = statement.Text(0);
        statement.Done();
        return result;
    }
    private static void Check(int result, int expected = Ok)
    {
        if (result != expected) throw new IOException($"SQLite memory operation failed (code {result}).");
    }

    [LibraryImport(Library, EntryPoint = "sqlite3_libversion_number")]
    private static partial int LibVersion();
    [LibraryImport(Library, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenV2(string filename, out IntPtr db, int flags, IntPtr vfs);
    [LibraryImport(Library, EntryPoint = "sqlite3_close_v2")]
    private static partial int CloseV2(IntPtr db);
    [LibraryImport(Library, EntryPoint = "sqlite3_busy_timeout")]
    private static partial int BusyTimeout(IntPtr db, int milliseconds);
    [LibraryImport(Library, EntryPoint = "sqlite3_limit")]
    private static partial int Limit(IntPtr db, int category, int value);
    [LibraryImport(Library, EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Exec(IntPtr db, string sql, IntPtr callback, IntPtr context, IntPtr error);
    [LibraryImport(Library, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PrepareV2(IntPtr db, string sql, int length, out IntPtr statement, IntPtr tail);
    [LibraryImport(Library, EntryPoint = "sqlite3_finalize")]
    private static partial int FinalizeStatement(IntPtr statement);
    [LibraryImport(Library, EntryPoint = "sqlite3_step")]
    private static partial int Step(IntPtr statement);
    [LibraryImport(Library, EntryPoint = "sqlite3_bind_blob")]
    private static unsafe partial int BindBlob(IntPtr statement, int index, byte* data, int length, IntPtr destructor);
    [LibraryImport(Library, EntryPoint = "sqlite3_bind_int64")]
    private static partial int BindInt64(IntPtr statement, int index, long value);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_int64")]
    private static partial long ColumnInt64(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_blob")]
    private static partial IntPtr ColumnBlob(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_text")]
    private static partial IntPtr ColumnText(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_bytes")]
    private static partial int ColumnBytes(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_type")]
    private static partial int ColumnType(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_changes")]
    private static partial int Changes(IntPtr db);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexMemoryScope))]
[JsonSerializable(typeof(CodexMemoryJobState))]
[JsonSerializable(typeof(CodexMemoryJobChange))]
[JsonSerializable(typeof(CodexMemoryExtractionOutput))]
internal sealed partial class CodexMemoryStorageJson : JsonSerializerContext;
