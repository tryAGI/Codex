using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexPersistentModelCatalogTests
{
    private const string Version = "0.172.0";
    private const string Payload = """{"models":[{"slug":"native","display_name":"Native","visibility":"list","supported_in_api":true,"future":{"enabled":true}}]}""";

    [Fact]
    public async Task ReopenedCacheIsUsedAfterTokenRotationAndByNativeModelList()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new DirectoryFixture();
        var scope = Scope();
        var credentials = new Credentials();
        var calls = 0;
        using (var first = new CodexClient(credentials, handler: new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply());
        })))
        {
            await first.GetPersistentModelCatalogAsync(Version, new(directory.Path), Read(scope), cancellationToken: ct);
        }
        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.json"));
        var stored = await File.ReadAllTextAsync(path, ct);
        Assert.DoesNotContain(scope.AccountId, stored);
        Assert.DoesNotContain(scope.UserId, stored);
        Assert.DoesNotContain(scope.Email!, stored);
        Assert.DoesNotContain(credentials.Current.AccessToken, stored);
        using (var json = JsonDocument.Parse(stored))
            Assert.Equal(64, json.RootElement.GetProperty("identity").GetString()!.Length);
        credentials.Current = new("rotated-secret", "account-private") { ChatGptUserId = "user-private" };
        scope = scope with { SelectionEpoch = "new-login-epoch" };
        using var second = new CodexClient(credentials, handler: new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP request.")));
        var source = second.CreatePersistentModelCatalogSource(Version, new(directory.Path), Read(scope));
        var catalog = await source.ReadAsync(ct);
        Assert.True(Assert.Single(catalog.Models).Raw.GetProperty("future").GetProperty("enabled").GetBoolean());
        var rpc = new CodexModelCatalogRpc([source], "openai");
        var list = await rpc.InvokeAsync("model/list", cancellationToken: ct);
        Assert.Equal("native", list.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredCatalogRevalidatesWithEtagAndRenewsPersistedFreshness()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new DirectoryFixture();
        var clock = new Clock();
        var scope = Scope();
        var requests = new List<string?>();
        using var client = new CodexClient(new Credentials(), handler: new Handler((request, _) =>
        {
            requests.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.Tag);
            return Task.FromResult(requests.Count == 1 ? Reply() : new HttpResponseMessage(HttpStatusCode.NotModified));
        }));
        var cache = new CodexFileModelCatalogCache(directory.Path, TimeSpan.FromMinutes(5), clock);
        var before = await client.GetPersistentModelCatalogAsync(Version, cache, Read(scope), cancellationToken: ct);
        clock.Now = clock.Now.AddMinutes(6);
        var after = await client.GetPersistentModelCatalogAsync(Version, cache, Read(scope), cancellationToken: ct);
        Assert.NotEqual(before.ObservedAt, after.ObservedAt);
        Assert.Equal(clock.Now, after.ObservedAt);
        Assert.Equal(before.Models[0].Model, after.Models[0].Model);
        Assert.Equal(new string?[] { null, "\"catalog-v1\"" }, requests);
        var reopened = new CodexFileModelCatalogCache(directory.Path, TimeSpan.FromMinutes(5), clock);
        await client.GetPersistentModelCatalogAsync(Version, reopened, Read(scope), cancellationToken: ct);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task PlanEmailProviderEntitlementOwnerAndVersionPartitionTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new DirectoryFixture();
        var cache = new CodexFileModelCatalogCache(directory.Path);
        var credentials = new Credentials();
        var scope = Scope();
        var requests = 0;
        using var client = new CodexClient(credentials, handler: new Handler((request, _) =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            requests++;
            return Task.FromResult(Reply());
        }));
        var variants = new[] { scope,
            scope with { PlanType = "team" }, scope with { RawPlanType = "new-plan" },
            scope with { Email = "new@example.test" }, scope with { EntitlementRevision = "changed" },
            scope with { ProviderRevision = "changed-route" }, scope with { OwnerScope = "other-owner" } };
        foreach (var identity in variants)
            await client.GetPersistentModelCatalogAsync(Version, cache, Read(identity), cancellationToken: ct);
        await client.GetPersistentModelCatalogAsync("0.172.1", cache, Read(scope), cancellationToken: ct);
        Assert.Equal(variants.Length + 1, requests);
        Assert.Equal(requests, Directory.GetFiles(directory.Path, "*.json").Length);
        await client.GetPersistentModelCatalogAsync(Version, cache, Read(scope), cancellationToken: ct);
        Assert.Equal(variants.Length + 1, requests);
        credentials.Current = new("new-user-token", scope.AccountId) { ChatGptUserId = "other-user" };
        await Assert.ThrowsAsync<CodexProtocolException>(() =>
            client.GetPersistentModelCatalogAsync(Version, cache, Read(scope), cancellationToken: ct));
        Assert.Equal(variants.Length + 1, requests);
    }

    [Fact]
    public async Task FailedForcedRefreshDoesNotLeaveOldFreshSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new DirectoryFixture();
        var cache = new CodexFileModelCatalogCache(directory.Path);
        var requests = new List<string?>();
        using var client = new CodexClient(new Credentials(), handler: new Handler((request, _) =>
        {
            requests.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.Tag);
            return Task.FromResult(requests.Count == 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Reply());
        }));
        await client.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), cancellationToken: ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() =>
            client.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), forceRefresh: true, cancellationToken: ct));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.json"));
        await client.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), cancellationToken: ct);
        Assert.Equal(new string?[] { null, "\"catalog-v1\"", null }, requests);
    }

    [Fact]
    public async Task SelectionChangeDuringProviderReadCannotPublishCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new DirectoryFixture();
        var cache = new CodexFileModelCatalogCache(directory.Path);
        var scope = Scope();
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
        {
            scope = scope with { SelectionEpoch = "switched-back" };
            return Task.FromResult(Reply());
        }));
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.GetPersistentModelCatalogAsync(Version, cache,
            _ => Task.FromResult(scope), cancellationToken: ct));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.json"));
    }

    [Fact]
    public async Task CorruptOrUnscopedEntriesAreMissesNotAuthorization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new DirectoryFixture();
        var cache = new CodexFileModelCatalogCache(directory.Path);
        var requests = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((request, _) =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            requests++;
            return Task.FromResult(Reply());
        }));
        await client.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), cancellationToken: ct);
        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.json"));
        await File.WriteAllTextAsync(path, "{", ct);
        await client.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), cancellationToken: ct);
        using var valid = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var text = valid.RootElement.GetRawText().Replace(valid.RootElement.GetProperty("identity").GetString()!, new string('0', 64), StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text, ct);
        await client.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), cancellationToken: ct);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task ConcurrentForcedRefreshCannotOverwriteAnOwnedCacheGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new DirectoryFixture();
        var cache = new CodexFileModelCatalogCache(directory.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new CodexClient(new Credentials(), handler: new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Reply();
        }));
        using var second = new CodexClient(new Credentials(), handler: new Handler((_, _) => throw new InvalidOperationException("Unexpected provider call.")));
        var pending = first.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), cancellationToken: ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        try
        {
            await Assert.ThrowsAsync<CodexProtocolException>(() =>
                second.GetPersistentModelCatalogAsync(Version, cache, Read(Scope()), forceRefresh: true, cancellationToken: ct));
        }
        finally { release.TrySetResult(); }
        await pending;
        Assert.Single(Directory.GetFiles(directory.Path, "*.json"));
    }

    private static CodexModelCatalogCacheScope Scope() => new("host-private", "epoch", "account-private", "user-private",
        "entitlement-v1", "provider-v1", "private@example.test", "pro", "pro");
    private static Func<CancellationToken, Task<CodexModelCatalogCacheScope>> Read(CodexModelCatalogCacheScope scope) =>
        ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(scope); };

    private static HttpResponseMessage Reply()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Payload, Encoding.UTF8, "application/json") };
        response.Headers.ETag = new EntityTagHeaderValue("\"catalog-v1\"");
        return response;
    }

    private sealed class DirectoryFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("codex-catalog-cache-");
        internal string Path => directory.FullName;
        public void Dispose() => directory.Delete(recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        internal CodexCredentials Current { get; set; } = new("access-secret", "account-private") { ChatGptUserId = "user-private" };
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
