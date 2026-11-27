using System.Runtime.CompilerServices;

namespace tryAGI.Codex;

/// <summary>Explicit local search source for a host-protected stable workspace. The lease
/// must authorize the entire traversal, prevent unauthorized topology/alias replacement
/// and remain held until enumeration finishes. This is not an OS-level race-safe walker
/// for arbitrary concurrently mutable roots; use an enforcing remote source for those.
/// The host supplies its ignore policy. Symlinks/reparse points and .git metadata are
/// intentionally not followed or disclosed by this conservative source.</summary>
public sealed class CodexStableDirectoryFileSearchSource : ICodexFileSearchSource
{
    private readonly Func<CodexFileSearchRoot, CancellationToken, ValueTask<IAsyncDisposable>> acquire;
    private readonly Func<CodexFileSearchRoot, CodexFileSearchEntry, CancellationToken, ValueTask<bool>> ignored;
    private readonly int maximumEntries;
    private const int MaximumDepth = 128;

    /// <summary>Creates a local source with an explicit stable-root lease and host ignore policy.
    /// The lease callback must not return a no-op lease for an untrusted mutable directory;
    /// it must reject any root for which the necessary protection cannot be established.</summary>
    public CodexStableDirectoryFileSearchSource(
        Func<CodexFileSearchRoot, CancellationToken, ValueTask<IAsyncDisposable>> acquire,
        Func<CodexFileSearchRoot, CodexFileSearchEntry, CancellationToken, ValueTask<bool>> ignored,
        int maximumEntries = 100000)
    {
        this.acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        this.ignored = ignored ?? throw new ArgumentNullException(nameof(ignored));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntries, 1000000);
        this.maximumEntries = maximumEntries;
    }

    /// <inheritdoc />
    public async ValueTask AuthorizeAsync(CodexFileSearchRoot root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var lease = await AcquireAsync(root, cancellationToken).ConfigureAwait(false);
        CheckRoot(root.Root);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CodexFileSearchEntry> EnumerateAsync(CodexFileSearchRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var lease = await AcquireAsync(root, cancellationToken).ConfigureAwait(false);
        CheckRoot(root.Root);
        var stack = new Stack<IEnumerator<string>>();
        var visited = 0;
        try
        {
            stack.Push(Entries(root.Root));
            while (stack.TryPeek(out var entries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.MoveNext())
                {
                    stack.Pop().Dispose();
                    continue;
                }
                if (++visited > maximumEntries)
                    throw new IOException("Local file search traversal budget exceeded.");
                if (visited % 256 == 0) await Task.Yield();
                var path = entries.Current;
                if (!CodexFileSystem.IsWithin(path, root.Root) ||
                    string.Equals(path, root.Root, CodexFileSystem.PathComparison))
                    throw new IOException("Local search entry escaped its root.");
                if (string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var kind = CodexFileSystemNative.Kind(path, false);
                if (kind is not (CodexFileSystemNative.Regular or CodexFileSystemNative.DirectoryKind)) continue;
                var entry = new CodexFileSearchEntry(Path.GetRelativePath(root.Root, path),
                    kind == CodexFileSystemNative.DirectoryKind);
                if (await ignored(root, entry, cancellationToken).ConfigureAwait(false)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
                if (entry.IsDirectory)
                {
                    if (stack.Count >= MaximumDepth) throw new IOException("Local file search depth exceeded.");
                    stack.Push(Entries(path));
                }
            }
        }
        finally
        {
            while (stack.TryPop(out var entries)) entries.Dispose();
        }
    }

    private async ValueTask<IAsyncDisposable> AcquireAsync(CodexFileSearchRoot root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(root.ConnectionId) || string.IsNullOrWhiteSpace(root.Root) ||
            !Path.IsPathFullyQualified(root.Root) ||
            root.Root != Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Root)) ||
            root.Root == Path.GetPathRoot(root.Root))
            throw new UnauthorizedAccessException("Local file search requires an exact absolute non-filesystem root.");
        return await acquire(root, ct).ConfigureAwait(false) ??
            throw new UnauthorizedAccessException("Local file search root has no authorization lease.");
    }

    private static void CheckRoot(string path)
    {
        var chain = new Stack<string>();
        for (var part = new DirectoryInfo(path); part is not null; part = part.Parent) chain.Push(part.FullName);
        foreach (var component in chain)
            if (CodexFileSystemNative.Kind(component, false) != CodexFileSystemNative.DirectoryKind)
                throw new UnauthorizedAccessException("Local search root contains a non-directory or symlink component.");
    }

    private static IEnumerator<string> Entries(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        }).GetEnumerator();
}
