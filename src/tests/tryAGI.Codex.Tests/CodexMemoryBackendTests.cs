using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexMemoryBackendTests
{
    private static readonly Guid Thread = Guid.Parse("1a99a409-a3a8-4e2a-a793-a1f4fa58f847");
    private static readonly CodexMemoryScope Scope = new("account", CodexMemoryVersion.V2);

    private static CodexMemoryBackend Create(Storage storage, Func<CodexMemoryPolicy>? policy = null,
        Func<CodexMemoryAccess, CancellationToken, ValueTask>? authorize = null,
        CodexMemoryScope? scope = null) =>
        new(storage, scope ?? Scope, Thread,
            policy ?? (() => new(true, true, true, CodexMemoryVersion.V2)),
            authorize ?? ((_, _) => ValueTask.CompletedTask));

    [Fact]
    public async Task ListUsesMetadataAndRevisionBoundKeyset()
    {
        var storage = new Storage();
        storage.Put("a.md", "first");
        storage.Put("b.md", "second");
        storage.Put("nested/c.md", "third");
        var backend = Create(storage);

        var first = await backend.ListAsync(maxResults: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("a.md", Assert.Single(first.Entries).Path);
        Assert.True(first.Truncated);
        Assert.Equal(0, storage.ContentReads);
        var second = await backend.ListAsync(cursor: first.NextCursor, maxResults: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("a.md", storage.LastListKey);
        Assert.Equal("b.md", Assert.Single(second.Entries).Path);
        Assert.Equal(0, storage.ContentReads);
        var third = await backend.ListAsync(cursor: second.NextCursor, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(third.Entries).IsDirectory);
        Assert.False(third.Truncated);

        storage.Put("d.md", "changed");
        await Assert.ThrowsAsync<ArgumentException>(() => backend.ListAsync(cursor: first.NextCursor, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadPreservesNewlinesAndChecksOneBasedBounds()
    {
        var storage = new Storage();
        storage.Put("a.md", "one\r\ntwo\nthree\n");
        var backend = Create(storage);
        var result = await backend.ReadAsync("a.md", 2, 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("two\n", result.Content);
        Assert.Equal(2, result.StartLineNumber);
        Assert.True(result.Truncated);
        Assert.Equal(1, storage.ContentReads);
        var tail = await backend.ReadAsync("a.md", 4, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tail.Content);
        Assert.False(tail.Truncated);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.ReadAsync("a.md", 5, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.ReadAsync("a.md", 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadTruncationKeepsUtf8Boundaries()
    {
        var storage = new Storage();
        storage.Put("a.md", "🙂abcdefgh🙂");
        var result = await Create(storage).ReadAsync("a.md", maxTokens: 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("🙂…2 tokens truncated…🙂", result.Content);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task SearchMinimalWindowsAndNormalizationMatchRust()
    {
        var storage = new Storage();
        storage.Put("a.md", "alpha\nALPHA\nBeta\nalpha beta\nother\n");
        var backend = Create(storage);
        var request = new CodexMemorySearchRequest(["alpha", "beta"], CodexMemoryMatchMode.AllWithinLines,
            LineCount: 3, CaseSensitive: false, ContextLines: 1);
        var result = await backend.SearchAsync(request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 2, 4 }, result.Matches.Select(x => x.MatchLineNumber).ToArray());
        Assert.Equal("alpha\nALPHA\nBeta\nalpha beta", result.Matches[0].Content);
        Assert.Equal(new[] { "alpha", "beta" }, result.Matches[0].MatchedQueries);

        storage.Put("b.md", "Open-AI\nnot a match\n");
        var normalized = await backend.SearchAsync(new(["open ai"], Path: "b.md",
            CaseSensitive: false, Normalized: true), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Open-AI", Assert.Single(normalized.Matches).Content);
        var sameLine = await backend.SearchAsync(request with { MatchMode = CodexMemoryMatchMode.AllOnSameLine }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, Assert.Single(sameLine.Matches).MatchLineNumber);
    }

    [Fact]
    public async Task SearchPagesDoNotReloadPrecedingFilesAndBindQueryScope()
    {
        var storage = new Storage();
        storage.Put("a.md", "needle\nneedle");
        storage.Put("b.md", "needle");
        storage.Put("c.md", "needle");
        var backend = Create(storage);
        var request = new CodexMemorySearchRequest(["needle"], MaxResults: 2);
        var first = await backend.SearchAsync(request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, first.Matches.Count);
        Assert.True(first.Truncated);
        var second = await backend.SearchAsync(request with { Cursor = first.NextCursor }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("a.md", storage.LastSearchKey);
        Assert.Equal(new[] { "b.md", "c.md" }, second.Matches.Select(x => x.Path).ToArray());
        Assert.False(second.Truncated);
        await Assert.ThrowsAsync<ArgumentException>(() => backend.SearchAsync(
            request with { Cursor = first.NextCursor, Queries = ["other"] }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(storage, scope: new("other", CodexMemoryVersion.V2))
            .SearchAsync(request with { Cursor = first.NextCursor }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/secret")]
    [InlineData("nested/.hidden")]
    [InlineData("C:/secret")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    public async Task RejectsUnsafePathsBeforeAccess(string path)
    {
        var storage = new Storage();
        var backend = Create(storage);
        await Assert.ThrowsAsync<ArgumentException>(() => backend.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task DisabledOrDeniedPolicyNeverTouchesStorage()
    {
        var storage = new Storage();
        var disabled = Create(storage, () => new(false, true, true, CodexMemoryVersion.V2));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disabled.ListAsync(cancellationToken: TestContext.Current.CancellationToken));
        var denied = Create(storage, authorize: (_, _) => throw new UnauthorizedAccessException());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ReadAsync("a.md", cancellationToken: TestContext.Current.CancellationToken));
        var wrongVersion = Create(storage, () => new(true, true, true, CodexMemoryVersion.V1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrongVersion.ListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, storage.OpenCount);
        Assert.Equal(0, storage.NoteWrites);
    }

    [Fact]
    public async Task NoteCreateIsAtomicAndAccountVersionScoped()
    {
        var storage = new Storage();
        var authorized = new List<CodexMemoryAccess>();
        var backend = Create(storage, authorize: (access, _) =>
        {
            authorized.Add(access);
            return ValueTask.CompletedTask;
        });
        const string filename = "2026-11-27T13-00-00-preference.md";
        await backend.AddAdHocNoteAsync(filename, "original", cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.AddAdHocNoteAsync(filename, "replacement", cancellationToken: TestContext.Current.CancellationToken));
        var path = "extensions/ad_hoc/notes/" + filename;
        Assert.Equal("original", (await backend.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken)).Content);
        Assert.Equal(new CodexMemoryAccess(Scope, Thread, "add_ad_hoc_note", path), authorized[0]);
        var other = Create(storage, scope: new("other", CodexMemoryVersion.V2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => other.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        await other.AddAdHocNoteAsync(filename, "independent", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("original", (await backend.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken)).Content);
        Assert.Equal("independent", (await other.ReadAsync(path, cancellationToken: TestContext.Current.CancellationToken)).Content);
    }

    [Theory]
    [InlineData("../note.md")]
    [InlineData("2026-11-27T13-00-00-UPPER.md")]
    [InlineData("2026-11-27T13-00-00-.md")]
    [InlineData("2026-11-27T13-00-00-a/b.md")]
    public async Task InvalidNoteNamesNeverWrite(string filename)
    {
        var storage = new Storage();
        await Assert.ThrowsAsync<ArgumentException>(() => Create(storage).AddAdHocNoteAsync(filename, "note", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, storage.NoteWrites);
    }

    [Fact]
    public async Task StorageCannotReturnAnUnrequestedPath()
    {
        var storage = new Storage { WrongFile = true };
        storage.Put("a.md", "sensitive");
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(storage).ReadAsync("a.md", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static CodexThreadDocument Document(string settings = "{}") =>
        new(1, Thread, Scope.AccountId, 1, Json(settings), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);

    private static CodexToolCall MemoryCall(string name, string input) =>
        new("memory-call", name, input, CodexToolCallKind.Function) { Namespace = "memories" };

    [Fact]
    public async Task SummaryDoesNotRequireDedicatedToolsAndNeverReadsWhenDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        storage.Put("memory_summary.md", "  original \"quoted\" summary  ");
        var policy = new CodexMemoryPolicy(false, true, false, CodexMemoryVersion.V2);
        var accesses = new List<CodexMemoryAccess>();
        var backend = Create(storage, () => policy, (access, _) =>
        {
            accesses.Add(access);
            return ValueTask.CompletedTask;
        });
        var binding = new CodexMemoryTurnBinding(backend);
        var original = new CodexResponseOptions("test", "trusted");
        Assert.Same(original, await binding.PrepareAsync(Document(), original, ct));
        Assert.Empty(accesses);
        Assert.Equal(0, storage.OpenCount);

        policy = policy.WithConfiguration(true, true, false);
        var composed = await binding.PrepareAsync(Document(), original, ct);
        Assert.Empty(composed.Tools);
        Assert.Equal("trusted", original.Instructions);
        var data = composed.Instructions.Split("MEMORY_SUMMARY_JSON = ", StringSplitOptions.None)[1];
        Assert.Equal("original \"quoted\" summary", Json(data).GetString());
        Assert.Equal("summary", Assert.Single(accesses).Operation);
        Assert.Equal(1, storage.ContentReads);
    }

    [Fact]
    public async Task MissingSummaryAllowsReadToolsButOptOutAndEphemeralRemainReadFree()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var policy = new CodexMemoryPolicy(true, true, true, CodexMemoryVersion.V2);
        var binding = new CodexMemoryTurnBinding(Create(storage, () => policy));
        var original = new CodexResponseOptions("test", "trusted");
        var composed = await binding.PrepareAsync(Document(), original, ct);
        Assert.Equal("trusted", composed.Instructions);
        var ns = Assert.Single(composed.Tools);
        Assert.Equal("memories", ns.GetProperty("name").GetString());
        Assert.Equal(new[] { "list", "read", "search" },
            ns.GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());
        var reads = storage.OpenCount;
        Assert.Same(original, await binding.PrepareAsync(Document("{\"ephemeral\":true}"), original, ct));
        Assert.Equal(reads, storage.OpenCount);
        policy = policy.WithConfiguration(true, false, true);
        Assert.Same(original, await binding.PrepareAsync(Document(), original, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => binding.ExecuteAsync(MemoryCall("list", "{}"), ct));
        Assert.Equal(reads, storage.OpenCount);
    }

    [Fact]
    public async Task ForeignThreadOrNamespaceCollisionCannotReadSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var binding = new CodexMemoryTurnBinding(Create(storage));
        var options = new CodexResponseOptions("test", "trusted");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => binding.PrepareAsync(
            Document() with { AccountId = "foreign" }, options, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => binding.PrepareAsync(
            Document() with { Id = Guid.NewGuid() }, options, ct));
        var host = Json("""{"type":"namespace","name":"memories","description":"host","tools":[{"type":"function","name":"list","parameters":{"type":"object"}}]}""");
        await Assert.ThrowsAsync<ArgumentException>(() => binding.PrepareAsync(Document(), options with { Tools = [host] }, ct));
        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task NativeToolsValidateRustShapesAndRequireExactNoteAuthorization()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        storage.Put("a.md", "alpha\nbeta\n");
        var backend = Create(storage);
        var readOnly = new CodexMemoryTurnBinding(backend);
        var read = Json((await readOnly.ExecuteAsync(MemoryCall("read", """{"path":"a.md","line_offset":2}"""), ct)).Text);
        Assert.Equal("beta\n", read.GetProperty("content").GetString());
        Assert.Equal(2, read.GetProperty("start_line_number").GetInt32());
        var search = Json((await readOnly.ExecuteAsync(MemoryCall("search",
            """{"queries":["alpha","beta"],"match_mode":{"type":"all_within_lines","line_count":2}}"""), ct)).Text);
        Assert.Equal("all_within_lines", search.GetProperty("match_mode").GetProperty("type").GetString());
        Assert.Equal("alpha\nbeta", search.GetProperty("matches")[0].GetProperty("content").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => readOnly.ExecuteAsync(MemoryCall("read",
            """{"path":"a.md","path":"b.md"}"""), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => readOnly.ExecuteAsync(MemoryCall("search",
            """{"query":"alpha"}"""), ct));
        var note = MemoryCall("add_ad_hoc_note", """{"filename":"2026-11-27T13-00-00-note.md","note":"approved"}""");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => readOnly.ExecuteAsync(note, ct));
        var denied = new CodexMemoryTurnBinding(backend, (_, _) => throw new UnauthorizedAccessException());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ExecuteAsync(note, ct));
        Assert.Equal(0, storage.NoteWrites);
        var authorizations = new List<CodexToolCall>();
        var writable = new CodexMemoryTurnBinding(backend, (call, _) =>
        {
            authorizations.Add(call);
            return ValueTask.CompletedTask;
        });
        var composed = await writable.PrepareAsync(Document(), new("test", "trusted"), ct);
        Assert.Equal(4, Assert.Single(composed.Tools).GetProperty("tools").GetArrayLength());
        Assert.Equal("{}", (await writable.ExecuteAsync(note, ct)).Text);
        Assert.Same(note, Assert.Single(authorizations));
        Assert.Equal(1, storage.NoteWrites);
    }

    [Fact]
    public async Task NativeThreadUsesMemoryToolsThroughExistingDurableAdmission()
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new Storage();
        memory.Put("memory_summary.md", "Prior project context");
        memory.Put("a.md", "Historical evidence");
        var binding = new CodexMemoryTurnBinding(Create(memory));
        var journal = new CodexThreadStore(new ThreadStorage(), Scope.AccountId);
        var thread = await journal.CreateAsync(Thread, Json("{}"), ct);
        var turn = Guid.NewGuid();
        var server = new MemoryServer();
        var agent = new CodexThreadAgent(journal, new Credentials(), new Budget(),
            clientFactory: (credentials, observer) => new(credentials, handler: server, usageObserver: observer));
        var original = new CodexResponseOptions("test", "trusted") { Identity = new("session", Thread.ToString(), turn.ToString()) };
        var run = await binding.RunWithMemoryAsync(agent, thread, turn, "owner", original,
            [Json("""{"role":"user","content":"Find prior evidence"}""")],
            (_, _) => throw new InvalidOperationException("Native memory must not escape to the host fallback."),
            cancellationToken: ct);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("Prior project context", server.Requests[0].GetProperty("instructions").GetString());
        Assert.Equal("memories", server.Requests[0].GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Contains("Historical evidence", server.Requests[1].GetProperty("input").GetRawText());
        Assert.Equal("trusted", original.Instructions);
        Assert.Equal(2, memory.ContentReads);
        var stored = Assert.Single(run.Thread.Turns);
        Assert.Equal("completed", stored.Status);
        Assert.Contains(stored.Dispatches, d => d.Kind == "tool" && d.CallId == "memory-call");
    }

    private sealed class ThreadStorage : ICodexThreadStorage
    {
        private JsonElement? row;
        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(row?.Clone());
        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement body, CancellationToken cancellationToken)
        {
            if (row?.GetProperty("revision").GetInt64() != revision) return Task.FromResult(false);
            row = body.Clone();
            return Task.FromResult(true);
        }
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("test", Scope.AccountId));
    }

    private sealed class Budget : ICodexUsageObserver
    {
        public Task ReserveAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) => Task.CompletedTask;
        public Task IncompleteAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public Task NotDispatchedAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MemoryServer : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(doc.RootElement.Clone());
            var item = Requests.Count == 1
                ? JsonNode.Parse("""{"type":"function_call","name":"read","namespace":"memories","call_id":"memory-call","arguments":"{\"path\":\"a.md\"}"}""")
                : JsonNode.Parse("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done"}]}""");
            var completed = new JsonObject
            {
                ["type"] = "response.completed",
                ["response"] = new JsonObject
                {
                    ["id"] = "resp_" + Requests.Count, ["status"] = "completed", ["model"] = "test",
                    ["output"] = new JsonArray(item), ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
                },
            };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + completed.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class Storage : ICodexMemoryStorage
    {
        private readonly Dictionary<CodexMemoryScope, SortedDictionary<string, string>> namespaces = [];
        private int revision;
        public int OpenCount { get; private set; }
        public int ContentReads { get; private set; }
        public int NoteWrites { get; private set; }
        public string? LastListKey { get; private set; }
        public string? LastSearchKey { get; private set; }
        public bool WrongFile { get; init; }

        private SortedDictionary<string, string> Files(CodexMemoryScope scope)
        {
            if (!namespaces.TryGetValue(scope, out var result))
                namespaces.Add(scope, result = new(StringComparer.Ordinal));
            return result;
        }

        public void Put(string path, string content)
        {
            Files(Scope)[path] = content;
            revision++;
        }

        public ValueTask<ICodexMemorySnapshot> OpenReadAsync(CodexMemoryAccess access, CancellationToken cancellationToken)
        {
            OpenCount++;
            return ValueTask.FromResult<ICodexMemorySnapshot>(
                new Snapshot(this, new(Files(access.Scope), StringComparer.Ordinal), revision));
        }

        public ValueTask<bool> CreateNoteAsync(CodexMemoryAccess access, string content, CancellationToken cancellationToken)
        {
            NoteWrites++;
            if (!Files(access.Scope).TryAdd(access.Path, content)) return ValueTask.FromResult(false);
            revision++;
            return ValueTask.FromResult(true);
        }

        private sealed class Snapshot(Storage owner, SortedDictionary<string, string> files, int revision) : ICodexMemorySnapshot
        {
            public string Revision => revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

            public ValueTask<IReadOnlyList<CodexMemoryEntry>> ListAsync(string path, string? afterPath,
                int limit, CancellationToken cancellationToken)
            {
                owner.LastListKey = afterPath;
                var prefix = path.Length == 0 ? "" : path + "/";
                var entries = new SortedDictionary<string, bool>(StringComparer.Ordinal);
                if (files.ContainsKey(path)) entries.Add(path, false);
                else
                {
                    foreach (var file in files.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        var nextSlash = file.IndexOf('/', prefix.Length);
                        entries[file[..(nextSlash < 0 ? file.Length : nextSlash)]] = nextSlash >= 0;
                    }
                }
                IReadOnlyList<CodexMemoryEntry> result = entries.Where(x => afterPath is null ||
                    string.CompareOrdinal(x.Key, afterPath) > 0).Take(limit)
                    .Select(x => new CodexMemoryEntry(x.Key, x.Value)).ToArray();
                return ValueTask.FromResult(result);
            }

            public ValueTask<CodexMemoryFile> ReadAsync(string path, int maxBytes, CancellationToken cancellationToken)
            {
                owner.ContentReads++;
                return ValueTask.FromResult(new CodexMemoryFile(owner.WrongFile ? "other.md" : path, files[path]));
            }

            public async IAsyncEnumerable<CodexMemoryFile> EnumerateFilesAsync(string path, string? atOrAfterPath,
                int maxBytes, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                owner.LastSearchKey = atOrAfterPath;
                foreach (var (key, content) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((path.Length == 0 || key == path || key.StartsWith(path + "/", StringComparison.Ordinal)) &&
                        (atOrAfterPath is null || string.CompareOrdinal(key, atOrAfterPath) >= 0))
                    {
                        owner.ContentReads++;
                        yield return new(key, content);
                    }
                }
                await Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
