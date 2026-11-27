using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexModelCatalogTests
{
    private const string Payload = """
        {"models":[{"slug":"test-model","display_name":"Test model","default_reasoning_level":"medium",
          "supported_reasoning_levels":[{"effort":"medium","description":"Balanced"},{"effort":"new-effort"}],
          "input_modalities":["text","image"],"context_window":500000,"max_context_window":872000,
          "supported_in_api":false,"future_capability":{"enabled":true}}]}
        """;

    [Fact]
    public async Task CacheRevalidatesOnlyForMatchingAccountUserAndClientVersion()
    {
        var auth = new Credentials();
        var requests = new List<string?>();
        using var client = new CodexClient(auth, handler: new Handler(request =>
        {
            Assert.Equal("chatgpt.com", request.RequestUri!.Host);
            Assert.Equal("/backend-api/codex/models", request.RequestUri.AbsolutePath);
            Assert.Equal(auth.Current.AccountId, request.Headers.GetValues("ChatGPT-Account-Id").Single());
            requests.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.Tag);
            if (requests[^1] is not null) return new(HttpStatusCode.NotModified);
            return Response();
        }));

        var initial = await client.GetModelCatalogAsync("0.172.0", cancellationToken: TestContext.Current.CancellationToken);
        var model = Assert.Single(initial.Models);
        Assert.Equal("test-model", model.Model);
        Assert.Equal(500000L, model.ContextWindow);
        Assert.True(model.SupportsReasoningEffort("new-effort"));
        Assert.False(model.SupportsReasoningEffort("ultra"));
        Assert.False(model.SupportedInApi);
        Assert.True(model.Raw.GetProperty("future_capability").GetProperty("enabled").GetBoolean());
        Assert.Same(initial, await client.GetModelCatalogAsync("0.172.0", cancellationToken: TestContext.Current.CancellationToken));

        auth.Current = new("rotated-token", "account-a") { ChatGptUserId = "user-a" };
        var revalidated = await client.GetModelCatalogAsync("0.172.0", true, TestContext.Current.CancellationToken);
        Assert.Equal(initial.Models, revalidated.Models);
        auth.Current = new("other-token", "account-b") { ChatGptUserId = "user-b" };
        await client.GetModelCatalogAsync("0.172.0", cancellationToken: TestContext.Current.CancellationToken);
        await client.GetModelCatalogAsync("0.172.1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new string?[] { null, "\"catalog-a\"", null, null }, requests);
    }

    [Fact]
    public async Task AccountSwitchDuringDiscoveryDoesNotExposeOldCatalog()
    {
        var auth = new Credentials();
        using var client = new CodexClient(auth, handler: new Handler(_ =>
        {
            auth.Current = new("other-token", "account-b") { ChatGptUserId = "user-b" };
            return Response();
        }));
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.GetModelCatalogAsync("0.172.0",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownUserIdentityCannotReuseAnAccountCatalog()
    {
        var auth = new Credentials { Current = new("token", "account-a") };
        var calls = 0;
        using var client = new CodexClient(auth, handler: new Handler(request =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            calls++;
            return Response();
        }));
        await client.GetModelCatalogAsync("0.172.0", cancellationToken: TestContext.Current.CancellationToken);
        await client.GetModelCatalogAsync("0.172.0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CustomOriginCannotReceiveChatGptCredentials()
    {
        using var client = new CodexClient(new Credentials(), new CodexEndpointOptions { BackendBaseUri = new("https://example.invalid/") },
            new Handler(_ => throw new InvalidOperationException("Unexpected dispatch.")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetModelCatalogAsync("0.172.0",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MissingCapabilitiesStayUnknownAndDuplicateSlugsAreRejected()
    {
        using var minimal = JsonDocument.Parse("""{"models":[{"slug":"unknown"}]}""");
        var model = Assert.Single(CodexClient.ParseModelCatalog(minimal.RootElement));
        Assert.Null(model.SupportsReasoningEffort("high"));
        Assert.Null(model.ContextWindow);
        Assert.Null(model.SupportedInApi);
        using var duplicate = JsonDocument.Parse("""{"models":[{"slug":"duplicate"},{"slug":"duplicate"}]}""");
        Assert.Throws<CodexProtocolException>(() => CodexClient.ParseModelCatalog(duplicate.RootElement));
    }

    private static HttpResponseMessage Response()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Payload, Encoding.UTF8, "application/json")
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"catalog-a\"");
        return response;
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public CodexCredentials Current { get; set; } = new("token", "account-a") { ChatGptUserId = "user-a" };
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
