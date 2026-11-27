using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexFileSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Root(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));

    private static Task<JsonElement> Search(CodexFileSearch host, string query, string[] roots, string? token = null) =>
        host.HandleAsync("fuzzyFileSearch", JsonSerializer.SerializeToElement(new { query, roots, cancellationToken = token }), Ct);
    private static Task<JsonElement> Start(CodexFileSearch host, string id, params string[] roots) =>
        host.HandleAsync("fuzzyFileSearch/sessionStart", JsonSerializer.SerializeToElement(new { sessionId = id, roots }), Ct);
    private static Task<JsonElement> Update(CodexFileSearch host, string id, string query) =>
        host.HandleAsync("fuzzyFileSearch/sessionUpdate", JsonSerializer.SerializeToElement(new { sessionId = id, query }), Ct);
    private static Task<JsonElement> Stop(CodexFileSearch host, string id) =>
        host.HandleAsync("fuzzyFileSearch/sessionStop", JsonSerializer.SerializeToElement(new { sessionId = id }), Ct);
    private static JsonElement[] Files(JsonElement value) => value.GetProperty("files").EnumerateArray().ToArray();
    private static string PathOf(JsonElement value) => value.GetProperty("path").GetString()!;

    [Fact]
    public async Task RealStableSourceReturnsPinnedWireFieldsAndSkipsIgnoredAndExternalLinks()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "ignored"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(root, "abcde"), "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "abexy"), "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "abce"), "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "ignored", "abe-secret"), "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, ".git", "abe-secret"), "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "abe-outside"), "x", Ct);
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), fixture.Outside);

        await using var host = Host(fixture.Source);
        var files = Files(await Search(host, "ABE", [root]));
        Assert.Equal(["abexy", Path.Combine("sub", "abce"), "abcde"], files.Select(PathOf));
        Assert.All(files, x =>
        {
            Assert.Equal(root, x.GetProperty("root").GetString());
            Assert.Equal("file", x.GetProperty("match_type").GetString());
            Assert.Equal(Path.GetFileName(PathOf(x)), x.GetProperty("file_name").GetString());
            Assert.True(x.GetProperty("score").GetUInt32() > 65536);
            Assert.False(x.TryGetProperty("fileName", out _));
            var indices = x.GetProperty("indices").EnumerateArray().Select(v => v.GetUInt32()).ToArray();
            Assert.Equal(indices.Order().Distinct(), indices);
        });
        Assert.Equal(0, fixture.ActiveLeases);
        var directory = Assert.Single(Files(await Search(host, "sub", [root])), x => x.GetProperty("match_type").GetString() == "directory");
        Assert.Equal("directory", directory.GetProperty("match_type").GetString());
        Assert.Equal("sub", PathOf(directory));
        Assert.Equal(0, fixture.ActiveLeases);
    }

    [Fact]
    public async Task SourceAuthorizationPrecedesTraversalAndUnauthorizedRootsAreNotInspected()
    {
        using var fixture = new Fixture();
        await using var host = Host(fixture.Source);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Search(host, "x", [fixture.Outside]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Start(host, "foreign", fixture.Outside));
        Assert.Equal(0, fixture.ActiveLeases);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Update(host, "foreign", "x"));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Search(host, "x", ["relative"]));
    }

    [Fact]
    public async Task ResultsAreBoundedStableAndUseGraphemePositions()
    {
        var root = Root("search-ranking");
        var source = new Source(async (scope, ct) =>
        {
            await Task.Yield();
            return Enumerable.Range(0, 100).Reverse().Select(x => new CodexFileSearchEntry($"f{x:D3}", false)).ToArray();
        });
        await using var host = Host(source);
        var result = Files(await Search(host, "f", [root, root]));
        Assert.Equal(50, result.Length);
        Assert.Equal(Enumerable.Range(0, 50).Select(x => $"f{x:D3}"), result.Select(PathOf));
        Assert.Equal(1, source.Enumerations);

        var unicode = new Source((_, _) => Task.FromResult<CodexFileSearchEntry[]>(
            [new("😀" + "e\u0301x", false)]));
        await using var other = Host(unicode);
        var match = Assert.Single(Files(await Search(other, "ÉX", [root])));
        Assert.Equal(new uint[] { 1, 2 }, match.GetProperty("indices").EnumerateArray().Select(x => x.GetUInt32()));
    }

    [Fact]
    public async Task EscapingSourcePathsAndScanBudgetFailClosed()
    {
        var root = Root("search-bound");
        await using var escaped = Host(new Source((_, _) =>
            Task.FromResult<CodexFileSearchEntry[]>([new(Path.Combine("..", "secret"), false)])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Search(escaped, "secret", [root]));
        await using var bounded = Host(new Source((_, _) =>
            Task.FromResult<CodexFileSearchEntry[]>([new("a", false), new("b", false)])), maximumEntries: 1);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Search(bounded, "a", [root]));
    }

    [Fact]
    public async Task SessionUpdatesAreIndependentAndClearSendsEmptySnapshot()
    {
        var a = Root("search-a");
        var b = Root("search-b");
        var source = new Source((scope, _) => Task.FromResult<CodexFileSearchEntry[]>(
            [new(scope.Root == a ? "alpha.txt" : "beta.txt", false)]));
        var notices = Channel.CreateUnbounded<Notice>();
        await using var host = Host(source, notices);
        await Start(host, "a", a);
        await Start(host, "b", b);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Start(host, "a", b));

        await Update(host, "a", "ALP");
        var first = await Next(notices);
        Assert.Equal("fuzzyFileSearch/sessionUpdated", first.Method);
        Assert.Equal("a", first.Body.GetProperty("sessionId").GetString());
        Assert.Equal("alpha.txt", PathOf(Assert.Single(Files(first.Body))));
        Assert.Equal("fuzzyFileSearch/sessionCompleted", (await Next(notices)).Method);
        await Update(host, "b", "bet");
        var second = await Next(notices);
        Assert.Equal("b", second.Body.GetProperty("sessionId").GetString());
        Assert.Equal("beta.txt", PathOf(Assert.Single(Files(second.Body))));
        Assert.Equal("fuzzyFileSearch/sessionCompleted", (await Next(notices)).Method);

        await Update(host, "a", "");
        var empty = await Next(notices);
        Assert.Equal("", empty.Body.GetProperty("query").GetString());
        Assert.Empty(Files(empty.Body));
        Assert.Equal("fuzzyFileSearch/sessionCompleted", (await Next(notices)).Method);
        await Stop(host, "a");
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Update(host, "a", "alp"));
        await Stop(host, "b");
    }

    [Fact]
    public async Task RevokedRootCannotBeReusedByExistingSession()
    {
        var root = Root("search-revoked");
        var source = new Source((_, _) => Task.FromResult<CodexFileSearchEntry[]>([new("alpha", false)]));
        var notices = Channel.CreateUnbounded<Notice>();
        await using var host = Host(source, notices);
        await Start(host, "a", root);
        await Update(host, "a", "alp");
        _ = await Next(notices);
        _ = await Next(notices);
        source.Denied = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Update(host, "a", "alp"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Update(host, "a", "beta"));
        Assert.Equal(1, source.Enumerations);
        Assert.False(notices.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NewQueryCancelsAndDrainsOldTraversalBeforePublishing()
    {
        var root = Root("search-generation");
        var source = new StreamingSource();
        var notices = Channel.CreateUnbounded<Notice>();
        await using var host = Host(source, notices);
        await Start(host, "a", root);
        await Update(host, "a", "old");
        var interim = await Next(notices);
        Assert.Equal("old", interim.Body.GetProperty("query").GetString());
        Assert.Equal(50, Files(interim.Body).Length);
        await source.Waiting.Task.WaitAsync(Ct);
        await Update(host, "a", "new");
        Assert.True(source.Released.Task.IsCompletedSuccessfully);
        var current = await Next(notices);
        Assert.Equal("new", current.Body.GetProperty("query").GetString());
        Assert.Equal("new.txt", PathOf(Assert.Single(Files(current.Body))));
        Assert.Equal("fuzzyFileSearch/sessionCompleted", (await Next(notices)).Method);
        await Stop(host, "a");
        Assert.False(notices.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StopDrainsInFlightNotificationAndForeignConnectionCannotControlSession()
    {
        var root = Root("search-stop");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source((_, _) => Task.FromResult<CodexFileSearchEntry[]>([new("alpha", false)]));
        await using var host = new CodexFileSearch("owner", source, async (_, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { released.TrySetResult(); }
        }, (_, _) => throw new InvalidOperationException("Unexpected failure."));
        await using var foreign = Host(source);
        await Start(host, "a", root);
        await Update(host, "a", "alp");
        await entered.Task.WaitAsync(Ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Stop(foreign, "a"));
        await Stop(host, "a");
        Assert.True(released.Task.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Update(host, "a", "alp"));
    }

    [Fact]
    public async Task ReusedOneShotCancellationTokenCancelsEarlierRequest()
    {
        var root = Root("search-token");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var source = new Source(async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return [new("new.txt", false)];
        });
        await using var host = Host(source);
        var first = Search(host, "old", [root], "token");
        await entered.Task.WaitAsync(Ct);
        var second = await Search(host, "new", [root], "token");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("new.txt", PathOf(Assert.Single(Files(second))));
    }

    [Fact]
    public async Task SessionFailureDoesNotEmitSuccessfulCompletion()
    {
        var source = new Source((_, _) => throw new IOException("Synthetic enumeration failure."));
        var notices = Channel.CreateUnbounded<Notice>();
        var failed = new TaskCompletionSource<CodexFileSearchFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new CodexFileSearch("owner", source,
            (method, body, _) => { notices.Writer.TryWrite(new(method, body)); return ValueTask.CompletedTask; },
            (error, _) => { failed.TrySetResult(error); return ValueTask.CompletedTask; });
        await Start(host, "a", Root("search-failure"));
        await Update(host, "a", "a");
        Assert.IsType<IOException>((await failed.Task.WaitAsync(Ct)).Error);
        await Stop(host, "a");
        Assert.False(notices.Reader.TryRead(out _));
    }

    private sealed record Notice(string Method, JsonElement Body);
    private static Task<Notice> Next(Channel<Notice> notices) =>
        notices.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

    private static CodexFileSearch Host(ICodexFileSearchSource source, Channel<Notice>? notices = null, int maximumEntries = 100000) =>
        new("owner", source, (method, body, _) =>
        {
            notices?.Writer.TryWrite(new(method, body));
            return ValueTask.CompletedTask;
        }, (error, _) => throw new InvalidOperationException("Unexpected search failure.", error.Error), maximumEntries);

    private sealed class Source(Func<CodexFileSearchRoot, CancellationToken, Task<CodexFileSearchEntry[]>> entries)
        : ICodexFileSearchSource
    {
        internal bool Denied { get; set; }
        private int enumerations;
        internal int Enumerations => Volatile.Read(ref enumerations);
        public ValueTask AuthorizeAsync(CodexFileSearchRoot root, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Denied) throw new UnauthorizedAccessException();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<CodexFileSearchEntry> EnumerateAsync(CodexFileSearchRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await AuthorizeAsync(root, cancellationToken);
            Interlocked.Increment(ref enumerations);
            foreach (var item in await entries(root, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private sealed class StreamingSource : ICodexFileSearchSource
    {
        private int enumerations;
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask AuthorizeAsync(CodexFileSearchRoot root, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<CodexFileSearchEntry> EnumerateAsync(CodexFileSearchRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref enumerations) == 1)
            {
                try
                {
                    for (var i = 0; i < 256; i++) yield return new($"old{i:D3}", false);
                    Waiting.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally { Released.TrySetResult(); }
            }
            else yield return new("new.txt", false);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string outer = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-search-").FullName);
        private int leases;
        internal int ActiveLeases => Volatile.Read(ref leases);
        internal string Root { get; }
        internal string Outside { get; }
        internal CodexStableDirectoryFileSearchSource Source { get; }

        internal Fixture()
        {
            Root = Path.Combine(outer, "root");
            Outside = Path.Combine(outer, "outside");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Outside);
            Source = new((scope, _) =>
            {
                if (scope.ConnectionId != "owner" || scope.Root != Root) throw new UnauthorizedAccessException();
                Interlocked.Increment(ref leases);
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
            }, (_, entry, _) =>
            {
                Assert.True(ActiveLeases > 0);
                return ValueTask.FromResult(entry.Path.Split(Path.DirectorySeparatorChar)[0] == "ignored");
            });
        }

        private sealed class Lease(Fixture fixture) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref fixture.leases);
                return ValueTask.CompletedTask;
            }
        }
        public void Dispose() => Directory.Delete(outer, true);
    }
}
