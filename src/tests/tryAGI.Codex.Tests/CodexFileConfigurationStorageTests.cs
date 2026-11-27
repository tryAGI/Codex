using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexFileConfigurationStorageTests
{
    [Fact]
    public async Task ReadWriteReloadUsesActualFileVersionAndPreservesUnrelatedToml()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        const string original = """
            # Preserve this heading.
            model = 'old' # model comment
            model_reasoning_effort = "medium"
            project_root_markers = [".git", "marker"]

            [features]
            code_mode = { enabled = false, other = 3 } # feature comment

            [mcp_servers.example]
            command = "example"
            args = ["--example"]

            [mcp_servers.example.env]
            TOKEN = 'unchanged-secret'

            [projects."/example"]
            trust_level = "trusted"
            """;
        await fixture.Write(original, ct);
        CodexThreadConfigurationSnapshot? reloaded = null;
        var storage = fixture.Open(reload: (snapshot, _) =>
        {
            reloaded = snapshot;
            return Task.CompletedTask;
        });
        var read = new CodexConfigurationReadRpc(storage.ReadConfigurationAsync);
        var write = new CodexConfigurationWriteRpc(storage);
        var initial = await read.InvokeAsync("config/read", Json("""{"includeLayers":true}"""), ct);
        var version = initial.GetProperty("layers")[0].GetProperty("version").GetString();
        var batch = new JsonObject
        {
            ["expectedVersion"] = version,
            ["reloadUserConfig"] = true,
            ["edits"] = JsonNode.Parse("""
                [{"keyPath":"model","value":"new","mergeStrategy":"replace"},
                 {"keyPath":"model_reasoning_effort","value":null,"mergeStrategy":"replace"},
                 {"keyPath":"features.code_mode","value":true,"mergeStrategy":"upsert"},
                 {"keyPath":"features.tool_suggest","value":true,"mergeStrategy":"replace"}]
                """)
        };
        var result = await write.InvokeAsync("config/batchWrite", Json(batch.ToJsonString()), ct);
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.NotEqual(version, result.GetProperty("version").GetString());
        var actual = await File.ReadAllTextAsync(fixture.FilePath, ct);
        Assert.Contains("# Preserve this heading.", actual);
        Assert.Contains("# model comment", actual);
        Assert.Contains("# feature comment", actual);
        Assert.Contains("TOKEN = 'unchanged-secret'", actual);
        Assert.Contains("project_root_markers = [\".git\", \"marker\"]", actual);
        Assert.Contains("[projects.\"/example\"]\ntrust_level = \"trusted\"", actual);
        var config = CodexToml.Parse(actual);
        Assert.Equal("new", config["model"]!.GetValue<string>());
        Assert.False(config.ContainsKey("model_reasoning_effort"));
        Assert.True(config["features"]!["code_mode"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(3L, config["features"]!["code_mode"]!["other"]!.GetValue<long>());
        Assert.True(config["features"]!["tool_suggest"]!.GetValue<bool>());
        Assert.NotNull(reloaded);
        Assert.Equal("new", CodexConfigurationResolver.Resolve(reloaded).Config.GetProperty("model").GetString());
        var reopened = fixture.Open();
        var next = await reopened.ReadAsync(ct);
        Assert.Equal(result.GetProperty("version").GetString(), next.Configuration.Metadata[0].Version);
        Assert.DoesNotContain("unchanged-secret", result.GetRawText());
        Assert.Empty(Directory.GetFiles(fixture.Home, "*.tmp"));
    }

    [Fact]
    public async Task MissingUserFileIsCreatedOnlyAfterSuccessfulAdmission()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var storage = fixture.Open();
        var before = await storage.ReadAsync(ct);
        Assert.Equal("missing", Assert.Single(before.Configuration.Metadata).Version);
        Assert.False(File.Exists(fixture.FilePath));
        fixture.Deny = true;
        var rpc = new CodexConfigurationWriteRpc(storage);
        var denied = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"model","value":"native","mergeStrategy":"replace","expectedVersion":"missing"}"""), ct));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigRequirementReadonly, denied.Failure);
        Assert.False(File.Exists(fixture.FilePath));
        fixture.Deny = false;
        var result = await rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"model","value":"native","mergeStrategy":"replace","expectedVersion":"missing"}"""), ct);
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal("native", CodexToml.Parse(await File.ReadAllTextAsync(fixture.FilePath, ct))["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task ManagedSourceAndExternalEditConflictsCannotOverwriteNewerFile()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        await fixture.Write("model = 'old'\n", ct);
        var storage = fixture.Open();
        var before = await storage.ReadAsync(ct);
        var candidate = new CodexConfigurationWriteCandidate(before.Configuration.Snapshot.Revision,
            before.Configuration.Metadata[0].Version, fixture.FilePath, Json("""{"model":"new"}"""), false);
        await fixture.Write("model = 'external' # changed\n", ct);
        Assert.Null(await storage.TryCommitAsync(candidate, ct));
        Assert.Equal("model = 'external' # changed\n", await File.ReadAllTextAsync(fixture.FilePath, ct));

        before = await storage.ReadAsync(ct);
        candidate = candidate with { ExpectedStackRevision = before.Configuration.Snapshot.Revision,
            ExpectedUserVersion = before.Configuration.Metadata[0].Version };
        fixture.AccountRevision = "new-policy";
        Assert.Null(await storage.TryCommitAsync(candidate, ct));
        Assert.Equal("model = 'external' # changed\n", await File.ReadAllTextAsync(fixture.FilePath, ct));
    }

    [Fact]
    public async Task CompetingAdaptersCommitOnlyOneExpectedPreimage()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        await fixture.Write("model = 'old'\n", ct);
        var first = fixture.Open();
        var second = fixture.Open();
        var before = await first.ReadAsync(ct);
        var a = new CodexConfigurationWriteCandidate(before.Configuration.Snapshot.Revision, before.Configuration.Metadata[0].Version,
            fixture.FilePath, Json("""{"model":"first"}"""), false);
        var b = a with { UserConfig = Json("""{"model":"second"}""") };
        var results = await Task.WhenAll(first.TryCommitAsync(a, ct), second.TryCommitAsync(b, ct));
        Assert.Single(results, item => item is not null);
        var persisted = CodexToml.Parse(await File.ReadAllTextAsync(fixture.FilePath, ct));
        Assert.Equal(results[0] is null ? "second" : "first", persisted["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task RawCandidateCannotChangeSecretsOrManagedRequirements()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        await fixture.Write("model = 'old'\n[mcp_servers.example.env]\nTOKEN = 'secret'\n", ct);
        var storage = fixture.Open();
        var before = await storage.ReadAsync(ct);
        var invalid = new CodexConfigurationWriteCandidate(before.Configuration.Snapshot.Revision, before.Configuration.Metadata[0].Version,
            fixture.FilePath, Json("""{"model":"new","mcp_servers":{"example":{"env":{"TOKEN":"changed"}}}}"""), false);
        await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => storage.TryCommitAsync(invalid, ct));
        Assert.Contains("TOKEN = 'secret'", await File.ReadAllTextAsync(fixture.FilePath, ct));
        fixture.Requirements = Json("""{"model":["old"]}""");
        var rpc = new CodexConfigurationWriteRpc(storage);
        var denied = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"model","value":"new","mergeStrategy":"replace"}"""), ct));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigRequirementReadonly, denied.Failure);
    }

    [Fact]
    public async Task FailureAfterPersistenceIsNotReportedAsSuccessfulReload()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        await fixture.Write("model = 'old'\n", ct);
        var calls = 0;
        var storage = fixture.Open(reload: (_, _) =>
        {
            calls++;
            throw new IOException("injected reload failure");
        });
        var rpc = new CodexConfigurationWriteRpc(storage);
        var error = await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("config/batchWrite",
            Json("""{"reloadUserConfig":true,"edits":[{"keyPath":"model","value":"new","mergeStrategy":"replace"}]}"""), ct));
        Assert.Contains("unconfirmed", error.Message);
        Assert.Equal(1, calls);
        Assert.Equal("new", CodexToml.Parse(await File.ReadAllTextAsync(fixture.FilePath, ct))["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostCommitHotReloadUpdatesActualFeatureRegistryWithoutDiscardingRuntimePolicy()
    {
        RequireUnix();
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        await fixture.Write("[features]\ncode_mode = false\ntool_suggest = false\nmemories = true\n", ct);
        var registry = new CodexRuntimeFeatureRegistry(
            [new("code_mode", "stable", false), new("tool_suggest", "stable", true, true),
             new("memories", "stable", false, true)],
            new("initial", new Dictionary<string, bool> { ["code_mode"] = false, ["tool_suggest"] = false },
                new Dictionary<string, bool> { ["memories"] = false }));
        registry.SetRuntimeEnablement("initial", new Dictionary<string, bool> { ["tool_suggest"] = false });
        var old = registry.Capture();
        var storage = fixture.Open(reload: registry.ReloadConfigurationAsync);
        var rpc = new CodexConfigurationWriteRpc(storage);
        var result = await rpc.InvokeAsync("config/batchWrite", Json("""
            {"reloadUserConfig":true,"edits":[
              {"keyPath":"features.code_mode","value":true,"mergeStrategy":"replace"},
              {"keyPath":"features.tool_suggest","value":true,"mergeStrategy":"replace"}]}
            """), ct);
        Assert.Equal("ok", result.GetProperty("status").GetString());
        var current = registry.Capture();
        Assert.True(current.IsEnabled("code_mode"));
        Assert.False(current.IsEnabled("tool_suggest"));
        Assert.False(current.IsEnabled("memories"));
        Assert.False(old.IsEnabled("code_mode"));
        Assert.NotEqual(old.PolicyRevision, current.PolicyRevision);
        Assert.False(registry.TryReloadConfiguration(old.PolicyRevision, (await storage.ReadAsync(ct)).Configuration.Snapshot));
    }

    [Fact]
    public void EditorKeepsUntouchedInlineNumericTypeAndRejectsEmbeddedComments()
    {
        const string original = "[features]\ncode_mode = {enabled = false, ratio = 1.0}\n";
        var candidate = CodexToml.Parse(original);
        candidate["features"]!["code_mode"]!["enabled"] = true;
        var rewritten = CodexTomlEdit.Rewrite(original, candidate);
        Assert.Equal(1.0, CodexToml.Parse(rewritten)["features"]!["code_mode"]!["ratio"]!.GetValue<double>());
        const string multiline = "[features]\ncode_mode = {\n enabled = false, # keep me\n ratio = 1.0\n}\n";
        var other = CodexToml.Parse(multiline);
        other["features"]!["code_mode"]!["enabled"] = true;
        Assert.Throws<CodexConfigurationWriteException>(() => CodexTomlEdit.Rewrite(multiline, other));
    }

    [Fact]
    public void EditorHandlesDottedAndImplicitTablePathsWithoutChangingArrayOfTables()
    {
        const string valid = """
            [features.code_mode]
            enabled = false # keep
            [[hooks.pre_tool_use]]
            command = "test"
            """;
        var candidate = CodexToml.Parse(valid);
        candidate["features"]!["code_mode"]!["enabled"] = true;
        candidate["features"]!["memories"] = true;
        var changed = CodexTomlEdit.Rewrite(valid, candidate);
        Assert.Contains("enabled = true # keep", changed);
        Assert.Contains("[[hooks.pre_tool_use]]\ncommand = \"test\"", changed);
        Assert.True(JsonNode.DeepEquals(candidate, CodexToml.Parse(changed)));
        var dotted = CodexToml.Parse("features.memories = false # note\n");
        dotted["features"]!.AsObject().Remove("memories");
        var deleted = CodexTomlEdit.Rewrite("features.memories = false # note\n", dotted);
        Assert.Contains("# note", deleted);
        Assert.True(JsonNode.DeepEquals(dotted, CodexToml.Parse(deleted)));
    }

    private static void RequireUnix() => Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "Unix config storage.");
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory;
        internal string Home => directory.FullName;
        internal string FilePath => Path.Combine(Home, "config.toml");
        internal bool Deny { get; set; }
        internal string AccountRevision { get; set; } = "account";
        internal JsonElement? Requirements { get; set; }

        internal Fixture()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "native-config-" + Guid.NewGuid().ToString("N"));
            directory = Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal Task Write(string value, CancellationToken ct) => File.WriteAllTextAsync(FilePath, value, new UTF8Encoding(false), ct);
        internal CodexFileConfigurationStorage Open(Func<CodexThreadConfigurationSnapshot, CancellationToken, Task>? reload = null) =>
            new(new(Home), ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new CodexThreadConfigurationSnapshot(AccountRevision, [], Requirements));
            }, _ => throw new InvalidOperationException("Unexpected layer."), new HashSet<string>(["code_mode", "memories", "tool_suggest"], StringComparer.Ordinal),
                (_, _, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<IAsyncDisposable?>(Deny ? null : new Grant());
                }, reload);
        public void Dispose() => directory.Delete(recursive: true);
        private sealed class Grant : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
