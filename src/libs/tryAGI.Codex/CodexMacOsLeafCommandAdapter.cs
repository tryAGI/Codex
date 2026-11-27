using System.Diagnostics;

namespace tryAGI.Codex;

/// <summary>Host-authorized workspace for the restricted macOS leaf command adapter.
/// The host must keep the canonical root and its ancestors stable during admission and execution,
/// exclude external inode aliases (including hard links) and prevent untrusted concurrent root changes.
/// The adapter does not grant arbitrary model-selected paths or claim race-safe external filesystem I/O.</summary>
/// <param name="WorkspaceRoot">Absolute, canonical, host-approved directory.</param>
/// <param name="AllowWrites">Whether the exact admitted action may write within the workspace.</param>
public sealed record CodexMacOsLeafPolicy(string WorkspaceRoot, bool AllowWrites);

/// <summary>Explicit macOS Seatbelt command adapter for single-process, non-PTY commands.
/// It launches a fixed system sandbox-exec with a deny-default policy, a cleared environment,
/// one approved workspace, minimal system runtime access, no direct network, and no fork.
/// It supports neither unrestricted execution nor general process trees; it is not registered
/// automatically and is not a process/spawn adapter. Every invocation requires host authorization.
/// The host must establish stable root ownership before returning a policy.
/// </summary>
public sealed class CodexMacOsLeafCommandAdapter : ICodexCommandAdapter
{
    /// <summary>Explicit restricted leaf read-only profile, distinct from Rust built-ins.</summary>
    public const string ReadOnlyProfile = "macos-leaf-read-only";
    /// <summary>Explicit restricted leaf workspace-write profile, distinct from Rust built-ins.</summary>
    public const string WorkspaceProfile = "macos-leaf-workspace";

    private const string Launcher = "/usr/bin/sandbox-exec";
    private readonly Func<CodexCommandRequest, CancellationToken, ValueTask<CodexMacOsLeafPolicy>> authorize;

    // A deliberately limited policy derived from the operations needed by the macOS runtime.
    // Parameters are passed separately with -D, never interpolated from request strings.
    // No allow-default, inherited environment, arbitrary network service or process-fork rule.
    private const string BasePolicy = """
        (version 1)
        (deny default)
        (allow process-exec (literal (param "EXEC")))
        (deny process-fork)
        (allow process-info* (target self))
        (allow signal (target self))
        (allow sysctl-read
          (sysctl-name-prefix "hw.optional.arm.")
          (sysctl-name-prefix "hw.optional.armv8_")
          (sysctl-name-prefix "hw.perflevel")
          (sysctl-name "hw.activecpu") (sysctl-name "hw.byteorder")
          (sysctl-name "hw.cacheconfig") (sysctl-name "hw.cachelinesize_compat")
          (sysctl-name "hw.cpufamily") (sysctl-name "hw.cputype")
          (sysctl-name "hw.logicalcpu") (sysctl-name "hw.logicalcpu_max")
          (sysctl-name "hw.physicalcpu") (sysctl-name "hw.physicalcpu_max")
          (sysctl-name "hw.machine") (sysctl-name "hw.model")
          (sysctl-name "hw.memsize") (sysctl-name "hw.ncpu")
          (sysctl-name "hw.nperflevels") (sysctl-name "hw.pagesize")
          (sysctl-name "hw.pagesize_compat")
          (sysctl-name "kern.argmax") (sysctl-name "kern.osproductversion")
          (sysctl-name "kern.osrelease") (sysctl-name "kern.ostype")
          (sysctl-name "kern.osvariant_status") (sysctl-name "kern.osversion")
          (sysctl-name "kern.usrstack64") (sysctl-name "kern.version")
          (sysctl-name "sysctl.proc_cputype"))
        (allow file-read* file-test-existence
          (literal (param "EXEC"))
          (subpath "/usr/lib")
          (subpath "/System/Library/Frameworks")
          (subpath "/System/Library/PrivateFrameworks")
          (literal "/System/Library/CoreServices/SystemVersion.plist")
          (literal "/"))
        (allow file-map-executable
          (literal (param "EXEC"))
          (subpath "/usr/lib")
          (subpath "/System/Library/Frameworks")
          (subpath "/System/Library/PrivateFrameworks"))
        (allow system-mac-syscall (mac-policy-name "vnguard"))
        (allow system-mac-syscall
          (require-all (mac-policy-name "Sandbox") (mac-syscall-number 67)))
        (allow file-read-metadata
          (path-ancestors (param "ROOT"))
          (literal "/private/var")
          (literal "/var"))
        (allow file-read* file-test-existence (subpath (param "ROOT")))
        (allow file-read* file-write-data (literal "/dev/null") (literal "/dev/zero"))
        (allow file-read* (literal "/dev/random") (literal "/dev/urandom"))
        (allow file-read-metadata (literal "/dev") (literal "/dev/fd"))
        (allow file-read-data (literal "/dev/fd/0"))
        (allow file-write-data (literal "/dev/fd/1") (literal "/dev/fd/2"))
        (allow mach-lookup
          (global-name "com.apple.system.opendirectoryd.libinfo")
          (global-name "com.apple.system.logger")
          (global-name "com.apple.logd"))
        """;

