using System.Runtime.CompilerServices;

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
