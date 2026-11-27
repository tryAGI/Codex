using System.Net;
using Xunit;

namespace tryAGI.Codex.Tests;

public sealed class CodexProviderRoutingTests
{
    private static readonly CodexProviderRoute Chat = new("openai", CodexProviderAuthMode.ChatGptSubscription,
        new Uri("https://chatgpt.com/backend-api/codex/"));
    private static readonly CodexProviderRoute Api = new("api", CodexProviderAuthMode.OpenAiApiKey,
        new Uri("https://api.openai.com/v1/"));

    [Fact]
    public void DefaultRouteCannotFallBackToPaidApi()
    {
        var routing = new CodexProviderRouting([Chat, Api], Chat.Id);
        Assert.Equal(Chat, routing.Resolve(null, CodexProviderAuthMode.ChatGptSubscription));
        Assert.Equal(Api, routing.Resolve(Api.Id, CodexProviderAuthMode.OpenAiApiKey));
        Assert.Equal(CodexProviderRouteFailure.AuthenticationMismatch,
            Assert.Throws<CodexProviderRouteException>(() => routing.Resolve(Api.Id, CodexProviderAuthMode.ChatGptSubscription)).Failure);
        Assert.Equal(CodexProviderRouteFailure.UnknownProvider,
            Assert.Throws<CodexProviderRouteException>(() => routing.Resolve("missing", CodexProviderAuthMode.ChatGptSubscription)).Failure);
        var apiDefault = new CodexProviderRouting([Chat, Api], Api.Id);
        Assert.Equal(CodexProviderRouteFailure.ExplicitPaidProviderRequired,
            Assert.Throws<CodexProviderRouteException>(() => apiDefault.Resolve(null, CodexProviderAuthMode.OpenAiApiKey)).Failure);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/responses")]
    [InlineData("https://chatgpt.com.evil.test/backend-api/codex/responses")]
    [InlineData("https://chatgpt.com:8443/backend-api/codex/responses")]
    [InlineData("https://chatgpt.com/backend-api/codex-fake/responses")]
    [InlineData("https://chatgpt.com/backend-api/codex/%2f..%2fother")]
    [InlineData("https://user@chatgpt.com/backend-api/codex/responses")]
    public void SubscriptionCredentialsCannotCrossRouteBoundary(string destination)
        => Assert.Throws<CodexProviderRouteException>(() => CodexProviderRouting.ValidateDestination(Chat, new Uri(destination)));

    [Fact]
    public void OpenAiCredentialsCannotBeRegisteredOnCustomGateway()
    {
        Assert.Throws<CodexProviderRouteException>(() => new CodexProviderRouting(
            [Chat with { BaseUri = new Uri("https://gateway.test/") }], Chat.Id));
        CodexProviderRouting.ValidateDestination(Chat, new Uri("https://chatgpt.com/backend-api/codex/responses"));
        CodexProviderRouting.ValidateDestination(Api, new Uri("https://api.openai.com/v1/responses"));
    }

    [Fact]
    public async Task NetworkPolicyDeniesBeforeDispatchAndRevokesInFlightRequest()
    {
        using var revoke = new CancellationTokenSource();
        var policy = CodexProviderNetworkPolicy.Restricted(["chatgpt.com"], revoke.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CodexProviderNetworkPolicy.PolicyHandler(policy, Chat, new BlockingHandler(entered));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<CodexProviderRouteException>(() => client.GetAsync("https://api.openai.com/v1/models", TestContext.Current.CancellationToken));
        var pending = client.GetAsync("https://chatgpt.com/backend-api/codex/models", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        revoke.Cancel();
        var exception = await Assert.ThrowsAsync<CodexProviderRouteException>(() => pending);
        Assert.Equal(CodexProviderRouteFailure.NetworkPolicyRevoked, exception.Failure);
    }

    [Fact]
    public void RestrictedPolicyMatchesExactHostsAndEmptyPolicyDeniesAll()
    {
        var policy = CodexProviderNetworkPolicy.Restricted(["CHATGPT.COM"], TestContext.Current.CancellationToken);
        policy.Validate(new Uri("wss://chatgpt.com/backend-api/codex/"));
        Assert.Throws<CodexProviderRouteException>(() => policy.Validate(new Uri("https://other.chatgpt.com/")));
        Assert.Throws<CodexProviderRouteException>(() => CodexProviderNetworkPolicy.Restricted([], TestContext.Current.CancellationToken).Validate(Chat.BaseUri));
    }

    private sealed class BlockingHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}