    /// <summary>Creates the adapter with a mandatory trusted exact-action authorization callback.
    /// The callback must reject policy requirements that this leaf capability cannot enforce.
    /// Returning a policy is explicit admission, not a model-selected permission.</summary>
    public CodexMacOsLeafCommandAdapter(
        Func<CodexCommandRequest, CancellationToken, ValueTask<CodexMacOsLeafPolicy>> authorize)
    {
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
    }

    /// <inheritdoc />
    public async Task<ICodexCommandSession> StartAsync(CodexCommandRequest request,
        Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS() || !File.Exists(Launcher))
            throw new PlatformNotSupportedException("The macOS Seatbelt launcher is unavailable.");
        if (request.Tty || request.Size is not null)
            throw new PlatformNotSupportedException("The restricted leaf adapter does not support PTY sessions.");
        if (request.SandboxPolicy is not null ||
            request.PermissionProfile is not (null or ReadOnlyProfile or WorkspaceProfile) ||
            request.Environment.Count != 0)
            throw new PlatformNotSupportedException("The requested sandbox profile or environment cannot be enforced by this leaf adapter.");
        // Snapshot exact argv before validation and the asynchronous authorization boundary.
        var exact = request with { Command = Array.AsReadOnly(request.Command.ToArray()) };
        if (exact.Timeout <= TimeSpan.Zero || exact.Timeout > TimeSpan.FromMinutes(5) ||
            exact.OutputBytesCap is < 0 or > 8388608 || exact.Command.Count is < 1 or > 1024 ||
            exact.Command.Any(x => x is null || x.Contains('\0')) ||
            exact.Command.Sum(x => (long)x.Length) > 131072)
            throw new ArgumentException("Invalid bounded leaf command.", nameof(request));
        var executable = SystemExecutable(exact.Command[0]);
        var policy = await authorize(exact, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Leaf execution has not been authorized.");
        cancellationToken.ThrowIfCancellationRequested();
        if ((exact.PermissionProfile == ReadOnlyProfile && policy.AllowWrites) ||
            (exact.PermissionProfile == WorkspaceProfile && !policy.AllowWrites))
            throw new UnauthorizedAccessException("The resolved leaf policy does not match the requested profile.");
        var root = CanonicalDirectory(policy.WorkspaceRoot);
        var cwd = exact.Cwd is null ? root : CanonicalDirectory(exact.Cwd);
        if (!CodexFileSystem.IsWithin(cwd, root))
            throw new UnauthorizedAccessException("Working directory is outside the authorized leaf workspace.");

        var start = new ProcessStartInfo
        {
            FileName = Launcher,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        start.Environment["LANG"] = "C";
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(policy.AllowWrites
            ? BasePolicy + "\n(allow file-write* (subpath (param \"ROOT\")))"
            : BasePolicy);
        start.ArgumentList.Add("-D");
        start.ArgumentList.Add("EXEC=" + executable);
        start.ArgumentList.Add("-D");
        start.ArgumentList.Add("ROOT=" + root);
        start.ArgumentList.Add(executable);
        foreach (var argument in exact.Command.Skip(1)) start.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();

        // The fixed system launcher applies Seatbelt before exec. A launcher failure is a
        // non-successful command exit; there is never a direct Process.Start of the target.
        var process = Process.Start(start) ?? throw new InvalidOperationException("Seatbelt launcher did not start.");
        try
        {
            return new Session(process, exact, output, cancellationToken);
        }
        catch
        {
            try
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { process.Dispose(); }
            throw;
        }
    }

    private static string SystemExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path) ||
            Path.GetDirectoryName(path) is not ("/bin" or "/usr/bin" or "/sbin" or "/usr/sbin") ||
            path == Launcher)
            throw new PlatformNotSupportedException("Only exact absolute system executables are supported by the leaf adapter.");
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new PlatformNotSupportedException("The leaf executable must be an existing non-symlink system binary.");
        return path;
    }

    private static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
            throw new UnauthorizedAccessException("Leaf workspace and cwd must be absolute canonical directories.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full) || full != Path.TrimEndingDirectorySeparator(path))
            throw new UnauthorizedAccessException("A filesystem root or non-canonical leaf directory is not supported.");
        for (DirectoryInfo? part = new(full); part is not null; part = part.Parent)
            if (!part.Exists || part.LinkTarget is not null || (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Leaf directory contains a symlink or an unavailable component.");
        return full;
    }

    private static void Kill(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    private sealed class Session : ICodexCommandSession
    {
        private readonly Process process;
        private readonly CodexCommandRequest request;
        private readonly Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output;
        private readonly CancellationTokenSource stop;
        private readonly CancellationTokenRegistration cancellation;
        private readonly SemaphoreSlim stdinGate = new(1, 1);
        private readonly object gate = new();
        private bool stdinClosed;
        private bool disposed;
        private Task? closing;
        private Exception? stopFailure;

        internal Session(Process process, CodexCommandRequest request,
            Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
            CancellationToken cancellationToken)
        {
            this.process = process;
            this.request = request;
            this.output = output;
            if (!request.StreamStdin)
            {
                process.StandardInput.Close();
                stdinClosed = true;
            }
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(request.Timeout);
            cancellation = stop.Token.Register(static state => ((Session)state!).StopFromCancellation(), this);
            Completion = ObserveAsync();
        }

        public Task<int> Completion { get; }

        private void StopFromCancellation()
        {
            try { Terminate(); }
            catch (Exception error) { lock (gate) stopFailure ??= error; }
        }

        private void Terminate()
        {
            lock (gate)
            {
                if (!disposed) Kill(process);
            }
        }

        private async Task<int> ObserveAsync()
        {
            // Each pipe pump independently kills the leaf on output callback failure.
            // All callbacks must finish before completion or disposal settles.
            var stdout = PumpAsync(process.StandardOutput.BaseStream, CodexCommandStream.Stdout);
            var stderr = PumpAsync(process.StandardError.BaseStream, CodexCommandStream.Stderr);
            await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }

        private async Task PumpAsync(Stream stream, CodexCommandStream channel)
        {
            var buffer = new byte[65536];
            try
            {
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    await output(channel, buffer.AsMemory(0, count), stop.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                Terminate();
                throw;
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, bool closeStdin, CancellationToken cancellationToken)
        {
            if (!request.StreamStdin) throw new InvalidOperationException("Stdin streaming was not requested.");
            await stdinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (gate) ObjectDisposedException.ThrowIf(disposed || closing is not null, this);
                if (stdinClosed) throw new InvalidOperationException("Stdin is closed.");
                if (!bytes.IsEmpty)
                {
                    await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (closeStdin)
                {
                    process.StandardInput.Close();
                    stdinClosed = true;
                }
            }
            finally { stdinGate.Release(); }
        }

        public ValueTask ResizeAsync(CodexCommandTerminalSize size, CancellationToken cancellationToken) =>
            ValueTask.FromException(new PlatformNotSupportedException("Leaf execution does not support PTY."));

        public async ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Terminate();
            // Completion retains the exit and pipe-drain outcome even if disposal races this control.
            await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                closing ??= CloseAsync();
                return new(closing);
            }
        }

        private async Task CloseAsync()
        {
            Exception? failure = null;
            try { Terminate(); }
            catch (Exception error) { failure = error; }
            try { await stop.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            // Never declare successful cleanup until the actual leaf has exited and
            // every output callback has settled. Fork is denied by the kernel policy.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Completion.ConfigureAwait(false); }
            catch (Exception) { /* Completion already carries the execution/output failure. */ }
            await cancellation.DisposeAsync().ConfigureAwait(false);
            await stdinGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (gate)
                {
                    disposed = true;
                    failure ??= stopFailure;
                    process.Dispose();
                    stop.Dispose();
                }
            }
            finally { stdinGate.Release(); }
            if (failure is not null) throw new InvalidOperationException("Leaf process cleanup failed.", failure);
        }
    }
}
