using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexConfigurationResolutionTests
{
    [Fact]
    public void ResolvesPrecedenceDisabledLayersAndExactLeafOrigins()
    {
        var result = CodexConfigurationResolver.Resolve(new("revision", [
            Layer(CodexConfigurationLayerKind.Package, "package", """{"model":"package","nested":{"keep":1,"replace":1},"a/b":{"~key":1}}"""),
            Layer(CodexConfigurationLayerKind.User, "user", """{"model":"user","nested":{"replace":2}}"""),
            Layer(CodexConfigurationLayerKind.Project, "disabled", """{"model":"untrusted","nested":{"keep":9}}""", false),
            Layer(CodexConfigurationLayerKind.Runtime, "request", """{"model":"explicit","nested":{"extra":[1,2]}}""")
        ]));
        Assert.Equal("explicit", result.Config.GetProperty("model").GetString());
        Assert.Equal(1, result.Config.GetProperty("nested").GetProperty("keep").GetInt32());
        Assert.Equal(2, result.Config.GetProperty("nested").GetProperty("replace").GetInt32());
        Assert.Equal("request", result.Origins["/model"].Name);
        Assert.Equal("user", result.Origins["/nested/replace"].Name);
        Assert.Equal("package", result.Origins["/nested/keep"].Name);
        Assert.Equal("package", result.Origins["/a~1b/~0key"].Name);
    }

    [Fact]
    public void StructuredFeaturesPreserveMetadataAcrossBooleanAndTableOverrides()
    {
        var result = CodexConfigurationResolver.Resolve(new("revision", [
            Layer(CodexConfigurationLayerKind.Package, "package", """{"features":{"code_mode":true,"guardianv3":{"enabled":false,"future":true}}}"""),
            Layer(CodexConfigurationLayerKind.User, "user", """{"features":{"code_mode":{"future":"kept"},"guardianv3":true}}"""),
            Layer(CodexConfigurationLayerKind.Project, "project", """{"features":{"code_mode":false}}""")
        ]));
        Assert.False(result.GetFeatureOverride("code_mode"));
        Assert.True(result.GetFeatureOverride("guardianv3"));
        Assert.Null(result.GetFeatureOverride("not_configured"));
        var features = result.Config.GetProperty("features");
        Assert.Equal("kept", features.GetProperty("code_mode").GetProperty("future").GetString());
        Assert.True(features.GetProperty("guardianv3").GetProperty("future").GetBoolean());
        Assert.Equal("project", result.Origins["/features/code_mode/enabled"].Name);
        Assert.Equal("package", result.Origins["/features/guardianv3/future"].Name);
    }

    [Fact]
    public void ProviderIdentityAndShellFilterRepresentationsAreNotPartiallyInherited()
    {
        var result = CodexConfigurationResolver.Resolve(new("revision", [
            Layer(CodexConfigurationLayerKind.Package, "package", """{"tui":{"status_line_provider":{"server":"not-allowed"}},"shell_environment_policy":{"exclude":["secret"],"include_only":["PATH"],"inherit":"core"}}"""),
            Layer(CodexConfigurationLayerKind.User, "user", """{"tui":{"status_line_provider":{"server":"first","tool":"first-tool"}},"shell_environment_policy":{"filters":{"PATH":"include"}}}"""),
            Layer(CodexConfigurationLayerKind.Profile, "profile", """{"tui":{"status_line_provider":{"server":"second"}}}"""),
            Layer(CodexConfigurationLayerKind.Project, "project", """{"tui":{"status_line_provider":{"server":"not-allowed"}}}""")
        ]));
        var provider = result.Config.GetProperty("tui").GetProperty("status_line_provider");
        Assert.Equal("second", provider.GetProperty("server").GetString());
        Assert.False(provider.TryGetProperty("tool", out _));
        Assert.False(result.Origins.ContainsKey("/tui/status_line_provider/tool"));
        var shell = result.Config.GetProperty("shell_environment_policy");
        Assert.False(shell.TryGetProperty("exclude", out _));
        Assert.False(shell.TryGetProperty("include_only", out _));
        Assert.Equal("core", shell.GetProperty("inherit").GetString());
        Assert.True(shell.TryGetProperty("filters", out _));
    }

    [Fact]
    public void RejectsDuplicateKeysAndOutOfOrderLayers()
    {
        Assert.Throws<CodexThreadRpcException>(() => CodexConfigurationResolver.Resolve(new("revision", [
            Layer(CodexConfigurationLayerKind.User, "user", """{"nested":[{"duplicate":1,"duplicate":2}]}""")
        ])));
        Assert.Throws<CodexThreadRpcException>(() => CodexConfigurationResolver.Resolve(new("revision", [
            Layer(CodexConfigurationLayerKind.Runtime, "runtime", """{}"""),
            Layer(CodexConfigurationLayerKind.User, "user", """{}""")
        ])));
    }

    [Fact]
    public async Task LaterRequirementCannotEraseMalformedHostConstraint()
    {
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "codex-config-resolution-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(directory.FullName, "requirements.toml");
            await File.WriteAllTextAsync(path, """allowed_approval_policies = ["never"]""", TestContext.Current.CancellationToken);
            var source = new CodexFileConfigurationSource(new(directory.FullName)
            {
                IgnoreProjectConfig = true,
                RequirementsFiles = [path]
            });
            using var requirements = JsonDocument.Parse("""{"approval_policy":"invalid"}""");
            var account = new CodexThreadConfigurationSnapshot("account", [], requirements.RootElement.Clone());
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => source.LoadAsync(
                new("thread/start", null, directory.FullName), account, TestContext.Current.CancellationToken));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task LegacyManagedLayersAreLoadedAboveSessionFlagsAndCannotBeBypassedByTurnOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "codex-managed-config-" + Guid.NewGuid().ToString("N")));
        try
        {
            var file = Path.Combine(directory.FullName, "managed.toml");
            var mdm = Path.Combine(directory.FullName, "mdm.toml");
            await File.WriteAllTextAsync(file, "model = \"file-model\"\nservice_tier = \"default\"\n", ct);
            await File.WriteAllTextAsync(mdm, "model = \"managed-model\"\n", ct);
            var source = new CodexFileConfigurationSource(new(directory.FullName)
            {
                IgnoreProjectConfig = true, LegacyManagedConfigFile = file, LegacyManagedMdmConfigFile = mdm
            });
            var snapshot = await source.LoadAsync(new("thread/start", null, directory.FullName),
                new("base", [Layer(CodexConfigurationLayerKind.User, "account", """{"model":"account-model"}""")]), ct);
            Assert.Equal(CodexConfigurationLayerKind.LegacyManagedMdm, snapshot.Layers[^1].Kind);
            var raw = CodexConfigurationResolver.Resolve(snapshot);
            Assert.Equal("managed-model", raw.Config.GetProperty("model").GetString());
            Assert.Equal(CodexConfigurationLayerKind.LegacyManagedMdm, raw.Origins["/model"].Kind);

            var storage = new TestStorage();
            var store = new CodexThreadStore(storage, "account");
            var host = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
                Defaults(directory.FullName), (_, _, _) => throw new InvalidOperationException("Unexpected dispatch."),
                (_, _) => Task.CompletedTask, configurationSource: (_, _) => Task.FromResult(snapshot));
            var response = await host.InvokeAsync("thread/start", Json("""{"model":"requested","serviceTier":"priority"}"""), ct);
            var id = Guid.Parse(response.GetProperty("thread").GetProperty("id").GetString()!);
            var thread = await store.ReadAsync(id, ct);
            Assert.Equal("managed-model", thread.Settings.GetProperty("model").GetString());
            Assert.Equal("default", thread.Settings.GetProperty("serviceTier").GetString());
            Assert.Equal("requested", thread.Settings.GetProperty("nativeConfigurationOverrides").GetProperty("model").GetString());

            var revision = thread.Revision;
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("turn/start",
                Json($$"""{"threadId":"{{id}}","input":[{"type":"text","text":"hello"}],"serviceTierForTurn":"priority"}"""), ct));
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("turn/start",
                Json($$"""{"threadId":"{{id}}","input":[{"type":"text","text":"hello"}],"model":"override"}"""), ct));
            Assert.Equal(revision, (await store.ReadAsync(id, ct)).Revision);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task LegacyManagedCannotRelaxModernRequirements()
    {
        var storage = new TestStorage();
        var store = new CodexThreadStore(storage, "account");
        var snapshot = new CodexThreadConfigurationSnapshot("revision",
            [Layer(CodexConfigurationLayerKind.LegacyManagedFile, "managed", """{"model":"legacy"}""")],
            Json("""{"model":["required"]}"""));
        var host = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Defaults(Path.GetTempPath()), (_, _, _) => throw new InvalidOperationException("Unexpected dispatch."),
            (_, _) => Task.CompletedTask, configurationSource: (_, _) => Task.FromResult(snapshot));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/start", Json("{}"), TestContext.Current.CancellationToken));
        Assert.Empty(storage.Rows);
    }

    private static JsonElement Defaults(string cwd) => Json($$"""{"model":"initial","modelProvider":"openai","cwd":{{JsonSerializer.Serialize(cwd)}},"approvalPolicy":"never","sandbox":"read-only","approvalsReviewer":"user"}""");

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TestStorage : ICodexThreadStorage
    {
        internal Dictionary<Guid, JsonElement> Rows { get; } = [];
        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.TryGetValue(threadId, out var row) ? (JsonElement?)row.Clone() : null);
        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document, CancellationToken cancellationToken)
        {
            var actual = Rows.TryGetValue(threadId, out var row) ? (long?)row.GetProperty("revision").GetInt64() : null;
            if (actual != expectedRevision) return Task.FromResult(false);
            Rows[threadId] = document.Clone();
            return Task.FromResult(true);
        }
    }

    private static CodexThreadConfigurationLayer Layer(CodexConfigurationLayerKind kind, string name, string json, bool trusted = true)
    {
        using var document = JsonDocument.Parse(json);
        return new(kind, name, document.RootElement.Clone(), trusted);
    }
}
