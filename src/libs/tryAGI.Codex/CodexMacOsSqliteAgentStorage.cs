using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sql = tryAGI.Codex.CodexMacOsAgentSqlite.Connection;

namespace tryAGI.Codex;

/// <summary>Local account-bound native thread, named graph and mailbox storage using one
/// durable macOS SQLite database. Intended for a trusted embedding host, not a remote RPC
/// authorization boundary. The host must select a private stable local directory, prevent
/// untrusted writers, links and database replacement, supply required encryption at rest,
/// authorize ordinary journal/tool operations and explicitly grant wake admissions.
/// Supports fresh named spawn and bound idle wake; active-turn mail admission and permanent
/// deletion are not implemented. Do not restore older snapshots while the store is in use.</summary>
public sealed partial class CodexMacOsSqliteAgentStorage : ICodexNamedAgentLifecycleStorage, IAsyncDisposable
{
    private readonly CodexMacOsAgentSqlite database;
    private readonly string accountId;
    private readonly CodexThreadStore validator;
    private const int MaximumEdges = 4096;
    private const int MaximumDepth = 128;

    private CodexMacOsSqliteAgentStorage(CodexMacOsAgentSqlite database, string accountId)
    {
        this.database = database;
        this.accountId = accountId;
        validator = new(this, accountId);
    }

    /// <summary>Explicitly initialize a new private database or reopen an initialized database
    /// of the same account. Uses full synchronous rollback journaling and separate SQLite
    /// connections for cross-instance atomicity; never overwrites an existing file.</summary>
    public static async Task<CodexMacOsSqliteAgentStorage> OpenAsync(string path, string accountId,
        bool createNew = false, CancellationToken cancellationToken = default)
    {
        Token(accountId);
        var db = await CodexMacOsAgentSqlite.OpenAsync(path, accountId, createNew, cancellationToken).ConfigureAwait(false);
        return new(db, accountId);
    }

