using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexResponsesIdentityTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        internal CodexCredentials Active = new("old-token", "account-a") { ChatGptUserId = "user-a" };
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) => ValueTask.FromResult(Active);
    }
    private sealed class Observer(CodexResponsesIdentity identity, Action? afterReservation = null) : ICodexUsageObserver
    {
        internal readonly List<string> Events = [];
        public Task ReserveAsync(string id, string model, CancellationToken ct) { Events.Add("reserve"); return Task.CompletedTask; }
        public async Task<CodexResponsesIdentity?> ReserveWithIdentityAsync(string id, string model, CancellationToken ct)
        { await ReserveAsync(id, model, ct); afterReservation?.Invoke(); return identity; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) { Events.Add("complete"); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Events.Add("unknown"); return Task.CompletedTask; }
        public Task NotDispatchedAsync(string id, string model, CancellationToken ct) { Events.Add("not_dispatched"); return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task> inspect) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; await inspect(request);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-a\",\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private static async Task Read(CodexClient client, CodexResponsesIdentity? identity = null)
    { await foreach (var _ in client.StreamResponseAsync(new("model", "instructions") { Identity = identity }, [])) { } }

    [Fact]
    public async Task DurableIdentityMatchesBodyHeadersAndDiagnosticsBeforeDispatch()
    {
        var identity = new CodexResponsesIdentity("session-a", "thread-a", "turn-a")
        { ParentThreadId = "parent-a", ParentTurnId = "parent-turn", RootTurnId = "parent-turn", Subagent = "collab_spawn" };
        var observer = new Observer(identity);
        var handler = new Handler(async request =>
        {
            Assert.Equal(new[] { "reserve" }, observer.Events);
            Assert.Equal("thread-a", Assert.Single(request.Headers.GetValues("thread-id")));
            Assert.Equal("thread-a", Assert.Single(request.Headers.GetValues("x-client-request-id")));
            Assert.Equal("session-a", Assert.Single(request.Headers.GetValues("session-id")));
            Assert.Equal("collab_spawn", Assert.Single(request.Headers.GetValues("x-openai-subagent")));
            using var doc = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync());
            var metadata = doc.RootElement.GetProperty("client_metadata");
            Assert.Equal("turn-a", metadata.GetProperty("turn_id").GetString());
            Assert.Equal("parent-turn", metadata.GetProperty("parent_turn_id").GetString());
            Assert.False(doc.RootElement.GetProperty("store").GetBoolean());
        });
        using var client = new CodexClient(new Credentials(), handler: handler, usageObserver: observer);
        var points = new List<CodexResponseDiagnostic>();
        await foreach (var _ in client.StreamResponseAsync(new("model", "instructions"), [], observeDiagnostics: points.Add, cancellationToken: TestContext.Current.CancellationToken)) { }
        Assert.Equal(identity, Assert.Single(points, p => p.Phase == "request.prepared").Identity);
        Assert.Equal(new[] { "reserve", "complete" }, observer.Events);
    }

    [Fact]
    public async Task ConflictingIdentityReleasesReservationWithoutDispatch()
    {
        var observer = new Observer(new("session", "thread", "reserved-turn"));
        var handler = new Handler(_ => Task.CompletedTask);
        using var client = new CodexClient(new Credentials(), handler: handler, usageObserver: observer);
        await Assert.ThrowsAsync<CodexProtocolException>(() => Read(client, new("session", "thread", "other-turn")));
        Assert.Equal(new[] { "reserve", "not_dispatched" }, observer.Events); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InvalidHeaderIdentityCannotReserveOrDispatch()
    {
        var observer = new Observer(new("s", "t", "u"));
        var handler = new Handler(_ => Task.CompletedTask);
        using var client = new CodexClient(new Credentials(), handler: handler, usageObserver: observer);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(client, new("bad\r\nheader", "thread", "turn")));
        Assert.Empty(observer.Events); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AccountSwitchAfterReservationCannotSendWithOldBudget()
    {
        var inner = new Credentials(); var bound = new CodexAccountBoundCredentialProvider(inner);
        await bound.GetCredentialsAsync(TestContext.Current.CancellationToken);
        var observer = new Observer(new("s", "t", "u"), () => inner.Active = new("new-token", "account-b"));
        var handler = new Handler(_ => Task.CompletedTask);
        using var client = new CodexClient(bound, handler: handler, usageObserver: observer);
        await Assert.ThrowsAsync<CodexProtocolException>(() => Read(client));
        Assert.Equal(0, handler.Calls); Assert.Equal(new[] { "reserve", "not_dispatched" }, observer.Events);
        Assert.DoesNotContain("account-a", bound.ToString());
    }

    [Fact]
    public async Task TokenRotationAndLegacyIdentityUpgradeKeepBindingButKnownUserReplacementFails()
    {
        var inner = new Credentials { Active = new("old", "account-a") };
        var bound = new CodexAccountBoundCredentialProvider(inner); await bound.GetCredentialsAsync(TestContext.Current.CancellationToken);
        inner.Active = new("new", "account-a") { ChatGptUserId = "user-a" };
        Assert.Equal("new", (await bound.GetCredentialsAsync(TestContext.Current.CancellationToken)).AccessToken);
        inner.Active = new("later", "account-a") { ChatGptUserId = "user-b" };
        await Assert.ThrowsAsync<CodexProtocolException>(async () => await bound.GetCredentialsAsync(TestContext.Current.CancellationToken));
    }
}
