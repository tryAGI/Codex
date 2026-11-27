namespace tryAGI.Codex;

public sealed partial class CodexMacOsSqliteMemoryJobStorage
{
    private const string DiscoverySchema = """
        CREATE TABLE discovery(
            thread BLOB PRIMARY KEY NOT NULL,
            watermark INTEGER NOT NULL CHECK(watermark>=0),
            updated_at INTEGER NOT NULL,
            kind BLOB NOT NULL,
            archived INTEGER NOT NULL CHECK(archived IN (0,1)),
            eligible INTEGER NOT NULL CHECK(eligible IN (0,1))
        ) WITHOUT ROWID;
        CREATE INDEX discovery_recent_eligible ON discovery(updated_at DESC,thread) WHERE eligible=1;
        """;

    // Caller owns the write transaction. A changed generation without metadata removes its old
    // discovery row; existing source rows from legacy databases remain explicitly claimable.
    private bool PublishDiscovery(CodexMemorySourceUpdate update, byte[] thread, bool sameGeneration)
    {
        var metadata = update.Discovery;
        if (metadata is null)
        {
            if (!sameGeneration)
            {
                using var delete = Prepare(database, "DELETE FROM discovery WHERE thread=?1;");
                delete.Bind(1, thread);
                delete.Done();
            }
            return true;
        }
        var eligible = update.Enabled && !metadata.Archived;
        if (sameGeneration)
        {
            using var existing = Prepare(database, """
                SELECT watermark,updated_at,kind,archived,eligible FROM discovery WHERE thread=?1;
                """);
            existing.Bind(1, thread);
            if (existing.Next())
            {
                var identical = existing.Number(0) == update.Watermark &&
                    existing.Number(1) == metadata.UpdatedAt.UtcTicks &&
                    Utf8.GetString(existing.Blob(2)) == metadata.SourceKind &&
                    existing.Number(3) == (metadata.Archived ? 1 : 0) &&
                    existing.Number(4) == (eligible ? 1 : 0);
                existing.Done();
                return identical;
            }
        }
        using var write = Prepare(database, """
            INSERT INTO discovery(thread,watermark,updated_at,kind,archived,eligible)
            VALUES(?1,?2,?3,?4,?5,?6)
            ON CONFLICT(thread) DO UPDATE SET watermark=excluded.watermark,updated_at=excluded.updated_at,
                kind=excluded.kind,archived=excluded.archived,eligible=excluded.eligible;
            """);
        write.Bind(1, thread);
        write.Bind(2, update.Watermark);
        write.Bind(3, metadata.UpdatedAt.UtcTicks);
        write.Bind(4, Utf8.GetBytes(metadata.SourceKind));
        write.Bind(5, metadata.Archived ? 1 : 0);
        write.Bind(6, eligible ? 1 : 0);
        write.Done();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CodexMemoryStartupCandidate>> ScanStartupAsync(CodexMemoryStartupScan scan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        scan.Validate();
        CheckKey(new(scan.Scope, null));
        var frozen = scan with { AllowedSources = Array.AsReadOnly(scan.AllowedSources.ToArray()) };
        frozen.Validate();
        await AuthorizeAsync(new(new(scope, null), "scan_startup", StartupScan: frozen), cancellationToken)
            .ConfigureAwait(false);
        if (frozen.ScanLimit == 0 || frozen.AllowedSources.Count == 0 || frozen.EarliestUpdate > frozen.LatestUpdate)
            return [];
        return await RunAsync<IReadOnlyList<CodexMemoryStartupCandidate>>(() =>
        {
            // Only numbered SQL placeholders are generated; all source names are bound data.
            // The index and LIMIT apply before the scheduler probes any job or output row.
            var parameters = string.Join(",", Enumerable.Range(4, frozen.AllowedSources.Count).Select(i => "?" +
                i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var limitParameter = "?" + (4 + frozen.AllowedSources.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var query = Prepare(database, """
                SELECT thread,watermark,updated_at,kind FROM discovery
                WHERE eligible=1 AND updated_at>=?1 AND updated_at<=?2 AND thread<>?3 AND kind IN (
                """ + parameters + ") ORDER BY updated_at DESC,thread LIMIT " + limitParameter + ";");
            query.Bind(1, frozen.EarliestUpdate.UtcTicks);
            query.Bind(2, frozen.LatestUpdate.UtcTicks);
            query.Bind(3, frozen.CurrentThreadId.ToByteArray());
            for (var i = 0; i < frozen.AllowedSources.Count; i++)
                query.Bind(4 + i, Utf8.GetBytes(frozen.AllowedSources[i]));
            query.Bind(4 + frozen.AllowedSources.Count, frozen.ScanLimit);
            var result = new List<CodexMemoryStartupCandidate>();
            while (query.Next())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new(new Guid(query.Blob(0)), query.Number(1),
                    new DateTimeOffset(query.Number(2), TimeSpan.Zero), Utf8.GetString(query.Blob(3))));
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }
}
