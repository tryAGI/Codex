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

    [Fact]
    public async Task RevocationStopsAnAlreadyOpenResponseStream()
    {
        var ct = TestContext.Current.CancellationToken;
        using var revoke = new CancellationTokenSource();
        using var stream = new WaitingStream();
        var policy = CodexProviderNetworkPolicy.Restricted(["chatgpt.com"], revoke.Token);
        using var client = new HttpClient(new CodexProviderNetworkPolicy.PolicyHandler(policy, Chat, new StreamingHandler(stream)));
        using var response = await client.GetAsync("https://chatgpt.com/backend-api/codex/responses", HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var pending = body.ReadAsync(new byte[16], ct).AsTask();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        revoke.Cancel();
        var error = await Assert.ThrowsAsync<CodexProviderRouteException>(() => pending);
        Assert.Equal(CodexProviderRouteFailure.NetworkPolicyRevoked, error.Failure);
        Assert.True(stream.Disposed);
        await Assert.ThrowsAsync<CodexProviderRouteException>(() => body.ReadAsync(new byte[16], ct).AsTask());
    }

    [Fact]
    public async Task BufferedReadCannotStartAfterHeadersAreRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var revoke = new CancellationTokenSource();
        using var stream = new WaitingStream();
        using var client = new HttpClient(new CodexProviderNetworkPolicy.PolicyHandler(
            CodexProviderNetworkPolicy.Unrestricted(revoke.Token), Chat, new StreamingHandler(stream)));
        using var response = await client.GetAsync("https://chatgpt.com/backend-api/codex/responses", HttpCompletionOption.ResponseHeadersRead, ct);
        revoke.Cancel();
        var error = await Assert.ThrowsAsync<CodexProviderRouteException>(() => response.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(CodexProviderRouteFailure.NetworkPolicyRevoked, error.Failure);
        Assert.False(stream.Entered.Task.IsCompleted);
    }

    [Fact]
    public async Task OrdinaryBodyReadPreservesBytesAndContentHeaders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream([1, 2, 3, 4]);
        using var client = new HttpClient(new CodexProviderNetworkPolicy.PolicyHandler(
            CodexProviderNetworkPolicy.Unrestricted(ct), Chat, new StreamingHandler(stream)));
        using var response = await client.GetAsync("https://chatgpt.com/backend-api/codex/responses", ct);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    private sealed class StreamingHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = new StreamContent(body);
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class WaitingStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
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
