using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexResponsesRoutingTests
{
    private const string Header = "x-codex-turn-state";
    private static readonly CodexResponsesIdentity Identity = new("session", "thread", "turn")
    {
        VoiceSessionId = "voice",
        InstallationId = "installation",
        WindowId = "window",
        ContextWindowId = "context",
        RoutingHint = "route",
        BetaFeatures = "one,two",
    };

    private sealed class Credentials : ICodexCredentialProvider
    {
        internal CodexCredentials Active = new("token-a", "account-a");
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) => ValueTask.FromResult(Active);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    private static HttpResponseMessage Completed(params string[] states)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"response\",\"output\":[]}}\n\n",
                Encoding.UTF8, "text/event-stream"),
        };
        if (states.Length != 0) response.Headers.TryAddWithoutValidation(Header, states);
        return response;
    }

    private static string? State(HttpRequestMessage request) =>
        request.Headers.TryGetValues(Header, out var values) ? Assert.Single(values) : null;

    private static async Task Read(CodexClient client, CodexResponsesIdentity? identity)
    {
        await foreach (var _ in client.StreamResponseAsync(new("model", "instructions") { Identity = identity }, [],
            TestContext.Current.CancellationToken)) { }
    }

    // Rust captures the first supplied token and replays it unchanged for the turn.
    [Fact]
    public async Task FirstStateIsImmutableAndMetadataCorrelatesTheBackingTurn()
    {
        var calls = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(async request =>
        {
            Assert.Equal(calls == 0 ? null : "sticky-a", State(request));
            Assert.Equal("window", Assert.Single(request.Headers.GetValues("x-codex-window-id")));
            Assert.Equal("route", Assert.Single(request.Headers.GetValues("x-codex-routing-hint")));
            Assert.Equal("one,two", Assert.Single(request.Headers.GetValues("x-codex-beta-features")));
            using var metadata = JsonDocument.Parse(Assert.Single(request.Headers.GetValues("x-codex-turn-metadata")));
            Assert.Equal("context", metadata.RootElement.GetProperty("context_window_id").GetString());
            Assert.Equal("turn", metadata.RootElement.GetProperty("turn_id").GetString());
            using var body = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException()).ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("installation", body.RootElement.GetProperty("client_metadata").GetProperty("x-codex-installation-id").GetString());
            // voice-session-id belongs to the realtime route, not the Responses headers.
            Assert.False(request.Headers.Contains("voice-session-id"));
            return Completed(++calls == 1 ? "sticky-a" : "sticky-b");
        }));
        for (var i = 0; i < 3; i++) await Read(client, Identity);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TurnRouteAndCredentialChangesCannotReplayState()
    {
        var credentials = new Credentials();
        var sent = new List<string?>();
        using var client = new CodexClient(credentials, handler: new Handler(request =>
        {
            sent.Add(State(request));
            return Task.FromResult(Completed("sticky-" + sent.Count));
        }));
        await Read(client, Identity);
        await Read(client, Identity);
        var next = Identity with { TurnId = "turn-2" };
        await Read(client, next);
        credentials.Active = new("token-b", "account-b");
        await Read(client, next);
        credentials.Active = new("token-c", "account-b");
        await Read(client, next);
        var route = next with { RoutingHint = "route-2" };
        await Read(client, route);
        await Read(client, null);
        await Read(client, route);
        Assert.Equal(new string?[] { null, "sticky-1", null, null, null, null, null, null }, sent);
    }

    [Fact]
    public async Task InterleavedThreadsRetainIndependentTurnStatesAndAccountChangeClearsAll()
    {
        var credentials = new Credentials();
        var sent = new List<string?>();
        using var client = new CodexClient(credentials, handler: new Handler(request =>
        {
            sent.Add(State(request));
            var thread = Assert.Single(request.Headers.GetValues("thread-id"));
            return Task.FromResult(Completed("sticky-" + thread));
        }));
        var other = Identity with { ThreadId = "other" };
        await Read(client, Identity);
        await Read(client, other);
        await Read(client, Identity);
        await Read(client, other);
        credentials.Active = new("changed-token", "changed-account");
        await Read(client, Identity);
        await Read(client, other);
        Assert.Equal(new string?[] { null, null, "sticky-thread", "sticky-other", null, null }, sent);
    }

    [Fact]
    public async Task EvictedThreadCannotRetainOrRestoreStaleTurnState()
    {
        var sent = new List<string?>();
        using var client = new CodexClient(new Credentials(), handler: new Handler(request =>
        {
            sent.Add(State(request));
            return Task.FromResult(Completed("sticky"));
        }));
        await Read(client, Identity);
        for (var i = 0; i < 64; i++) await Read(client, Identity with { ThreadId = "other-" + i });
        await Read(client, Identity);
        Assert.All(sent, Assert.Null);
    }

    [Fact]
    public async Task MissingOrAmbiguousStateDoesNotBecomeRoutingState()
    {
        var count = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(request =>
        {
            Assert.Equal(count == 4 ? "valid" : null, State(request));
            return Task.FromResult(++count switch
            {
                1 => Completed(),
                2 => Completed("a", "b"),
                3 => Completed("invalid state"),
                _ => Completed("valid"),
            });
        }));
        for (var i = 0; i < 5; i++) await Read(client, Identity);
    }

    [Fact]
    public async Task LateHeadersFromAnOldTurnCannotPoisonTheCurrentTurn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = Identity with { TurnId = "new-turn" };
        var calls = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(async request =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    entered.SetResult();
                    await release.Task.WaitAsync(TestContext.Current.CancellationToken);
                    return Completed("stale");
                case 2:
                    Assert.Null(State(request));
                    return Completed("current");
                default:
                    Assert.Equal("current", State(request));
                    return Completed();
            }
        }));
        var old = Read(client, Identity);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try { await Read(client, next); }
        finally { release.TrySetResult(); }
        await old;
        await Read(client, next);
    }
}
