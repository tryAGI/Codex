using System.Security.Cryptography;

namespace tryAGI.Codex;

/// <summary>Exact provenance of memory actually used by one authorized turn.</summary>
/// <param name="OperationId">Stable identity, retained across ambiguous settlement.</param>
/// <param name="ArtifactRevision">Published artifact whose evidence was used.</param>
/// <param name="ResetEpoch">Namespace generation observed when the artifact was read.</param>
/// <param name="Selected">Distinct exact source revisions actually used, not all retrieved files.</param>
public sealed record CodexMemoryUsageReport(Guid OperationId, string ArtifactRevision, long ResetEpoch,
    IReadOnlyList<CodexMemorySelectedSnapshot> Selected);

/// <summary>Exact authorization scope for one metadata-only maintenance operation.</summary>
/// <param name="Operation">Operation classification.</param>
/// <param name="Cutoff">Inclusive selection or exclusive retention cutoff, when applicable.</param>
/// <param name="Limit">Maximum affected rows, when applicable.</param>
/// <param name="Usage">Exact usage report, when applicable.</param>
public sealed record CodexMemoryMaintenanceAccess(string Operation, DateTimeOffset? Cutoff = null,
    int? Limit = null, CodexMemoryUsageReport? Usage = null);

public sealed partial class CodexMacOsSqliteMemoryJobStorage
{
    private const string MaintenanceSchema = """
        CREATE TABLE memory_epoch(
            id INTEGER PRIMARY KEY CHECK(id=1),
            generation INTEGER NOT NULL CHECK(typeof(generation)='integer' AND generation>=0)
        );
        INSERT INTO memory_epoch VALUES(1,0);
        CREATE TABLE memory_mode(
            thread BLOB PRIMARY KEY NOT NULL,
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=1),
            enabled INTEGER NOT NULL CHECK(enabled IN (0,1))
        ) WITHOUT ROWID;
        CREATE TABLE memory_retention(
            thread BLOB PRIMARY KEY NOT NULL REFERENCES extraction(thread) ON DELETE CASCADE,
            source_at INTEGER NOT NULL,
            recency INTEGER NOT NULL,
            usage_count INTEGER NOT NULL CHECK(typeof(usage_count)='integer' AND usage_count>=0),
            has_content INTEGER NOT NULL CHECK(has_content IN (0,1))
        ) WITHOUT ROWID;
        CREATE INDEX memory_retention_recency ON memory_retention(recency,source_at,thread);
        CREATE INDEX memory_retention_rank ON memory_retention(usage_count DESC,recency DESC,source_at DESC,thread)
            WHERE has_content=1;
        CREATE TABLE memory_usage_receipt(
            operation BLOB PRIMARY KEY NOT NULL,
            fingerprint BLOB NOT NULL,
            changed INTEGER NOT NULL CHECK(changed>=0)
        ) WITHOUT ROWID;
        CREATE TABLE memory_lifecycle_receipt(
            operation BLOB PRIMARY KEY NOT NULL,
            fingerprint BLOB NOT NULL,
            settlement INTEGER NOT NULL
        ) WITHOUT ROWID;
        """;

    private static void BackfillRetention(IntPtr db, DateTimeOffset now)
    {
        // A source watermark is an opaque generation, not necessarily a timestamp.
        // Legacy rows without an exact discovery timestamp are given a conservative fresh age.
        using var statement = Prepare(db, """
            INSERT INTO memory_retention(thread,source_at,recency,usage_count,has_content)
            SELECT e.thread,COALESCE(d.updated_at,?1),COALESCE(d.updated_at,?1),0,1
            FROM extraction e LEFT JOIN discovery d ON d.thread=e.thread AND d.watermark=e.watermark;
            """);
        statement.Bind(1, now.UtcTicks);
        statement.Done();
    }

    private void UpdateExtractionRetention(byte[] thread, long watermark, CodexMemoryExtractionOutput output)
    {
        var sourceAt = clock.GetUtcNow().UtcTicks;
        using (var timestamp = Prepare(database, "SELECT updated_at FROM discovery WHERE thread=?1 AND watermark=?2;"))
        {
            timestamp.Bind(1, thread);
            timestamp.Bind(2, watermark);
            if (timestamp.Next())
            {
                sourceAt = timestamp.Number(0);
                timestamp.Done();
            }
        }
        using var update = Prepare(database, """
            INSERT INTO memory_retention(thread,source_at,recency,usage_count,has_content)
            VALUES(?1,?2,?2,0,?3)
            ON CONFLICT(thread) DO UPDATE SET source_at=excluded.source_at,
                recency=CASE WHEN memory_retention.usage_count=0 THEN excluded.source_at
                    ELSE memory_retention.recency END,
                has_content=excluded.has_content;
            """);
        update.Bind(1, thread);
        update.Bind(2, sourceAt);
        update.Bind(3, string.IsNullOrWhiteSpace(output.RawMemory) &&
            string.IsNullOrWhiteSpace(output.RolloutSummary) ? 0 : 1);
        update.Done();
    }