    /// <inheritdoc />
    public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
    {
        Id(threadId);
        return database.ReadAsync(sql => ReadBody(sql, threadId), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
        CancellationToken cancellationToken)
    {
        Id(threadId);
        var next = validator.ValidateSnapshot(document);
        if (next.Id != threadId || next.Revision != checked((expectedRevision ?? 0) + 1) ||
            expectedRevision is < 1 || next.Settings.TryGetProperty("ephemeral", out var ephemeral) &&
                ephemeral.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw new ArgumentException("Thread CAS requires exact durable identity and next revision.", nameof(document));
        var body = CodexMacOsAgentSqlite.Text(document.GetRawText());
        if (body.Length > CodexThreadStore.MaximumDocumentBytes)
            throw new ArgumentException("Thread document exceeds its storage bound.", nameof(document));
        return database.WriteAsync(sql =>
        {
            var previous = Header(sql, threadId);
            if (previous?.Revision != expectedRevision) return false;
            var edge = Edge(sql, threadId);
            if (previous is null)
            {
                // Ordinary roots and graph-only materialization share the same journal.
                // Named child creation must use the dedicated atomic spawn transaction.
                if (edge is not null && (edge.Identity is not null || !OpenPath(sql, edge.Root, threadId))) return false;
                if (next.ForkedFromId is { } source && Header(sql, source) is null) return false;
                if (next.Turns.LastOrDefault()?.Status == "inProgress" ||
                    next.Turns.Any(turn => IsMailTrigger(turn.TurnTrigger))) return false;
                InsertThread(sql, next, body);
                if (edge is null) ExpectOne(sql.Change("INSERT INTO agent_roots(id,revision) VALUES(?1,0);", threadId));
                return true;
            }

            if (next.Turns.Length < previous.Turns || next.Turns.Length > previous.Turns + 1) return false;
            if (edge is not null && !OpenPath(sql, edge.Root, threadId, allowArchivedRecipient: true)) return false;
            var last = next.Turns.LastOrDefault();
            if (next.Turns.Length > previous.Turns)
            {
                if (previous.Active || previous.Archived || last is not { Status: "inProgress" }) return false;
                if (edge?.Identity is { } child)
                {
                    // No ordinary turn admission may bypass a named child's granted wake.
                    if (!IsMailTrigger(last.TurnTrigger) || !CheckWakeAdmission(sql, edge.Root, child, last)) return false;
                }
                else if (IsMailTrigger(last.TurnTrigger)) return false;
            }
            if (!previous.Archived && next.ArchivedAt is not null)
            {
                var root = edge?.Root ?? threadId;
                var subtree = Descendants(threadId, TreeRows(sql, root));
                if (!Settled(sql, threadId) || subtree.Any(row => !Settled(sql, row.Edge.ChildThreadId))) return false;
            }
            ExpectOne(sql.Change("""
                UPDATE agent_threads SET revision=?2,archived=?3,turns=?4,active=?5,body=?6
                WHERE id=?1 AND revision=?7;
                """, threadId, next.Revision, Archived(next), next.Turns.Length, Active(next), body, previous.Revision));
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => database.DisposeAsync();

    private sealed record ThreadHeader(long Revision, bool Archived, int Turns, bool Active);
    private sealed record EdgeRow(Guid Root, CodexThreadSpawnEdge Edge, CodexNamedAgentIdentity? Identity);

    private static int Archived(CodexThreadDocument value) => value.ArchivedAt is null ? 0 : 1;
    private static int Active(CodexThreadDocument value) => value.Turns.LastOrDefault()?.Status == "inProgress" ? 1 : 0;
    private static bool IsMailTrigger(string? value) => value?.StartsWith("native:agent-mail:", StringComparison.Ordinal) == true;

    private static ThreadHeader? Header(Sql sql, Guid id) => sql.Query("""
        SELECT revision,archived,turns,active FROM agent_threads WHERE id=?1;
        """, row => new ThreadHeader(row.Number(0), row.Number(1) != 0, checked((int)row.Number(2)), row.Number(3) != 0), 1, id).SingleOrDefault();

    private static JsonElement? ReadBody(Sql sql, Guid id)
        => sql.Query("SELECT body FROM agent_threads WHERE id=?1;", row =>
        {
            var bytes = row.Blob(0);
            if (bytes.Length > CodexThreadStore.MaximumDocumentBytes) throw new InvalidDataException("Oversize native thread document.");
            using var json = JsonDocument.Parse(bytes);
            return (JsonElement?)json.RootElement.Clone();
        }, 1, id).SingleOrDefault();

    private CodexThreadDocument ReadDocument(Sql sql, Guid id)
    {
        var body = ReadBody(sql, id) ?? throw new KeyNotFoundException("Native thread not found.");
        var document = validator.ValidateSnapshot(body);
        if (document.Id != id) throw new InvalidDataException("Native thread identity mismatch.");
        return document;
    }

    private static void InsertThread(Sql sql, CodexThreadDocument value, byte[] body)
        => ExpectOne(sql.Change("""
            INSERT INTO agent_threads(id,revision,archived,turns,active,body) VALUES(?1,?2,?3,?4,?5,?6);
            """, value.Id, value.Revision, Archived(value), value.Turns.Length, Active(value), body));

    private static void ExpectOne(int changes)
    {
        if (changes != 1) throw new InvalidDataException("Unexpected SQLite agent transaction row count.");
    }

    private static void Id(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Agent identity must be nonempty.");
    }

    private static void Token(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid agent storage identity.");
        _ = CodexMacOsAgentSqlite.Text(value);
    }

    private void Account(string value)
    {
        if (!string.Equals(value, accountId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Agent storage account mismatch.");
    }

    private static long? RootRevision(Sql sql, Guid root)
        => sql.Query("SELECT revision FROM agent_roots WHERE id=?1;", row => (long?)row.Number(0), 1, root).SingleOrDefault();

    private static string Revision(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static long RequireRoot(Sql sql, Guid root)
        => RootRevision(sql, root) ?? throw new KeyNotFoundException("Agent graph root not found.");

    private static void AdvanceRoot(Sql sql, Guid root, long revision)
        => ExpectOne(sql.Change("UPDATE agent_roots SET revision=?2 WHERE id=?1 AND revision=?3;",
            root, checked(revision + 1), revision));

    private static EdgeRow ParseEdge(CodexMacOsAgentSqlite.Statement row)
    {
        var root = row.Id(0);
        var child = row.Id(1);
        var parent = row.Id(2);
        var status = (CodexThreadSpawnEdgeStatus)row.Number(3);
        var identity = JsonSerializer.Deserialize(row.Blob(4), CodexAgentStorageJson.Default.CodexNamedAgentIdentity);
        if (root == Guid.Empty || child == Guid.Empty || parent == Guid.Empty || !Enum.IsDefined(status) ||
            identity is not null && (identity.ThreadId != child || identity.ParentThreadId != parent || identity.RootThreadId != root))
            throw new InvalidDataException("Invalid persisted agent edge.");
        return new(root, new(parent, child, status), identity);
    }

    private static EdgeRow? Edge(Sql sql, Guid child)
        => sql.Query("SELECT root,child,parent,status,identity FROM agent_edges WHERE child=?1;",
            ParseEdge, 1, child).SingleOrDefault();

    private static EdgeRow[] TreeRows(Sql sql, Guid root)
        => sql.Query("SELECT root,child,parent,status,identity FROM agent_edges WHERE root=?1;",
            ParseEdge, MaximumEdges, root).ToArray();

    private static EdgeRow[] Descendants(Guid parent, EdgeRow[] rows)
    {
        var children = rows.ToLookup(row => row.Edge.ParentThreadId);
        var visited = new HashSet<Guid> { parent };
        var output = new List<EdgeRow>();
        var queue = new Queue<Guid>();
        queue.Enqueue(parent);
        while (queue.TryDequeue(out var current))
            foreach (var row in children[current])
            {
                if (!visited.Add(row.Edge.ChildThreadId)) throw new InvalidDataException("Persisted agent graph cycle.");
                output.Add(row);
                queue.Enqueue(row.Edge.ChildThreadId);
            }
        return output.ToArray();
    }

    private static bool OpenPath(Sql sql, Guid root, Guid recipient, bool allowArchivedRecipient = false)
    {
        var current = recipient;
        var seen = new HashSet<Guid>();
        while (true)
        {
            if (!seen.Add(current) || seen.Count > MaximumDepth + 1)
                throw new InvalidDataException("Invalid agent ancestor chain.");
            if (Header(sql, current) is { Archived: true } && !(allowArchivedRecipient && current == recipient)) return false;
            if (current == root) return RootRevision(sql, root) is not null;
            var edge = Edge(sql, current);
            if (edge is null || edge.Root != root || edge.Edge.Status != CodexThreadSpawnEdgeStatus.Open) return false;
            current = edge.Edge.ParentThreadId;
        }
    }

    private static bool Member(Sql sql, Guid root, Guid recipient)
        => recipient == root ? RootRevision(sql, root) is not null : Edge(sql, recipient)?.Root == root;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexNamedAgentIdentity))]
[JsonSerializable(typeof(CodexAgentMailbox))]
internal sealed partial class CodexAgentStorageJson : JsonSerializerContext;
