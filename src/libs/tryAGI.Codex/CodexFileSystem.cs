using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace tryAGI.Codex;

/// <summary>Explicitly bound local-host filesystem RPCs. The caller owns device
/// selection, consent, root/symlink policy and authenticated connection identity.
/// This executor is never implicitly registered as a model tool.</summary>
public sealed class CodexFileSystem : IAsyncDisposable
{
    /// <summary>Largest file <c>fs/readFile</c> returns.</summary>
    public const long MaximumReadBytes = 512L * 1024 * 1024;
    private readonly Func<CodexFileSystemAccess, CancellationToken, ValueTask> authorize;
    private readonly CodexFileSystemWatches watches;
    private int disposed;

    /// <summary>Creates a file system handler.</summary>
    /// <param name="authorize">Checks every request against the host's root and policy; throw to deny.</param>
    /// <param name="sendNotification">Delivers <c>fs/changed</c> notifications to a connection.</param>
    public CodexFileSystem(Func<CodexFileSystemAccess, CancellationToken, ValueTask> authorize,
        Func<string, CodexFsChanged, CancellationToken, ValueTask> sendNotification)
    {
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        watches = new(sendNotification ?? throw new ArgumentNullException(nameof(sendNotification)));
    }

    /// <summary>Returns <see langword="true"/> for the <c>fs/*</c> methods this handler serves.</summary>
    public static bool Handles(string method) => method is "fs/readFile" or "fs/writeFile" or "fs/createDirectory"
        or "fs/getMetadata" or "fs/readDirectory" or "fs/remove" or "fs/copy" or "fs/watch" or "fs/unwatch";

