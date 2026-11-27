using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexModelCatalogRpcTests
{
    private const string Catalog = """
        {"models":[
          {"slug":"visible-later","display_name":"Later","visibility":"list","priority":5,"supported_in_api":true},
          {"slug":"hidden-first","display_name":"Hidden","visibility":"hide","priority":0,"supported_in_api":true},
          {"slug":"chat-only","display_name":"Chat only","visibility":"list","priority":1,"supported_in_api":false,
           "default_reasoning_level":"medium","supported_reasoning_levels":[{"effort":"medium","description":"Balanced"},{"effort":"future","description":"New"}],
           "input_modalities":["text"],"additional_speed_tiers":["fast"],"service_tiers":[{"id":"fast","name":"Fast","description":"Optional"}],
           "default_service_tier":"fast","multi_agent_version":"v2","model_specialty":"cyber",
           "available_access_programs":{"cyber":["standard","daybreak_blue","future"],"daybreak_availability":"enrollment_required"},
           "upgrade":{"model":"newer","migration_markdown":"Migration","retirement_at":"2030-01-01T00:00:00Z"},
           "availability_nux":{"message":"Notice"}}
        ]}
        """;

    [Fact]
    public async Task ProviderCatalogPreservesDisplayOrderWhilePrioritySelectsDefaultBeforePagination()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = CodexModelCatalogSource.ForStatic("openai", CodexProviderAuthMode.ChatGptSubscription,
            "0.172.0", Json(Catalog));
        var snapshot = await fixture.ReadAsync(ct);
        var source = new CodexModelCatalogSource("openai", CodexProviderAuthMode.ChatGptSubscription,
            _ => Task.FromResult(snapshot), CodexModelCatalogOrigin.Provider);
        var host = new CodexModelCatalogRpc([source], "openai");

        var first = await host.InvokeAsync("model/list", Json("""{"limit":1}"""), ct);
        var earlier = Assert.Single(first.GetProperty("data").EnumerateArray());
        Assert.Equal("visible-later", earlier.GetProperty("id").GetString());
        Assert.False(earlier.GetProperty("isDefault").GetBoolean());
        Assert.Equal("1", first.GetProperty("nextCursor").GetString());

        var second = await host.InvokeAsync("model/list", Json("""{"cursor":"1","limit":1}"""), ct);
        var recommended = Assert.Single(second.GetProperty("data").EnumerateArray());
        Assert.Equal("chat-only", recommended.GetProperty("id").GetString());
        Assert.True(recommended.GetProperty("isDefault").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        var all = await host.InvokeAsync("model/list", Json("""{"includeHidden":true}"""), ct);
        var rows = all.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(new[] { "visible-later", "hidden-first", "chat-only" },
            rows.Select(row => row.GetProperty("id").GetString()));
        Assert.True(rows[1].GetProperty("hidden").GetBoolean());
        Assert.True(rows[2].GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task StaticCatalogProjectsRustWireShapeAndPriorityDefaultBeforePagination()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = CodexModelCatalogSource.ForStatic("openai", CodexProviderAuthMode.ChatGptSubscription, "0.172.0",
            Json(Catalog), CodexModelCatalogOrigin.Bundled);
        var host = new CodexModelCatalogRpc([source], "openai");
        var first = await host.InvokeAsync("model/list", Json("""{"limit":1}"""), ct);
        var chat = Assert.Single(first.GetProperty("data").EnumerateArray());
        Assert.Equal("chat-only", chat.GetProperty("id").GetString());
        Assert.True(chat.GetProperty("isDefault").GetBoolean());
        Assert.False(chat.GetProperty("hidden").GetBoolean());
        Assert.False(chat.GetProperty("supportsPersonality").GetBoolean());
        Assert.Equal("future", chat.GetProperty("supportedReasoningEfforts")[1].GetProperty("reasoningEffort").GetString());
        Assert.Equal("medium", chat.GetProperty("defaultReasoningEffort").GetString());
        Assert.Equal("fast", chat.GetProperty("serviceTiers")[0].GetProperty("id").GetString());
        Assert.Equal("daybreakBlue", chat.GetProperty("availableAccessPrograms").GetProperty("cyber")[1].GetString());
        Assert.Equal(2, chat.GetProperty("availableAccessPrograms").GetProperty("cyber").GetArrayLength());
        Assert.Equal("enrollmentRequired", chat.GetProperty("availableAccessPrograms").GetProperty("daybreakAvailability").GetString());
        Assert.Equal(1893456000L, chat.GetProperty("upgradeInfo").GetProperty("retirementAt").GetInt64());
        Assert.Equal("1", first.GetProperty("nextCursor").GetString());
        var second = await host.InvokeAsync("model/list", Json("""{"cursor":"1","limit":1}"""), ct);
        Assert.Equal("visible-later", second.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.False(second.GetProperty("data")[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        var all = await host.InvokeAsync("model/list", Json("""{"includeHidden":true}"""), ct);
        Assert.Equal("hidden-first", all.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.True(all.GetProperty("data")[0].GetProperty("hidden").GetBoolean());
        Assert.True(all.GetProperty("data")[1].GetProperty("isDefault").GetBoolean());
        var original = await source.ReadAsync(ct);
        Assert.Null(original.Find("visible-later")!.InputModalities);
        Assert.Equal(CodexModelCatalogOrigin.Bundled, source.Origin);
    }

    [Fact]
    public async Task ExplicitProviderSelectionFiltersApiModelsAndDoesNotChangeDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = CodexModelCatalogSource.ForStatic("openai", CodexProviderAuthMode.ChatGptSubscription, "v", Json(Catalog));
        var api = CodexModelCatalogSource.ForStatic("api", CodexProviderAuthMode.OpenAiApiKey, "v", Json(Catalog));
        var host = new CodexModelCatalogRpc([chat, api], "openai");
        var alternate = await host.InvokeAsync("model/list", Json("""{"modelProvider":"api","includeHidden":true}"""), ct);
        var entries = alternate.GetProperty("data");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("visible-later", entries[1].GetProperty("id").GetString());
        Assert.True(entries[1].GetProperty("isDefault").GetBoolean());
        var current = await host.InvokeAsync("model/list", cancellationToken: ct);
        Assert.Equal("chat-only", current.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task DynamicPaidProviderIsNeverSelectedImplicitlyAndFailureDoesNotFallback()
    {
        var calls = 0;
        var api = new CodexModelCatalogSource("api", CodexProviderAuthMode.OpenAiApiKey, _ =>
        {
            calls++;
            throw new CodexProtocolException("Unavailable provider.");
        });
        var chat = CodexModelCatalogSource.ForStatic("openai", CodexProviderAuthMode.ChatGptSubscription, "v", Json(Catalog));
        var host = new CodexModelCatalogRpc([api, chat], "api");
        var ct = TestContext.Current.CancellationToken;
        var implicitError = await Assert.ThrowsAsync<CodexProviderRouteException>(() => host.InvokeAsync("model/list", cancellationToken: ct));
        Assert.Equal(CodexProviderRouteFailure.ExplicitPaidProviderRequired, implicitError.Failure);
        await Assert.ThrowsAsync<CodexProtocolException>(() => host.InvokeAsync("model/list", Json("""{"modelProvider":"api"}"""), ct));
        Assert.Equal(1, calls);
        var missing = await Assert.ThrowsAsync<CodexProviderRouteException>(() => host.InvokeAsync("model/list", Json("""{"modelProvider":"missing"}"""), ct));
        Assert.Equal(CodexProviderRouteFailure.UnknownProvider, missing.Failure);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"cursor\":\"-1\"}")]
    [InlineData("{\"cursor\":\"999\"}")]
    [InlineData("{\"limit\":-1}")]
    [InlineData("{\"includeHidden\":\"false\"}")]
    [InlineData("{\"limit\":1,\"limit\":2}")]
    public async Task RejectsInvalidParameters(string json)
    {
        var source = CodexModelCatalogSource.ForStatic("openai", CodexProviderAuthMode.ChatGptSubscription, "v", Json(Catalog));
        var host = new CodexModelCatalogRpc([source], "openai");
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("model/list", Json(json), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProviderMismatchIsRejected()
    {
        var source = new CodexModelCatalogSource("openai", CodexProviderAuthMode.ChatGptSubscription,
            _ => Task.FromResult(new CodexModelCatalogSnapshot("different", "v", DateTimeOffset.UtcNow, null, [])));
        var host = new CodexModelCatalogRpc([source], "openai");
        await Assert.ThrowsAsync<CodexProtocolException>(() => host.InvokeAsync("model/list", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
