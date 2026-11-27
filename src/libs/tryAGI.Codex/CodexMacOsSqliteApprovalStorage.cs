using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Opt-in local SQLite implementation of the existing durable approval CAS contract.
/// Uses the macOS system SQLite library; no external executable or NuGet provider is required.
/// The host must select a private, local, stable storage directory, exclude untrusted writers,
/// links and database replacement, and supply any required encryption at rest. The database
/// contains approval identities and action fingerprints, not action argument bodies.
/// Never delete or restore older database snapshots while approvals are usable.
/// This storage is not an execution authority and must only be used by the trusted host.</summary>
public sealed partial class CodexMacOsSqliteApprovalStorage : ICodexApprovalStorage, IAsyncDisposable
{
    private const int ApplicationId = 0x43415052; // CAPR
    private const int SchemaVersion = 1;
    private const int MaximumRecordBytes = 65536;
    private const int Ok = 0, Row = 100, Done = 101;
    private const int OpenReadWrite = 0x2, OpenFullMutex = 0x10000, OpenNoFollow = 0x01000000;
    private const int SqliteLimitLength = 0;
    private const string Library = "/usr/lib/libsqlite3.dylib";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IntPtr database;
    private bool disposed;

    private CodexMacOsSqliteApprovalStorage(IntPtr database) => this.database = database;

    /// <summary>Explicitly initializes a new database file or opens an existing initialized one.
    /// Creation is exclusive and never overwrites an existing file. Missing or foreign databases
    /// fail closed on reopen. SQLite uses durable rollback journaling and full sync on each connection.</summary>
    public static Task<CodexMacOsSqliteApprovalStorage> OpenAsync(string path, bool createNew = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("This approval storage adapter requires macOS.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path) || path.Any(char.IsControl) ||
            Path.GetDirectoryName(path) is not { } directory || directory == "/")
            throw new ArgumentException("Approval database requires a normalized private file path.", nameof(path));
        return Task.Run(() => OpenCore(path, directory, createNew, cancellationToken), cancellationToken);
    }