    /// <summary>Serves one <c>fs/*</c> request.</summary>
    /// <param name="connectionId">Connection that sent the request; watches belong to it.</param>
    /// <param name="method">The <c>fs/*</c> method.</param>
    /// <param name="parameters">Request parameters.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="CodexFileSystemException">The request is invalid, denied or failed.</exception>
    public async Task<JsonElement> HandleAsync(string connectionId, string method, JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Handles(method)) throw new CodexFileSystemException(-32601, "Unknown filesystem method.");
        try
        {
            switch (method)
            {
                case "fs/readFile":
                {
                    var path = Absolute(Read(parameters, CodexFileSystemJson.Default.CodexFsPath).Path);
                    await Authorize(connectionId, method, [path], [], cancellationToken).ConfigureAwait(false);
                    await using var stream = CodexFileSystemNative.OpenRegularRead(path);
                    if (stream.Length > MaximumReadBytes) throw Invalid("File exceeds the 512 MiB read limit.");
                    using var bytes = new MemoryStream((int)stream.Length);
                    var chunk = new byte[65536];
                    while (true)
                    {
                        var length = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                        if (length == 0) break;
                        if (bytes.Length + length > MaximumReadBytes) throw Invalid("File exceeds the 512 MiB read limit.");
                        bytes.Write(chunk, 0, length);
                    }
                    return Json(new CodexFsReadFile(Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length)),
                        CodexFileSystemJson.Default.CodexFsReadFile);
                }
                case "fs/writeFile":
                {
                    var input = Read(parameters, CodexFileSystemJson.Default.CodexFsWriteFile);
                    var path = Absolute(input.Path);
                    // STANDARD Rust decoder requires canonical padding/alphabet/trailing bits.
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(input.DataBase64 ?? throw Invalid("Missing dataBase64.")); }
                    catch (FormatException error) { throw new CodexFileSystemException(-32600, "Invalid dataBase64.", error); }
                    if (Convert.ToBase64String(bytes) != input.DataBase64) throw Invalid("Invalid standard base64 encoding.");
                    await Authorize(connectionId, method, [], [path], cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case "fs/createDirectory":
                {
                    var input = Read(parameters, CodexFileSystemJson.Default.CodexFsCreateDirectory);
                    var path = Absolute(input.Path);
                    await Authorize(connectionId, method, [], [path], cancellationToken).ConfigureAwait(false);
                    if (input.Recursive ?? true) System.IO.Directory.CreateDirectory(path);
                    else CodexFileSystemNative.CreateSingleDirectory(path);
                    break;
                }
                case "fs/getMetadata":
                {
                    var path = Absolute(Read(parameters, CodexFileSystemJson.Default.CodexFsPath).Path);
                    await Authorize(connectionId, method, [path], [], cancellationToken).ConfigureAwait(false);
                    var link = CodexFileSystemNative.Kind(path, false) == CodexFileSystemNative.Symlink;
                    var kind = CodexFileSystemNative.Kind(path, true);
                    var target = link ? new FileInfo(path).ResolveLinkTarget(true)?.FullName ??
                        throw new IOException("Symlink target is unavailable.") : path;
                    return Json(new CodexFsMetadata(kind == CodexFileSystemNative.DirectoryKind,
                        kind == CodexFileSystemNative.Regular, link, Timestamp(() => File.GetCreationTimeUtc(target)),
                        Timestamp(() => File.GetLastWriteTimeUtc(target))), CodexFileSystemJson.Default.CodexFsMetadata);
                }
                case "fs/readDirectory":
                {
                    var path = Absolute(Read(parameters, CodexFileSystemJson.Default.CodexFsPath).Path);
                    await Authorize(connectionId, method, [path], [], cancellationToken).ConfigureAwait(false);
                    var entries = new List<CodexFsDirectoryEntry>();
                    foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var kind = CodexFileSystemNative.Kind(entry, true);
                            entries.Add(new(Path.GetFileName(entry), kind == CodexFileSystemNative.DirectoryKind,
                                kind == CodexFileSystemNative.Regular));
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                    return Json(new CodexFsDirectory(entries.ToArray()), CodexFileSystemJson.Default.CodexFsDirectory);
                }
                case "fs/remove":
                {
                    var input = Read(parameters, CodexFileSystemJson.Default.CodexFsRemove);
                    var path = Absolute(input.Path);
                    await Authorize(connectionId, method, [], [path], cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var kind = CodexFileSystemNative.Kind(path, false);
                        if (kind == CodexFileSystemNative.DirectoryKind)
                            System.IO.Directory.Delete(path, input.Recursive ?? true);
                        else if (OperatingSystem.IsWindows() && kind == CodexFileSystemNative.Symlink &&
                            (File.GetAttributes(path) & FileAttributes.Directory) != 0) System.IO.Directory.Delete(path);
                        else File.Delete(path);
                    }
                    catch (Exception error) when ((input.Force ?? true) && error is FileNotFoundException or DirectoryNotFoundException) { }
                    break;
                }
                case "fs/copy":
                {
                    var input = Read(parameters, CodexFileSystemJson.Default.CodexFsCopy);
                    var source = Absolute(input.SourcePath); var destination = Absolute(input.DestinationPath);
                    await Authorize(connectionId, method, [source], [destination], cancellationToken).ConfigureAwait(false);
                    var kind = CodexFileSystemNative.Kind(source, false);
                    if (kind == CodexFileSystemNative.DirectoryKind)
                    {
                        if (!input.Recursive) throw Invalid("Directory copy requires recursive: true.");
                        var resolvedSource = Canonical(source); var resolvedDestination = Canonical(destination);
                        if (CodexFileSystemNative.DestinationIsSourceOrDescendant(resolvedSource, resolvedDestination))
                            throw Invalid("Cannot copy a directory to itself or a descendant.");
                        CopyDirectory(source, destination, cancellationToken);
                    }
                    else if (kind == CodexFileSystemNative.Symlink) CopyLink(source, destination);
                    else if (kind == CodexFileSystemNative.Regular) File.Copy(source, destination, overwrite: true);
                    else throw Invalid("Copy supports only regular files, directories and symlinks.");
                    break;
                }
                case "fs/watch":
                {
                    var input = Read(parameters, CodexFileSystemJson.Default.CodexFsWatch);
                    var path = Absolute(input.Path);
                    if (input.WatchId is null) throw Invalid("Missing watchId.");
                    await Authorize(connectionId, method, [path], [], cancellationToken).ConfigureAwait(false);
                    await watches.WatchAsync(connectionId, input.WatchId, path, cancellationToken).ConfigureAwait(false);
                    return Json(new CodexFsPath(path), CodexFileSystemJson.Default.CodexFsPath);
                }
                case "fs/unwatch":
                {
                    var input = Read(parameters, CodexFileSystemJson.Default.CodexFsUnwatch);
                    if (input.WatchId is null) throw Invalid("Missing watchId.");
                    await Authorize(connectionId, method, [], [], cancellationToken).ConfigureAwait(false);
                    await watches.UnwatchAsync(connectionId, input.WatchId, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
            return Json(new CodexFsEmpty(), CodexFileSystemJson.Default.CodexFsEmpty);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new CodexFileSystemException(-32600, "Invalid filesystem parameters.", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw new CodexFileSystemException(-32603, "Local filesystem operation failed.", error); }
    }

    /// <summary>Stops every watch owned by a closed connection.</summary>
    public Task ConnectionClosedAsync(string connectionId) => watches.ConnectionClosedAsync(connectionId);
    /// <summary>Returns the state of all active watches.</summary>
    public Task<CodexFileSystemWatchStatus[]> WatchStatusAsync(CancellationToken cancellationToken = default) =>
        watches.StatusAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return watches.DisposeAsync();
    }

    private async ValueTask Authorize(string connection, string method, string[] reads, string[] writes, CancellationToken ct)
    {
        await authorize(new(connection, method, reads, writes), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }
    private static T Read<T>(JsonElement value, JsonTypeInfo<T> type) where T : class =>
        value.Deserialize(type) ?? throw Invalid("Missing filesystem parameters.");
    private static JsonElement Json<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToElement(value, type);
    private static CodexFileSystemException Invalid(string message) => CodexFileSystemNative.Invalid(message);
    internal static string Absolute(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        ? Path.GetFullPath(path) : throw Invalid("Filesystem paths must be absolute.");
    internal static bool IsWithin(string path, string root) => string.Equals(path, root, PathComparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Canonical(string path)
    {
        var current = Path.GetPathRoot(path) ?? throw Invalid("Filesystem path has no root.");
        foreach (var component in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    private static long Timestamp(Func<DateTime> read)
    {
        try { var value = new DateTimeOffset(read()).ToUnixTimeMilliseconds(); return Math.Max(0, value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return 0; }
    }
    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        System.IO.Directory.CreateDirectory(destination);
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(entry));
            switch (CodexFileSystemNative.Kind(entry, false))
            {
                case CodexFileSystemNative.DirectoryKind: CopyDirectory(entry, target, ct); break;
                case CodexFileSystemNative.Regular: File.Copy(entry, target, overwrite: true); break;
                case CodexFileSystemNative.Symlink: CopyLink(entry, target); break;
            }
        }
    }
    private static void CopyLink(string source, string destination)
    {
        var target = new FileInfo(source).LinkTarget ?? throw new IOException("Missing symlink target.");
        if (OperatingSystem.IsWindows() && (File.GetAttributes(source) & FileAttributes.Directory) != 0)
            System.IO.Directory.CreateSymbolicLink(destination, target);
        else File.CreateSymbolicLink(destination, target);
    }
}
