using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexRustRolloutImportTests
{
    private const string Account = "import-account";
    private const string Timestamp = "2026-11-27T12:00:00Z";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ValueTask Authorize(CodexRustRolloutImportAccess access, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Assert.Equal(Account, access.AccountId);
        return ValueTask.CompletedTask;
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string Record(string type, string payload, int? ordinal = null) =>
        "{\"timestamp\":\"" + Timestamp + "\",\"type\":\"" + type + "\",\"payload\":" + payload +
        (ordinal is { } value ? ",\"ordinal\":" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + "}";

    private static string Header(Guid thread, bool paginated = false) =>
        Record("session_meta", "{\"id\":\"" + thread + "\"" +
            (paginated ? ",\"history_mode\":\"paginated\"" : "") + "}", paginated ? 0 : null);

    private static string Event(string type, Guid turn, int? ordinal = null) =>
        Record("event_msg", "{\"type\":\"" + type + "\",\"turn_id\":\"" + turn + "\"}", ordinal);

    private static string User(string text, int? ordinal = null) =>
        Record("response_item", "{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" +
            JsonSerializer.Serialize(text) + "}]}", ordinal);

    private static string Assistant(string text, int? ordinal = null) =>
        Record("response_item", "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":" +
            JsonSerializer.Serialize(text) + "}]}", ordinal);

    private const string CallPayload = """{"type":"function_call","name":"lookup","call_id":"historical-call","arguments":"{\"query\":\"x\"}","id_allocation":{"sequence":7}}""";
    private const string ResultPayload = """{"type":"function_call_output","call_id":"historical-call","output":"historical result"}""";

    private static (CodexRustRolloutImportRequest Request, byte[] Bytes) Source(Guid thread, params string[] lines)
    {
        // Preserve mixed line separators, including a valid unterminated final line.
        var bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
        return (new(Guid.NewGuid(), thread, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
            "immutable-archive-123", Json("""{"model":"host-resolved","instructions":"host instructions"}""")), bytes);
    }

    [Fact]
    public async Task ImportsPlainTurnsAndExactProvenanceThroughSegmentedRoot()
    {
        var thread = Guid.NewGuid();
        var turn = Guid.NewGuid();
        var second = Guid.NewGuid();
        var (request, bytes) = Source(thread, Header(thread, true),
            Event("task_started", turn, 1),
            Record("turn_context", "{\"turn_id\":\"" + turn + "\",\"model\":\"source-model\",\"realtime_active\":false}", 2),
            User("hello", 3), Record("event_msg", """{"type":"user_message","message":"hello","local_images":[]}""", 4),
            Record("response_item", CallPayload, 5), Record("response_item", ResultPayload, 6),
            Record("event_msg", """{"type":"agent_message","message":"answer","phase":null,"memory_citation":null}""", 7),
            Assistant("answer", 8), Event("task_complete", turn, 9),
            Event("turn_started", second, 10), User("follow-up", 12), Assistant("second answer", 13),
            Event("turn_complete", second, 14));
        var roots = new Roots();
        var pages = new Pages();
        var storage = new CodexSegmentedThreadStorage(roots, pages, Account, indexHistory: true);
        var store = new CodexThreadStore(storage, Account);
        using var stream = new MemoryStream(bytes, writable: false);
        var imported = await store.ImportRustRolloutAsync(request, stream, Authorize, Ct);
        Assert.Equal(thread, imported.Id);
        Assert.Equal(1L, imported.Revision);
        Assert.Equal("host-resolved", imported.Settings.GetProperty("model").GetString());
        Assert.Equal(new[] { turn, second }, imported.Turns.Select(x => x.Id));
        Assert.All(imported.Turns, t => Assert.Equal("completed", t.Status));
        Assert.Single(imported.Turns[0].Input);
        Assert.Equal(3, imported.Turns[0].Output.Length);
        Assert.False(imported.Turns[0].Output[0].TryGetProperty("id_allocation", out _));
        var receipt = Assert.Single(imported.Turns[0].Dispatches);
        Assert.Equal("succeeded", receipt.State);
        Assert.Equal("historical result", receipt.Result!.Value.GetProperty("output").GetString());
        Assert.Equal("historical-call", Assert.Single(CodexThreadStore.DispatchedToolCallIds(imported)));
        var provenance = imported.RustRolloutImport!;
        Assert.Equal(request.OperationId, provenance.OperationId);
        Assert.Equal(request.LogicalSha256.ToLowerInvariant(), provenance.LogicalSha256);
        Assert.Equal(bytes.Length, provenance.Checkpoint.Cursor.ByteOffset);
        Assert.Equal(14UL, provenance.Checkpoint.LastOrdinal);
        Assert.Equal("codexThreadSegments", roots.Single.GetProperty("kind").GetString());
        Assert.Equal(1, roots.Publications);
        Assert.True(pages.Count > 0);
        Assert.Equal(bytes, stream.ToArray());
        var restored = await store.ReadAsync(thread, Ct);
        Assert.Equal(provenance, restored.RustRolloutImport);
        var recovered = await store.ReconcileRustRolloutImportAsync(request, Authorize, Ct);
        Assert.Equal(provenance, recovered!.RustRolloutImport);
        // Same operation returns the existing receipt without reading the source again.
        using var unreadable = new MemoryStream();
        unreadable.Dispose();
        Assert.Equal(provenance, (await store.ImportRustRolloutAsync(request, unreadable, Authorize, Ct)).RustRolloutImport);
        Assert.Equal(1, roots.Publications);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.ReconcileRustRolloutImportAsync(
            request with { OperationId = Guid.NewGuid() }, Authorize, Ct));
    }

    [Fact]
    public async Task LostRootCommitReceiptReconcilesWithoutReplayingImport()
    {
        var thread = Guid.NewGuid();
        var turn = Guid.NewGuid();
        var (request, bytes) = Source(thread, Header(thread), Event("task_started", turn), User("question"),
            Assistant("answer"), Event("task_complete", turn));
        var roots = new Roots { LoseReceipt = true };
        var pages = new Pages();
        var store = new CodexThreadStore(new CodexSegmentedThreadStorage(roots, pages, Account), Account);
        using var stream = new MemoryStream(bytes, writable: false);
        var error = await Assert.ThrowsAsync<CodexRustRolloutImportSettlementException>(() =>
            store.ImportRustRolloutAsync(request, stream, Authorize, Ct));
        Assert.Equal(request.OperationId, error.Request.OperationId);
        Assert.Equal(1, roots.Publications);
        var recovered = await store.ReconcileRustRolloutImportAsync(request, Authorize, Ct);
        Assert.NotNull(recovered);
        Assert.Equal(request.OperationId, recovered.RustRolloutImport!.OperationId);
        Assert.Single(recovered.Turns);
        Assert.Equal(1, roots.Publications);
    }

    [Fact]
    public async Task InterruptedTailRequiresHostConfirmationAndNeverCreatesActiveDispatch()
    {
        var thread = Guid.NewGuid();
        var turn = Guid.NewGuid();
        var (request, bytes) = Source(thread, Header(thread), Event("task_started", turn), User("question"),
            Record("response_item", CallPayload));
        var roots = new Roots();
        var store = new CodexThreadStore(roots, Account);
        using var stream = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsAsync<CodexRustRolloutCompatibilityException>(() =>
            store.ImportRustRolloutAsync(request, stream, Authorize, Ct));
        Assert.Equal(0, roots.Publications);
        var recovered = await store.ImportRustRolloutAsync(request with { RecoverInterruptedTail = true }, stream, Authorize, Ct);
        var imported = Assert.Single(recovered.Turns);
        Assert.Equal("interrupted", imported.Status);
        var fence = Assert.Single(imported.Dispatches);
        Assert.Equal("unknown", fence.State);
        Assert.Null(fence.Result);
        Assert.NotNull(imported.CompletedAt);
        Assert.True(recovered.RustRolloutImport!.RecoverInterruptedTail);
        Assert.Equal(1, roots.Publications);
    }

    [Theory]
    [InlineData("compacted", "{}")]
    [InlineData("retained_context", "{}")]
    [InlineData("realtime_item", "{}")]
    [InlineData("event_msg", "{\"type\":\"thread_rolled_back\",\"num_turns\":1}")]
    [InlineData("response_item", "{\"type\":\"additional_tools\",\"tools\":[]}")]
    [InlineData("response_item", "{\"type\":\"message\",\"role\":\"user\",\"role\":\"assistant\",\"content\":[]}")]
    [InlineData("event_msg", "{\"type\":\"agent_message\",\"message\":\"event-only text\"}")]
    [InlineData("event_msg", "{\"type\":\"user_message\",\"message\":\"question\",\"images\":[\"image\"]}")]
    public async Task UnsupportedOrAmbiguousRecordsNeverPublish(string type, string payload)
    {
        var thread = Guid.NewGuid();
        var turn = Guid.NewGuid();
        var (request, bytes) = Source(thread, Header(thread), Event("task_started", turn), User("question"),
            Record(type, payload), Event("task_complete", turn));
        var roots = new Roots();
        var store = new CodexThreadStore(roots, Account);
        using var stream = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsAsync<CodexRustRolloutCompatibilityException>(() =>
            store.ImportRustRolloutAsync(request, stream, Authorize, Ct));
        Assert.Equal(0, roots.Publications);
    }

    [Fact]
    public async Task DigestMismatchAndExistingThreadRefusePublication()
    {
        var thread = Guid.NewGuid();
        var turn = Guid.NewGuid();
        var (request, bytes) = Source(thread, Header(thread), Event("task_started", turn), User("question"),
            Assistant("answer"), Event("task_complete", turn));
        var roots = new Roots();
        var store = new CodexThreadStore(roots, Account);
        using var stream = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportRustRolloutAsync(
            request with { LogicalSha256 = new string('0', 64) }, stream, Authorize, Ct));
        Assert.Equal(0, roots.Publications);
        await store.CreateAsync(thread, Json("{}"), Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.ImportRustRolloutAsync(request, stream, Authorize, Ct));
        Assert.Equal(1, roots.Publications);
        Assert.Empty((await store.ReadAsync(thread, Ct)).Turns);
    }

    private sealed class Roots : ICodexThreadStorage
    {
        private readonly Dictionary<Guid, JsonElement> rows = [];
        internal int Publications { get; private set; }
        internal bool LoseReceipt { get; set; }
        internal JsonElement Single => rows.Values.Single();
        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(rows.TryGetValue(threadId, out var row) ? (JsonElement?)row.Clone() : null);
        }
        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            long? revision = rows.TryGetValue(threadId, out var row) ? row.GetProperty("revision").GetInt64() : null;
            if (revision != expectedRevision) return Task.FromResult(false);
            rows[threadId] = document.Clone();
            Publications++;
            if (LoseReceipt) { LoseReceipt = false; throw new IOException("Lost native root receipt."); }
            return Task.FromResult(true);
        }
    }

    private sealed class Pages : ICodexThreadSegmentStorage
    {
        private readonly Dictionary<(Guid, string), byte[]> rows = [];
        internal int Count => rows.Count;
        public Task<ReadOnlyMemory<byte>?> ReadSegmentAsync(Guid threadId, string sha256, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ReadOnlyMemory<byte>?>(rows.TryGetValue((threadId, sha256), out var bytes) ? bytes : null);
        }
        public Task PutSegmentAsync(Guid threadId, string sha256, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)));
            if (rows.TryGetValue((threadId, sha256), out var prior)) Assert.True(prior.AsSpan().SequenceEqual(bytes.Span));
            else rows.Add((threadId, sha256), bytes.ToArray());
            return Task.CompletedTask;
        }
    }
}
