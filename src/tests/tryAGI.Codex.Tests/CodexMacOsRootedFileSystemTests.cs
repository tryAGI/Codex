using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexMacOsRootedFileSystemTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireMac() => Assert.SkipUnless(
        OperatingSystem.IsMacOSVersionAtLeast(12) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
        "Actual rooted Darwin descriptor tests require macOS arm64.");

    private static Task<JsonElement> Request(CodexMacOsRootedFileSystem host, string method, string path) =>
        host.HandleAsync("owner", method, JsonSerializer.SerializeToElement(new { path }), Ct);

    [Fact]
    public async Task ReadsMetadataAndDirectoryEntriesFromPinnedRoot()
    {
        RequireMac();
        using var fixture = new Fixture();
        var folder = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "test.bin");
        var expected = new byte[] { 0, 1, 128, 255 };
        await File.WriteAllBytesAsync(file, expected, Ct);
        await using var host = await fixture.Open();

        var read = await Request(host, "fs/readFile", file);
        Assert.Equal(expected, Convert.FromBase64String(read.GetProperty("dataBase64").GetString()!));

        var metadata = await Request(host, "fs/getMetadata", file);
        Assert.True(metadata.GetProperty("isFile").GetBoolean());
        Assert.False(metadata.GetProperty("isDirectory").GetBoolean());
        Assert.False(metadata.GetProperty("isSymlink").GetBoolean());
        var expectedModified = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
        Assert.InRange(metadata.GetProperty("modifiedAtMs").GetInt64(), expectedModified - 1, expectedModified + 1);
        Assert.True(metadata.GetProperty("createdAtMs").GetInt64() > 0);

        var directory = await Request(host, "fs/readDirectory", fixture.Root);
        var nested = Assert.Single(directory.GetProperty("entries").EnumerateArray());
        Assert.Equal("nested", nested.GetProperty("fileName").GetString());
        Assert.True(nested.GetProperty("isDirectory").GetBoolean());

        var entries = await Request(host, "fs/readDirectory", folder);
        var child = Assert.Single(entries.GetProperty("entries").EnumerateArray());
        Assert.Equal("test.bin", child.GetProperty("fileName").GetString());
        Assert.True(child.GetProperty("isFile").GetBoolean());
        Assert.Equal(["fs/rootOpen", "fs/readFile", "fs/getMetadata", "fs/readDirectory", "fs/readDirectory"],
            fixture.Accesses.Select(x => x.Method));
    }

    [Fact]
    public async Task ExistingIntermediateAndLeafSymlinksNeverResolveOutside()
    {
        RequireMac();
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "outside.txt"), "outside-fixture", Ct);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "external"), fixture.Outside);
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "leaf"), Path.Combine(fixture.Outside, "outside.txt"));
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "internal"), Path.Combine(fixture.Root, "valid"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "valid"), "inside-fixture", Ct);

        await using var host = await fixture.Open();
        foreach (var path in new[] { Path.Combine(fixture.Root, "external", "outside.txt"),
            Path.Combine(fixture.Root, "leaf"), Path.Combine(fixture.Root, "internal") })
        {
            await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/readFile", path));
            await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/getMetadata", path));
        }
        await Assert.ThrowsAsync<CodexFileSystemException>(() =>
            Request(host, "fs/readDirectory", Path.Combine(fixture.Root, "external")));
        var entries = await Request(host, "fs/readDirectory", fixture.Root);
        Assert.Equal("valid", Assert.Single(entries.GetProperty("entries").EnumerateArray()).GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task SwappingAuthorizedParentForSymlinkBeforeKernelOpenIsDenied()
    {
        RequireMac();
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "nested");
        var retained = Path.Combine(fixture.Root, "retained");
        Directory.CreateDirectory(original);
        await File.WriteAllTextAsync(Path.Combine(original, "data"), "inside-fixture", Ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.Outside, "data"), "outside-fixture", Ct);

        await using var host = await fixture.Open(access =>
        {
            if (access.Method == "fs/readFile")
            {
                Directory.Move(original, retained);
                Directory.CreateSymbolicLink(original, fixture.Outside);
            }
        });
        await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/readFile", Path.Combine(original, "data")));
        Assert.Equal("outside-fixture", await File.ReadAllTextAsync(Path.Combine(fixture.Outside, "data"), Ct));
        Assert.Equal("inside-fixture", await File.ReadAllTextAsync(Path.Combine(retained, "data"), Ct));
    }

    [Fact]
    public async Task ReplacedRootInodeCannotSilentlyRebindAuthorizedCapability()
    {
        RequireMac();
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "file"), "original", Ct);
        var moved = Path.Combine(fixture.Outer, "old-root");
        await using var host = await fixture.Open(access =>
        {
            if (access.Method == "fs/readFile")
            {
                Directory.Move(fixture.Root, moved);
                Directory.CreateDirectory(fixture.Root);
                File.WriteAllText(Path.Combine(fixture.Root, "file"), "replacement");
            }
        });
        await Assert.ThrowsAsync<CodexFileSystemException>(() =>
            Request(host, "fs/readFile", Path.Combine(fixture.Root, "file")));
        Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "file"), Ct));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(moved, "file"), Ct));
    }

    [Fact]
    public async Task HardLinkedRegularFileAndFifoAreDeniedWithoutBlockingOrListing()
    {
        RequireMac();
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Outside, "original");
        var alias = Path.Combine(fixture.Root, "alias");
        var fifo = Path.Combine(fixture.Root, "fifo");
        await File.WriteAllTextAsync(outside, "outside-fixture", Ct);
        await NativeFixture("/bin/ln", outside, alias);
        await NativeFixture("/usr/bin/mkfifo", fifo);

        await using var host = await fixture.Open();
        await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/readFile", alias));
        await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/getMetadata", alias));
        await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/readFile", fifo).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var result = await Request(host, "fs/readDirectory", fixture.Root);
        Assert.Empty(result.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task DeniedAuthorizationCannotOpenRootOrReadAnEntry()
    {
        RequireMac();
        using var fixture = new Fixture();
        var denied = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CodexMacOsRootedFileSystem.OpenAsync("owner", fixture.Root, (_, _) =>
            {
                denied++;
                throw new UnauthorizedAccessException();
            }, cancellationToken: Ct));
        Assert.Equal(1, denied);

        await using var host = await fixture.Open(access =>
        {
            if (access.Method != "fs/rootOpen") throw new UnauthorizedAccessException();
        });
        await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/readDirectory", fixture.Root));
        Assert.Equal(2, fixture.Accesses.Count);
    }

    [Fact]
    public async Task LexicalEscapesForeignConnectionAndAllMutatingMethodsFailClosed()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var host = await fixture.Open();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Request(host, "fs/readFile", Path.Combine(fixture.Outside, "secret")));
        await Assert.ThrowsAsync<CodexFileSystemException>(() =>
            Request(host, "fs/readFile", fixture.Root + "/../outside/secret"));
        await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/readFile", "relative"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.HandleAsync("foreign", "fs/readDirectory",
            JsonSerializer.SerializeToElement(new { path = fixture.Root }), Ct));
        foreach (var method in new[] { "fs/writeFile", "fs/createDirectory", "fs/remove", "fs/copy", "fs/watch", "fs/unwatch" })
        {
            Assert.True(CodexMacOsRootedFileSystem.Handles(method));
            var error = await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, method, fixture.Root));
            Assert.Equal(-32010, error.Code);
        }
        Assert.Single(fixture.Accesses);
    }

    [Fact]
    public async Task RootLinkAndOversizedReadsAreRefused()
    {
        RequireMac();
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.Outer, "root-link");
        Directory.CreateSymbolicLink(link, fixture.Root);
        await Assert.ThrowsAsync<IOException>(() => CodexMacOsRootedFileSystem.OpenAsync("owner", link,
            (_, _) => ValueTask.CompletedTask, cancellationToken: Ct));
        var path = Path.Combine(fixture.Root, "large");
        await File.WriteAllBytesAsync(path, new byte[9], Ct);
        await using var host = await fixture.Open(maximumReadBytes: 8);
        await Assert.ThrowsAsync<CodexFileSystemException>(() => Request(host, "fs/readFile", path));
    }

    [Fact]
    public async Task DisposalReleasesAnchorAndStopsSubsequentAdmission()
    {
        RequireMac();
        using var fixture = new Fixture();
        var host = await fixture.Open();
        await host.DisposeAsync();
        await host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Request(host, "fs/readDirectory", fixture.Root));
        Assert.Single(fixture.Accesses);
    }

    [Fact]
    public async Task UnsupportedPlatformIsRefusedBeforeAuthorization()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOSVersionAtLeast(12) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
            "Unsupported-platform branch is tested on other hosts.");
        var calls = 0;
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => CodexMacOsRootedFileSystem.OpenAsync(
            "owner", "/unused", (_, _) => { calls++; return ValueTask.CompletedTask; }, cancellationToken: Ct));
        Assert.Equal(0, calls);
    }

    private static async Task NativeFixture(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture process did not start.");
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(Ct));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Outer { get; } = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-rooted-fs-").FullName);
        internal string Root { get; }
        internal string Outside { get; }
        internal List<CodexFileSystemAccess> Accesses { get; } = [];

        internal Fixture()
        {
            Root = Path.Combine(Outer, "root");
            Outside = Path.Combine(Outer, "outside");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Outside);
        }

        internal Task<CodexMacOsRootedFileSystem> Open(Action<CodexFileSystemAccess>? onAuthorize = null,
            long maximumReadBytes = 16 * 1024 * 1024) => CodexMacOsRootedFileSystem.OpenAsync(
                "owner", Root, (access, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Accesses.Add(access);
                    Assert.Equal("owner", access.ConnectionId);
                    Assert.Empty(access.WritePaths);
                    onAuthorize?.Invoke(access);
                    return ValueTask.CompletedTask;
                }, maximumReadBytes, Ct);

        public void Dispose() => Directory.Delete(Outer, true);
    }
}
