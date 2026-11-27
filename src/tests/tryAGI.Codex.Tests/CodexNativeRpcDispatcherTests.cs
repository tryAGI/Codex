using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexNativeRpcDispatcherTests
{
    [Fact]
    public async Task RegisteredFacadesShareOneNativeRequestSurface()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new CodexRuntimeFeatureRegistry([new("memories", "beta", false, true)],
            new("policy", new Dictionary<string, bool>(), new Dictionary<string, bool>()));
        var features = new CodexRuntimeFeatureRpc(registry, (_, _) => Task.FromResult("policy"));
        var reader = new CodexConfigurationReadRpc((_, _) =>
            Task.FromResult(new CodexConfigurationRpcSnapshot(new("revision", []), [])),
            (_, _) => Task.FromResult(registry.Capture().States));
        var accounts = new CodexAccountRpc((_, _) =>
            Task.FromResult(new CodexAccountSelection("epoch", CodexAccountKind.ChatGpt, "chatgpt", true,
                "account", "user", PlanType: "pro")));
        var models = new CodexModelCatalogRpc([CodexModelCatalogSource.ForStatic("openai",
            CodexProviderAuthMode.ChatGptSubscription, "0.172.0",
            Json("""{"models":[{"slug":"native","display_name":"Native","visibility":"list","supported_in_api":true}]}"""))], "openai");
        var dispatcher = new CodexNativeRpcDispatcher(models: models, accounts: accounts,
            configRead: reader, features: features);
        Assert.Contains("account/read", dispatcher.Methods);
        Assert.Contains("model/list", dispatcher.Methods);
        Assert.Contains("config/read", dispatcher.Methods);
        Assert.DoesNotContain("account/logout", dispatcher.Methods);
        Assert.DoesNotContain("config/batchWrite", dispatcher.Methods);
        Assert.All(dispatcher.Methods, method => Assert.True(CodexAppServerProtocol.IsClientRequest(method)));

        var account = await dispatcher.DispatchAsync(Json("""{"id":"account-1","method":"account/read"}"""), ct);
        Assert.Equal("account-1", account.GetProperty("id").GetString());
        Assert.Equal("chatgpt", account.GetProperty("result").GetProperty("account").GetProperty("type").GetString());
        Assert.False(account.TryGetProperty("jsonrpc", out _));
        var model = await dispatcher.DispatchAsync(Json("""{"id":42,"method":"model/list","params":{}}"""), ct);
        Assert.Equal(42L, model.GetProperty("id").GetInt64());
        Assert.Equal("native", model.GetProperty("result").GetProperty("data")[0].GetProperty("id").GetString());
        var update = await dispatcher.DispatchAsync(Json("""
            {"jsonrpc":"2.0","id":"set","method":"experimentalFeature/enablement/set","params":{"enablement":{"memories":true}}}
            """), ct);
        Assert.Equal("2.0", update.GetProperty("jsonrpc").GetString());
        Assert.True(update.GetProperty("result").GetProperty("enablement").GetProperty("memories").GetBoolean());
        var list = await dispatcher.DispatchAsync(Json("""{"id":"list","method":"experimentalFeature/list"}"""), ct);
        Assert.True(list.GetProperty("result").GetProperty("data")[0].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task AdditionalRoutesAreExactAndDoNotFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var dispatcher = new CodexNativeRpcDispatcher(additionalRoutes:
            [new("thread/read", (parameters, _) =>
            {
                calls++;
                return Task.FromResult(parameters!.Value.Clone());
            })]);
        var result = await dispatcher.DispatchAsync(Json("""{"id":-42,"method":"thread/read","params":{"threadId":"example"}}"""), ct);
        Assert.Equal(-42L, result.GetProperty("id").GetInt64());
        Assert.Equal("example", result.GetProperty("result").GetProperty("threadId").GetString());
        var unknown = await dispatcher.DispatchAsync(Json("""{"id":"missing","method":"account/logout"}"""), ct);
        Assert.Equal(-32601, unknown.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("missing", unknown.GetProperty("id").GetString());
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => dispatcher.InvokeAsync("thread/start", cancellationToken: ct));
        Assert.Equal(1, calls);

        Assert.Throws<ArgumentException>(() => new CodexNativeRpcDispatcher(additionalRoutes:
            [new("not-a-protocol-method", (_, _) => Task.FromResult(Json("{}")))]));
        Assert.Throws<ArgumentException>(() => new CodexNativeRpcDispatcher(additionalRoutes:
            [new("thread/read", (_, _) => Task.FromResult(Json("{}"))), new("thread/read", (_, _) => Task.FromResult(Json("{}")))]));
    }

    [Fact]
    public async Task TypedErrorsKeepCorrelationAndNeverLeakHostExceptionDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new CodexNativeRpcDispatcher(additionalRoutes:
        [
            new("config/value/write", (_, _) => throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigVersionConflict)),
            new("account/read", (_, _) => throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged)),
            new("model/list", (_, _) => throw new CodexProviderRouteException(CodexProviderRouteFailure.DestinationDenied)),
            new("thread/read", (_, _) => throw new InvalidOperationException("private-token")),
            new("thread/start", (_, _) => throw new CodexThreadRpcException(-32600, "private-token"))
        ]);
        var config = await dispatcher.DispatchAsync(Json("""{"id":"config","method":"config/value/write"}"""), ct);
        Assert.Equal("config", config.GetProperty("id").GetString());
        Assert.Equal("configVersionConflict", config.GetProperty("error").GetProperty("data").GetProperty("config_write_error_code").GetString());
        var account = await dispatcher.DispatchAsync(Json("""{"id":2,"method":"account/read"}"""), ct);
        Assert.Equal("SessionChanged", account.GetProperty("error").GetProperty("data").GetProperty("reason").GetString());
        var model = await dispatcher.DispatchAsync(Json("""{"id":"model","method":"model/list"}"""), ct);
        Assert.Equal("DestinationDenied", model.GetProperty("error").GetProperty("data").GetProperty("reason").GetString());
        var unexpected = await dispatcher.DispatchAsync(Json("""{"id":"thread","method":"thread/read"}"""), ct);
        Assert.Equal(-32603, unexpected.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("private-token", unexpected.GetRawText());
        var rejected = await dispatcher.DispatchAsync(Json("""{"id":"rejected","method":"thread/start"}"""), ct);
        Assert.Equal("rejected", rejected.GetProperty("id").GetString());
        Assert.Equal(-32600, rejected.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("private-token", rejected.GetRawText());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"method\":\"account/read\"}")]
    [InlineData("{\"id\":1,\"id\":2,\"method\":\"account/read\"}")]
    [InlineData("{\"id\":1.5,\"method\":\"account/read\"}")]
    [InlineData("{\"id\":1,\"method\":\"account/read\",\"jsonrpc\":\"1.0\"}")]
    [InlineData("{\"id\":1,\"method\":\"account/read\",\"result\":{}}")]
    public async Task MalformedRequestsCannotDispatch(string json)
    {
        var dispatcher = new CodexNativeRpcDispatcher(additionalRoutes:
            [new("account/read", (_, _) => throw new InvalidOperationException("Unexpected dispatch."))]);
        var response = await dispatcher.DispatchAsync(Json(json), TestContext.Current.CancellationToken);
        Assert.Equal(-32600, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task CancellationDoesNotProduceFalseSuccessOrAnAutomaticReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var calls = 0;
        var dispatcher = new CodexNativeRpcDispatcher(additionalRoutes:
            [new("account/read", (_, token) =>
            {
                calls++;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Json("{}"));
            })]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(Json("""{"id":"once","method":"account/read"}"""), cancellation.Token));
        Assert.Equal(1, calls);
        await dispatcher.CancelPendingLoginAsync(ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => dispatcher.RunPendingLoginAsync(Guid.NewGuid(), ct));
    }

    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
