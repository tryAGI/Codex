using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexConfigurationWriteRpcTests
{
    [Fact]
    public async Task BatchWritesOncePreservingUnrelatedSettingsAndReportsHigherPrecedence()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var rpc = new CodexConfigurationWriteRpc(storage);
        var result = await rpc.InvokeAsync("config/batchWrite", Json("""
            {"expectedVersion":"user-v1","edits":[
              {"keyPath":"model","value":"requested","mergeStrategy":"replace"},
              {"keyPath":"model_reasoning_effort","value":"high","mergeStrategy":"upsert"},
              {"keyPath":"features.code_mode","value":true,"mergeStrategy":"replace"}
            ]}
            """), ct);
        Assert.Equal("okOverridden", result.GetProperty("status").GetString());
        Assert.Equal("user-v2", result.GetProperty("version").GetString());
        Assert.Equal(storage.State.FilePath, result.GetProperty("filePath").GetString());
        var metadata = result.GetProperty("overriddenMetadata");
        Assert.Equal("managed-model", metadata.GetProperty("effectiveValue").GetString());
        Assert.Equal("managed-v1", metadata.GetProperty("overridingLayer").GetProperty("version").GetString());
        Assert.Equal(1, storage.Writes);
        var candidate = storage.LastCandidate!;
        Assert.Equal("stack-v1", candidate.ExpectedStackRevision);
        Assert.Equal("user-v1", candidate.ExpectedUserVersion);
        Assert.Equal("requested", candidate.UserConfig.GetProperty("model").GetString());
        Assert.Equal("high", candidate.UserConfig.GetProperty("model_reasoning_effort").GetString());
        var feature = candidate.UserConfig.GetProperty("features").GetProperty("code_mode");
        Assert.True(feature.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, feature.GetProperty("other").GetInt32());
        Assert.Equal("untouched-secret", candidate.UserConfig.GetProperty("mcp_servers").GetProperty("example").GetProperty("env").GetProperty("TOKEN").GetString());
        Assert.DoesNotContain("untouched-secret", result.GetRawText());
        Assert.DoesNotContain("untouched-secret", JsonSerializer.Serialize(candidate));
    }

    [Fact]
    public async Task NullRemovesOnlyUserOverrideAndPreservesStructuredFeatureParameters()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var rpc = new CodexConfigurationWriteRpc(storage);
        var result = await rpc.InvokeAsync("config/batchWrite", Json("""
            {"edits":[
              {"keyPath":"model_reasoning_effort","value":null,"mergeStrategy":"replace"},
              {"keyPath":"features.code_mode.enabled","value":null,"mergeStrategy":"upsert"},
              {"keyPath":"features.network_proxy","value":null,"mergeStrategy":"replace"}
            ]}
            """), ct);
        Assert.Equal("ok", result.GetProperty("status").GetString());
        var user = storage.LastCandidate!.UserConfig;
        Assert.False(user.TryGetProperty("model_reasoning_effort", out _));
        var code = user.GetProperty("features").GetProperty("code_mode");
        Assert.False(code.TryGetProperty("enabled", out _));
        Assert.Equal(3, code.GetProperty("other").GetInt32());
        var network = user.GetProperty("features").GetProperty("network_proxy");
        Assert.False(network.GetProperty("enabled").GetBoolean());
        Assert.True(network.TryGetProperty("credentials", out _));
    }

    [Theory]
    [InlineData("model_verbosity", "\"invalid\"", CodexConfigurationWriteFailure.ConfigValidationError)]
    [InlineData("mcp_servers.example.env", "\"secret\"", CodexConfigurationWriteFailure.ConfigSchemaUnknownKey)]
    [InlineData("features.unregistered", "true", CodexConfigurationWriteFailure.ConfigSchemaUnknownKey)]
    [InlineData("features.code_mode", "{\"enabled\":true}", CodexConfigurationWriteFailure.ConfigValidationError)]
    [InlineData("approval_policy", "\"never\"", CodexConfigurationWriteFailure.ConfigSchemaUnknownKey)]
    public async Task InvalidSecondEditCannotPersistFirstEdit(string key, string value, CodexConfigurationWriteFailure failure)
    {
        var storage = new Storage();
        var rpc = new CodexConfigurationWriteRpc(storage);
        var json = $$"""
            {"edits":[{"keyPath":"model","value":"first","mergeStrategy":"replace"},
            {"keyPath":"{{key}}","value":{{value}},"mergeStrategy":"replace"}]}
            """;
        var error = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() =>
            rpc.InvokeAsync("config/batchWrite", Json(json), TestContext.Current.CancellationToken));
        Assert.Equal(failure, error.Failure);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task ManagedRequirementAndWrongFileAreRejectedBeforeWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var snapshot = storage.State.Configuration.Snapshot;
        storage.State = storage.State with
        {
            Configuration = storage.State.Configuration with
            {
                Snapshot = snapshot with { Requirements = Json("""{"model":["managed-model"]}""") }
            }
        };
        var rpc = new CodexConfigurationWriteRpc(storage);
        var denied = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"model","value":"managed-model","mergeStrategy":"replace"}"""), ct));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigRequirementReadonly, denied.Failure);
        var file = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"model_reasoning_effort","value":"high","mergeStrategy":"replace","filePath":"relative"}"""), ct));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigLayerReadonly, file.Failure);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task VersionConflictAndAmbiguousCommitDoNotReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var rpc = new CodexConfigurationWriteRpc(storage);
        var old = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"model_verbosity","value":"high","mergeStrategy":"replace","expectedVersion":"old"}"""), ct));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigVersionConflict, old.Failure);
        Assert.Equal(0, storage.Writes);
        storage.Conflict = true;
        var race = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => Write(rpc, ct));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigVersionConflict, race.Failure);
        Assert.Equal(1, storage.Writes);
        storage.Conflict = false;
        storage.Ambiguous = true;
        var unknown = await Assert.ThrowsAsync<CodexThreadRpcException>(() => Write(rpc, ct));
        Assert.Contains("unconfirmed", unknown.Message);
        Assert.DoesNotContain("untouched-secret", unknown.Message);
        Assert.Equal(2, storage.Writes);
    }

    [Fact]
    public async Task ReloadRequiresRegisteredHostCapabilityAndEmptyBatchIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var rpc = new CodexConfigurationWriteRpc(storage);
        var empty = await rpc.InvokeAsync("config/batchWrite", Json("""{"edits":[]}"""), ct);
        Assert.Equal("user-v1", empty.GetProperty("version").GetString());
        Assert.Equal(0, storage.Writes);
        var request = Json("""
            {"edits":[{"keyPath":"model_verbosity","value":"low","mergeStrategy":"replace"}],"reloadUserConfig":true}
            """);
        await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => rpc.InvokeAsync("config/batchWrite", request, ct));
        Assert.Equal(0, storage.Writes);
        storage.State = storage.State with { SupportsHotReload = true };
        await rpc.InvokeAsync("config/batchWrite", request, ct);
        Assert.True(storage.LastCandidate!.ReloadUserConfig);
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task InheritedNetworkBrokerIsDisabledWithoutDeletingItsConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var snapshot = storage.State.Configuration.Snapshot;
        var layer = new CodexThreadConfigurationLayer(CodexConfigurationLayerKind.Package, "package",
            Json("""{"features":{"network_proxy":{"enabled":true,"credential_broker":{"secret":"inherited"}}}}"""));
        var user = snapshot.Layers[0] with { Config = Json("""{"model":"user-model"}""") };
        var packageFile = Path.Combine(Path.GetDirectoryName(storage.State.FilePath)!, "packaged.toml");
        storage.State = storage.State with
        {
            Configuration = new(snapshot with { Layers = [layer, user, snapshot.Layers[1]] },
                [new(layer.Kind, layer.Name, "package-v1", Element(new JsonObject { ["type"] = "packagedDefaults", ["file"] = packageFile })),
                    .. storage.State.Configuration.Metadata])
        };
        var rpc = new CodexConfigurationWriteRpc(storage);
        var result = await rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"features.network_proxy","value":null,"mergeStrategy":"replace"}"""), ct);
        var candidate = storage.LastCandidate!.UserConfig;
        Assert.False(candidate.GetProperty("features").GetProperty("network_proxy").GetBoolean());
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.DoesNotContain("inherited", result.GetRawText());
    }

    [Fact]
    public async Task InvalidHigherLayerCannotLeakRawValuesThroughOverrideMetadata()
    {
        var storage = new Storage();
        var snapshot = storage.State.Configuration.Snapshot;
        storage.State = storage.State with
        {
            Configuration = storage.State.Configuration with
            {
                Snapshot = snapshot with { Layers = [snapshot.Layers[0], snapshot.Layers[1] with
                    { Config = Json("""{"model":{"secret":"untouched-secret"}}""") }] }
            }
        };
        var rpc = new CodexConfigurationWriteRpc(storage);
        var error = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() => rpc.InvokeAsync("config/value/write",
            Json("""{"keyPath":"model","value":"requested","mergeStrategy":"replace"}"""), TestContext.Current.CancellationToken));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigValidationError, error.Failure);
        Assert.DoesNotContain("untouched-secret", error.Message);
        Assert.Equal(0, storage.Writes);
    }

    private static Task<JsonElement> Write(CodexConfigurationWriteRpc rpc, CancellationToken ct) =>
        rpc.InvokeAsync("config/value/write", Json("""{"keyPath":"model_verbosity","value":"high","mergeStrategy":"replace"}"""), ct);

    private static CodexConfigurationWriteSnapshot Snapshot()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var userFile = Path.Combine(cwd, "config.toml");
        var managed = Path.Combine(cwd, "managed.toml");
        var layers = new CodexThreadConfigurationLayer[]
        {
            new(CodexConfigurationLayerKind.User, "user", Json("""
                {"model":"user-model","model_reasoning_effort":"medium",
                "features":{"code_mode":{"enabled":false,"other":3},"network_proxy":{"enabled":true,"credentials":{"provider":"ref"}}},
                "mcp_servers":{"example":{"env":{"TOKEN":"untouched-secret"}}}}
                """)),
            new(CodexConfigurationLayerKind.LegacyManagedFile, "managed", Json("""{"model":"managed-model"}"""))
        };
        var metadata = new CodexConfigurationRpcLayerMetadata[]
        {
            new(layers[0].Kind, "user", "user-v1", Element(new JsonObject { ["type"] = "user", ["file"] = userFile, ["profile"] = null })),
            new(layers[1].Kind, "managed", "managed-v1", Element(new JsonObject { ["type"] = "legacyManagedConfigTomlFromFile", ["file"] = managed }))
        };
        return new(new(new("stack-v1", layers), metadata), "user", userFile,
            new HashSet<string>(["code_mode", "network_proxy"], StringComparer.Ordinal));
    }

    private static JsonElement Element(JsonNode value) => Json(value.ToJsonString());
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

    private sealed class Storage : ICodexConfigurationWriteStorage
    {
        internal CodexConfigurationWriteSnapshot State { get; set; } = Snapshot();
        internal int Writes { get; private set; }
        internal CodexConfigurationWriteCandidate? LastCandidate { get; private set; }
        internal bool Conflict { get; set; }
        internal bool Ambiguous { get; set; }
        public Task<CodexConfigurationWriteSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(State);
        }

        public Task<string?> TryCommitAsync(CodexConfigurationWriteCandidate candidate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            LastCandidate = candidate;
            if (Ambiguous) throw new IOException("untouched-secret");
            return Task.FromResult<string?>(Conflict ? null : "user-v2");
        }
    }
}
