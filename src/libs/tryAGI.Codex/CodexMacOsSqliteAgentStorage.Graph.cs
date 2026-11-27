using System.Text.Json;
using Sql = tryAGI.Codex.CodexMacOsAgentSqlite.Connection;

namespace tryAGI.Codex;

public sealed partial class CodexMacOsSqliteAgentStorage
{
    /// <inheritdoc />
    public Task<CodexThreadSpawnGraph> ReadSpawnGraphAsync(string accountId, Guid rootThreadId,
        CancellationToken cancellationToken)
    {
        Account(accountId);
        Id(rootThreadId);
        return database.ReadAsync(sql =>
        {
            var root = Edge(sql, rootThreadId)?.Root ?? rootThreadId;
            var revision = RequireRoot(sql, root);
            var rows = TreeRows(sql, root);
            var snapshot = Snapshot(root, revision, rows);
            _ = validator.ValidateAgentIdentitySnapshot(snapshot, root);
            return root == rootThreadId ? snapshot.Graph :
                new CodexThreadSpawnGraph(accountId, rootThreadId, Revision(revision),
                    Array.AsReadOnly(Descendants(rootThreadId, rows).Select(row => row.Edge).ToArray()));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CodexAgentIdentitySnapshot> ReadAgentIdentitySnapshotAsync(string accountId, Guid rootThreadId,
        CancellationToken cancellationToken)
    {
        Account(accountId);
        Id(rootThreadId);
        return database.ReadAsync(sql =>
        {
            var snapshot = Snapshot(rootThreadId, RequireRoot(sql, rootThreadId), TreeRows(sql, rootThreadId));
            _ = validator.ValidateAgentIdentitySnapshot(snapshot, rootThreadId);
            return snapshot;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CompareExchangeSpawnEdgeAsync(string accountId, Guid rootThreadId, string expectedGraphRevision,
        CodexThreadSpawnEdge edge, CancellationToken cancellationToken)
    {
        Account(accountId);
        Id(rootThreadId);
        ArgumentNullException.ThrowIfNull(edge);
        Id(edge.ChildThreadId); Id(edge.ParentThreadId);
        if (edge.ChildThreadId == edge.ParentThreadId || !Enum.IsDefined(edge.Status))
            throw new ArgumentException("Invalid agent edge.", nameof(edge));
        return database.WriteAsync(sql =>
        {
            var root = Edge(sql, rootThreadId)?.Root ?? rootThreadId;
            var revision = RequireRoot(sql, root);
            if (expectedGraphRevision != Revision(revision)) return false;
            var rows = TreeRows(sql, root);
            _ = validator.ValidateAgentIdentitySnapshot(Snapshot(root, revision, rows), root);
            var scoped = Descendants(rootThreadId, rows);
            if (edge.ChildThreadId == rootThreadId || edge.ChildThreadId == root ||
                edge.ParentThreadId != rootThreadId && !scoped.Any(row => row.Edge.ChildThreadId == edge.ParentThreadId))
                return false;
            var previous = Edge(sql, edge.ChildThreadId);
            if (previous is not null && (previous.Root != root ||
                !scoped.Any(row => row.Edge.ChildThreadId == edge.ChildThreadId))) return false;
            if (previous?.Edge == edge) return true;
            if (previous is null && (rows.Length >= MaximumEdges ||
                Header(sql, edge.ChildThreadId) is not null || RootRevision(sql, edge.ChildThreadId) is not null))
                return false;
            if (previous?.Identity is not null && previous.Edge.ParentThreadId != edge.ParentThreadId)
                return false; // Named creation provenance is immutable.
            if (edge.Status == CodexThreadSpawnEdgeStatus.Open && !OpenPath(sql, root, edge.ParentThreadId)) return false;
            if (previous is not null && (edge.Status == CodexThreadSpawnEdgeStatus.Closed ||
                    edge.ParentThreadId != previous.Edge.ParentThreadId || previous.Edge.Status != edge.Status) &&
                (!Settled(sql, edge.ChildThreadId) ||
                    Descendants(edge.ChildThreadId, rows).Any(row => !Settled(sql, row.Edge.ChildThreadId))))
                return false;

            var replacement = new EdgeRow(root, edge, previous?.Identity);
            var prospective = rows.Where(row => row.Edge.ChildThreadId != edge.ChildThreadId).Append(replacement).ToArray();
            if (!ValidProspectiveGraph(root, revision, prospective)) return false;
            if (previous is null) InsertEdge(sql, replacement);
            else ExpectOne(sql.Change("UPDATE agent_edges SET parent=?2,status=?3 WHERE child=?1;",
                edge.ChildThreadId, edge.ParentThreadId, (int)edge.Status));
            AdvanceRoot(sql, root, revision);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CompareCreateNamedAgentAsync(CodexNamedAgentSpawnPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Account(plan.AccountId);
        var identity = plan.Identity ?? throw new ArgumentException("Missing named child identity.", nameof(plan));
        Id(identity.ThreadId); Id(identity.RootThreadId);
        if (identity.ParentThreadId is not { } parent || parent == Guid.Empty ||
            identity.SpawnOrigin is not { IsValid: true } origin ||
            identity.ThreadId == identity.RootThreadId || identity.ThreadId == parent ||
            plan.MaximumDepth is < 1 or > MaximumDepth)
            throw new ArgumentException("Invalid named spawn plan.", nameof(plan));
        var document = validator.ValidateSnapshot(plan.Document);
        var expectedFresh = new CodexThreadDocument(1, identity.ThreadId, accountId, 1,
            document.Settings, document.CreatedAt, document.CreatedAt, []);
        if (!JsonElement.DeepEquals(CodexThreadStore.Encode(document), CodexThreadStore.Encode(expectedFresh)) ||
            CodexRealtimeProtocol.Text(document.Settings, "source") != "subagent" ||
            CodexRealtimeProtocol.Text(document.Settings, "threadSource") != "subagent" ||
            document.Settings.TryGetProperty("ephemeral", out var ephemeral) &&
                ephemeral.ValueKind is not (JsonValueKind.False or JsonValueKind.Null) ||
            CodexRetainedJournalMetadata.ItemHash(document.Settings) != origin.InitialSettingsSha256 ||
            CodexRealtimeProtocol.Text(document.Settings, "model") != origin.InitialModel ||
            CodexRealtimeProtocol.Text(document.Settings, "reasoningEffort") != origin.InitialReasoningEffort)
            throw new ArgumentException("Named spawn requires its exact fresh child document.", nameof(plan));
        var mailbox = CodexThreadStore.ValidateAgentMailboxSnapshot(plan.Mailbox, accountId,
            identity.RootThreadId, identity.ThreadId);
        if (mailbox.Revision != 1 || mailbox.Claim is not null || mailbox.Receipts.Count != 0 ||
            mailbox.Pending.Count != 1 || mailbox.Pending[0] is not { Mode: CodexAgentMailMode.Followup } mail ||
            mail.Id != origin.InitialMessageId || mail.SenderThreadId != parent ||
            string.IsNullOrWhiteSpace(mail.Content) ||
            CodexThreadStore.AgentMailFingerprint(mail) != origin.InitialMessageFingerprint)
            throw new ArgumentException("Named spawn requires its exact initial follow-up.", nameof(plan));
        var body = CodexMacOsAgentSqlite.Text(plan.Document.GetRawText());
        if (body.Length > CodexThreadStore.MaximumDocumentBytes)
            throw new ArgumentException("Named spawn document exceeds its storage bound.", nameof(plan));

        return database.WriteAsync(sql =>
        {
            var root = identity.RootThreadId;
            var revision = RequireRoot(sql, root);
            if (Revision(revision) != origin.GraphRevision || Header(sql, parent)?.Revision != origin.ParentRevision ||
                Header(sql, identity.ThreadId) is not null || Edge(sql, identity.ThreadId) is not null ||
                RootRevision(sql, identity.ThreadId) is not null || !OpenPath(sql, root, parent))
                return false;
            var rows = TreeRows(sql, root);
            var directory = validator.ValidateAgentIdentitySnapshot(Snapshot(root, revision, rows), root);
            if (rows.Length >= MaximumEdges || directory.ContainsSpawnDispatch(parent, origin.ParentTurnId, origin.SourceDispatchId))
                return false;
            CodexNamedAgentIdentity parentIdentity;
            try { parentIdentity = directory.GetActive(parent); }
            catch (KeyNotFoundException) { return false; }
            if (identity.Path != CodexAgentPath.Join(parentIdentity.Path, CodexAgentPath.Name(identity.Path)) ||
                identity.Path.Count(c => c == '/') - 1 > plan.MaximumDepth)
                return false;
            // Only this source journal is read. Ordinary graph and mailbox reads never decode history.
            var source = ReadDocument(sql, parent);
            if (source.Revision != origin.ParentRevision || source.ArchivedAt is not null ||
                source.Turns.LastOrDefault() is not { Status: "inProgress" } turn ||
                turn.Id != origin.ParentTurnId || turn.RootTurnId != origin.RootTurnId ||
                turn.OwnerId != origin.ParentOwnerId ||
                !turn.Dispatches.Any(dispatch => dispatch.CallId == origin.SourceDispatchId &&
                    dispatch.Kind == "tool" && dispatch.State == "dispatching" &&
                    dispatch.RequestSha256 == origin.SourceDispatchSha256))
                return false;
            var row = new EdgeRow(root, new(parent, identity.ThreadId, CodexThreadSpawnEdgeStatus.Open), identity);
            if (!ValidProspectiveGraph(root, revision, [.. rows, row])) return false;
            // All changes are in one SQLite transaction. No compensating writes are needed.
            InsertThread(sql, document, body);
            InsertEdge(sql, row);
            WriteMailbox(sql, mailbox, 0);
            AdvanceRoot(sql, root, revision);
            return true;
        }, cancellationToken);
    }

    private CodexAgentIdentitySnapshot Snapshot(Guid root, long revision, EdgeRow[] rows)
        => new(new(accountId, root, Revision(revision), Array.AsReadOnly(rows.Select(row => row.Edge).ToArray())),
            Array.AsReadOnly(rows.Where(row => row.Identity is not null).Select(row => row.Identity!).ToArray()));

    private bool ValidProspectiveGraph(Guid root, long revision, EdgeRow[] rows)
    {
        try
        {
            _ = validator.ValidateAgentIdentitySnapshot(Snapshot(root, revision, rows), root);
            // The local adapter bounds all ancestor walks, including graph-only reservations.
            var parents = rows.ToDictionary(row => row.Edge.ChildThreadId, row => row.Edge.ParentThreadId);
            foreach (var child in parents.Keys)
            {
                var current = child;
                var depth = 0;
                while (current != root)
                {
                    if (++depth > MaximumDepth || !parents.TryGetValue(current, out current)) return false;
                }
            }
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static void InsertEdge(Sql sql, EdgeRow row)
        => ExpectOne(sql.Change("""
            INSERT INTO agent_edges(child,root,parent,status,identity) VALUES(?1,?2,?3,?4,?5);
            """, row.Edge.ChildThreadId, row.Root, row.Edge.ParentThreadId, (int)row.Edge.Status,
            JsonSerializer.SerializeToUtf8Bytes(row.Identity, CodexAgentStorageJson.Default.CodexNamedAgentIdentity)));
}
