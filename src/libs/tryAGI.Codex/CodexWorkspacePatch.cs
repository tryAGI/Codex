using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex;

/// <summary>A host-captured original file; null contents explicitly mean an absent path.
/// Permissions are host metadata, never a model-selected mode.</summary>
/// <param name="Path">Workspace-relative path.</param>
/// <param name="Contents">Original bytes, or <see langword="null"/> when the path is absent.</param>
/// <param name="Permissions">Unix permission bits, if captured.</param>
public sealed record CodexWorkspaceFile(string Path, ReadOnlyMemory<byte>? Contents, int? Permissions = null);

/// <summary>Options of <see cref="CodexWorkspacePatch.Plan"/>.</summary>
public sealed record CodexWorkspacePatchOptions
{
    /// <summary>Treat paths that differ only in case as different files.</summary>
    public bool CaseSensitivePaths { get; init; } = true;
    /// <summary>How line endings are reconstructed.</summary>
    public CodexPatchUpdateMode UpdateMode { get; init; } = CodexPatchUpdateMode.PreserveLineEndings;
    /// <summary>Required <c>*** Environment ID:</c> value, if any.</summary>
    public string? EnvironmentId { get; init; }
    /// <summary>Most files the patch may touch.</summary>
    public int MaximumFiles { get; init; } = 48;
    /// <summary>Most bytes across all original and resulting files.</summary>
    public int MaximumTotalBytes { get; init; } = 16 * 1024 * 1024;
}

/// <summary>A net file change after every sequential hunk has been evaluated.
/// The host must compare all original preconditions before authorizing/writing the plan.</summary>
public sealed class CodexWorkspaceChange
{
    private readonly byte[]? contents;
    internal CodexWorkspaceChange(string path, string? before, byte[]? after, int? permissions)
    { Path = path; BeforeSha256 = before; contents = after?.ToArray(); Permissions = permissions; }
    /// <summary>Workspace-relative path.</summary>
    public string Path { get; }
    /// <summary>SHA-256 of the original bytes, or <see langword="null"/> when absent.</summary>
    public string? BeforeSha256 { get; }
    /// <summary>SHA-256 of the result, or <see langword="null"/> when deleted.</summary>
    public string? AfterSha256 => contents is null ? null : CodexWorkspacePatch.Hash(contents);
    // Copy on access: neither the source capture nor an exposed result can mutate the plan.
    /// <summary>A copy of the resulting bytes, or <see langword="null"/> when deleted.</summary>
    public ReadOnlyMemory<byte>? AfterContents => contents is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(contents.ToArray());
    /// <summary>Unix permission bits of the result.</summary>
    public int? Permissions { get; }
    /// <summary><c>create</c>, <c>update</c> or <c>delete</c>.</summary>
    public string Operation => BeforeSha256 is null ? "create" : contents is null ? "delete" : "update";
}

/// <summary>The net result of a patch.</summary>
/// <param name="Changes">Net file changes.</param>
/// <param name="Preconditions">Original SHA-256 of every touched path (<see langword="null"/> for absent); recheck all of them before writing.</param>
public sealed record CodexWorkspacePatchPlan(IReadOnlyList<CodexWorkspaceChange> Changes,
    IReadOnlyDictionary<string, string?> Preconditions);