    private static CodexMacOsSqliteApprovalStorage OpenCore(string path, string directory, bool createNew, CancellationToken ct)
    {
        // The trusted directory must remain stable. Validate the final directory before
        // creating/opening the database, and never follow a symlink to a database file.
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (!Directory.Exists(directory) || CodexFileSystem.Canonical(directory) != directory ||
            (File.GetUnixFileMode(directory) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0)
            throw new UnauthorizedAccessException("Approval database directory must be private and canonical.");
        if (LibVersion() < 3031000) throw new PlatformNotSupportedException("SQLite 3.31 or later is required.");
        if (createNew)
        {
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            file.Flush(flushToDisk: true);
        }
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null ||
            CodexFileSystemNative.Kind(path, false) != CodexFileSystemNative.Regular ||
            (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw new UnauthorizedAccessException("Approval database must be a private existing regular file.");
        ct.ThrowIfCancellationRequested();

        var code = OpenV2(path, out var db, OpenReadWrite | OpenFullMutex | OpenNoFollow, IntPtr.Zero);
        try
        {
            Check(code);
            Check(BusyTimeout(db, 5000));
            _ = Limit(db, SqliteLimitLength, MaximumRecordBytes * 2);
            Execute(db, "PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;");
            if (!createNew) VerifySchema(db);
            if (ScalarText(db, "PRAGMA journal_mode=DELETE;") != "delete")
                throw new IOException("Could not establish SQLite rollback journal mode.");
            Execute(db, "PRAGMA synchronous=EXTRA; PRAGMA fullfsync=ON;");
            if (ScalarNumber(db, "PRAGMA synchronous;") != 3 || ScalarNumber(db, "PRAGMA fullfsync;") != 1)
                throw new IOException("Could not establish durable SQLite synchronization.");
            if (createNew)
            {
                Execute(db, "BEGIN IMMEDIATE;");
                Execute(db, """
                    CREATE TABLE codex_approval (
                        account BLOB NOT NULL,
                        request BLOB NOT NULL,
                        revision INTEGER NOT NULL CHECK(revision >= 1),
                        record BLOB NOT NULL,
                        PRIMARY KEY(account, request)
                    ) WITHOUT ROWID;
                    PRAGMA application_id=1128353874;
                    PRAGMA user_version=1;
                    COMMIT;
                    """);
            }
            VerifySchema(db);
            ct.ThrowIfCancellationRequested();
            return new(db);
        }
        catch
        {
            if (db != IntPtr.Zero) _ = CloseV2(db);
            throw;
        }
    }

    private static void VerifySchema(IntPtr db)
    {
        if (ScalarNumber(db, "PRAGMA application_id;") != ApplicationId ||
            ScalarNumber(db, "PRAGMA user_version;") != SchemaVersion)
            throw new InvalidDataException("Unknown or uninitialized approval database format.");
        // A missing/corrupt table is not an empty journal.
        using var statement = Prepare(db, "SELECT account,request,revision,record FROM codex_approval LIMIT 0;");
        Check(Step(statement.Handle), Done);
    }

    /// <inheritdoc />
    public async Task<CodexApprovalRecord?> ReadAsync(string accountId, Guid requestId, CancellationToken cancellationToken)
    {
        var account = AccountKey(accountId, requestId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var statement = Prepare(database, "SELECT revision,record FROM codex_approval WHERE account=?1 AND request=?2;");
                Bind(statement.Handle, 1, account);
                Bind(statement.Handle, 2, requestId.ToByteArray());
                var result = Step(statement.Handle);
                if (result == Done) return null;
                Check(result, Row);
                var revision = ColumnInt64(statement.Handle, 0);
                var bytes = ReadBlob(statement.Handle, 1);
                var record = JsonSerializer.Deserialize(bytes, CodexApprovalStorageJson.Default.CodexApprovalRecord)
                    ?? throw new InvalidDataException("Approval database contains an empty record.");
                if (record.AccountId != accountId || record.Id != requestId || record.Revision != revision || revision < 1)
                    throw new InvalidDataException("Approval database record identity or revision mismatch.");
                Check(Step(statement.Handle), Done);
                cancellationToken.ThrowIfCancellationRequested();
                return record;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<bool> CompareExchangeAsync(string accountId, Guid requestId, long? expectedRevision,
        CodexApprovalRecord record, CancellationToken cancellationToken)
    {
        var account = AccountKey(accountId, requestId);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id != requestId || record.AccountId != accountId ||
            expectedRevision is < 1 || record.Revision != checked((expectedRevision ?? 0) + 1))
            throw new ArgumentException("Approval CAS must bind the exact key and next revision.", nameof(record));
        var body = JsonSerializer.SerializeToUtf8Bytes(record, CodexApprovalStorageJson.Default.CodexApprovalRecord);
        if (body.Length > MaximumRecordBytes) throw new InvalidDataException("Approval record exceeds its storage bound.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A single SQLite write statement is its own atomic transaction. The
                // statement must reach SQLITE_DONE with durable synchronous settings.
                // Any error is propagated (possibly ambiguous); never retry a commit.
                using var statement = Prepare(database, expectedRevision is null
                    ? "INSERT INTO codex_approval(account,request,revision,record) VALUES(?1,?2,?3,?4) ON CONFLICT(account,request) DO NOTHING;"
                    : "UPDATE codex_approval SET revision=?3,record=?4 WHERE account=?1 AND request=?2 AND revision=?5;");
                Bind(statement.Handle, 1, account);
                Bind(statement.Handle, 2, requestId.ToByteArray());
                Check(BindInt64(statement.Handle, 3, record.Revision));
                Bind(statement.Handle, 4, body);
                if (expectedRevision is { } expected) Check(BindInt64(statement.Handle, 5, expected));
                cancellationToken.ThrowIfCancellationRequested();
                Check(Step(statement.Handle), Done);
                var changed = Changes(database);
                if (changed is not (0 or 1)) throw new InvalidDataException("Unexpected approval CAS row count.");
                cancellationToken.ThrowIfCancellationRequested();
                return changed == 1;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Closes this independent database connection after its in-flight CAS settles.</summary>
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

    private static byte[] AccountKey(string accountId, Guid requestId)
    {
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(accountId) ||
            accountId.Length > 512 || accountId.Any(char.IsControl))
            throw new ArgumentException("Invalid approval storage identity.");
        return Utf8.GetBytes(accountId);
    }

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        internal IntPtr Handle { get; } = handle;
        public void Dispose() { _ = FinalizeStatement(Handle); }
    }

    private static Statement Prepare(IntPtr db, string sql)
    {
        var result = PrepareV2(db, sql, -1, out var statement, IntPtr.Zero);
        if (result != Ok)
        {
            if (statement != IntPtr.Zero) _ = FinalizeStatement(statement);
            Check(result);
        }
        if (statement == IntPtr.Zero) throw new InvalidDataException("SQLite statement is empty.");
        return new(statement);
    }

    private static void Execute(IntPtr db, string sql)
    {
        // SQL is static; identities and payload bytes always use bound parameters.
        Check(Exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    }

    private static long ScalarNumber(IntPtr db, string sql)
    {
        using var statement = Prepare(db, sql);
        Check(Step(statement.Handle), Row);
        var result = ColumnInt64(statement.Handle, 0);
        Check(Step(statement.Handle), Done);
        return result;
    }

    private static string ScalarText(IntPtr db, string sql)
    {
        using var statement = Prepare(db, sql);
        Check(Step(statement.Handle), Row);
        var pointer = ColumnText(statement.Handle, 0);
        var text = Marshal.PtrToStringUTF8(pointer) ?? throw new InvalidDataException("Missing SQLite pragma value.");
        Check(Step(statement.Handle), Done);
        return text;
    }

    private static byte[] ReadBlob(IntPtr statement, int column)
    {
        var length = ColumnBytes(statement, column);
        if (length is < 1 or > MaximumRecordBytes) throw new InvalidDataException("Invalid approval record size.");
        var pointer = ColumnBlob(statement, column);
        if (pointer == IntPtr.Zero) throw new InvalidDataException("Missing approval record.");
        var result = new byte[length];
        Marshal.Copy(pointer, result, 0, length);
        return result;
    }

    private static unsafe void Bind(IntPtr statement, int index, byte[] bytes)
    {
        fixed (byte* pointer = bytes)
            Check(BindBlob(statement, index, pointer, bytes.Length, new IntPtr(-1))); // SQLITE_TRANSIENT
    }

    private static void Check(int result, int expected = Ok)
    {
        if (result != expected) throw new IOException($"SQLite approval operation failed (code {result}).");
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
    [LibraryImport(Library, EntryPoint = "sqlite3_changes")]
    private static partial int Changes(IntPtr db);
}

/// <summary>NativeAOT-safe JSON serialization of durable approval records.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexApprovalRecord))]
public sealed partial class CodexApprovalStorageJson : JsonSerializerContext;
