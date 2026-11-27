using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexConfigurationReadRpcTests
{
    [Fact]
    public async Task ReturnsEffectivePublicConfigAndExactOriginsWithoutRawSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var snapshot = Fixture(cwd);
        string? observedCwd = null;
        var rpc = new CodexConfigurationReadRpc((path, _) =>
        {
            observedCwd = path;
            return Task.FromResult(snapshot);
        });
        var response = await rpc.InvokeAsync("config/read", Element(new JsonObject
        {
            ["cwd"] = cwd, ["includeLayers"] = true
        }), ct);
        Assert.Equal(cwd, observedCwd);
        var config = response.GetProperty("config");
        Assert.Equal("managed", config.GetProperty("model").GetString());
        Assert.Equal("medium", config.GetProperty("model_reasoning_effort").GetString());
        Assert.Equal("openai", config.GetProperty("model_provider").GetString());
        Assert.False(config.TryGetProperty("mcp_servers", out _));
        Assert.False(config.TryGetProperty("developer_instructions", out _));
        var origins = response.GetProperty("origins");
        Assert.Equal("legacyManagedConfigTomlFromFile", origins.GetProperty("model").GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("managed-v2", origins.GetProperty("model").GetProperty("version").GetString());
        Assert.Equal("user-v1", origins.GetProperty("model_reasoning_effort").GetProperty("version").GetString());
        Assert.False(origins.TryGetProperty("model_provider", out _));
        var layers = response.GetProperty("layers");
        Assert.Equal(3, layers.GetArrayLength());
        Assert.Equal("managed-v2", layers[0].GetProperty("version").GetString());
        Assert.True(layers[1].TryGetProperty("disabledReason", out _));
        Assert.Equal("{}", layers[1].GetProperty("config").GetRawText());
        Assert.Equal("user-v1", layers[2].GetProperty("version").GetString());
        Assert.DoesNotContain("test-secret", response.GetRawText());
        var noLayers = await rpc.InvokeAsync("config/read", cancellationToken: ct);
        Assert.False(noLayers.TryGetProperty("layers", out _));
    }

    [Fact]
    public async Task ReadsOnlySupportedManagedRequirementsAndRetainsUnknownSpeedState()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = Fixture(Path.GetFullPath(Path.GetTempPath()));
        var rpc = new CodexConfigurationReadRpc((_, _) => Task.FromResult(snapshot));
        var value = await rpc.InvokeAsync("configRequirements/read", cancellationToken: ct);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("supportsIndependentSpeedModes").ValueKind);
        var requirements = value.GetProperty("requirements");
        Assert.Equal("never", requirements.GetProperty("allowedApprovalPolicies")[0].GetString());
        Assert.Equal("read-only", requirements.GetProperty("allowedSandboxModes")[0].GetString());
        Assert.False(requirements.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task RequiresExactAuthenticLayerMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = Fixture(Path.GetFullPath(Path.GetTempPath()));
        var missing = snapshot with { Metadata = snapshot.Metadata.Skip(1).ToArray() };
        var rpc = new CodexConfigurationReadRpc((_, _) => Task.FromResult(missing));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("config/read", cancellationToken: ct));
        var invalid = snapshot.Metadata.ToArray();
        invalid[1] = invalid[1] with { SourceName = Json("""{"type":"user","file":"/config.toml","secret":"test-secret"}""") };
        var wrong = new CodexConfigurationReadRpc((_, _) => Task.FromResult(snapshot with { Metadata = invalid }));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => wrong.InvokeAsync("config/read", cancellationToken: ct));
    }

    [Fact]
    public async Task FeatureListUsesActualNativeRegistryAndLoadedThreadScope()
    {
        var ct = TestContext.Current.CancellationToken;
        var thread = Guid.NewGuid();
        Guid? received = null;
        var rpc = new CodexConfigurationReadRpc((_, _) => throw new InvalidOperationException("Unexpected config read."),
            (id, _) =>
            {
                received = id;
                return Task.FromResult<IReadOnlyList<CodexExperimentalFeatureState>>(
                [
                    new("feature_a", "beta", true, false, "Feature A", "Description", "Announcement"),
                    new("feature_b", "stable", false, true, "Hidden name"),
                    new("legacy", "removed", false, false)
                ]);
            });
        var first = await rpc.InvokeAsync("experimentalFeature/list", Element(new JsonObject
        {
            ["threadId"] = thread.ToString(), ["limit"] = 0
        }), ct);
        Assert.Equal(thread, received);
        Assert.Equal("1", first.GetProperty("nextCursor").GetString());
        var item = first.GetProperty("data")[0];
        Assert.Equal("feature_a", item.GetProperty("name").GetString());
        Assert.True(item.GetProperty("enabled").GetBoolean());
        Assert.False(item.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal("Feature A", item.GetProperty("displayName").GetString());
        var second = await rpc.InvokeAsync("experimentalFeature/list", Json("""{"cursor":"1","limit":2}"""), ct);
        Assert.Equal(2, second.GetProperty("data").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("data")[0].GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("experimentalFeature/list", Json("""{"cursor":"4"}"""), ct));
    }

    [Theory]
    [InlineData("config/read", "{\"cwd\":\"relative\"}")]
    [InlineData("config/read", "{\"includeLayers\":true,\"includeLayers\":false}")]
    [InlineData("configRequirements/read", "{\"cwd\":\"relative\"}")]
    [InlineData("config/value/write", "{}")]
    [InlineData("experimentalFeature/list", "{\"threadId\":\"unknown\"}")]
    public async Task RejectsUnsupportedOrMalformedRequestsBeforeAccess(string method, string json)
    {
        var rpc = new CodexConfigurationReadRpc((_, _) => throw new InvalidOperationException("Unexpected config read."),
            (_, _) => throw new InvalidOperationException("Unexpected feature read."));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync(method, Json(json), TestContext.Current.CancellationToken));
    }

    private static CodexConfigurationRpcSnapshot Fixture(string cwd)
    {
        var package = Path.Combine(cwd, "packaged.toml");
        var user = Path.Combine(cwd, "config.toml");
        var project = Path.Combine(cwd, ".codex");
        var managed = Path.Combine(cwd, "managed.toml");
        var layers = new CodexThreadConfigurationLayer[]
        {
            new(CodexConfigurationLayerKind.Package, "package", Json("""{"model_provider":"openai"}""")),
            new(CodexConfigurationLayerKind.User, "user", Json("""{"model":"user","model_reasoning_effort":"medium","mcp_servers":{"tool":{"env":{"TOKEN":"test-secret"}}},"developer_instructions":"test-secret"}""")),
            new(CodexConfigurationLayerKind.Project, "project", Json("""{"model":"untrusted","developer_instructions":"test-secret"}"""), false),
            new(CodexConfigurationLayerKind.LegacyManagedFile, "managed", Json("""{"model":"managed"}"""))
        };
        var metadata = new CodexConfigurationRpcLayerMetadata[]
        {
            new(layers[0].Kind, "package", "package-v1", Element(new JsonObject { ["type"] = "packagedDefaults", ["file"] = package })),
            new(layers[1].Kind, "user", "user-v1", Element(new JsonObject { ["type"] = "user", ["file"] = user, ["profile"] = null })),
            new(layers[2].Kind, "project", "project-v1", Element(new JsonObject { ["type"] = "project", ["dotCodexFolder"] = project })),
            new(layers[3].Kind, "managed", "managed-v2", Element(new JsonObject { ["type"] = "legacyManagedConfigTomlFromFile", ["file"] = managed }))
        };
        return new(new("revision", layers, Json("""{"approval_policy":["never"],"sandbox_mode":["read-only"],"model":["managed"]}""")), metadata);
    }

    private static JsonElement Element(JsonNode node) => Json(node.ToJsonString());
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
