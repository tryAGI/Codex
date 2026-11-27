using System.Net;
using System.Text;
using Xunit;

namespace tryAGI.Codex.Tests;

public sealed class CodexOAuthCredentialProviderTests
{
    private const string Response = """{"access_token":"new-token","refresh_token":"rotated","expires_in":3600}""";

    [Fact]
    public async Task ConcurrentProvidersUseOneDurableRotation()
    {
        var store = new Store();
        using var handler = new Handler((_, _) => Task.FromResult(Success()));
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var first = new CodexOAuthCredentialProvider(store, "session", auth);
        var second = new CodexOAuthCredentialProvider(store, "session", auth);
        var results = await Task.WhenAll(first.GetCredentialsAsync(TestContext.Current.CancellationToken).AsTask(), second.GetCredentialsAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.All(results, result => Assert.Equal("new-token", result.AccessToken));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("rotated", store.State!.Tokens.RefreshToken);
        Assert.Null(store.State.RefreshAttemptId);
    }

    [Fact]
    public async Task AmbiguousFailureBlocksReuseOfOldRefreshToken()
    {
        var store = new Store();
        using var handler = new Handler((_, _) => throw new HttpRequestException("secret"));
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var provider = new CodexOAuthCredentialProvider(store, "session", auth);
        var failure = await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => provider.GetCredentialsAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CodexOAuthRefreshFailure.UncertainRotation, failure.Failure);
        Assert.DoesNotContain("secret", failure.ToString());
        var pending = await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => provider.GetCredentialsAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CodexOAuthRefreshFailure.PendingAttempt, pending.Failure);
        Assert.Equal(1, handler.Calls);
        Assert.NotNull(store.State!.RefreshAttemptId);
    }

    [Fact]
    public async Task CancellationAfterDispatchDoesNotDiscardRotatedToken()
    {
        var store = new Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Success();
        });
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var provider = new CodexOAuthCredentialProvider(store, "session", auth);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refresh = provider.GetCredentialsAsync(cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal("new-token", (await provider.GetCredentialsAsync(TestContext.Current.CancellationToken)).AccessToken);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnauthorizedRecoveryUsesNewerGenerationWithoutRepeatingRefresh()
    {
        var store = new Store();
        var old = store.State!.Tokens.Credentials;
        using var handler = new Handler((_, _) => Task.FromResult(Success()));
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var provider = new CodexOAuthCredentialProvider(store, "session", auth);
        Assert.Equal("new-token", (await provider.RefreshAfterUnauthorizedAsync(old, TestContext.Current.CancellationToken)).AccessToken);
        Assert.Equal("new-token", (await provider.RefreshAfterUnauthorizedAsync(old, TestContext.Current.CancellationToken)).AccessToken);
        var switched = new CodexCredentials("old-token", "different-account");
        var failure = await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => provider.RefreshAfterUnauthorizedAsync(switched, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CodexOAuthRefreshFailure.SessionChanged, failure.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingSessionDoesNotSendAnyRequest()
    {
        var store = new Store { State = null };
        using var handler = new Handler((_, _) => Task.FromResult(Success()));
        using var auth = new CodexDeviceAuthorization(handler: handler);
        var provider = new CodexOAuthCredentialProvider(store, "session", auth);
        await Assert.ThrowsAsync<CodexAuthorizationUnavailableException>(() => provider.GetCredentialsAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Response, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return callback(request, cancellationToken);
        }
    }

    private sealed class Store : ICodexOAuthSessionStore
    {
        private readonly SemaphoreSlim gate = new(1);
        public CodexOAuthSessionState? State { get; set; } = new("v1",
            new CodexOAuthTokens("old-token", "refresh", "account", DateTimeOffset.UtcNow.AddMinutes(-1), "user"));

        public async ValueTask<ICodexOAuthSessionLease> AcquireAsync(string sessionId, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return new Lease(this);
        }

        private sealed class Lease(Store owner) : ICodexOAuthSessionLease
        {
            public ValueTask<CodexOAuthSessionState?> ReadAsync(CancellationToken cancellationToken)
                => ValueTask.FromResult(owner.State);

            public ValueTask<bool> TryBeginRefreshAsync(string expectedRevision, Guid attemptId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.State is not { RefreshAttemptId: null } state || state.Revision != expectedRevision)
                    return ValueTask.FromResult(false);
                owner.State = new(state.Revision, state.Tokens, attemptId);
                return ValueTask.FromResult(true);
            }

            public ValueTask<bool> TryCompleteRefreshAsync(string expectedRevision, Guid attemptId, CodexOAuthTokens tokens, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.State?.Revision != expectedRevision || owner.State.RefreshAttemptId != attemptId)
                    return ValueTask.FromResult(false);
                owner.State = new("v2", tokens);
                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync()
            {
                owner.gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
