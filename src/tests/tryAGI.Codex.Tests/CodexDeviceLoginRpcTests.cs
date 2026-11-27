using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexDeviceLoginRpcTests
{
    [Fact]
    public async Task StartReturnsDeviceCodeAndCommitsOnlyAfterHostDrivesAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new AuthHandler();
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var authority = new Authority();
        var events = new ConcurrentQueue<JsonElement>();
        var rpc = Create(auth, authority, events);
        var response = await Start(rpc, ct);
        Assert.Equal("chatgptDeviceCode", response.GetProperty("type").GetString());
        Assert.Equal("https://auth.openai.com/codex/device", response.GetProperty("verificationUrl").GetString());
        Assert.Equal("TEST-CODE", response.GetProperty("userCode").GetString());
        Assert.DoesNotContain("device-private-id", response.GetRawText());
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, authority.Commits);
        Assert.Empty(events);
        var id = Id(response);
        Assert.True(await rpc.RunPendingAsync(id, ct));
        Assert.Equal(3, handler.Calls);
        Assert.Equal(1, authority.Commits);
        Assert.Equal("account", authority.CommittedTokens!.AccountId);
        var notification = Assert.Single(events);
        Assert.Equal(id.ToString(), notification.GetProperty("loginId").GetString());
        Assert.True(notification.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("refresh-secret", notification.GetRawText());
        Assert.False(await rpc.RunPendingAsync(id, ct));
        Assert.Equal(1, authority.Commits);
    }

    [Fact]
    public async Task CancelBeforePollingProducesExactlyOneCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new AuthHandler();
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var authority = new Authority();
        var events = new ConcurrentQueue<JsonElement>();
        var rpc = Create(auth, authority, events);
        var id = Id(await Start(rpc, ct));
        var wrong = await Cancel(rpc, Guid.NewGuid(), ct);
        Assert.Equal("notFound", wrong.GetProperty("status").GetString());
        Assert.Equal("canceled", (await Cancel(rpc, id, ct)).GetProperty("status").GetString());
        Assert.Equal("notFound", (await Cancel(rpc, id, ct)).GetProperty("status").GetString());
        Assert.False(await rpc.RunPendingAsync(id, ct));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, authority.Commits);
        var completion = Assert.Single(events);
        Assert.False(completion.GetProperty("success").GetBoolean());
        Assert.Equal(id.ToString(), completion.GetProperty("loginId").GetString());
    }

    [Fact]
    public async Task CancelDuringPollDoesNotCommitOrPublishTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new AuthHandler { BlockPoll = true };
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var authority = new Authority();
        var events = new ConcurrentQueue<JsonElement>();
        var rpc = Create(auth, authority, events);
        var id = Id(await Start(rpc, ct));
        var running = rpc.RunPendingAsync(id, ct);
        await handler.PollEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.RunPendingAsync(id, ct));
        Assert.Equal("canceled", (await Cancel(rpc, id, ct)).GetProperty("status").GetString());
        Assert.False(await running.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(0, authority.Commits);
        Assert.False(Assert.Single(events).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task SupersessionAndLogoutFenceThePendingLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new AuthHandler();
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var authority = new Authority();
        var events = new ConcurrentQueue<JsonElement>();
        var rpc = Create(auth, authority, events);
        var first = Id(await Start(rpc, ct));
        var second = Id(await Start(rpc, ct));
        Assert.NotEqual(first, second);
        Assert.False(await rpc.RunPendingAsync(first, ct));
        Assert.Equal(first.ToString(), Assert.Single(events).GetProperty("loginId").GetString());
        var logout = await rpc.InvokeAsync("account/logout", cancellationToken: ct);
        Assert.Equal("{}", logout.GetRawText());
        Assert.Equal(1, authority.Logouts);
        Assert.False(await rpc.RunPendingAsync(second, ct));
        Assert.Equal(0, authority.Commits);
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.False(item.GetProperty("success").GetBoolean()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrAmbiguousCommitNeverReportsSuccessOrRetries(bool ambiguous)
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new AuthHandler();
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var authority = new Authority { CommitResult = false, Ambiguous = ambiguous };
        var events = new ConcurrentQueue<JsonElement>();
        var rpc = Create(auth, authority, events);
        var id = Id(await Start(rpc, ct));
        Assert.False(await rpc.RunPendingAsync(id, ct));
        Assert.False(await rpc.RunPendingAsync(id, ct));
        Assert.Equal(1, authority.Commits);
        var result = Assert.Single(events);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("refresh-secret", result.GetRawText());
    }

    [Fact]
    public async Task DeniedOrUnsupportedLoginNeverContactsProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new AuthHandler();
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var authority = new Authority { Deny = true };
        var rpc = Create(auth, authority, new());
        await Assert.ThrowsAsync<CodexAuthorizationUnavailableException>(() => Start(rpc, ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("account/login/start", Json("""{"type":"apiKey","apiKey":"fake"}"""), ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("account/login/start", Json("""{"type":"chatgpt"}"""), ct));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, authority.Commits);
    }

    private static CodexDeviceLoginRpc Create(CodexDeviceAuthorization auth, Authority authority, ConcurrentQueue<JsonElement> events) =>
        new(auth, authority, (method, value) =>
        {
            Assert.Equal("account/login/completed", method);
            events.Enqueue(value.Clone());
            return Task.CompletedTask;
        });

    private static Task<JsonElement> Start(CodexDeviceLoginRpc rpc, CancellationToken ct) =>
        rpc.InvokeAsync("account/login/start", Json("""{"type":"chatgptDeviceCode"}"""), ct);

    private static Task<JsonElement> Cancel(CodexDeviceLoginRpc rpc, Guid id, CancellationToken ct) =>
        rpc.InvokeAsync("account/login/cancel", Json($$"""{"loginId":"{{id}}"}"""), ct);

    private static Guid Id(JsonElement value) => Guid.Parse(value.GetProperty("loginId").GetString()!);
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class Authority : ICodexAccountLifecycleAuthority
    {
        internal bool Deny { get; init; }
        internal bool CommitResult { get; init; } = true;
        internal bool Ambiguous { get; init; }
        internal int Commits { get; private set; }
        internal int Logouts { get; private set; }
        internal CodexOAuthTokens? CommittedTokens { get; private set; }
        private readonly CodexAccountLifecycleTarget target = new("session", "epoch", "revision");

        public Task<CodexAccountLifecycleTarget> AuthorizeAsync(CodexAccountLifecycleOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Deny) throw new CodexAuthorizationUnavailableException();
            return Task.FromResult(target);
        }

        public Task<bool> TryCommitLoginAsync(CodexAccountLifecycleTarget captured, Guid loginId, CodexOAuthTokens tokens, CancellationToken cancellationToken)
        {
            Assert.Equal(target, captured);
            Assert.NotEqual(Guid.Empty, loginId);
            Commits++;
            if (Ambiguous) throw new IOException("refresh-secret");
            if (CommitResult) CommittedTokens = tokens;
            return Task.FromResult(CommitResult);
        }

        public Task<bool> TryLogoutAsync(CodexAccountLifecycleTarget captured, CancellationToken cancellationToken)
        {
            Assert.Equal(target, captured);
            Logouts++;
            return Task.FromResult(true);
        }
    }

    private sealed class AuthHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool BlockPoll { get; init; }
        internal TaskCompletionSource PollEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal("auth.openai.com", request.RequestUri.Host);
            if (path.EndsWith("/usercode", StringComparison.Ordinal))
                return Reply("""{"device_auth_id":"device-private-id","user_code":"TEST-CODE","interval":"1"}""");
            if (path.EndsWith("/deviceauth/token", StringComparison.Ordinal))
            {
                PollEntered.TrySetResult();
                if (BlockPoll) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Reply("""{"authorization_code":"code","code_verifier":"verifier"}""");
            }
            Assert.Equal("/oauth/token", path);
            var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"https://api.openai.com/auth":{"chatgpt_account_id":"account","chatgpt_user_id":"user"}}""")).TrimEnd('=').Replace('+','-').Replace('/','_');
            return Reply($$"""{"access_token":"header.{{claims}}.signature","refresh_token":"refresh-secret","expires_in":3600}""");
        }
        private static HttpResponseMessage Reply(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
