using System.Net;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexMcpConnectionPoolTests
{
    private static readonly CodexMcpConnectionScope Scope = new("account", "connection", "server", "thread", "env");
    private static CodexMcpRegistration Registration(CodexMcpConnectionScope? scope = null,
        long revision = 1, bool enabled = true) => new(scope ?? Scope, revision, new string('a', 64), enabled);

    private static async Task<CodexMcpClient> Ready(CancellationToken ct)
    {
        var transport = new CodexMcpHttpTransport(new Uri("https://mcp.example.test/mcp"), async (http, token) =>
        {
            using var request = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(token));
            if (request.RootElement.GetProperty("method").GetString() == "notifications/initialized")
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            var id = request.RootElement.GetProperty("id").GetInt64();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":" + id +
                    ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var client = new CodexMcpClient(transport, (_, _) => ValueTask.FromResult(true));
        try
        {
            await client.InitializeAsync("test", "1", ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task ConfigurationAndStatusNeverConnectAndConcurrentOpenReusesExactGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var opens = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new CodexMcpConnectionPool(async (registration, token) =>
        {
            Assert.Equal(Registration(), registration);
            Interlocked.Increment(ref opens);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return await Ready(token);
        });
        await pool.ConfigureAsync(Registration(), ct);
        Assert.Equal(CodexMcpRuntimeStatus.NotStarted, pool.GetStatus(Scope)!.RuntimeStatus);
        Assert.Single(pool.ListStatuses("account", "connection"));
        Assert.Empty(pool.ListStatuses("other", "connection"));
        Assert.Equal(0, opens);
        var a = pool.OpenAsync(Scope, 1, ct);
        await started.Task.WaitAsync(ct);
        var b = pool.OpenAsync(Scope, 1, ct);
        Assert.Equal(CodexMcpRuntimeStatus.Starting, pool.GetStatus(Scope)!.RuntimeStatus);
        release.SetResult();
        var first = await a;
        Assert.Same(first, await b);
        Assert.Same(first, await pool.OpenAsync(Scope, 1, ct));
        Assert.Equal(1, opens);
        Assert.Equal(CodexMcpRuntimeStatus.Connected, pool.GetStatus(Scope)!.RuntimeStatus);
        Assert.Equal("2025-06-18", pool.GetStatus(Scope)!.ProtocolVersion);
        await pool.DisposeAsync();
        Assert.Equal(CodexMcpState.Closed, first.State);
    }

    [Fact]
    public async Task AccountConnectionThreadAndEnvironmentAreDistinctPartitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var scopes = new[] { Scope, Scope with { AccountId = "other" }, Scope with { ConnectionId = "other" },
            Scope with { ThreadId = "other" }, Scope with { EnvironmentId = "other" },
            Scope with { EnvironmentId = null }, Scope with { ThreadId = null, EnvironmentId = null } };
        var opened = new List<CodexMcpClient>();
        await using var pool = new CodexMcpConnectionPool((_, token) => Ready(token));
        foreach (var scope in scopes)
        {
            await pool.ConfigureAsync(Registration(scope), ct);
            opened.Add(await pool.OpenAsync(scope, 1, ct));
        }
        Assert.Equal(scopes.Length, opened.Distinct().Count());
        Assert.Equal(5, pool.ListStatuses("account", "connection").Count);
        Assert.Single(pool.ListStatuses("other", "connection"));
        Assert.Single(pool.ListStatuses("account", "other"));
        await pool.DisposeAsync();
        Assert.All(opened, client => Assert.Equal(CodexMcpState.Closed, client.State));
    }

    [Fact]
    public async Task RevisionFencesStaleOpensAndDisablingClosesPreviousConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var pool = new CodexMcpConnectionPool((_, token) => Ready(token));
        await pool.ConfigureAsync(Registration(), ct);
        var old = await pool.OpenAsync(Scope, 1, ct);
        await pool.ConfigureAsync(Registration(), ct);
        Assert.Same(old, await pool.OpenAsync(Scope, 1, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.ConfigureAsync(
            Registration() with { AuthorityFingerprint = new string('b', 64) }, ct));
        await pool.ConfigureAsync(Registration(revision: 2, enabled: false), ct);
        Assert.Equal(CodexMcpState.Closed, old.State);
        Assert.Equal(CodexMcpRuntimeStatus.Disabled, pool.GetStatus(Scope)!.RuntimeStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.OpenAsync(Scope, 1, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pool.OpenAsync(Scope, 2, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.ConfigureAsync(Registration(), ct));
        await pool.ConfigureAsync(Registration(revision: 3), ct);
        Assert.NotSame(old, await pool.OpenAsync(Scope, 3, ct));
    }

    [Fact]
    public async Task ReplacementCancelsStartupAndFencesEvenLateReadyClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<CodexMcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new CodexMcpConnectionPool(async (registration, token) =>
        {
            if (registration.Revision != 1)
            {
                newerStarted.TrySetResult();
                return await Ready(token);
            }
            using var signal = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            // Deliberately model a completion arriving after cancellation.
            return await late.Task.WaitAsync(ct);
        });
        await pool.ConfigureAsync(Registration(), ct);
        var oldOpen = pool.OpenAsync(Scope, 1, ct);
        await started.Task.WaitAsync(ct);
        var replace = pool.ConfigureAsync(Registration(revision: 2), ct);
        await cancelled.Task.WaitAsync(ct);
        var newOpen = pool.OpenAsync(Scope, 2, ct);
        Assert.False(newerStarted.Task.IsCompleted);
        var stale = await Ready(ct);
        late.SetResult(stale);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldOpen);
        await replace;
        Assert.Equal(CodexMcpState.Closed, stale.State);
        Assert.NotSame(stale, await newOpen);
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelSharedStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        await using var pool = new CodexMcpConnectionPool(async (_, token) =>
        {
            Interlocked.Increment(ref opens);
            await release.Task.WaitAsync(token);
            return await Ready(token);
        });
        await pool.ConfigureAsync(Registration(), ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = pool.OpenAsync(Scope, 1, cancelled.Token);
        var second = pool.OpenAsync(Scope, 1, ct);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal(CodexMcpState.Ready, (await second).State);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task UnauthorizedStartupIsNotAutomaticallyRetriedOrAuthenticated()
    {
        var ct = TestContext.Current.CancellationToken;
        var opens = 0;
        await using var pool = new CodexMcpConnectionPool((_, _) =>
        {
            Interlocked.Increment(ref opens);
            return Task.FromException<CodexMcpClient>(new CodexMcpHttpException(HttpStatusCode.Unauthorized));
        });
        await pool.ConfigureAsync(Registration(), ct);
        await Assert.ThrowsAsync<CodexMcpHttpException>(() => pool.OpenAsync(Scope, 1, ct));
        Assert.Equal(CodexMcpRuntimeStatus.AuthenticationRequired, pool.GetStatus(Scope)!.RuntimeStatus);
        await Assert.ThrowsAsync<CodexMcpHttpException>(() => pool.OpenAsync(Scope, 1, ct));
        Assert.Equal(1, opens);
        await pool.ConfigureAsync(Registration(revision: 2), ct);
        await Assert.ThrowsAsync<CodexMcpHttpException>(() => pool.OpenAsync(Scope, 2, ct));
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task FactoryCannotAccidentallyShareClientAcrossAccounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var shared = await Ready(ct);
        await using var pool = new CodexMcpConnectionPool((_, _) => Task.FromResult(shared));
        var other = Scope with { AccountId = "other" };
        await pool.ConfigureAsync(Registration(), ct);
        await pool.ConfigureAsync(Registration(other), ct);
        Assert.Same(shared, await pool.OpenAsync(Scope, 1, ct));
        await Assert.ThrowsAsync<IOException>(() => pool.OpenAsync(other, 1, ct));
        Assert.Equal(CodexMcpRuntimeStatus.Failed, pool.GetStatus(other)!.RuntimeStatus);
        Assert.Equal(CodexMcpState.Ready, shared.State);
        Assert.Same(shared, await pool.OpenAsync(Scope, 1, ct));
    }

    [Fact]
    public async Task ChainedReplacementCannotSkipUnsettledPredecessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<CodexMcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = new List<long>();
        await using var pool = new CodexMcpConnectionPool(async (registration, token) =>
        {
            opens.Add(registration.Revision);
            if (registration.Revision == 1)
            {
                started.TrySetResult();
                return await late.Task.WaitAsync(ct);
            }
            return await Ready(token);
        });
        await pool.ConfigureAsync(Registration(), ct);
        var old = pool.OpenAsync(Scope, 1, ct);
        await started.Task.WaitAsync(ct);
        var second = pool.ConfigureAsync(Registration(revision: 2), ct);
        var third = pool.ConfigureAsync(Registration(revision: 3), ct);
        var newest = pool.OpenAsync(Scope, 3, ct);
        Assert.Equal(new long[] { 1 }, opens);
        var stale = await Ready(ct);
        late.SetResult(stale);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        await second;
        await third;
        Assert.Equal(CodexMcpState.Closed, stale.State);
        Assert.Equal(CodexMcpState.Ready, (await newest).State);
        Assert.Equal(new long[] { 1, 3 }, opens);
    }

    [Fact]
    public async Task InvalidScopeAndUnregisteredGenerationCannotStartFactory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var pool = new CodexMcpConnectionPool((_, _) => throw new InvalidOperationException("Unexpected startup."));
        await Assert.ThrowsAsync<ArgumentException>(() => pool.ConfigureAsync(Registration(
            Scope with { ThreadId = null }), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => pool.ConfigureAsync(Registration() with { AuthorityFingerprint = "bad" }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.OpenAsync(Scope, 1, ct));
        Assert.Null(pool.GetStatus(Scope));
    }
}
