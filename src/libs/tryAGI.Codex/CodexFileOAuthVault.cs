using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex;

/// <summary>Durable encrypted-record storage for a host-provisioned private directory on macOS/Linux.
/// Use with <see cref="CodexEncryptedOAuthSessionStore"/> and a separately protected host key.
/// Cooperating processes must use this same directory and lease protocol. The host must secure
/// the directory and its ancestors against other writers. This is not protection against a privileged
/// filesystem rollback, nor an operating-system keyring.</summary>
public sealed partial class CodexFileOAuthVault : ICodexOAuthVault
{
    private const int HeaderSize = 40;
    private const int MaximumCiphertextBytes = 262_144 + 29;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode NonOwner = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static ReadOnlySpan<byte> Magic => "COAUTH01"u8;
    private readonly string directory;
    private readonly TimeSpan leaseTimeout;

    /// <summary>Creates a vault in an existing owner-only directory. A host on another platform must
    /// provide its own durable vault adapter rather than assuming Unix durability semantics.</summary>
    public CodexFileOAuthVault(string directory, TimeSpan? leaseTimeout = null)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The file OAuth vault requires macOS or Linux.");
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("A private absolute vault directory is required.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
        this.leaseTimeout = leaseTimeout ?? TimeSpan.FromSeconds(30);
        if (this.leaseTimeout <= TimeSpan.Zero || this.leaseTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(leaseTimeout));
        EnsureDirectory();
    }

    /// <inheritdoc />
    public async ValueTask<ICodexOAuthVaultLease> AcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = SessionDigest(sessionId);
        var recordPath = Path.Combine(directory, name + ".session");
        var lockPath = Path.Combine(directory, name + ".lock");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectory();
            // Never unlink a lock file: another process could still own the old inode.
            CheckFile(lockPath);
            FileStream? stream = null;
            try
            {
                var options = new FileStreamOptions
                { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
                stream = new FileStream(lockPath, options);
            }
            catch (IOException)
            {
                if (Stopwatch.GetElapsedTime(started) >= leaseTimeout)
                    throw new IOException("OAuth vault lease could not be acquired.");
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (UnauthorizedAccessException)
            { throw new IOException("OAuth vault lease is not authorized."); }

            try
            {
                CheckFile(lockPath);
                CheckFile(recordPath);
                cancellationToken.ThrowIfCancellationRequested();
                return new Lease(this, recordPath, stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    private static string SessionDigest(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256 || sessionId.Any(char.IsControl))
            throw new ArgumentException("Invalid OAuth vault session ID.", nameof(sessionId));
        try { return Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(sessionId))); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid OAuth vault session ID.", nameof(sessionId)); }
    }

    private void EnsureDirectory()
    {
        try
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0 ||
                OperatingSystem.IsWindows() || (File.GetUnixFileMode(directory) & NonOwner) != 0)
                throw new IOException("OAuth vault directory is not private.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("OAuth vault directory is unavailable or not private."); }
    }

    // Returns false only for an absent record, never for an unsafe or unreadable existing file.
    private static bool CheckFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & NonOwner) != 0)
                throw new IOException("OAuth vault file is not private.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("OAuth vault file is unavailable or not private."); }
    }

    private sealed class Lease(CodexFileOAuthVault owner, string path, FileStream fileLock) : ICodexOAuthVaultLease
    {
        private readonly SemaphoreSlim operations = new(1, 1);
        private bool disposed;

        public async ValueTask<CodexOAuthVaultRecord?> ReadAsync(CancellationToken cancellationToken)
        {
            await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { operations.Release(); }
        }

        private async ValueTask<CodexOAuthVaultRecord?> ReadCoreAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            owner.EnsureDirectory();
            if (!CheckFile(path)) return null;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length <= HeaderSize || stream.Length > HeaderSize + MaximumCiphertextBytes)
                    throw InvalidRecord();
                var bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                if (!bytes.AsSpan(0, 8).SequenceEqual(Magic)) throw InvalidRecord();
                var version = Encoding.ASCII.GetString(bytes, 8, 32);
                if (!Guid.TryParseExact(version, "N", out _)) throw InvalidRecord();
                return new(version, bytes.AsMemory(HeaderSize));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new IOException("OAuth vault record could not be read."); }
        }

        public async ValueTask<bool> CompareExchangeAsync(string? expectedVersion, CodexOAuthVaultRecord? replacement,
            CancellationToken cancellationToken)
        {
            if (expectedVersion is not null && !Guid.TryParseExact(expectedVersion, "N", out _))
                throw new ArgumentException("Invalid OAuth vault version.", nameof(expectedVersion));
            if (replacement is not null && (!Guid.TryParseExact(replacement.Version, "N", out _) ||
                replacement.Ciphertext.Length is <= 0 or > MaximumCiphertextBytes || replacement.Version == expectedVersion))
                throw new ArgumentException("Invalid OAuth vault replacement.", nameof(replacement));

            await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var current = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
                if (current?.Version != expectedVersion) return false;
                if (replacement is null)
                {
                    if (current is null) return true;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        File.Delete(path);
                        // Once the namespace mutation is dispatched, settle its durability even
                        // if the caller canceled. Failure here means an uncertain commit.
                        owner.SyncDirectory();
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { throw new IOException("OAuth vault deletion outcome is unconfirmed."); }
                    return true;
                }

                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var ownsTemp = false;
                try
                {
                    var options = new FileStreamOptions
                    { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
                    await using (var stream = new FileStream(temp, options))
                    {
                        ownsTemp = true;
                        var header = new byte[HeaderSize];
                        Magic.CopyTo(header);
                        Encoding.ASCII.GetBytes(replacement.Version, header.AsSpan(8));
                        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                        await stream.WriteAsync(replacement.Ciphertext, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    owner.EnsureDirectory();
                    CheckFile(path);
                    File.Move(temp, path, overwrite: true);
                    ownsTemp = false;
                    owner.SyncDirectory();
                    return true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new IOException("OAuth vault write outcome is unconfirmed."); }
                finally
                {
                    if (ownsTemp)
                    {
                        try { File.Delete(temp); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                }
            }
            finally { operations.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (disposed) return;
                disposed = true;
                await fileLock.DisposeAsync().ConfigureAwait(false);
            }
            finally { operations.Release(); }
        }
    }

    private static CodexOAuthRefreshException InvalidRecord() => new(CodexOAuthRefreshFailure.InvalidCredentials);

    private void SyncDirectory()
    {
        // The owner controls directory ancestry; validate the leaf again before opening.
        EnsureDirectory();
        var descriptor = OpenDirectory(directory, 0);
        if (descriptor < 0) throw new IOException("OAuth vault directory could not be synchronized.");
        try
        {
            while (Sync(descriptor) != 0)
            {
                // EINTR is the only retryable fsync failure.
                if (Marshal.GetLastPInvokeError() != 4)
                    throw new IOException("OAuth vault directory could not be synchronized.");
            }
        }
        finally { _ = Close(descriptor); }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Sync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);
}