/// <summary>Pure byte-preserving patch planning, with no path resolution or filesystem access.
/// The host captures every touched path (including absent destinations), enforces its workspace
/// boundary and symlink policy, performs review, then revalidates all hashes before atomic effects.</summary>
public static class CodexWorkspacePatch
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Bom = [0xef, 0xbb, 0xbf];
    private sealed class FileState(string path, byte[]? original, int? permissions)
    {
        internal string Path { get; } = path;
        internal byte[]? Original { get; } = original;
        internal byte[]? Current { get; set; } = original;
        internal int? OriginalPermissions { get; } = permissions;
        internal int? CurrentPermissions { get; set; } = permissions;
    }

    /// <summary>Returns every path a patch touches, including move destinations, so the host can capture them.</summary>
    public static IReadOnlyList<string> Paths(CodexPatchDocument document, bool caseSensitive = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        var paths = new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var hunk in document.Hunks)
        {
            paths.Add(Path(hunk.Path));
            if (hunk.MoveTo is { } destination) paths.Add(Path(destination));
        }
        return Array.AsReadOnly(paths.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Evaluates a patch against captured originals without touching the file system.</summary>
    /// <param name="patch">Patch text.</param>
    /// <param name="originals">Captured state of every path from <see cref="Paths"/>.</param>
    /// <param name="options">Limits and line-ending mode.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <exception cref="CodexPatchException">The patch is malformed or does not apply.</exception>
    public static CodexWorkspacePatchPlan Plan(string patch, IReadOnlyList<CodexWorkspaceFile> originals,
        CodexWorkspacePatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originals);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (options.MaximumFiles is < 1 or > 256 || options.MaximumTotalBytes is < 1 or > 64 * 1024 * 1024 ||
            options.UpdateMode is not (CodexPatchUpdateMode.NormalizeToLf or CodexPatchUpdateMode.PreserveLineEndings))
            throw new ArgumentOutOfRangeException(nameof(options));
        var document = CodexPatch.Parse(patch);
        if (document.EnvironmentId is not null && document.EnvironmentId != options.EnvironmentId)
            throw new CodexPatchException("Patch environment does not match the host binding.");
        var comparer = options.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var paths = Paths(document, options.CaseSensitivePaths);
        if (paths.Count > options.MaximumFiles || originals.Count != paths.Count)
            throw new CodexPatchException("Capture exactly every touched source and destination within the file budget.");
        var files = new Dictionary<string, FileState>(comparer);
        long bytes = 0;
        foreach (var capture in originals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path(capture.Path);
            var contents = capture.Contents;
            if (contents?.Length > CodexPatch.MaximumSourceCharacters || (bytes += contents?.Length ?? 0) > options.MaximumTotalBytes)
                throw new CodexPatchException("Original bytes exceed the bounded workspace budget.");
            if (contents is null && capture.Permissions is not null || capture.Permissions is < 0 or > 0xfff)
                throw new CodexPatchException("Invalid captured file permissions.");
            if (!files.TryAdd(path, new(path, contents?.ToArray(), capture.Permissions)))
                throw new CodexPatchException("Duplicate captured path or filesystem alias.");
        }
        if (paths.Any(path => !files.ContainsKey(path)))
            throw new CodexPatchException("Missing source/destination capture; no filesystem effects planned.");
        foreach (var hunk in document.Hunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = files[Path(hunk.Path)];
            switch (hunk.Kind)
            {
                case CodexPatchKind.Add:
                    state.Current = Utf8.GetBytes(hunk.Content ?? throw new CodexPatchException("Add content is missing."));
                    break;
                case CodexPatchKind.Delete:
                    if (state.Current is null) throw new CodexPatchException("Delete requires an existing file: " + state.Path);
                    state.Current = null; state.CurrentPermissions = null;
                    break;
                case CodexPatchKind.Update:
                    if (state.Current is null) throw new CodexPatchException("Update requires an existing file: " + state.Path);
                    var updated = Update(state.Current, hunk, options.UpdateMode, cancellationToken);
                    if (hunk.MoveTo is { } destination)
                    {
                        var target = files[Path(destination)];
                        if (ReferenceEquals(target, state)) throw new CodexPatchException("Move source and destination alias the same file.");
                        // An existing destination retains its permissions; an absent destination
                        // inherits the source mode as an explicit host preservation policy.
                        target.CurrentPermissions = target.Current is null ? state.CurrentPermissions : target.CurrentPermissions;
                        target.Current = updated; state.Current = null; state.CurrentPermissions = null;
                    }
                    else state.Current = updated;
                    break;
                default: throw new CodexPatchException("Unknown patch hunk kind.");
            }
            if (files.Values.Any(file => file.Current?.Length > CodexPatch.MaximumSourceCharacters) ||
                files.Values.Sum(file => (long)(file.Current?.Length ?? 0)) > options.MaximumTotalBytes)
                throw new CodexPatchException("Planned bytes exceed the bounded workspace budget.");
        }
        var changes = new List<CodexWorkspaceChange>();
        var preconditions = new Dictionary<string, string?>(comparer);
        foreach (var file in files.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = file.Original is null ? null : Hash(file.Original);
            preconditions.Add(file.Path, before);
            if (file.Original is null && file.Current is null || file.Original is not null && file.Current is not null &&
                file.Original.AsSpan().SequenceEqual(file.Current) && file.OriginalPermissions == file.CurrentPermissions) continue;
            changes.Add(new(file.Path, before, file.Current, file.CurrentPermissions));
        }
        return new(Array.AsReadOnly(changes.ToArray()), new ReadOnlyDictionary<string, string?>(preconditions));
    }

    private static byte[] Update(byte[] original, CodexPatchHunk hunk, CodexPatchUpdateMode mode, CancellationToken ct)
    {
        var bom = original.AsSpan().StartsWith(Bom);
        string text;
        try { text = Utf8.GetString(original.AsSpan(bom ? Bom.Length : 0)); }
        catch (DecoderFallbackException) { throw new CodexPatchException("Update source must be valid UTF-8 text."); }
        var updated = Utf8.GetBytes(CodexPatch.Update(text, hunk, mode, ct));
        if (!bom) return updated;
        var result = new byte[Bom.Length + updated.Length]; Bom.CopyTo(result, 0); updated.CopyTo(result, Bom.Length);
        return result;
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Path(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl) || path.StartsWith('/') ||
            path.Contains('\\') || path.Split('/').Any(segment => segment is "" or "." or "..") ||
            path.Length > 1 && path[1] == ':')
            throw new CodexPatchException("Use canonical relative workspace paths without traversal or aliases.");
        CodexPatch.ValidateText(path);
        return path;
    }
}
