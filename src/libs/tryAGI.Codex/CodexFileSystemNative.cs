using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace tryAGI.Codex;

// Only OS primitives missing from System.IO: regular-file/nonblocking open and
// exclusive mkdir. No subprocess, Codex executable, shell or provider transport.
internal static partial class CodexFileSystemNative
{
    internal const int Regular = 0x8000, DirectoryKind = 0x4000, Symlink = 0xa000;

    internal static int Kind(string path, bool follow)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if (!follow && (attributes & FileAttributes.ReparsePoint) != 0) return Symlink;
            if (follow && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = new FileInfo(path).ResolveLinkTarget(true) ?? throw new FileNotFoundException();
                attributes = File.GetAttributes(target.FullName);
            }
            return (attributes & FileAttributes.Directory) != 0 ? DirectoryKind :
                (attributes & FileAttributes.Device) != 0 ? 0 : Regular;
        }
        return UnixKind(path, follow);
    }

    private static unsafe int UnixKind(string path, bool follow)
    {
        Span<byte> status = stackalloc byte[512];
        fixed (byte* buffer = status)
        {
            if ((follow ? Stat(path, buffer) : LStat(path, buffer)) != 0) ThrowIo(path);
        }
        return Mode(status);
    }

    private static int Mode(ReadOnlySpan<byte> status)
    {
        // libc ABI: Darwin x64/arm64 mode_t at 4; Linux x64 at 24,
        // Linux arm64 at 16. Fail closed on other ABI layouts.
        if (OperatingSystem.IsMacOS()) return BitConverter.ToUInt16(status[4..]) & 0xf000;
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
            return (int)BitConverter.ToUInt32(status[(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16)..]) & 0xf000;
        throw new PlatformNotSupportedException("Native filesystem stat ABI has not been reviewed for this platform.");
    }

    internal static bool DestinationIsSourceOrDescendant(string source, string destination)
    {
        if (CodexFileSystem.IsWithin(destination, source)) return true;
        if (OperatingSystem.IsWindows()) return false; // Canonical junctions + ordinal-ignore-case above.
        var sourceIdentity = Identity(source);
        for (string? ancestor = destination; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            try { if (Identity(ancestor) == sourceIdentity) return true; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return false;
    }

    private static unsafe (ulong Device, ulong Inode) Identity(string path)
    {
        Span<byte> status = stackalloc byte[512];
        fixed (byte* buffer = status)
            if (Stat(path, buffer) != 0) ThrowIo(path);
        _ = Mode(status); // Validate the ABI before reading identity fields.
        return (OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(status) : BitConverter.ToUInt64(status),
            BitConverter.ToUInt64(status[8..]));
    }

    internal static unsafe FileStream OpenRegularRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Kind(path, true) != Regular) throw Invalid("fs/readFile requires a regular file");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                65536, FileOptions.Asynchronous);
        }
        // O_NONBLOCK prevents FIFO open from waiting for a writer before fstat.
        // The handle check also protects against a special-file replacement race.
        var flags = OperatingSystem.IsMacOS() ? 0x4 | 0x1000000 : 0x800 | 0x80000;
        var descriptor = Open(path, flags);
        if (descriptor < 0) ThrowIo(path);
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            Span<byte> status = stackalloc byte[512];
            fixed (byte* buffer = status)
                if (FStat(descriptor, buffer) != 0) ThrowIo(path);
            if (Mode(status) != Regular) throw Invalid("fs/readFile requires a regular file");
            return new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void CreateSingleDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateDirectory(path, IntPtr.Zero)) ThrowIo(path);
        }
        else if (Mkdir(path, 0x1ff) != 0) ThrowIo(path);
    }

    private static void ThrowIo(string path)
    {
        var error = Marshal.GetLastPInvokeError();
        if (OperatingSystem.IsWindows() ? error == 87 : error == 22) throw Invalid("Invalid filesystem operation.");
        if (error is 2 or 3) throw new FileNotFoundException("Filesystem path was not found.", path);
        throw new IOException(new Win32Exception(error).Message, error);
    }
    internal static CodexFileSystemException Invalid(string message) => new(-32600, message);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Stat(string path, byte* status);
    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LStat(string path, byte* status);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static unsafe partial int FStat(int descriptor, byte* status);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Mkdir(string path, uint mode);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, IntPtr securityAttributes);
}
