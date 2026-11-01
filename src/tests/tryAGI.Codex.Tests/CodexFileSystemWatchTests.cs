using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexFileSystemWatchTests : IAsyncLifetime
{
    private readonly string root = Directory.CreateTempSubdirectory("adv-native-watch-").FullName;
    private readonly Channel<Notice> notices = Channel.CreateUnbounded<Notice>();
    private readonly CodexFileSystem fs;
    private sealed record Notice(string Connection, CodexFsChanged Body);
    public CodexFileSystemWatchTests()
    {
        fs = new((_, _) => ValueTask.CompletedTask, (connection, body, _) =>
            { notices.Writer.TryWrite(new(connection, body)); return ValueTask.CompletedTask; });
    }
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() { await fs.DisposeAsync(); Directory.Delete(root, true); }
    private Task<JsonElement> Watch(string owner, string id, string path) => fs.HandleAsync(owner, "fs/watch",
        JsonSerializer.SerializeToElement(new CodexFsWatch(path, id), CodexFileSystemJson.Default.CodexFsWatch));
    private Task<JsonElement> Unwatch(string owner, string id) => fs.HandleAsync(owner, "fs/unwatch",
        JsonSerializer.SerializeToElement(new CodexFsUnwatch(id), CodexFileSystemJson.Default.CodexFsUnwatch));
    private async Task<Notice> Next() => await notices.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public void NotificationPathsUseRustUtf8OrderingRatherThanUtf16Ordering()
    {
        var paths = new SortedSet<string>(["😀", "\ue000", "a", "😀"], CodexFileSystemWatches.PathOrder);
        Assert.Equal(["a", "\ue000", "😀"], paths);
    }

    [Fact]
    public async Task IDsAreConnectionOwnedAndForeignUnwatchIsNoOp()
    {
        var response = await Watch("a", "same", root); Assert.Equal(root, response.GetProperty("path").GetString());
        await Watch("b", "same", root);
        Assert.Equal(-32600, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Watch("a", "same", root))).Code);
        await Unwatch("foreign", "same"); Assert.Equal(2, (await fs.WatchStatusAsync(TestContext.Current.CancellationToken)).Length);
        await Unwatch("a", "same");
        Assert.Equal("b", Assert.Single(await fs.WatchStatusAsync(TestContext.Current.CancellationToken)).ConnectionId);
    }

    [Fact]
    public async Task RealOsEventsAreOwnerOnlySortedAndDeduplicated()
    {
        await Watch("a", "directory", root);
        foreach (var name in new[] { "z", "a", "z" }) await File.WriteAllTextAsync(Path.Combine(root, name), "x", TestContext.Current.CancellationToken);
        var notice = await Next();
        Assert.Equal("a", notice.Connection); Assert.Equal("directory", notice.Body.WatchId);
        Assert.NotEmpty(notice.Body.ChangedPaths);
        Assert.Equal(notice.Body.ChangedPaths.Distinct().Order(StringComparer.Ordinal), notice.Body.ChangedPaths);
        Assert.All(notice.Body.ChangedPaths, p => Assert.Equal(root, Path.GetDirectoryName(p)));
        Assert.True(Assert.Single(await fs.WatchStatusAsync(TestContext.Current.CancellationToken)).OsWatcherActive);
    }

    [Fact]
    public async Task FileWatchFiltersUnrelatedSiblings()
    {
        var path = Path.Combine(root, "watched"); await File.WriteAllTextAsync(path, "initial", TestContext.Current.CancellationToken);
        await Watch("a", "file", path);
        await File.WriteAllTextAsync(Path.Combine(root, "unrelated"), "x", TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken); Assert.False(notices.Reader.TryRead(out _));
        await File.AppendAllTextAsync(path, "changed", TestContext.Current.CancellationToken);
        Assert.Equal([path], (await Next()).Body.ChangedPaths);
    }

    [Fact]
    public async Task MissingTargetMovesFromAncestorWithoutBroadRecursiveWatch()
    {
        var path = Path.Combine(root, "first", "second", "file");
        await Watch("a", "missing", path);
        Directory.CreateDirectory(Path.Combine(root, "first"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(root, "first", "second"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(notices.Reader.TryRead(out _));
        await File.WriteAllTextAsync(path, "created", TestContext.Current.CancellationToken);
        Assert.Equal([path], (await Next()).Body.ChangedPaths);
        await Unwatch("a", "missing");
        Assert.Empty(await fs.WatchStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnwatchAwaitsInFlightCallbackAndSuppressesQueuedNotifications()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        await using var controlled = new CodexFileSystem((_, _) => ValueTask.CompletedTask, async (_, _, _) =>
        { Interlocked.Increment(ref deliveries); started.TrySetResult(); await release.Task; });
        await controlled.HandleAsync("a", "fs/watch", JsonSerializer.SerializeToElement(new CodexFsWatch(root, "w"), CodexFileSystemJson.Default.CodexFsWatch), TestContext.Current.CancellationToken);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "trigger"), "x", TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var stopping = controlled.HandleAsync("a", "fs/unwatch", JsonSerializer.SerializeToElement(new CodexFsUnwatch("w"), CodexFileSystemJson.Default.CodexFsUnwatch), TestContext.Current.CancellationToken);
            Assert.False(stopping.IsCompleted);
            release.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "later"), "x", TestContext.Current.CancellationToken); await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.Equal(1, deliveries);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task DisconnectRemovesOnlyThatConnectionsRegistrations()
    {
        await Watch("a", "one", root); await Watch("a", "two", root); await Watch("b", "one", root);
        await fs.ConnectionClosedAsync("a");
        Assert.Equal("b", Assert.Single(await fs.WatchStatusAsync(TestContext.Current.CancellationToken)).ConnectionId);
        await File.WriteAllTextAsync(Path.Combine(root, "event"), "x", TestContext.Current.CancellationToken);
        Assert.Equal("b", (await Next()).Connection);
        await fs.DisposeAsync(); Assert.Empty(await fs.WatchStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedOsConstructionRetainsRegistrationWithoutClaimingWorkingWatcher()
    {
        await using var failed = new CodexFileSystemWatches((_, _, _) => throw new InvalidOperationException("No event expected"),
            _ => throw new PlatformNotSupportedException());
        await failed.WatchAsync("a", "noop", root, TestContext.Current.CancellationToken);
        var status = Assert.Single(await failed.StatusAsync(TestContext.Current.CancellationToken));
        Assert.False(status.OsWatcherActive); Assert.Equal(nameof(PlatformNotSupportedException), status.ErrorType);
        await failed.UnwatchAsync("a", "noop", TestContext.Current.CancellationToken); Assert.Empty(await failed.StatusAsync(TestContext.Current.CancellationToken));
    }
}
