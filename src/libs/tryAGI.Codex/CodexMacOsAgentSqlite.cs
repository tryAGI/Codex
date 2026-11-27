using System.Runtime.InteropServices;
using System.Text;

namespace tryAGI.Codex;

// Minimal AOT-safe local SQLite transport. All SQL is static; identity and payload
// values are bound. Separate connections coordinate through SQLite transactions.
internal sealed partial class CodexMacOsAgentSqlite : IAsyncDisposable
{
    private const int ApplicationId = 0x43414754; // CAGT
    private const int MaximumBlobBytes = 32 * 1024 * 1024;
    private const string Library = "/usr/lib/libsqlite3.dylib";
    private const int Ok = 0, Row = 100, Done = 101;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IntPtr database;
    private bool disposed;

    private CodexMacOsAgentSqlite(IntPtr database) => this.database = database;

    internal static byte[] Text(string value) => Utf8.GetBytes(value);
    internal static string Text(byte[] value) => Utf8.GetString(value);

    internal static Task<CodexMacOsAgentSqlite> OpenAsync(string path, string accountId, bool createNew,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Local agent SQLite storage requires macOS.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || path.Any(char.IsControl) ||
            Path.GetDirectoryName(path) is not { } directory || directory == "/")
            throw new ArgumentException("Agent database requires a normalized private file path.", nameof(path));
        return Task.Run(() => OpenCore(path, directory, accountId, createNew, ct), ct);
    }

    private static CodexMacOsAgentSqlite OpenCore(string path, string directory, string accountId, bool createNew,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory) || CodexFileSystem.Canonical(directory) != directory ||
            (File.GetUnixFileMode(directory) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0)
            throw new UnauthorizedAccessException("Agent database directory must be private and canonical.");
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
            throw new UnauthorizedAccessException("Agent database must be a private existing regular file.");

        const int openReadWrite = 2, openFullMutex = 0x10000, openNoFollow = 0x01000000;
        var status = OpenV2(path, out var db, openReadWrite | openFullMutex | openNoFollow, IntPtr.Zero);
        try
        {
            Check(status);
            Check(BusyTimeout(db, 5000));
            _ = Limit(db, 0, MaximumBlobBytes * 2);
            var sql = new Connection(db);
            sql.Raw("PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;");
            if (!createNew) Verify(sql, accountId);
            if (sql.Query("PRAGMA journal_mode=DELETE;", r => r.String(0), 1).Single() != "delete")
                throw new IOException("Could not establish SQLite rollback journaling.");
            sql.Raw("PRAGMA synchronous=EXTRA; PRAGMA fullfsync=ON;");
            if (sql.Query("PRAGMA synchronous;", r => r.Number(0), 1).Single() != 3 ||
                sql.Query("PRAGMA fullfsync;", r => r.Number(0), 1).Single() != 1)
                throw new IOException("Could not establish durable SQLite synchronization.");
            if (createNew)
            {
                sql.Raw("""
                    BEGIN IMMEDIATE;
                    CREATE TABLE agent_meta(account BLOB NOT NULL PRIMARY KEY) WITHOUT ROWID;
                    CREATE TABLE agent_threads(id BLOB PRIMARY KEY, revision INTEGER NOT NULL, archived INTEGER NOT NULL,
                        turns INTEGER NOT NULL, active INTEGER NOT NULL, body BLOB NOT NULL) WITHOUT ROWID;
                    CREATE TABLE agent_roots(id BLOB PRIMARY KEY, revision INTEGER NOT NULL) WITHOUT ROWID;
                    CREATE TABLE agent_edges(child BLOB PRIMARY KEY, root BLOB NOT NULL, parent BLOB NOT NULL,
                        status INTEGER NOT NULL, identity BLOB NOT NULL) WITHOUT ROWID;
                    CREATE INDEX agent_edges_root ON agent_edges(root);
                    CREATE TABLE agent_mail(recipient BLOB PRIMARY KEY, revision INTEGER NOT NULL, body BLOB NOT NULL) WITHOUT ROWID;
                    CREATE TABLE agent_grants(claim BLOB PRIMARY KEY, recipient BLOB NOT NULL, turn BLOB NOT NULL,
                        owner BLOB NOT NULL, state INTEGER NOT NULL, UNIQUE(recipient,turn)) WITHOUT ROWID;
                    CREATE UNIQUE INDEX agent_grant_active ON agent_grants(recipient) WHERE state=0;
                    PRAGMA application_id=0x43414754;
                    PRAGMA user_version=1;
                    """);
                if (sql.Change("INSERT INTO agent_meta(account) VALUES(?1);", Text(accountId)) != 1)
                    throw new InvalidDataException("Could not initialize agent account.");
                sql.Raw("COMMIT;");
            }
            Verify(sql, accountId);
            ct.ThrowIfCancellationRequested();
            return new(db);
        }
        catch
        {
            if (db != IntPtr.Zero) _ = CloseV2(db);
            throw;
        }
    }

    private static void Verify(Connection sql, string accountId)
    {
        if (sql.Query("PRAGMA application_id;", r => r.Number(0), 1).Single() != ApplicationId ||
            sql.Query("PRAGMA user_version;", r => r.Number(0), 1).Single() != 1)
            throw new InvalidDataException("Unknown or uninitialized agent database format.");
        if (sql.Query("SELECT account FROM agent_meta;", r => Text(r.Blob(0)), 1).Single() != accountId)
            throw new UnauthorizedAccessException("Agent database belongs to another account.");
        // Missing tables or columns must not be interpreted as empty state.
        _ = sql.Query("SELECT id,revision,archived,turns,active,body FROM agent_threads LIMIT 0;", r => r.Number(1), 1);
        _ = sql.Query("SELECT id,revision FROM agent_roots LIMIT 0;", r => r.Number(1), 1);
        _ = sql.Query("SELECT child,root,parent,status,identity FROM agent_edges LIMIT 0;", r => r.Number(3), 1);
        _ = sql.Query("SELECT recipient,revision,body FROM agent_mail LIMIT 0;", r => r.Number(1), 1);
        _ = sql.Query("SELECT claim,recipient,turn,owner,state FROM agent_grants LIMIT 0;", r => r.Number(4), 1);
    }

    internal Task<T> ReadAsync<T>(Func<Connection, T> work, CancellationToken ct)
        => RunAsync(work, write: false, ct);

    internal Task<bool> WriteAsync(Func<Connection, bool> work, CancellationToken ct)
        => RunAsync(work, write: true, ct);

    private async Task<T> RunAsync<T>(Func<Connection, T> work, bool write, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var sql = new Connection(database);
                sql.Raw(write ? "BEGIN IMMEDIATE;" : "BEGIN;");
                try
                {
                    var result = work(sql);
                    ct.ThrowIfCancellationRequested();
                    sql.Raw(write && result is false ? "ROLLBACK;" : "COMMIT;");
                    return result;
                }
                catch
                {
                    // A commit error is ambiguous. Never retry; preserve the original error.
                    try { sql.Raw("ROLLBACK;"); }
                    catch (IOException) { }
                    throw;
                }
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

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

    internal sealed class Connection(IntPtr db)
    {
        internal void Raw(string sql) => Check(Exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

        internal int Change(string sql, params object[] values)
        {
            using var statement = Prepare(db, sql, values);
            Check(Step(statement.Handle), Done);
            return Changes(db);
        }

        internal List<T> Query<T>(string sql, Func<Statement, T> project, int maximumRows, params object[] values)
        {
            using var statement = Prepare(db, sql, values);
            var result = new List<T>();
            while (true)
            {
                var code = Step(statement.Handle);
                if (code == Done) return result;
                Check(code, Row);
                if (result.Count >= maximumRows) throw new InvalidDataException("SQLite agent query exceeded its bound.");
                result.Add(project(statement));
            }
        }
    }

    internal sealed class Statement(IntPtr handle) : IDisposable
    {
        internal IntPtr Handle { get; } = handle;
        internal long Number(int column) => ColumnInt64(Handle, column);
        internal string String(int column) => Marshal.PtrToStringUTF8(ColumnText(Handle, column))
            ?? throw new InvalidDataException("Missing SQLite text.");
        internal byte[] Blob(int column)
        {
            var length = ColumnBytes(Handle, column);
            var pointer = ColumnBlob(Handle, column);
            if (length is < 1 or > MaximumBlobBytes || pointer == IntPtr.Zero)
                throw new InvalidDataException("Invalid SQLite agent blob.");
            var result = new byte[length];
            Marshal.Copy(pointer, result, 0, length);
            return result;
        }
        internal Guid Id(int column) => new(Blob(column));
        public void Dispose() { _ = FinalizeStatement(Handle); }
    }

    private static Statement Prepare(IntPtr db, string sql, object[] values)
    {
        var code = PrepareV2(db, sql, -1, out var handle, IntPtr.Zero);
        if (code != Ok || handle == IntPtr.Zero)
        {
            if (handle != IntPtr.Zero) _ = FinalizeStatement(handle);
            Check(code);
            throw new InvalidDataException("Empty SQLite statement.");
        }
        var statement = new Statement(handle);
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var index = i + 1;
                switch (values[i])
                {
                    case Guid id: Bind(handle, index, id.ToByteArray()); break;
                    case string text: Bind(handle, index, Text(text)); break;
                    case byte[] bytes: Bind(handle, index, bytes); break;
                    case long number: Check(BindInt64(handle, index, number)); break;
                    case int number: Check(BindInt64(handle, index, number)); break;
                    default: throw new ArgumentException("Unsupported SQLite agent parameter.");
                }
            }
            return statement;
        }
        catch { statement.Dispose(); throw; }
    }

    private static unsafe void Bind(IntPtr statement, int index, byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumBlobBytes) throw new ArgumentException("Invalid SQLite agent parameter size.");
        fixed (byte* pointer = bytes)
            Check(BindBlob(statement, index, pointer, bytes.Length, new IntPtr(-1)));
    }

    private static void Check(int result, int expected = Ok)
    {
        if (result != expected) throw new IOException($"SQLite agent storage operation failed (code {result}).");
    }

    [LibraryImport(Library, EntryPoint = "sqlite3_libversion_number")] private static partial int LibVersion();
    [LibraryImport(Library, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenV2(string filename, out IntPtr db, int flags, IntPtr vfs);
    [LibraryImport(Library, EntryPoint = "sqlite3_close_v2")] private static partial int CloseV2(IntPtr db);
    [LibraryImport(Library, EntryPoint = "sqlite3_busy_timeout")] private static partial int BusyTimeout(IntPtr db, int milliseconds);
    [LibraryImport(Library, EntryPoint = "sqlite3_limit")] private static partial int Limit(IntPtr db, int category, int value);
    [LibraryImport(Library, EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Exec(IntPtr db, string sql, IntPtr callback, IntPtr context, IntPtr error);
    [LibraryImport(Library, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PrepareV2(IntPtr db, string sql, int length, out IntPtr statement, IntPtr tail);
    [LibraryImport(Library, EntryPoint = "sqlite3_finalize")] private static partial int FinalizeStatement(IntPtr statement);
    [LibraryImport(Library, EntryPoint = "sqlite3_step")] private static partial int Step(IntPtr statement);
    [LibraryImport(Library, EntryPoint = "sqlite3_bind_blob")]
    private static unsafe partial int BindBlob(IntPtr statement, int index, byte* data, int length, IntPtr destructor);
    [LibraryImport(Library, EntryPoint = "sqlite3_bind_int64")] private static partial int BindInt64(IntPtr statement, int index, long value);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_int64")] private static partial long ColumnInt64(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_blob")] private static partial IntPtr ColumnBlob(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_text")] private static partial IntPtr ColumnText(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_column_bytes")] private static partial int ColumnBytes(IntPtr statement, int column);
    [LibraryImport(Library, EntryPoint = "sqlite3_changes")] private static partial int Changes(IntPtr db);
}
