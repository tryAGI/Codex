using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexRuntimeFeatureRegistryTests
{
    [Fact]
    public async Task RuntimeUpdatesOnlyAffectRegisteredUpstreamKeysAndPreserveOtherOverrides()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Registry();
        var rpc = Rpc(registry);
        var initial = registry.Capture();
        Assert.False(initial.IsEnabled("memories"));
        Assert.True(initial.IsEnabled("tool_suggest"));
        Assert.False(initial.IsEnabled("unregistered"));

        var response = await rpc.InvokeAsync("experimentalFeature/enablement/set", Json("""
            {"enablement":{"memories":true,"tool_suggest":false,"code_mode":true,
              "api_key_model_discovery":true,"unregistered":true}}
            """), ct);
        var accepted = response.GetProperty("enablement");
        Assert.Equal(2, accepted.EnumerateObject().Count());
        Assert.True(accepted.GetProperty("memories").GetBoolean());
        Assert.False(accepted.GetProperty("tool_suggest").GetBoolean());
        var current = registry.Capture();
        Assert.True(current.IsEnabled("memories"));
        Assert.False(current.IsEnabled("tool_suggest"));
        Assert.False(current.IsEnabled("api_key_model_discovery"));
        Assert.False(current.IsEnabled("code_mode"));
        Assert.Equal(initial.Generation + 1, current.Generation);
        Assert.False(initial.IsEnabled("memories"));
        await rpc.InvokeAsync("experimentalFeature/enablement/set", Json("""{"enablement":{"memories":false}}"""), ct);
        Assert.False(registry.Capture().IsEnabled("memories"));
        Assert.False(registry.Capture().IsEnabled("tool_suggest"));
        var version = registry.Capture().Generation;
        await rpc.InvokeAsync("experimentalFeature/enablement/set", Json("""{"enablement":{}}"""), ct);
        Assert.Equal(version, registry.Capture().Generation);
    }

    [Fact]
    public void ManagedRequirementsWinOverThreadConfigAndDiscardLatentOverrides()
    {
        var registry = Registry();
        registry.SetRuntimeEnablement("policy-a", Map(("memories", true)));
        Assert.True(registry.Capture(Map(("memories", false))).IsEnabled("memories"));
        Assert.True(registry.TryReplacePolicy("policy-a", new("policy-b", Map(), Map(("memories", false)))));
        Assert.False(registry.Capture(Map(("memories", true))).IsEnabled("memories"));
        var generation = registry.Capture().Generation;
        var error = Assert.Throws<CodexConfigurationWriteException>(() =>
            registry.SetRuntimeEnablement("policy-b", Map(("tool_suggest", false), ("memories", true))));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigRequirementReadonly, error.Failure);
        Assert.Equal(generation, registry.Capture().Generation);
        Assert.True(registry.Capture().IsEnabled("tool_suggest"));
        Assert.Single(registry.SetRuntimeEnablement("policy-b", Map(("memories", false))));
        Assert.True(registry.TryReplacePolicy("policy-b", new("policy-c", Map(), Map())));
        Assert.False(registry.Capture().IsEnabled("memories"));
        Assert.False(registry.TryReplacePolicy("policy-a", new("stale", Map(), Map())));
    }

    [Fact]
    public async Task PolicyChangeDuringAdmissionCannotCommitStaleRuntimeUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Registry();
        var rpc = new CodexRuntimeFeatureRpc(registry, (_, _) =>
        {
            Assert.True(registry.TryReplacePolicy("policy-a", new("policy-b", Map(), Map(("memories", false)))));
            return Task.FromResult("policy-a");
        });
        var error = await Assert.ThrowsAsync<CodexConfigurationWriteException>(() =>
            rpc.InvokeAsync("experimentalFeature/enablement/set", Json("""{"enablement":{"memories":true}}"""), ct));
        Assert.Equal(CodexConfigurationWriteFailure.ConfigVersionConflict, error.Failure);
        Assert.False(registry.Capture().IsEnabled("memories"));
    }

    [Fact]
    public async Task ConfigurationFeatureListReflectsActualRegistryAndManagedState()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Registry();
        var reader = new CodexConfigurationReadRpc((_, _) => throw new InvalidOperationException(),
            (_, _) => Task.FromResult(registry.Capture().States));
        var rpc = Rpc(registry);
        await rpc.InvokeAsync("experimentalFeature/enablement/set", Json("""{"enablement":{"memories":true}}"""), ct);
        var list = await reader.InvokeAsync("experimentalFeature/list", cancellationToken: ct);
        var item = list.GetProperty("data").EnumerateArray().Single(feature => feature.GetProperty("name").GetString() == "memories");
        Assert.True(item.GetProperty("enabled").GetBoolean());
        Assert.False(item.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal("beta", item.GetProperty("stage").GetString());
        Assert.Equal("Memory", item.GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enablement\":{\"memories\":true,\"memories\":false}}")]
    [InlineData("{\"enablement\":{\"memories\":\"true\"}}")]
    [InlineData("{\"enablement\":{},\"extra\":1}")]
    [InlineData("{\"enablement\":{\"memories/other\":true}}")]
    public async Task InvalidRequestsNeverReachAdmission(string json)
    {
        var registry = Registry();
        var rpc = new CodexRuntimeFeatureRpc(registry, (_, _) => throw new InvalidOperationException("Unexpected admission."));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            rpc.InvokeAsync("experimentalFeature/enablement/set", Json(json), TestContext.Current.CancellationToken));
        Assert.Equal(0, registry.Capture().Generation);
    }

    [Fact]
    public async Task AuthorizationDenialDoesNotChangeRegistry()
    {
        var registry = Registry();
        var rpc = new CodexRuntimeFeatureRpc(registry, (_, _) => throw new CodexAuthorizationUnavailableException());
        await Assert.ThrowsAsync<CodexAuthorizationUnavailableException>(() => rpc.InvokeAsync("experimentalFeature/enablement/set",
            Json("""{"enablement":{"memories":true}}"""), TestContext.Current.CancellationToken));
        Assert.False(registry.Capture().IsEnabled("memories"));
    }

    private static CodexRuntimeFeatureRegistry Registry() => new(
        [
            new("memories", "beta", false, true, "Memory"),
            new("tool_suggest", "stable", true, true),
            new("api_key_model_discovery", "beta", false, false),
            new("code_mode", "beta", false, true),
            new("removed_key", "removed", false)
        ], new("policy-a", Map(), Map()));

    private static CodexRuntimeFeatureRpc Rpc(CodexRuntimeFeatureRegistry registry) =>
        new(registry, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(registry.Capture().PolicyRevision); });
    private static IReadOnlyDictionary<string, bool> Map(params (string Key, bool Enabled)[] items) =>
        items.ToDictionary(item => item.Key, item => item.Enabled, StringComparer.Ordinal);
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