    private static (long Revision, bool Enabled) ReadMemoryMode(IntPtr db, string schema, byte[] thread)
    {
        CheckMemorySchema(schema);
        using var statement = Prepare(db, $"SELECT revision,enabled FROM {schema}.memory_mode WHERE thread=?1;");
        statement.Bind(1, thread);
        if (!statement.Next()) return (0, true);
        var result = (statement.Number(0), statement.Number(1) == 1);
        statement.Done();
        return result;
    }

    private static void CheckMemorySchema(string schema)
    {
        if (schema is not ("main" or "legacy"))
            throw new ArgumentException("Invalid internal memory database schema.", nameof(schema));
    }

    /// <summary>
    /// Returns the Rust phase-2 ranked input set: recent nonblank eligible extraction rows,
    /// ranked by use count and recency, then emitted in deterministic thread order.
    /// No extracted content is loaded by this selection.
    /// </summary>
    public async Task<IReadOnlyList<CodexMemorySelectedSnapshot>> SelectPhase2InputsAsync(DateTimeOffset cutoff,
        int maximumResults = 100, CancellationToken cancellationToken = default)
    {
        CheckMaintenanceLimit(maximumResults);
        await AuthorizeAsync(new(new(scope, null), "select_phase2_inputs",
            Maintenance: new("select_phase2_inputs", cutoff, maximumResults)), cancellationToken).ConfigureAwait(false);
        if (maximumResults == 0) return [];
        return await RunAsync<IReadOnlyList<CodexMemorySelectedSnapshot>>(() =>
        {
            using var query = Prepare(database, """
                SELECT e.thread,e.watermark FROM memory_retention r
                JOIN extraction e ON e.thread=r.thread JOIN source s ON s.thread=e.thread
                WHERE r.has_content=1 AND r.recency>=?1 AND s.enabled=1 AND e.watermark<=s.watermark
                    AND NOT EXISTS(SELECT 1 FROM memory_mode m WHERE m.thread=e.thread AND m.enabled=0)
                ORDER BY r.usage_count DESC,r.recency DESC,r.source_at DESC,e.thread LIMIT ?2;
                """);
            query.Bind(1, cutoff.UtcTicks);
            query.Bind(2, maximumResults);
            var result = new List<CodexMemorySelectedSnapshot>();
            while (query.Next())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(new Guid(query.Blob(0)), query.Number(1)));
            }
            return result.OrderBy(x => x.ThreadId).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records only actual, host-authorized usage of exact source generations in the current
    /// published baseline. An operation increments each matching source at most once.
    /// Stale artifacts, reset epochs and replaced extraction rows never gain usage credit.
    /// </summary>
    public async Task<int> RecordMemoryUsageAsync(CodexMemoryUsageReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(report.Selected);
        if (report.OperationId == Guid.Empty || report.ResetEpoch < 0 ||
            string.IsNullOrWhiteSpace(report.ArtifactRevision) || report.ArtifactRevision.Length > 4096 ||
            report.Selected.Count > 1000)
            throw new ArgumentException("Invalid memory usage report.", nameof(report));
        var selected = report.Selected.ToArray();
        if (selected.Length > 1000) throw new ArgumentException("Memory usage report is too large.", nameof(report));
        var selectionHash = ArtifactSelectionHash(selected);
        var frozen = report with { Selected = Array.AsReadOnly(selected) };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashBytes(hash, Utf8.GetBytes(report.ArtifactRevision));
        HashNumber(hash, report.ResetEpoch);
        hash.AppendData(selectionHash);
        var fingerprint = hash.GetHashAndReset();
        await AuthorizeAsync(new(new(scope, null), "record_memory_usage",
            Maintenance: new("record_memory_usage", Usage: frozen)), cancellationToken).ConfigureAwait(false);

        return await RunAsync(() => Transaction(database, true, cancellationToken, () =>
        {
            using (var receipt = Prepare(database, "SELECT fingerprint,changed FROM memory_usage_receipt WHERE operation=?1;"))
            {
                receipt.Bind(1, report.OperationId.ToByteArray());
                if (receipt.Next())
                {
                    if (!CryptographicOperations.FixedTimeEquals(receipt.Blob(0), fingerprint))
                        throw new InvalidOperationException("Memory usage operation identity was reused with different contents.");
                    var changed = checked((int)receipt.Number(1));
                    receipt.Done();
                    return changed;
                }
            }

            bool active;
            using (var pointer = Prepare(database, """
                SELECT 1 FROM progress p CROSS JOIN memory_epoch e
                WHERE p.id=1 AND e.id=1 AND p.artifact=?1 AND e.generation=?2;
                """))
            {
                pointer.Bind(1, Utf8.GetBytes(report.ArtifactRevision));
                pointer.Bind(2, report.ResetEpoch);
                active = pointer.Next();
                if (active) pointer.Done();
            }
            var count = 0;
            if (active)
            {
                var now = clock.GetUtcNow().UtcTicks;
                foreach (var item in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var update = Prepare(database, """
                        UPDATE memory_retention SET usage_count=usage_count+1,recency=MAX(recency,?3)
                        WHERE thread=?1 AND EXISTS(
                            SELECT 1 FROM selected k JOIN extraction e ON e.thread=k.thread AND e.watermark=k.watermark
                            JOIN source s ON s.thread=e.thread
                            WHERE k.thread=?1 AND k.watermark=?2 AND s.enabled=1 AND e.watermark<=s.watermark
                              AND NOT EXISTS(SELECT 1 FROM memory_mode m WHERE m.thread=e.thread AND m.enabled=0)
                        );
                        """);
                    update.Bind(1, item.ThreadId.ToByteArray());
                    update.Bind(2, item.SourceWatermark);
                    update.Bind(3, now);
                    update.Done();
                    count += Changes(database);
                }
            }
            using var insert = Prepare(database,
                "INSERT INTO memory_usage_receipt(operation,fingerprint,changed) VALUES(?1,?2,?3);");
            insert.Bind(1, report.OperationId.ToByteArray());
            insert.Bind(2, fingerprint);
            insert.Bind(3, count);
            insert.Done();
            return count;
        }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prunes only a bounded batch of unused, unselected extraction outputs. Source metadata,
    /// job watermarks and receipts remain intact. Active phase-2 or per-source leases protect
    /// their inputs from retention; a later extraction must still have a newer source generation.
    /// </summary>
    public async Task<int> PruneUnusedExtractionsAsync(DateTimeOffset cutoff, int limit = 200,
        CancellationToken cancellationToken = default)
    {
        CheckMaintenanceLimit(limit);
        await AuthorizeAsync(new(new(scope, null), "prune_memory_extractions",
            Maintenance: new("prune_memory_extractions", cutoff, limit)), cancellationToken).ConfigureAwait(false);
        if (limit == 0) return 0;
        return await RunAsync(() => Transaction(database, true, cancellationToken, () =>
        {
            var now = clock.GetUtcNow().UtcTicks;
            using var statement = Prepare(database, """
                DELETE FROM extraction WHERE thread IN (
                    SELECT r.thread FROM memory_retention r
                    WHERE r.recency<?1
                      AND NOT EXISTS(SELECT 1 FROM selected k WHERE k.thread=r.thread)
                      AND NOT EXISTS(SELECT 1 FROM job j WHERE j.thread IN (?2,r.thread)
                        AND j.status=?3 AND j.lease_until>?4)
                    ORDER BY r.recency,r.source_at,r.thread LIMIT ?5
                );
                """);
            statement.Bind(1, cutoff.UtcTicks);
            statement.Bind(2, Global);
            statement.Bind(3, (long)CodexMemoryJobStatus.Running);
            statement.Bind(4, now);
            statement.Bind(5, limit);
            statement.Done();
            return Changes(database);
        }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes only a bounded batch of unreferenced local artifacts older than cutoff.
    /// Current publication, notes and the live consolidation claim are never collected.
    /// </summary>
    public async Task<int> PruneUnpublishedArtifactsAsync(DateTimeOffset cutoff, int limit = 200,
        CancellationToken cancellationToken = default)
    {
        CheckMaintenanceLimit(limit);
        await AuthorizeAsync(new(new(scope, null), "prune_memory_artifacts",
            Maintenance: new("prune_memory_artifacts", cutoff, limit)), cancellationToken).ConfigureAwait(false);
        if (limit == 0) return 0;
        return await RunAsync(() => Transaction(database, true, cancellationToken, () =>
        {
            var state = ReadJob(Global);
            var active = state is { Status: CodexMemoryJobStatus.Running } &&
                state.LeaseUntil > clock.GetUtcNow() ? state.OwnershipToken!.Value.ToByteArray() : Global;
            using var statement = Prepare(database, """
                DELETE FROM memory_manifest WHERE root IN (
                    SELECT m.root FROM memory_manifest m
                    WHERE m.root<>X'00' AND m.created_at<?1 AND m.claim<>?2
                        AND NOT EXISTS(SELECT 1 FROM progress p WHERE p.artifact=m.root)
                    ORDER BY m.created_at,m.root LIMIT ?3
                );
                """);
            statement.Bind(1, cutoff.UtcTicks);
            statement.Bind(2, active);
            statement.Bind(3, limit);
            statement.Done();
            return Changes(database);
        }), cancellationToken).ConfigureAwait(false);
    }

    private static void CheckMaintenanceLimit(int limit)
    {
        if (limit is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
    }
}
