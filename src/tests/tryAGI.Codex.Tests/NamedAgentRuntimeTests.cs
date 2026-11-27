using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class NamedAgentRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static Guid Root => Id(1);
    private static Guid Child => Id(2);
    private static Guid Sibling => Id(3);
    private static Guid ParentTurn => Id(10);
    private static Guid RootTurn => Id(11);
    private static Guid Turn => Id(30);
    private static Guid Claim => Id(31);
    private const string Owner = "parent";
    private const string ChildOwner = "child";
    private const string Success = """{"type":"response.completed","response":{"id":"child-response","model":"child","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}}""";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static void RequireMac() => Assert.SkipUnless(OperatingSystem.IsMacOS(), "Actual runtime storage requires macOS.");

    private sealed class Fixture : IDisposable
    {
        private string DirectoryPath { get; } = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-agent-runtime-").FullName);
        internal Task<CodexMacOsSqliteAgentStorage> OpenAsync(bool create = false)
            => CodexMacOsSqliteAgentStorage.OpenAsync(Path.Combine(DirectoryPath, "agents.sqlite"), "account", create, Ct);
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new CodexCredentials("test", "account"));
    }

    private sealed class Budget : ICodexUsageObserver
    {
        public Task ReserveAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) => Task.CompletedTask;
        public Task IncompleteAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Provider(string response = Success) : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + response + "\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static CodexThreadStore Store(CodexMacOsSqliteAgentStorage db) => new(db, "account");
    private static CodexThreadAgent Agent(CodexThreadStore store, Provider provider) => new(store, new Credentials(),
        new Budget(), clientFactory: (credentials, usage) => new CodexClient(credentials, handler: provider, usageObserver: usage));

    private static Task<CodexToolOutput> NoTools(CodexToolCall call, CancellationToken ct)
        => throw new InvalidOperationException("No tools are expected.");

    private static CodexNamedAgentWakeRequest Request(Guid child, Guid turn, Guid claim)
        => new(child, 1, 1, claim, turn, ChildOwner, new CodexResponseOptions("child", "instructions")
        {
            Identity = new("session", child.ToString(), turn.ToString())
            {
                ParentThreadId = Root.ToString(), ParentTurnId = ParentTurn.ToString(), RootTurnId = RootTurn.ToString(),
            },
        });

    private static async Task PrepareAsync(CodexThreadStore store, bool sibling = false)
    {
        var parent = await store.CreateAsync(Root, Json("""{"model":"parent"}"""), Ct);
        parent = await store.StartTurnAsync(Root, parent.Revision, ParentTurn, Owner,
            [Json("""{"role":"user","content":"delegate"}""")], RootTurn, Ct);
        async Task Spawn(Guid child, string name, string dispatch, Guid message)
        {
            parent = (await store.AdmitAsync(Root, parent.Revision, ParentTurn, Owner, dispatch, "tool",
                Json("""{"message":"do work"}"""), Ct)).Thread;
            var graph = await store.ReadAgentIdentitiesAsync(Root, Ct);
            await store.SpawnNamedAgentAsync(new(Root, Root, parent.Revision, graph.Graph.Revision, ParentTurn,
                Owner, dispatch, child, name, Json("""{"model":"child"}"""),
                new(message, Root, CodexAgentMailMode.Followup, "work"), 3), Ct);
        }
        await Spawn(Child, "worker", "spawn-one", Id(20));
        if (sibling) await Spawn(Sibling, "other", "spawn-two", Id(21));
    }

    [Fact]
    public async Task WaitAndWatchObserveExactLocalTurnWithoutPollingOrCancellingItsRun()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var db = await fixture.OpenAsync(create: true);
        var store = Store(db);
        await PrepareAsync(store, sibling: true);
        var runtime = new CodexNamedAgentRuntime(store, Root, 1);
        using var provider = new Provider();
        var agent = Agent(store, provider);
        var request = Request(Child, Turn, Claim);
        var run = runtime.RunWakeAsync(request, agent, NoTools, Ct);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(1, runtime.ActiveExecutions);
            await using var watch = runtime.WatchAsync(Root, Child, Turn, Ct).GetAsyncEnumerator(Ct);
            Assert.True(await watch.MoveNextAsync());
            Assert.Equal(CodexNamedAgentExecutionStatus.Running, watch.Current.Status);
            Assert.True(watch.Current.IsLocal);
            var wait = runtime.WaitAsync(Root, Child, Turn, Ct);
            Assert.False(wait.IsCompleted);
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                var abandoned = runtime.WaitAsync(Root, Child, Turn, cancel.Token);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
            }
            Assert.Equal(1, runtime.ActiveExecutions);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunWakeAsync(request, agent, NoTools, Ct));
            var sibling = Request(Sibling, Id(40), Id(41));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunWakeAsync(sibling, agent, NoTools, Ct));
            Assert.Null(await db.ReadWakeAuthorizationAsync(Root, Sibling, sibling.ClaimId, sibling.TurnId, ChildOwner, Ct));

            provider.Release.TrySetResult();
            Assert.IsType<CodexThreadAgentResult>(await run);
            var finished = await wait.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(CodexNamedAgentExecutionStatus.Completed, finished.Status);
            Assert.False(finished.IsLocal);
            Assert.Equal(Turn, finished.TurnId);
            Assert.True(await watch.MoveNextAsync());
            Assert.Equal(finished, watch.Current);
            Assert.False(await watch.MoveNextAsync());
            Assert.Equal(0, runtime.ActiveExecutions);
            Assert.Equal(1, provider.Requests);
        }
        finally { provider.Release.TrySetResult(); }
    }

    [Fact]
    public async Task InterruptTargetsOnlyExactLocalInvocationAndReportsDurableInterruption()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var first = await fixture.OpenAsync(create: true);
        await using var second = await fixture.OpenAsync();
        var store = Store(first);
        await PrepareAsync(store);
        var runtime = new CodexNamedAgentRuntime(store, Root, 1);
        var external = new CodexNamedAgentRuntime(Store(second), Root, 1);
        using var provider = new Provider();
        var run = runtime.RunWakeAsync(Request(Child, Turn, Claim), Agent(store, provider), NoTools, Ct);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            var observed = await external.WaitAsync(Root, Child, Turn, Ct);
            Assert.Equal(CodexNamedAgentExecutionStatus.Running, observed.Status);
            Assert.False(observed.IsLocal);
            Assert.False((await external.InterruptAsync(Root, Child, Turn, Ct)).Requested);
            Assert.False((await runtime.InterruptAsync(Root, Child, Id(99), Ct)).Requested);
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.InterruptAsync(Child, Child, Turn, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.InterruptAsync(Child, Root, ParentTurn, Ct));
            var waiting = runtime.WaitAsync(Root, Child, Turn, Ct);
            var interrupted = await runtime.InterruptAsync(Root, Child, Turn, Ct);
            Assert.True(interrupted.Requested);
            Assert.Equal(CodexNamedAgentExecutionStatus.Running, interrupted.Before.Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            var settled = await waiting.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(CodexNamedAgentExecutionStatus.Interrupted, settled.Status);
            Assert.False(settled.IsLocal);
            Assert.Equal(0, runtime.ActiveExecutions);
            Assert.Equal("interrupted", Assert.Single((await store.ReadAsync(Child, Ct)).Turns).Status);
            Assert.Equal(1, provider.Requests);
            Assert.False((await runtime.InterruptAsync(Root, Child, Turn, Ct)).Requested);
        }
        finally { provider.Release.TrySetResult(); }
    }

    [Fact]
    public async Task InterruptWaitsForAdmittedToolToSettleWithoutStartingAnotherRequest()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var db = await fixture.OpenAsync(create: true);
        var store = Store(db);
        await PrepareAsync(store);
        var runtime = new CodexNamedAgentRuntime(store, Root, 1);
        const string toolResponse = """{"type":"response.completed","response":{"id":"tool-response","model":"child","output":[{"type":"function_call","name":"read","call_id":"call-read","arguments":"{}"}]}}""";
        using var provider = new Provider(toolResponse);
        provider.Release.TrySetResult();
        var request = Request(Child, Turn, Claim);
        request = request with { Options = request.Options with
        {
            Tools = [Json("""{"type":"function","name":"read","parameters":{"type":"object","properties":{},"additionalProperties":false}}""")],
        } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken toolToken = default;
        var run = runtime.RunWakeAsync(request, Agent(store, provider), async (_, ct) =>
        {
            toolToken = ct;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new CodexToolOutput("observed result");
        }, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            var wait = runtime.WaitAsync(Root, Child, Turn, Ct);
            Assert.True((await runtime.InterruptAsync(Root, Child, Turn, Ct)).Requested);
            Assert.False(toolToken.IsCancellationRequested);
            Assert.False(run.IsCompleted);
            Assert.Equal(1, runtime.ActiveExecutions);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(CodexNamedAgentExecutionStatus.Interrupted, (await wait).Status);
            var turn = Assert.Single((await store.ReadAsync(Child, Ct)).Turns);
            var receipt = Assert.Single(turn.Dispatches, item => item.CallId == "call-read");
            Assert.Equal("succeeded", receipt.State);
            Assert.Contains("observed result", receipt.Result!.Value.GetRawText());
            Assert.Equal(1, provider.Requests);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task KnownPreAdmissionFailureFencesItsOwnGrantAndAllowsFreshWake()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var db = await fixture.OpenAsync(create: true);
        var store = Store(db);
        await PrepareAsync(store);
        var runtime = new CodexNamedAgentRuntime(store, Root, 1);
        var request = Request(Child, Turn, Claim);
        using var provider = new Provider();
        provider.Release.TrySetResult();
        var agent = Agent(store, provider);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.RunWakeAsync(request with
        {
            Options = request.Options with { Identity = request.Options.Identity! with { ParentThreadId = Id(99).ToString() } },
        }, agent, NoTools, Ct));
        Assert.Equal(CodexAgentWakeGrantState.Fenced,
            await db.ReadWakeAuthorizationAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
        Assert.Equal(CodexAgentWakeGrantState.Fenced, await runtime.ReconcileStoppedWakeAsync(request, Ct));
        Assert.Null((await store.ReadAgentMailboxAsync(Root, Child, Ct)).Claim);
        Assert.Empty((await store.ReadAsync(Child, Ct)).Turns);
        Assert.Equal(0, provider.Requests);
        Assert.IsType<CodexThreadAgentResult>(await runtime.RunWakeAsync(Request(Child, Id(40), Id(41)), agent, NoTools, Ct));
        Assert.Equal(1, provider.Requests);
    }

    [Fact]
    public async Task SettledSubtreeCloseIsAtomicAndPreservesMailAndHistoricalIdentities()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using (var first = await fixture.OpenAsync(create: true))
        {
            var store = Store(first);
            await PrepareAsync(store);
            var directory = await store.ReadAgentIdentitiesAsync(Root, Ct);
            var grandchild = Id(4);
            await store.UpsertSpawnEdgeAsync(Root, directory.Graph.Revision, new(Child, grandchild,
                CodexThreadSpawnEdgeStatus.Open), Ct);
            var graph = await store.ReadSpawnGraphAsync(Root, Ct);
            var runtime = new CodexNamedAgentRuntime(store, Root, 1);
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.CloseSettledSubtreeAsync(Child, Child, graph.Revision, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => runtime.CloseSettledSubtreeAsync(Child, Root, graph.Revision, Ct));
            Assert.True(await first.AuthorizeWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            await Assert.ThrowsAsync<CodexThreadConflictException>(() => runtime.CloseSettledSubtreeAsync(Root, Child, graph.Revision, Ct));
            var unchanged = await store.ReadSpawnGraphAsync(Root, Ct);
            Assert.Equal(graph.Revision, unchanged.Revision);
            Assert.All(unchanged.Edges, edge => Assert.Equal(CodexThreadSpawnEdgeStatus.Open, edge.Status));
            Assert.True(await first.FenceUnadmittedWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            await runtime.CloseSettledSubtreeAsync(Root, Child, graph.Revision, Ct);
            Assert.All((await store.ReadSpawnGraphAsync(Root, Ct)).Edges, edge => Assert.Equal(CodexThreadSpawnEdgeStatus.Closed, edge.Status));
            Assert.False(await first.AuthorizeWakeAsync(Root, Child, Id(33), Id(32), ChildOwner, Ct));
        }
        await using var reopened = await fixture.OpenAsync();
        var after = Store(reopened);
        var recorded = await after.ReadAgentIdentitiesAsync(Root, Ct);
        Assert.Empty(recorded.RestorableDescendants);
        Assert.Equal("/root/worker", recorded.GetRecorded(Child).Path);
        Assert.Equal(2, recorded.Graph.Edges.Count);
        Assert.All(recorded.Graph.Edges, edge => Assert.Equal(CodexThreadSpawnEdgeStatus.Closed, edge.Status));
        Assert.Equal(Id(20), Assert.Single((await after.ReadAgentMailboxAsync(Root, Child, Ct)).Pending).Id);
        Assert.Empty((await after.ReadAsync(Child, Ct)).Turns);
    }

    [Fact]
    public async Task OpenRealtimeOwnershipPreventsClosingASettledNamedAgent()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var db = await fixture.OpenAsync(create: true);
        var store = Store(db);
        await PrepareAsync(store);
        var child = await store.ReadAsync(Child, Ct);
        child = await store.CreateRealtimeSessionAsync(Child, child.Revision, "voice", "rtc_test", "v3", "voice-owner", Ct);
        var graph = await store.ReadSpawnGraphAsync(Root, Ct);
        var runtime = new CodexNamedAgentRuntime(store, Root, 1);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => runtime.CloseSettledSubtreeAsync(Root, Child, graph.Revision, Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.SetSpawnEdgeStatusAsync(Root,
            graph.Revision, Child, CodexThreadSpawnEdgeStatus.Closed, Ct));
        Assert.Equal(graph.Revision, (await store.ReadSpawnGraphAsync(Root, Ct)).Revision);
    }
}
