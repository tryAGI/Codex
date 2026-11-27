using System.Security.Cryptography;

namespace tryAGI.Codex;

public sealed partial class CodexMacOsSqliteMemoryJobStorage
{
    /// <summary>
    /// Creates the account-wide lifecycle adapter for an exclusively local memory installation.
    /// This instance must be the V2 store. Supply the existing V1 store if that namespace exists;
    /// never create an unused V1 store just to reset it. The host must keep the namespace topology,
    /// private database paths and permissions stable, serialize source registration with host
    /// permission changes, and ensure all memory artifacts and notes reside in these stores.
    /// Both databases must be in the same private directory on a filesystem supporting SQLite
    /// rollback-journal atomic transactions, with no legacy writers or external artifacts.
    /// The adapter borrows the stores; the host owns their lifetimes.
    /// </summary>
    public ICodexMemoryLifecycleStorage CreateLocalLifecycleStorage(CodexMacOsSqliteMemoryJobStorage? legacy = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scope.Version != CodexMemoryVersion.V2 ||
            legacy is not null && (legacy.scope.Version != CodexMemoryVersion.V1 ||
                legacy.scope.AccountId != scope.AccountId || legacy.databasePath == databasePath ||
                Path.GetDirectoryName(legacy.databasePath) != Path.GetDirectoryName(databasePath)))
            throw new ArgumentException("Lifecycle storage requires same-account sibling V2 and optional V1 databases.", nameof(legacy));
        return new LocalLifecycle(this, legacy);
    }

    /// <summary>Reads the explicit persisted extraction mode; missing mode means enabled, subject to source policy.</summary>
    public async Task<CodexThreadMemoryMode> ReadThreadMemoryModeAsync(Guid threadId,
        CancellationToken cancellationToken = default)
    {
        var key = new CodexMemoryJobKey(scope, threadId);
        CheckKey(key);
        await AuthorizeAsync(new(key, "read_memory_mode"), cancellationToken).ConfigureAwait(false);
        return await RunAsync(() => ReadMemoryMode(database, "main", threadId.ToByteArray()).Enabled
            ? CodexThreadMemoryMode.Enabled : CodexThreadMemoryMode.Disabled, cancellationToken).ConfigureAwait(false);
    }

    private sealed class LocalLifecycle(CodexMacOsSqliteMemoryJobStorage current,
        CodexMacOsSqliteMemoryJobStorage? legacy) : ICodexMemoryLifecycleStorage
    {
        public async ValueTask<CodexMemoryReadinessObservation> ReadReadinessAsync(string accountId,
            CancellationToken cancellationToken)
        {
            CheckAccount(accountId);
            await current.AuthorizeAsync(new(new(current.scope, null), "memory_lifecycle_status"), cancellationToken).ConfigureAwait(false);
            if (legacy is not null)
                await legacy.AuthorizeAsync(new(new(legacy.scope, null), "memory_lifecycle_status"), cancellationToken).ConfigureAwait(false);
            return await current.RunAsync(() => current.WithLifecycleDatabases(legacy, false, cancellationToken, () =>
            {
                using var query = Prepare(current.database, """
                    SELECT p.max_count,COALESCE(m.valid_summary,0)
                    FROM main.progress p LEFT JOIN main.memory_manifest m ON m.root=p.artifact WHERE p.id=1;
                    """);
                if (!query.Next()) throw new InvalidDataException("Missing V2 memory progress.");
                var result = new CodexMemoryReadinessObservation(checked((uint)query.Number(0)),
                    legacy is not null, query.Number(1) == 1);
                query.Done();
                return result;
            }), cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<CodexMemoryLifecycleReceipt> ApplyAsync(CodexMemoryRpcAccess access,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(access);
            CheckAccount(access.AccountId);
            if (access.OperationId is null || access.OperationId == Guid.Empty ||
                !(access.Method == "memory/reset" && access.ThreadId is null && access.Mode is null ||
                  access.Method == "thread/memoryMode/set" && access.ThreadId is { } id && id != Guid.Empty &&
                    access.Mode is CodexThreadMemoryMode.Enabled or CodexThreadMemoryMode.Disabled))
                throw new ArgumentException("Invalid memory lifecycle mutation.", nameof(access));

            await current.AuthorizeAsync(new(new(current.scope, access.ThreadId), "memory_lifecycle_apply",
                OperationId: access.OperationId, Lifecycle: access), cancellationToken).ConfigureAwait(false);
            if (legacy is not null)
                await legacy.AuthorizeAsync(new(new(legacy.scope, access.ThreadId), "memory_lifecycle_apply",
                    OperationId: access.OperationId, Lifecycle: access), cancellationToken).ConfigureAwait(false);

            return await current.RunAsync(() => current.WithLifecycleDatabases(legacy, true, cancellationToken,
                () => current.ApplyLifecycle(access, legacy?.scope)), cancellationToken).ConfigureAwait(false);
        }

        private void CheckAccount(string accountId)
        {
            if (!string.Equals(accountId, current.scope.AccountId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Memory lifecycle belongs to another account.");
            ObjectDisposedException.ThrowIf(legacy?.disposed == true, legacy!);
        }
    }

    // The current connection is already gated. ATTACH the explicitly registered existing
    // sibling, use full synchronous rollback journals on both, and settle in one transaction.
    // SQLite's multi-database super-journal provides the atomic commit boundary. No WAL,
    // in-memory databases, independently committed clears or automatically created roots.
    private T WithLifecycleDatabases<T>(CodexMacOsSqliteMemoryJobStorage? legacy, bool write,
        CancellationToken ct, Func<T> action)
    {
        if (ScalarText(database, "PRAGMA main.journal_mode;") != "delete" ||
            ScalarNumber(database, "PRAGMA main.synchronous;") != 3 ||
            ScalarNumber(database, "PRAGMA fullfsync;") != 1)
            throw new InvalidOperationException("Memory lifecycle requires durable rollback-journal settings.");
        if (legacy is null)
            return Transaction(database, write, ct, action);

        ValidateExistingLifecyclePath(legacy.databasePath);
        using (var attach = Prepare(database, "ATTACH DATABASE CAST(?1 AS TEXT) AS legacy;"))
        {
            attach.Bind(1, Utf8.GetBytes(legacy.databasePath));
            attach.Done();
        }
        try
        {
            if (ScalarText(database, "PRAGMA legacy.journal_mode;") != "delete")
                throw new InvalidOperationException("Legacy memory database is not using a rollback journal.");
            Execute(database, "PRAGMA legacy.synchronous=EXTRA;");
            if (ScalarNumber(database, "PRAGMA legacy.synchronous;") != 3 ||
                ScalarNumber(database, "PRAGMA fullfsync;") != 1)
                throw new InvalidOperationException("Legacy memory database is not durably synchronized.");
            return Transaction(database, write, ct, () =>
            {
                VerifyAttachedIdentity(legacy.scope);
                return action();
            });
        }
        finally { Execute(database, "DETACH DATABASE legacy;"); }
    }

    private static void ValidateExistingLifecyclePath(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory) || CodexFileSystem.Canonical(directory) != directory ||
            (File.GetUnixFileMode(directory) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0 ||
            !File.Exists(path) || new FileInfo(path).LinkTarget is not null ||
            CodexFileSystemNative.Kind(path, false) != CodexFileSystemNative.Regular ||
            (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw new UnauthorizedAccessException("Legacy memory database must remain a private existing regular file.");
    }

    private void VerifyAttachedIdentity(CodexMemoryScope legacyScope)
    {
        if (ScalarNumber(database, "PRAGMA legacy.application_id;") != ApplicationId ||
            ScalarNumber(database, "PRAGMA legacy.user_version;") != SchemaVersion)
            throw new InvalidDataException("Legacy memory database schema changed.");
        using var statement = Prepare(database, "SELECT body FROM legacy.identity WHERE id=1;");
        if (!statement.Next() || Deserialize(statement.Blob(0), CodexMemoryStorageJson.Default.CodexMemoryScope) != legacyScope)
            throw new UnauthorizedAccessException("Legacy memory database identity changed.");
        statement.Done();
    }

    private CodexMemoryLifecycleReceipt ApplyLifecycle(CodexMemoryRpcAccess access, CodexMemoryScope? legacyScope)
    {
        var fingerprint = LifecycleFingerprint(access);
        var operation = access.OperationId!.Value.ToByteArray();
        using (var receipt = Prepare(database, "SELECT fingerprint,settlement FROM main.memory_lifecycle_receipt WHERE operation=?1;"))
        {
            receipt.Bind(1, operation);
            if (receipt.Next())
            {
                if (!CryptographicOperations.FixedTimeEquals(receipt.Blob(0), fingerprint))
                    throw new InvalidOperationException("Memory lifecycle operation identity was reused with different contents.");
                var settlement = (CodexMemoryLifecycleSettlement)receipt.Number(1);
                if (settlement is not (CodexMemoryLifecycleSettlement.Applied or CodexMemoryLifecycleSettlement.NotFound or
                    CodexMemoryLifecycleSettlement.Denied or CodexMemoryLifecycleSettlement.Conflict))
                    throw new InvalidDataException("Invalid memory lifecycle receipt.");
                receipt.Done();
                return new(access, settlement);
            }
        }

        // An external pointer cannot be cleared by this local-only adapter. Refuse the
        // entire mutation before changing either namespace instead of acknowledging a partial reset.
        RequireLocalArtifacts("main");
        if (legacyScope is not null) RequireLocalArtifacts("legacy");

        var outcome = CodexMemoryLifecycleSettlement.Applied;
        if (access.Method == "memory/reset")
        {
            ClearMemoryNamespace("main");
            if (legacyScope is not null) ClearMemoryNamespace("legacy");
        }
        else
        {
            var thread = access.ThreadId!.Value.ToByteArray();
            var currentPresence = ThreadPresence("main", thread);
            var legacyPresence = legacyScope is not null ? ThreadPresence("legacy", thread) : 0;
            if (currentPresence == 2 || legacyPresence == 2 || currentPresence == 0 && legacyPresence == 0)
                outcome = CodexMemoryLifecycleSettlement.NotFound;
            else
            {
                var enabled = access.Mode == CodexThreadMemoryMode.Enabled;
                SetThreadMode("main", scope, thread, enabled);
                if (legacyScope is not null) SetThreadMode("legacy", legacyScope, thread, enabled);
            }
        }

        using (var insert = Prepare(database, """
            INSERT INTO main.memory_lifecycle_receipt(operation,fingerprint,settlement) VALUES(?1,?2,?3);
            """))
        {
            insert.Bind(1, operation);
            insert.Bind(2, fingerprint);
            insert.Bind(3, (long)outcome);
            insert.Done();
        }
        return new(access, outcome);
    }

    private static byte[] LifecycleFingerprint(CodexMemoryRpcAccess access)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashBytes(hash, Utf8.GetBytes(access.AccountId));
        HashBytes(hash, Utf8.GetBytes(access.Method));
        hash.AppendData((access.ThreadId ?? Guid.Empty).ToByteArray());
        HashNumber(hash, access.Mode is { } mode ? (int)mode : -1);
        return hash.GetHashAndReset();
    }

    private void RequireLocalArtifacts(string schema)
    {
        CheckMemorySchema(schema);
        using var statement = Prepare(database, $"""
            SELECT 1 FROM {schema}.progress p WHERE p.id=1 AND p.artifact IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM {schema}.memory_manifest m WHERE m.root=p.artifact AND m.root<>X'00');
            """);
        if (statement.Next())
            throw new InvalidOperationException("An external memory artifact requires its authoritative lifecycle adapter.");
    }

    private void ClearMemoryNamespace(string schema)
    {
        CheckMemorySchema(schema);
        // Keep source and thread modes, I/O policy, and operation/attempt tombstones.
        // The reset epoch fences even uncommitted insert-only claims prepared before reset.
        // Tombstones contain identifiers/digests only; they cannot resurrect outputs.
        Execute(database, $"""
            DELETE FROM {schema}.selected;
            UPDATE {schema}.progress SET artifact=NULL,max_count=0 WHERE id=1;
            DELETE FROM {schema}.memory_manifest WHERE root<>X'00';
            DELETE FROM {schema}.memory_entry WHERE root=X'00';
            DELETE FROM {schema}.extraction;
            DELETE FROM {schema}.job;
            UPDATE {schema}.memory_epoch SET generation=generation+1 WHERE id=1;
            UPDATE {schema}.memory_view SET revision=revision+1 WHERE id=1;
            """);
    }

    // 0 = unknown, 1 = known and active, 2 = archived. The host supplies authoritative
    // source registration and authorization for native threads; this is not history discovery.
    private int ThreadPresence(string schema, byte[] thread)
    {
        CheckMemorySchema(schema);
        using var statement = Prepare(database, $"""
            SELECT COALESCE(d.archived,0) FROM {schema}.source s
                LEFT JOIN {schema}.discovery d ON d.thread=s.thread AND d.watermark=s.watermark
            WHERE s.thread=?1;
            """);
        statement.Bind(1, thread);
        if (!statement.Next()) return 0;
        var presence = statement.Number(0) == 1 ? 2 : 1;
        statement.Done();
        return presence;
    }

    private void SetThreadMode(string schema, CodexMemoryScope targetScope, byte[] thread, bool enabled)
    {
        CheckMemorySchema(schema);
        var prior = ReadMemoryMode(database, schema, thread);
        // Persist explicit enabled mode as well, so a subsequent toggle has a monotonic fence.
        if (prior.Revision != 0 && prior.Enabled == enabled) return;
        using (var update = Prepare(database, $"""
            INSERT INTO {schema}.memory_mode(thread,revision,enabled) VALUES(?1,?2,?3)
            ON CONFLICT(thread) DO UPDATE SET revision=excluded.revision,enabled=excluded.enabled;
            """))
        {
            update.Bind(1, thread);
            update.Bind(2, checked(prior.Revision + 1));
            update.Bind(3, enabled ? 1 : 0);
            update.Done();
        }
        // The source's independent host policy is never weakened by enabling a mode.
        if (enabled && prior.Enabled) return;
        using (var jobs = Prepare(database, $"DELETE FROM {schema}.job WHERE thread=?1;"))
        {
            jobs.Bind(1, thread);
            jobs.Done();
        }
        if (enabled) return;

        var changed = DeleteThreadRow(schema, "extraction", thread);
        var selected = DeleteThreadRow(schema, "selected", thread);
        if (selected) Execute(database, $"UPDATE {schema}.progress SET artifact=NULL WHERE id=1;");
        using (var artifacts = Prepare(database, $"""
            DELETE FROM {schema}.memory_manifest WHERE root IN
                (SELECT root FROM {schema}.memory_artifact_source WHERE thread=?1);
            """))
        {
            artifacts.Bind(1, thread);
            artifacts.Done();
            changed |= Changes(database) != 0;
        }
        if (changed || selected)
        {
            var global = new CodexMemoryJobKey(targetScope, null);
            var epoch = ScalarNumber(database, $"SELECT generation FROM {schema}.memory_epoch WHERE id=1;");
            var observed = new CodexMemoryJobObservation(global, ReadJob(Global, schema), false, null, null, epoch);
            var planner = new CodexMemoryJobs(targetScope, this, (_, _) => ValueTask.CompletedTask, clock);
            WriteJob(Global, planner.PrepareEnqueue(observed, Guid.NewGuid(), 0).Next, schema);
        }
    }

    private bool DeleteThreadRow(string schema, string table, byte[] thread)
    {
        CheckMemorySchema(schema);
        if (table is not ("extraction" or "selected")) throw new ArgumentException("Invalid lifecycle table.", nameof(table));
        using var statement = Prepare(database, $"DELETE FROM {schema}.{table} WHERE thread=?1;");
        statement.Bind(1, thread);
        statement.Done();
        return Changes(database) != 0;
    }
}
