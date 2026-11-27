using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexMacOsLeafCommandAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualSeatbeltAllowsWorkspaceAndDeniesOutsideAndSymlinkAccess()
    {
        RequireMac();
        using var fixture = new Fixture();
        var allowed = Path.Combine(fixture.Root, "sample");
        var outside = Path.Combine(fixture.Outside, "sample");
        await File.WriteAllTextAsync(allowed, "allowed-fixture", Ct);
        await File.WriteAllTextAsync(outside, "outside-fixture", Ct);
        var link = Path.Combine(fixture.Root, "link");
        File.CreateSymbolicLink(link, outside);
        await using var host = fixture.Host(writes: true);

        var read = await fixture.Exec(host, ["/bin/cat", allowed]);
        Assert.Equal(0, Code(read));
        Assert.Equal("allowed-fixture", read.GetProperty("stdout").GetString());
        foreach (var target in new[] { outside, link })
        {
            var denied = await fixture.Exec(host, ["/bin/cat", target]);
            Assert.NotEqual(0, Code(denied));
            Assert.DoesNotContain("outside-fixture", denied.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        }

        var created = Path.Combine(fixture.Root, "new");
        var forbidden = Path.Combine(fixture.Outside, "new");
        Assert.Equal(0, Code(await fixture.Exec(host, ["/usr/bin/touch", created])));
        Assert.True(File.Exists(created));
        Assert.NotEqual(0, Code(await fixture.Exec(host, ["/usr/bin/touch", forbidden])));
        Assert.False(File.Exists(forbidden));
        var writeThroughLink = await fixture.Exec(host, ["/bin/bash", "--noprofile", "--norc", "-c",
            "printf changed > \"$1\"", "fixture", link]);
        Assert.NotEqual(0, Code(writeThroughLink));
        Assert.Equal("outside-fixture", await File.ReadAllTextAsync(outside, Ct));
    }

    [Fact]
    public async Task ActualReadOnlyProfileCannotWriteWithinWorkspace()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var host = fixture.Host(writes: false);
        var path = Path.Combine(fixture.Root, "forbidden");
        var result = await fixture.Exec(host, ["/usr/bin/touch", path],
            profile: CodexMacOsLeafCommandAdapter.ReadOnlyProfile);
        Assert.NotEqual(0, Code(result));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ActualKernelDeniesForkAndDirectLoopbackConnection()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var host = fixture.Host(writes: true);
        var shell = await fixture.Exec(host, ["/bin/bash", "--noprofile", "--norc", "-c", "printf 'leaf-ready\\n'"]);
        Assert.Equal(0, Code(shell));
        Assert.Equal("leaf-ready\n", shell.GetProperty("stdout").GetString());
        var fork = await fixture.Exec(host, ["/bin/bash", "--noprofile", "--norc", "-c", "/bin/bash --noprofile --norc -c 'exit 0' & wait $!"]);
        Assert.NotEqual(0, Code(fork));
        Assert.Contains("fork", fork.GetProperty("stderr").GetString()!, StringComparison.OrdinalIgnoreCase);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var network = await fixture.Exec(host, ["/usr/bin/nc", "-z", "-w", "1", "127.0.0.1", port]);
        Assert.NotEqual(0, Code(network));
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task ActualPipeStreamingAndCloseStdinPreserveBinaryOutput()
    {
        RequireMac();
        using var fixture = new Fixture();
        var notices = Channel.CreateUnbounded<JsonElement>();
        await using var host = fixture.Host(writes: false, notify: (method, body, _) =>
        {
            Assert.Equal("command/exec/outputDelta", method);
            notices.Writer.TryWrite(body);
            return ValueTask.CompletedTask;
        });
        var run = host.HandleAsync("command/exec", JsonSerializer.SerializeToElement(new
        {
            command = new[] { "/bin/cat" },
            cwd = fixture.Root,
            processId = "pipe",
            streamStdin = true,
            streamStdoutStderr = true,
        }), Ct);
        await host.HandleAsync("command/exec/write", JsonSerializer.SerializeToElement(new
        {
            processId = "pipe",
            deltaBase64 = "AP8=",
            closeStdin = true,
        }), Ct);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, Code(result));
        Assert.Equal("", result.GetProperty("stdout").GetString());
        var bytes = new List<byte>();
        while (notices.Reader.TryRead(out var body))
        {
            Assert.Equal("stdout", body.GetProperty("stream").GetString());
            bytes.AddRange(Convert.FromBase64String(body.GetProperty("deltaBase64").GetString()!));
        }
        Assert.Equal(new byte[] { 0, 255 }, bytes);
    }

    [Fact]
    public async Task DisconnectActuallyReapsOwnedLeafBeforeReturning()
    {
        RequireMac();
        using var fixture = new Fixture();
        var output = Channel.CreateUnbounded<JsonElement>();
        var host = fixture.Host(writes: false, notify: (method, body, _) =>
        {
            output.Writer.TryWrite(body);
            return ValueTask.CompletedTask;
        });
        try
        {
            var run = host.HandleAsync("command/exec", JsonSerializer.SerializeToElement(new
            {
                command = new[] { "/bin/bash", "--noprofile", "--norc", "-c", "printf '%s\\n' \"$$\"; read -r line" },
                cwd = fixture.Root,
                processId = "leaf",
                streamStdin = true,
                streamStdoutStderr = true,
            }), Ct);
            var text = new StringBuilder();
            while (!text.ToString().Contains('\n'))
            {
                var delta = await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
                if (delta.GetProperty("stream").GetString() == "stdout")
                    text.Append(Encoding.UTF8.GetString(Convert.FromBase64String(delta.GetProperty("deltaBase64").GetString()!)));
            }
            var pid = int.Parse(text.ToString().Trim(), System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.GetProcessById(pid);
            Assert.False(process.HasExited);
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            // The PID was emitted by the sandboxed target before disconnect; it must have exited.
            Assert.True(process.HasExited);
        }
        finally { await host.DisposeAsync(); }
    }

    [Fact]
    public async Task TerminateActuallyStopsAnOwnedLeaf()
    {
        RequireMac();
        using var fixture = new Fixture();
        var output = Channel.CreateUnbounded<JsonElement>();
        await using var host = fixture.Host(writes: false, notify: (_, body, _) =>
        {
            output.Writer.TryWrite(body);
            return ValueTask.CompletedTask;
        });
        var run = host.HandleAsync("command/exec", JsonSerializer.SerializeToElement(new
        {
            command = new[] { "/bin/bash", "--noprofile", "--norc", "-c", "printf 'ready\\n'; read -r line" },
            cwd = fixture.Root,
            processId = "kill",
            streamStdin = true,
            streamStdoutStderr = true,
        }), Ct);
        var ready = await output.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("ready\n", Encoding.UTF8.GetString(Convert.FromBase64String(ready.GetProperty("deltaBase64").GetString()!)));
        await host.HandleAsync("command/exec/terminate", JsonSerializer.SerializeToElement(new { processId = "kill" }), Ct);
        Assert.NotEqual(0, Code(await run.WaitAsync(TimeSpan.FromSeconds(10), Ct)));
    }

    [Fact]
    public async Task UnsupportedPolicyAndUnauthorizedWorkspaceDoNotStart()
    {
        RequireMac();
        using var fixture = new Fixture();
        var authorizations = 0;
        var adapter = new CodexMacOsLeafCommandAdapter((_, _) =>
        {
            authorizations++;
            return ValueTask.FromResult(new CodexMacOsLeafPolicy(fixture.Root, false));
        });
        await using var host = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => host.HandleAsync("command/exec",
            JsonSerializer.SerializeToElement(new { command = new[] { "/usr/bin/true" }, processId = "pty", tty = true }), Ct));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => host.HandleAsync("command/exec",
            JsonSerializer.SerializeToElement(new { command = new[] { "/usr/bin/true" }, permissionProfile = ":danger-full-access" }), Ct));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => host.HandleAsync("command/exec",
            JsonSerializer.SerializeToElement(new { command = new[] { "/usr/bin/true" }, env = new { DYLD_INSERT_LIBRARIES = "ignored" } }), Ct));
        Assert.Equal(0, authorizations);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.HandleAsync("command/exec",
            JsonSerializer.SerializeToElement(new { command = new[] { "/usr/bin/true" }, cwd = fixture.Outside }), Ct));
        Assert.Equal(1, authorizations);
    }

    [Fact]
    public async Task NonMacPlatformFailsClosed()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "The non-macOS denial is tested on another platform.");
        var adapter = new CodexMacOsLeafCommandAdapter((_, _) => throw new InvalidOperationException("Must not authorize."));
        await using var host = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => host.HandleAsync("command/exec",
            JsonSerializer.SerializeToElement(new { command = new[] { "/usr/bin/true" } }), Ct));
    }

    private static void RequireMac() => Assert.SkipUnless(OperatingSystem.IsMacOS(), "Actual Seatbelt enforcement requires macOS.");

    private static int Code(JsonElement result) => result.GetProperty("exitCode").GetInt32();

    private sealed class Fixture : IDisposable
    {
        private readonly string outer = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-leaf-").FullName);
        internal string Root { get; }
        internal string Outside { get; }

        internal Fixture()
        {
            Root = Path.Combine(outer, "allowed");
            Outside = Path.Combine(outer, "outside");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Outside);
        }

        internal CodexCommandExecution Host(bool writes,
            Func<string, JsonElement, CancellationToken, ValueTask>? notify = null) =>
            new("owner", new CodexMacOsLeafCommandAdapter((_, _) => ValueTask.FromResult(new CodexMacOsLeafPolicy(Root, writes))),
                notify ?? ((_, _, _) => ValueTask.CompletedTask));

        internal Task<JsonElement> Exec(CodexCommandExecution host, string[] command, string? profile = null) =>
            host.HandleAsync("command/exec", JsonSerializer.SerializeToElement(new { command, cwd = Root, permissionProfile = profile }), Ct);

        public void Dispose() => Directory.Delete(outer, true);
    }
}
