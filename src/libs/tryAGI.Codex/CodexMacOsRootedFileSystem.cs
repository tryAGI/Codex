using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace tryAGI.Codex;

/// <summary>Opt-in, connection-bound read-only filesystem RPC host for macOS arm64.
/// The host chooses and authorizes a stable absolute root, and its directory descriptor
/// remains pinned for the lifetime of this instance. Each request resolves relative to
/// that descriptor in one kernel openat with O_NOFOLLOW_ANY; it never reopens a caller
/// path with System.IO. Opened objects, rather than changing path names, are the read
/// boundary. A replaced root is refused at admission. The host still owns root topology,
/// mount/alias policy, special-device exclusion, and authorization of each disclosure.
/// This handler does not certify continuous namespace containment under arbitrary renames
/// or concurrent hard-link creation. Writes, removes, copies, recursive operations, watches
/// and other platforms are unsupported; never route them to the unrestricted path handler
/// as a fallback for a request in this capability.</summary>
public sealed partial class CodexMacOsRootedFileSystem : IAsyncDisposable
{
    private const int NoFollowAny = 0x20000000;
    private const int CloseOnExec = 0x01000000;
    private const int NonBlock = 0x00000004;
    private const int NoControllingTerminal = 0x00020000;
    private const int DirectoryOnly = 0x00100000;
    private const int AtSymlinkNoFollow = 0x0020;
    private const int OpenFlags = NoFollowAny | CloseOnExec | NonBlock | NoControllingTerminal;
    private const int MaximumPathCharacters = 4096;
    private const int MaximumEntries = 10000;
    private const long DefaultReadBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string connectionId;
    private readonly string root;
    private readonly SafeFileHandle rootHandle;
    private readonly EntryStat rootStat;
    private readonly Func<CodexFileSystemAccess, CancellationToken, ValueTask> authorize;
    private readonly long maximumReadBytes;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private bool disposed;

    private readonly record struct EntryStat(uint Device, ushort Mode, ushort Links, ulong Inode,
        long Size, long CreatedAtMs, long ModifiedAtMs)
    {
        internal int Kind => Mode & 0xf000;
    }

    private CodexMacOsRootedFileSystem(string connectionId, string root, SafeFileHandle rootHandle,
        EntryStat rootStat, Func<CodexFileSystemAccess, CancellationToken, ValueTask> authorize, long maximumReadBytes)
    {
        this.connectionId = connectionId;
        this.root = root;
        this.rootHandle = rootHandle;
        this.rootStat = rootStat;
        this.authorize = authorize;
        this.maximumReadBytes = maximumReadBytes;
    }

