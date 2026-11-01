using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexFileSystemTests : IAsyncLifetime
{
    private readonly string root = Directory.CreateTempSubdirectory("adv-native-fs-").FullName;
    private readonly List<CodexFileSystemAccess> access = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CodexFileSystem fs;
    public CodexFileSystemTests()
    {
        fs = new((request, _) => { access.Add(request); return ValueTask.CompletedTask; }, (_, _, _) => ValueTask.CompletedTask);
    }
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() { await fs.DisposeAsync(); Directory.Delete(root, true); }
    private string PathFor(string name) => Path.Combine(root, name);
    private Task<JsonElement> Call(string method, object value) => fs.HandleAsync("owner", method, JsonSerializer.SerializeToElement(value, Json));

    [Fact]
    public void ContainmentRecognizesFilesystemRootAndRejectsSiblingPrefixes()
    {
        var filesystemRoot = Path.GetPathRoot(root) ?? throw new InvalidOperationException("Fixture root is not absolute.");
        Assert.True(CodexFileSystem.IsWithin(root, filesystemRoot));
        Assert.True(CodexFileSystem.IsWithin(PathFor("child"), root));
        Assert.False(CodexFileSystem.IsWithin(root + "-sibling", root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task RawBytesRoundTripWithoutTextConversion(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(x => (byte)x).ToArray();
        var path = PathFor("binary");
        Assert.Equal("{}", (await Call("fs/writeFile", new CodexFsWriteFile(path, Convert.ToBase64String(bytes)))).GetRawText());
        var reply = await Call("fs/readFile", new CodexFsPath(path));
        Assert.Equal(bytes, Convert.FromBase64String(Assert.IsType<string>(reply.GetProperty("dataBase64").GetString())));
        Assert.Equal([path], access[0].WritePaths); Assert.Equal([path], access[1].ReadPaths);
    }

    [Theory]
    [InlineData("Zg")]
    [InlineData("Zh==")]
    [InlineData("Zg==\n")]
    [InlineData("____")]
    public async Task InvalidBase64FailsBeforeAuthorizationOrTruncation(string encoded)
    {
        var path = PathFor("preserve"); await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/writeFile", new CodexFsWriteFile(path, encoded)));
        Assert.Equal(-32600, error.Code); Assert.Empty(access);
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteDoesNotCreateParentsAndFollowsFinalSymlink()
    {
        var error = await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/writeFile", new CodexFsWriteFile(PathFor("missing/file"), "")));
        Assert.Equal(-32603, error.Code); Assert.False(Directory.Exists(PathFor("missing")));
        await File.WriteAllTextAsync(PathFor("target"), "long original", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(PathFor("link"), "target");
        await Call("fs/writeFile", new CodexFsWriteFile(PathFor("link"), "eA=="));
        Assert.Equal("x", await File.ReadAllTextAsync(PathFor("target"), TestContext.Current.CancellationToken));
        Assert.Equal("target", new FileInfo(PathFor("link")).LinkTarget);
    }

    [Fact]
    public async Task ReadRejectsDirectoryAndOversizedSparseFileWithoutPartialSuccess()
    {
        Assert.Equal(-32600, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/readFile", new CodexFsPath(root)))).Code);
        var path = PathFor("oversized");
        using (var file = File.Create(path)) file.SetLength(CodexFileSystem.MaximumReadBytes + 1);
        Assert.Equal(-32600, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/readFile", new CodexFsPath(path)))).Code);
    }

    [Fact]
    public async Task PosixFifoReadDoesNotWaitForWriter()
    {
        if (OperatingSystem.IsWindows()) return;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/mkfifo")
            { ArgumentList = { PathFor("fifo") }, UseShellExecute = false }) ?? throw new InvalidOperationException("FIFO fixture process did not start.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken); Assert.Equal(0, process.ExitCode);
        var call = Call("fs/readFile", new CodexFsPath(PathFor("fifo")));
        var error = await Assert.ThrowsAsync<CodexFileSystemException>(() => call.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(-32600, error.Code);
    }

    [Fact]
    public async Task RecursiveDefaultsAndExclusiveSingleDirectoryMatchRust()
    {
        await Call("fs/createDirectory", new CodexFsCreateDirectory(PathFor("parents/child")));
        await Call("fs/createDirectory", new CodexFsCreateDirectory(PathFor("parents/child"), null));
        Assert.True(Directory.Exists(PathFor("parents/child")));
        Assert.Equal(-32603, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/createDirectory", new CodexFsCreateDirectory(PathFor("absent/child"), false)))).Code);
        await Call("fs/createDirectory", new CodexFsCreateDirectory(PathFor("single"), false));
        Assert.Equal(-32603, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/createDirectory", new CodexFsCreateDirectory(PathFor("single"), false)))).Code);
    }

    [Fact]
    public async Task MetadataAndListingFollowTargetsButRetainLinkIdentity()
    {
        await File.WriteAllTextAsync(PathFor(".hidden"), "body", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(PathFor("directory"));
        File.CreateSymbolicLink(PathFor("file-link"), ".hidden");
        Directory.CreateSymbolicLink(PathFor("directory-link"), "directory");
        File.CreateSymbolicLink(PathFor("broken"), "absent");
        var metadata = await Call("fs/getMetadata", new CodexFsPath(PathFor("file-link")));
        Assert.True(metadata.GetProperty("isFile").GetBoolean()); Assert.True(metadata.GetProperty("isSymlink").GetBoolean());
        Assert.True(metadata.GetProperty("modifiedAtMs").GetInt64() > 0); Assert.False(metadata.TryGetProperty("size", out _));
        var entries = (await Call("fs/readDirectory", new CodexFsPath(root))).GetProperty("entries").EnumerateArray().ToArray();
        Assert.Contains(entries, e => e.GetProperty("fileName").GetString() == ".hidden");
        Assert.Contains(entries, e => e.GetProperty("fileName").GetString() == "directory-link" && e.GetProperty("isDirectory").GetBoolean());
        Assert.DoesNotContain(entries, e => e.GetProperty("fileName").GetString() == "broken");
        Assert.Equal(-32603, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/getMetadata", new CodexFsPath(PathFor("broken"))))).Code);
    }

    [Fact]
    public async Task RemoveDefaultsIgnoreOnlyMissingAndNeverTraverseFinalDirectoryLink()
    {
        await Call("fs/remove", new CodexFsRemove(PathFor("missing")));
        Assert.Equal(-32603, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/remove", new CodexFsRemove(PathFor("missing"), Force: false)))).Code);
        Directory.CreateDirectory(PathFor("target")); await File.WriteAllTextAsync(PathFor("target/retained"), "x", TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(PathFor("link"), "target");
        await Call("fs/remove", new CodexFsRemove(PathFor("link")));
        Assert.True(File.Exists(PathFor("target/retained"))); Assert.Null(new FileInfo(PathFor("link")).LinkTarget);
        Assert.Equal(-32603, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/remove", new CodexFsRemove(PathFor("target"), false, true)))).Code);
        await Call("fs/remove", new CodexFsRemove(PathFor("target"), null, null));
        Assert.False(Directory.Exists(PathFor("target")));
    }

    [Fact]
    public async Task DirectoryCopyMergesPreservesLinksAndRejectsResolvedDescendants()
    {
        Directory.CreateDirectory(PathFor("source/nested")); Directory.CreateDirectory(PathFor("destination"));
        await File.WriteAllTextAsync(PathFor("source/nested/file"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(PathFor("destination/retained"), "keep", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(PathFor("source/link"), "nested/file");
        Assert.Equal(-32600, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/copy", new CodexFsCopy(PathFor("source"), PathFor("destination"))))).Code);
        await Call("fs/copy", new CodexFsCopy(PathFor("source"), PathFor("destination"), true));
        Assert.Equal("x", await File.ReadAllTextAsync(PathFor("destination/nested/file"), TestContext.Current.CancellationToken));
        Assert.Equal("nested/file", new FileInfo(PathFor("destination/link")).LinkTarget);
        Assert.Equal("keep", await File.ReadAllTextAsync(PathFor("destination/retained"), TestContext.Current.CancellationToken));
        Directory.CreateSymbolicLink(PathFor("alias"), "source");
        foreach (var destination in new[] { "source", "source/new/deep", "alias/new/deep" })
            Assert.Equal(-32600, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/copy", new CodexFsCopy(PathFor("source"), PathFor(destination), true)))).Code);
        Assert.False(Directory.Exists(PathFor("source/new")));
    }

    [Fact]
    public async Task DirectoryCopyRejectsCaseAliasesOnCaseInsensitiveVolumes()
    {
        var source = PathFor("CaseSource"); Directory.CreateDirectory(source);
        var alias = PathFor("casesource");
        if (!Directory.Exists(alias)) return; // Different directories on a case-sensitive volume.
        var destination = Path.Combine(alias, "new");
        var error = await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/copy", new CodexFsCopy(source, destination, true)));
        Assert.Equal(-32600, error.Code);
        Assert.False(Directory.Exists(Path.Combine(source, "new")));
    }

    [Fact]
    public async Task CopyLinkNeverCopiesItsTargetAndCannotOverwriteExistingLink()
    {
        File.CreateSymbolicLink(PathFor("broken"), "missing");
        await Call("fs/copy", new CodexFsCopy(PathFor("broken"), PathFor("copied")));
        Assert.Equal("missing", new FileInfo(PathFor("copied")).LinkTarget);
        Assert.Equal(-32603, (await Assert.ThrowsAsync<CodexFileSystemException>(() => Call("fs/copy", new CodexFsCopy(PathFor("broken"), PathFor("copied"))))).Code);
    }

    [Fact]
    public async Task MissingFieldsNullCopyOptionAndRelativePathsAreInvalid()
    {
        foreach (var json in new[] { "{}", "{\"path\":null}", "{\"path\":\"relative\"}" })
            Assert.Equal(-32600, (await Assert.ThrowsAsync<CodexFileSystemException>(() => fs.HandleAsync("owner", "fs/readFile", JsonDocument.Parse(json).RootElement, TestContext.Current.CancellationToken))).Code);
        var copy = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            { ["sourcePath"] = root, ["destinationPath"] = PathFor("x"), ["recursive"] = null });
        Assert.Equal(-32600, (await Assert.ThrowsAsync<CodexFileSystemException>(() => fs.HandleAsync("owner", "fs/copy", copy, TestContext.Current.CancellationToken))).Code);
        Assert.Empty(access);
    }

    [Fact]
    public async Task DeniedOrCancelledCallCannotTouchFiles()
    {
        await using var denied = new CodexFileSystem((_, _) => throw new UnauthorizedAccessException(), (_, _, _) => ValueTask.CompletedTask);
        var input = JsonSerializer.SerializeToElement(new CodexFsWriteFile(PathFor("denied"), ""), Json);
        Assert.Equal(-32603, (await Assert.ThrowsAsync<CodexFileSystemException>(() => denied.HandleAsync("owner", "fs/writeFile", input, TestContext.Current.CancellationToken))).Code);
        Assert.False(File.Exists(PathFor("denied")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.HandleAsync("owner", "fs/writeFile", input, cancelled.Token));
        Assert.Empty(access); Assert.False(File.Exists(PathFor("denied")));
    }
}
