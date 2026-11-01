using System.Threading.Channels;
using System.Text;

namespace tryAGI.Codex;

public sealed record CodexFileSystemWatchStatus(string ConnectionId, string WatchId, bool OsWatcherActive, string? ErrorType);

internal sealed class CodexFileSystemWatches(
    Func<string, CodexFsChanged, CancellationToken, ValueTask> send,
    Func<string, FileSystemWatcher>? createWatcher = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<(string Connection, string Watch), Entry> entries = [];
    private bool disposed;
    // Rust sorts UTF-8 path bytes; UTF-16 ordinal order differs for supplementary
    // characters versus BMP characters above the surrogate range.
    internal static readonly IComparer<string> PathOrder = Comparer<string>.Create((left, right) =>
    {
        var a = left.EnumerateRunes().GetEnumerator(); var b = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = a.MoveNext(); var hasB = b.MoveNext();
            if (!hasA || !hasB) return hasA.CompareTo(hasB);
            var compared = a.Current.Value.CompareTo(b.Current.Value);
            if (compared != 0) return compared;
        }
    });

    internal async Task WatchAsync(string connection, string id, string path, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entries.ContainsKey((connection, id))) throw CodexFileSystemNative.Invalid("watchId already exists on this connection.");
            entries.Add((connection, id), new(connection, id, path, send, createWatcher));
        }
        finally { gate.Release(); }
    }

    internal async Task UnwatchAsync(string connection, string id, CancellationToken ct)
    {
        Entry? entry;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { entries.Remove((connection, id), out entry); }
        finally { gate.Release(); }
        if (entry is not null) await entry.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task ConnectionClosedAsync(string connection)
    {
        Entry[] owned;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var keys = entries.Keys.Where(k => k.Connection == connection).ToArray();
            owned = keys.Select(key => entries[key]).ToArray();
            foreach (var key in keys) entries.Remove(key);
        }
        finally { gate.Release(); }
        await Task.WhenAll(owned.Select(e => e.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    internal async Task<CodexFileSystemWatchStatus[]> StatusAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return entries.Values.Select(e => e.Status).ToArray(); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] all;
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; all = entries.Values.ToArray(); entries.Clear(); }
        finally { gate.Release(); }
        await Task.WhenAll(all.Select(e => e.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private sealed class Entry : IAsyncDisposable
    {
        private readonly string connection, id, requested, matched;
        private readonly Func<string, CodexFsChanged, CancellationToken, ValueTask> send;
        private readonly Func<string, FileSystemWatcher> createWatcher;
        private readonly Channel<string> changes = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task pump;
        private FileSystemWatcher? watcher;
        private string? actual, errorType;
        private bool lastExists, fallback;

        internal Entry(string connection, string id, string path,
            Func<string, CodexFsChanged, CancellationToken, ValueTask> send, Func<string, FileSystemWatcher>? createWatcher)
        {
            this.connection = connection; this.id = id; requested = path;
            matched = CodexFileSystem.Canonical(path); this.send = send;
            this.createWatcher = createWatcher ?? (root => new FileSystemWatcher(root));
            lastExists = Exists(matched); fallback = !lastExists;
            RefreshWatcher();
            pump = Task.Run(PumpAsync);
        }

        internal CodexFileSystemWatchStatus Status => new(connection, id, watcher is { EnableRaisingEvents: true }, errorType);
        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private void RefreshWatcher()
        {
            var root = Directory.Exists(matched) ? matched : Path.GetDirectoryName(matched);
            while (root is not null && !Directory.Exists(root)) root = Path.GetDirectoryName(root);
            if (root == actual && watcher is not null) return;
            watcher?.Dispose(); watcher = null; actual = root;
            if (root is null) { errorType = "NoExistingWatchAncestor"; return; }
            try
            {
                watcher = createWatcher(root);
                watcher.IncludeSubdirectories = false;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
                watcher.Renamed += Renamed;
                watcher.Error += (_, e) => errorType = e.GetException().GetType().Name;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            { errorType = error.GetType().Name; watcher?.Dispose(); watcher = null; }
        }
        private void Changed(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);
        private void Renamed(object sender, RenamedEventArgs e) { Enqueue(e.OldFullPath); Enqueue(e.FullPath); }
        private void Enqueue(string path)
        {
            if (!changes.Writer.TryWrite(path)) errorType = "NotificationQueueOverflow";
        }
        private string? Match(string path)
        {
            path = Path.GetFullPath(path);
            if (string.Equals(path, matched, CodexFileSystem.PathComparison))
            { lastExists = Exists(matched); return requested; }
            if (CodexFileSystem.IsWithin(matched, path))
            {
                var exists = Exists(matched);
                var notify = fallback || actual != matched ? exists || lastExists : true;
                lastExists = exists;
                return notify ? requested : null;
            }
            if (string.Equals(Path.GetDirectoryName(path), matched, CodexFileSystem.PathComparison))
            { lastExists = Exists(matched); return Path.Combine(requested, Path.GetFileName(path)); }
            return null;
        }
        private async Task PumpAsync()
        {
            var ct = lifetime.Token;
            try
            {
                while (await changes.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    var batch = new SortedSet<string>(PathOrder);
                    // Deadline is measured from the first event, never extended by later traffic.
                    using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    window.CancelAfter(TimeSpan.FromMilliseconds(200));
                    do
                    {
                        while (!window.IsCancellationRequested && changes.Reader.TryRead(out var path))
                            if (Match(path) is { } changed) batch.Add(changed);
                        RefreshWatcher();
                        try { if (!await changes.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false)) break; }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                    } while (!window.IsCancellationRequested);
                    ct.ThrowIfCancellationRequested();
                    if (batch.Count > 0) await send(connection, new(id, batch.ToArray()), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) { errorType = error.GetType().Name; }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            watcher?.Dispose();
            changes.Writer.TryComplete();
            await pump.ConfigureAwait(false);
            // Refresh may have already been admitted when cancellation arrived.
            // Dispose the final instance only after the pump has fully settled.
            watcher?.Dispose();
            lifetime.Dispose();
        }
    }
}