    /// <summary>Acquires the exact host-selected root after an explicit fs/rootOpen authorization.
    /// The host must protect the selected root identity during acquisition, and only pass roots
    /// that exclude untrusted devices, mounts and external aliases. No symlink component is
    /// accepted, including the root itself. No caller-selected root is implicitly opened.</summary>
    public static async Task<CodexMacOsRootedFileSystem> OpenAsync(string connectionId, string root,
        Func<CodexFileSystemAccess, CancellationToken, ValueTask> authorize,
        long maximumReadBytes = DefaultReadBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(authorize);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOSVersionAtLeast(12) || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("The rooted filesystem ABI is certified only for macOS arm64 (12 or later).");
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumReadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumReadBytes, CodexFileSystem.MaximumReadBytes);
        ValidateAbsolute(root);
        if (root == "/") throw Invalid("A whole-filesystem root is not supported.");
        await authorize(new(connectionId, "fs/rootOpen", [root], []), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var handle = OpenAbsoluteDirectory(root);
        try
        {
            var stat = Stat(handle);
            if (stat.Kind != CodexFileSystemNative.DirectoryKind) throw Invalid("The selected root is not a directory.");
            cancellationToken.ThrowIfCancellationRequested();
            return new(connectionId, root, handle, stat, authorize, maximumReadBytes);
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Claims all known filesystem RPCs for this capability, including unsupported
    /// mutations. A rejected mutation must not fall through to the path-based filesystem host.</summary>
    public static bool Handles(string method) => CodexFileSystem.Handles(method);

    /// <summary>Serves a filesystem request for the bound connection. ReadFile, GetMetadata
    /// and ReadDirectory are supported; all other known filesystem operations fail closed.
    /// Listing authorization covers the returned directory entry names and basic kinds.</summary>
    public async Task<JsonElement> HandleAsync(string connection, string method, JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connection != connectionId) throw new UnauthorizedAccessException("Filesystem connection mismatch.");
        if (!Handles(method)) throw new CodexFileSystemException(-32601, "Unknown filesystem method.");
        if (method is not ("fs/readFile" or "fs/getMetadata" or "fs/readDirectory"))
            throw new CodexFileSystemException(-32010, "This rooted filesystem capability is read-only and does not support the requested operation.");

        if (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Count() != 1 ||
            !parameters.TryGetProperty("path", out var field) || field.ValueKind != JsonValueKind.String ||
            field.GetString() is not { } path)
            throw Invalid("Expected one absolute path.");
        ValidateAbsolute(path);
        if (!CodexFileSystem.IsWithin(path, root))
            throw new UnauthorizedAccessException("Filesystem path is outside the bound root.");
        var relative = path == root ? "." : path[(root.Length + 1)..];
        if (relative == "." && method == "fs/readFile") throw Invalid("ReadFile requires a regular file.");

        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await authorize(new(connectionId, method, [path], []), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Check that the configured root name still identifies the pinned inode.
            // All requested I/O still uses the original root descriptor; a later rename
            // cannot silently rebind it to a replacement directory.
            using (var current = OpenAbsoluteDirectory(root))
            {
                var stat = Stat(current);
                if (stat.Device != rootStat.Device || stat.Inode != rootStat.Inode)
                    throw new UnauthorizedAccessException("The bound filesystem root was replaced.");
            }
            using var opened = OpenRelative(rootHandle, relative,
                method == "fs/readDirectory" ? DirectoryOnly : 0);
            var entry = Stat(opened);
            RequireAllowed(entry);
            cancellationToken.ThrowIfCancellationRequested();

            if (method == "fs/readFile")
            {
                if (entry.Kind != CodexFileSystemNative.Regular) throw Invalid("ReadFile requires a regular file.");
                return await ReadFileAsync(opened, entry.Size, cancellationToken).ConfigureAwait(false);
            }
            if (method == "fs/getMetadata")
            {
                var result = new CodexFsMetadata(entry.Kind == CodexFileSystemNative.DirectoryKind,
                    entry.Kind == CodexFileSystemNative.Regular, false, entry.CreatedAtMs, entry.ModifiedAtMs);
                return JsonSerializer.SerializeToElement(result, CodexFileSystemJson.Default.CodexFsMetadata);
            }
            if (entry.Kind != CodexFileSystemNative.DirectoryKind) throw Invalid("ReadDirectory requires a directory.");
            return ReadDirectory(opened, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new CodexFileSystemException(-32603, "Rooted filesystem operation refused or failed.", error);
        }
        finally { lifecycle.Release(); }
    }

    private void RequireAllowed(EntryStat stat)
    {
        if (!Allowed(stat)) throw new UnauthorizedAccessException("The opened object is not an allowed rooted file or directory.");
    }

    private bool Allowed(EntryStat stat) => stat.Device == rootStat.Device &&
        (stat.Kind == CodexFileSystemNative.DirectoryKind ||
            (stat.Kind == CodexFileSystemNative.Regular && stat.Links == 1));

    private async Task<JsonElement> ReadFileAsync(SafeFileHandle handle, long size, CancellationToken ct)
    {
        if (size < 0 || size > maximumReadBytes) throw Invalid("File exceeds the rooted read budget.");
        await using var stream = new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
        using var output = new MemoryStream((int)size);
        var bytes = new byte[65536];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximumReadBytes) throw Invalid("File exceeds the rooted read budget.");
            output.Write(bytes, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        RequireAllowed(Stat(handle));
        return JsonSerializer.SerializeToElement(
            new CodexFsReadFile(Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length)),
            CodexFileSystemJson.Default.CodexFsReadFile);
    }

    private JsonElement ReadDirectory(SafeFileHandle opened, CancellationToken ct)
    {
        // fdopendir takes ownership of its descriptor. Use a new directory descriptor,
        // never transfer ownership of the anchor or re-open an absolute path.
        using var directory = OpenRelative(opened, ".", DirectoryOnly);
        var descriptor = checked((int)directory.DangerousGetHandle());
        var iterator = FdOpenDir(descriptor);
        if (iterator == IntPtr.Zero) ThrowIo("fdopendir");
        directory.SetHandleAsInvalid();
        try
        {
            var entries = new List<CodexFsDirectoryEntry>();
            var visited = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var pointer = ReadDir(iterator);
                if (pointer == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0) ThrowIo("readdir");
                    break;
                }
                if (++visited > MaximumEntries + 2) throw Invalid("Directory exceeds the rooted listing budget.");

                // Darwin 64-bit dirent: d_reclen=16, d_namlen=18, d_type=20, d_name=21.
                var recordLength = (ushort)Marshal.ReadInt16(pointer, 16);
                var length = (ushort)Marshal.ReadInt16(pointer, 18);
                if (length == 0 || length > 1023 || recordLength <= 21 + length)
                    throw new IOException("Invalid Darwin directory entry.");
                var nameBytes = new byte[length];
                Marshal.Copy(IntPtr.Add(pointer, 21), nameBytes, 0, length);
                string name;
                try { name = StrictUtf8.GetString(nameBytes); }
                catch (DecoderFallbackException) { continue; }
                if (name is "." or ".." || name.Contains('/') || name.Any(char.IsControl)) continue;

                // A single-component no-follow fstatat supplies metadata without opening a
                // potentially special node or resolving a link target. Missing entries may
                // have been removed since enumeration. No external inode is listed as a file.
                var stat = StatRelative(descriptor, name);
                if (stat is not { } value || !Allowed(value)) continue;
                entries.Add(new(name, value.Kind == CodexFileSystemNative.DirectoryKind,
                    value.Kind == CodexFileSystemNative.Regular));
            }
            ct.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(new CodexFsDirectory(entries.ToArray()),
                CodexFileSystemJson.Default.CodexFsDirectory);
        }
        finally
        {
            if (CloseDir(iterator) != 0) ThrowIo("closedir");
        }
    }

    private static void ValidateAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathCharacters ||
            path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) ||
            path != Path.GetFullPath(path) || path != Path.TrimEndingDirectorySeparator(path))
            throw Invalid("Expected a normalized absolute path without traversal components.");
        if (path.Split('/').Skip(1).Any(x => x is "" or "." or ".."))
            throw Invalid("Expected a normalized absolute path without traversal components.");
    }

    private static SafeFileHandle OpenAbsoluteDirectory(string path) =>
        Own(Open(path, OpenFlags | DirectoryOnly), "root open");

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string relative, int flags) =>
        Own(OpenAt(parent, relative, OpenFlags | flags), "rooted openat");

    private static SafeFileHandle Own(int descriptor, string operation)
    {
        if (descriptor < 0) ThrowIo(operation);
        return new((IntPtr)descriptor, ownsHandle: true);
    }

    private static unsafe EntryStat Stat(SafeFileHandle handle)
    {
        Span<byte> bytes = stackalloc byte[512];
        fixed (byte* pointer = bytes)
            if (FStat(handle, pointer) != 0) ThrowIo("fstat");
        return DecodeStat(bytes);
    }

    private static unsafe EntryStat? StatRelative(int descriptor, string name)
    {
        Span<byte> bytes = stackalloc byte[512];
        fixed (byte* pointer = bytes)
        {
            if (FStatAt(descriptor, name, pointer, AtSymlinkNoFollow) != 0)
            {
                if (Marshal.GetLastPInvokeError() == 2) return null; // ENOENT.
                ThrowIo("fstatat");
            }
        }
        return DecodeStat(bytes);
    }

    // The Darwin arm64 64-bit-inode stat layout is checked against the platform SDK
    // and actual fixtures. Other OS/architecture layouts are not interpreted here.
    private static EntryStat DecodeStat(ReadOnlySpan<byte> bytes) =>
        new(BitConverter.ToUInt32(bytes), BitConverter.ToUInt16(bytes[4..]),
            BitConverter.ToUInt16(bytes[6..]), BitConverter.ToUInt64(bytes[8..]),
            BitConverter.ToInt64(bytes[96..]), Milliseconds(bytes[80..]), Milliseconds(bytes[48..]));

    private static long Milliseconds(ReadOnlySpan<byte> time)
    {
        var seconds = BitConverter.ToInt64(time);
        var nanos = BitConverter.ToInt64(time[8..]);
        if (seconds < 0 || nanos is < 0 or >= 1000000000) return 0;
        try { return checked(seconds * 1000 + nanos / 1000000); }
        catch (OverflowException) { return 0; }
    }

    private static void ThrowIo(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new IOException(operation + ": " + new Win32Exception(error).Message, error);
    }

    private static CodexFileSystemException Invalid(string message) => new(-32600, message);

    /// <summary>Stops admission and releases the pinned root after all in-flight I/O finishes.</summary>
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            rootHandle.Dispose();
        }
        finally { lifecycle.Release(); }
    }

    // No variadic mode argument is used: these opens never create files.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(SafeFileHandle directory, string path, int flags);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static unsafe partial int FStat(SafeFileHandle descriptor, byte* status);
    [LibraryImport("libc", EntryPoint = "fstatat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int FStatAt(int descriptor, string path, byte* status, int flags);
    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial IntPtr FdOpenDir(int descriptor);
    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial IntPtr ReadDir(IntPtr directory);
    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static partial int CloseDir(IntPtr directory);
}
