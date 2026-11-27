using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

// This lane requires a digest-verified Rust release and an isolated network
// namespace. Ordinary unit tests never launch the optional Rust runtime.
[Trait("Category", "RustDifferential")]
public sealed class RustFileSystemDifferentialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo directory;
        private readonly CodexFileSystem native;
        private readonly CodexAppServerClient rust;
        internal string NativeRoot { get; }
        internal string RustRoot { get; }

        private Fixture(DirectoryInfo directory, CodexFileSystem native, CodexAppServerClient rust, string nativeRoot, string rustRoot)
        {
            this.directory = directory;
            this.native = native;
            this.rust = rust;
            NativeRoot = nativeRoot;
            RustRoot = rustRoot;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("CODEX_RUST_DIFFERENTIAL") == "1",
                "Use the explicit isolated Rust differential runner.");
            Assert.True(OperatingSystem.IsLinux(), "The initial differential lane requires Linux network isolation.");
            var executable = Environment.GetEnvironmentVariable("CODEX_RUST_DIFFERENTIAL_EXECUTABLE")
                ?? throw new InvalidOperationException("The pinned Rust executable is required.");
            Assert.False(string.IsNullOrWhiteSpace(executable));
            Assert.True(Path.IsPathFullyQualified(executable));
            Assert.True(File.Exists(executable));
            var parentNamespace = Environment.GetEnvironmentVariable("CODEX_RUST_DIFFERENTIAL_PARENT_NETNS")
                ?? throw new InvalidOperationException("The original network namespace is required.");
            Assert.False(string.IsNullOrWhiteSpace(parentNamespace));
            var currentNamespace = new FileInfo("/proc/self/ns/net").LinkTarget;
            Assert.False(string.IsNullOrWhiteSpace(currentNamespace));
            Assert.NotEqual(parentNamespace, currentNamespace);
            var interfaces = File.ReadAllLines("/proc/net/dev").Skip(2).Select(line => line.Split(':')[0].Trim()).ToArray();
            Assert.Equal(new[] { "lo" }, interfaces);

            var root = Directory.CreateTempSubdirectory("codex-rust-differential-");
            var nativeRoot = root.CreateSubdirectory("native").FullName;
            var rustRoot = root.CreateSubdirectory("rust").FullName;
            var codexHome = root.CreateSubdirectory("codex-home").FullName;
            var native = new CodexFileSystem((access, _) =>
            {
                Assert.Equal("differential-owner", access.ConnectionId);
                foreach (var path in access.ReadPaths.Concat(access.WritePaths))
                {
                    var relative = Path.GetRelativePath(nativeRoot, path);
                    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                        throw new UnauthorizedAccessException("The differential fixture permits only its temporary native root.");
                }
                return ValueTask.CompletedTask;
            }, (_, _, _) => ValueTask.CompletedTask);
            try
            {
                var rust = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions
                {
                    ExecutablePath = executable,
                    WorkingDirectory = rustRoot,
                    CodexHome = codexHome,
                    ClientName = "tryagi_differential",
                    ExperimentalApi = true,
                    InitializeTimeout = TimeSpan.FromSeconds(40),
                    MaxOverloadRetries = 0,
                }, Ct);
                return new Fixture(root, native, rust, nativeRoot, rustRoot);
            }
            catch
            {
                await native.DisposeAsync();
                root.Delete(recursive: true);
                throw;
            }
        }

        internal async Task<(JsonElement Native, JsonElement Rust)> CallAsync(string method, Func<string, JsonObject> parameters)
        {
            using var nativeParams = JsonDocument.Parse(parameters(NativeRoot).ToJsonString());
            var nativeResult = await native.HandleAsync("differential-owner", method, nativeParams.RootElement, Ct)
                .WaitAsync(TimeSpan.FromSeconds(20), Ct);
            var rustResult = await rust.RequestAsync(method, parameters(RustRoot), Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            return (nativeResult, rustResult);
        }

        internal async Task CompareAsync(string method, Func<string, JsonObject> parameters)
        {
            var (nativeResult, rustResult) = await CallAsync(method, parameters);
            Assert.True(JsonElement.DeepEquals(nativeResult, rustResult),
                $"{method}: result differs between native SDK and pinned Rust App Server. Native: {nativeResult}; Rust: {rustResult}");
        }

        internal async Task CompareErrorAsync(string method, Func<string, JsonObject> parameters)
        {
            using var nativeParams = JsonDocument.Parse(parameters(NativeRoot).ToJsonString());
            var nativeError = await Assert.ThrowsAsync<CodexFileSystemException>(() =>
                native.HandleAsync("differential-owner", method, nativeParams.RootElement, Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct));
            var rustError = await Assert.ThrowsAsync<CodexAppServerException>(() =>
                rust.RequestAsync(method, parameters(RustRoot), Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct));
            Assert.Equal(rustError.Code, nativeError.Code);
        }

        public async ValueTask DisposeAsync()
        {
            try { await rust.DisposeAsync(); }
            finally
            {
                await native.DisposeAsync();
                directory.Delete(recursive: true);
            }
        }
    }

    private static JsonObject PathParams(string root, string name) => new() { ["path"] = Path.Combine(root, name) };
    private static JsonObject WriteParams(string root, string name, string data) =>
        new() { ["path"] = Path.Combine(root, name), ["dataBase64"] = data };

    [Fact]
    public async Task BinaryWriteReadAndOverwriteMatchRust()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var bytes in new[] { Enumerable.Range(0, 257).Select(x => (byte)x).ToArray(), Array.Empty<byte>() })
        {
            var encoded = Convert.ToBase64String(bytes);
            await f.CompareAsync("fs/writeFile", root => WriteParams(root, "binary", encoded));
            var (native, rust) = await f.CallAsync("fs/readFile", root => PathParams(root, "binary"));
            Assert.True(JsonElement.DeepEquals(native, rust));
            Assert.Equal(encoded, native.GetProperty("dataBase64").GetString());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(f.NativeRoot, "binary"), Ct));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(f.RustRoot, "binary"), Ct));
        }
    }

    [Fact]
    public async Task DirectoryAndMetadataProjectionMatchRust()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CompareAsync("fs/createDirectory", root => PathParams(root, "child/nested"));
        await f.CompareAsync("fs/writeFile", root => WriteParams(root, "child/note", "eA=="));
        foreach (var name in new[] { "child", "child/note" })
        {
            var (native, rust) = await f.CallAsync("fs/getMetadata", root => PathParams(root, name));
            var expectedKeys = new[] { "createdAtMs", "isDirectory", "isFile", "isSymlink", "modifiedAtMs" };
            foreach (var result in new[] { native, rust })
            {
                Assert.Equal(expectedKeys, result.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
                Assert.True(result.GetProperty("createdAtMs").TryGetInt64(out _));
                Assert.True(result.GetProperty("modifiedAtMs").TryGetInt64(out _));
            }
            foreach (var key in new[] { "isDirectory", "isFile", "isSymlink" })
                Assert.Equal(rust.GetProperty(key).GetBoolean(), native.GetProperty(key).GetBoolean());
        }
        var (nativeList, rustList) = await f.CallAsync("fs/readDirectory", root => PathParams(root, "child"));
        static (string? Name, bool Directory, bool File)[] Entries(JsonElement result) =>
            result.GetProperty("entries").EnumerateArray()
                .Select(e => (e.GetProperty("fileName").GetString(), e.GetProperty("isDirectory").GetBoolean(), e.GetProperty("isFile").GetBoolean()))
                .OrderBy(e => e.Item1, StringComparer.Ordinal).ToArray();
        Assert.Equal(Entries(rustList), Entries(nativeList));
        Assert.Equal(2, Entries(nativeList).Length);
    }

    [Fact]
    public async Task InvalidBase64PreservesExistingFileAndMatchesRustError()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CompareAsync("fs/writeFile", root => WriteParams(root, "preserve", "b2xk"));
        foreach (var invalid in new[] { "Zg", "Zh==", "____" })
            await f.CompareErrorAsync("fs/writeFile", root => WriteParams(root, "preserve", invalid));
        var (native, rust) = await f.CallAsync("fs/readFile", root => PathParams(root, "preserve"));
        Assert.True(JsonElement.DeepEquals(native, rust));
        Assert.Equal("b2xk", native.GetProperty("dataBase64").GetString());
    }

    [Fact]
    public async Task RelativePathsAreRejectedWithMatchingError()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CompareErrorAsync("fs/readFile", _ => new JsonObject { ["path"] = "relative" });
    }

    [Fact]
    public async Task CopyAndRemoveHaveMatchingEffects()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CompareAsync("fs/createDirectory", root => PathParams(root, "source/nested"));
        await f.CompareAsync("fs/writeFile", root => WriteParams(root, "source/nested/data", "AAEC"));
        await f.CompareAsync("fs/copy", root => new JsonObject
        {
            ["sourcePath"] = Path.Combine(root, "source"),
            ["destinationPath"] = Path.Combine(root, "destination"),
            ["recursive"] = true,
        });
        await f.CompareAsync("fs/readFile", root => PathParams(root, "destination/nested/data"));
        await f.CompareAsync("fs/remove", root => PathParams(root, "destination"));
        foreach (var root in new[] { f.NativeRoot, f.RustRoot })
        {
            Assert.False(Directory.Exists(Path.Combine(root, "destination")));
            Assert.Equal(new byte[] { 0, 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(root, "source/nested/data"), Ct));
        }
    }
}
