using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class SqliteAgentStorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static Guid Root => Id(1);
    private static Guid Child => Id(2);
    private static Guid ParentTurn => Id(10);
    private static Guid RootTurn => Id(11);
    private static Guid Claim => Id(31);
    private static Guid Turn => Id(30);
    private const string Owner = "parent-owner";
    private const string ChildOwner = "child-owner";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static void RequireMac() => Assert.SkipUnless(OperatingSystem.IsMacOS(), "Actual agent SQLite adapter requires macOS.");
    private const string Success = """{"type":"response.completed","response":{"id":"child-response","model":"child","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}}""";

    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-agent-sqlite-").FullName);
        internal string Path => System.IO.Path.Combine(DirectoryPath, "agents.sqlite");
        internal Task<CodexMacOsSqliteAgentStorage> Open(bool create = false, string account = "account")
            => CodexMacOsSqliteAgentStorage.OpenAsync(Path, account, create, Ct);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private static CodexThreadStore Store(CodexMacOsSqliteAgentStorage db) => new(db, "account");

    private static async Task<CodexNamedAgentSpawnRequest> Prepare(CodexThreadStore store)
    {
        var parent = await store.CreateAsync(Root, Json("""{"model":"parent"}"""), Ct);
        parent = await store.StartTurnAsync(Root, parent.Revision, ParentTurn, Owner,
            [Json("""{"role":"user","content":"delegate"}""")], RootTurn, Ct);
        var admitted = await store.AdmitAsync(Root, parent.Revision, ParentTurn, Owner, "spawn-call", "tool",
            Json("""{"task_name":"worker","message":"do work"}"""), Ct);
        var graph = await store.ReadAgentIdentitiesAsync(Root, Ct);
        return new(Root, Root, admitted.Thread.Revision, graph.Graph.Revision, ParentTurn, Owner, "spawn-call",
            Child, "worker", Json("""{"model":"child","sandbox":"read-only"}"""),
            new(Id(20), Root, CodexAgentMailMode.Followup, "do work"), 2);
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

    private sealed class Handler(List<JsonElement> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            requests.Add(Json(await request.Content!.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + Success + "\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static Task<CodexThreadAgentResult?> Wake(CodexThreadStore store, List<JsonElement> requests, string owner = ChildOwner)
        => store.RunAgentMailboxWakeAsync(new CodexThreadAgent(store, new Credentials(), new Budget(),
                clientFactory: (credentials, usage) => new CodexClient(credentials, handler: new Handler(requests), usageObserver: usage)),
            Root, Child, 1, 1, Claim, Turn, owner,
            new CodexResponseOptions("child", "instructions")
            {
                Identity = new("session", Child.ToString(), Turn.ToString())
                {
                    ParentThreadId = Root.ToString(), ParentTurnId = ParentTurn.ToString(), RootTurnId = RootTurn.ToString(),
                },
            }, (_, _) => throw new InvalidOperationException("No tools."), cancellationToken: Ct);

    private static async Task<(CodexAgentMailbox Box, JsonElement[] Input)> Reserve(CodexMacOsSqliteAgentStorage db,
        Guid claimId, Guid turnId, long mailboxRevision = 1)
    {
        var store = Store(db);
        var directory = await store.ReadAgentIdentitiesAsync(Root, Ct);
        var box = await store.ReadAgentMailboxAsync(Root, Child, Ct);
        Assert.Equal(mailboxRevision, box.Revision);
        var input = new[] { CodexAgentMailWakeAdmission.Project(directory.GetRecorded(Root), directory.GetActive(Child), Assert.Single(box.Pending)) };
        var reservation = new CodexAgentMailClaim(claimId, ChildOwner, turnId, 1, true)
        {
            WakeAdmission = new(directory.Graph.Revision, CodexAgentMailWakeAdmission.Fingerprint(input)),
        };
        var next = box with { Revision = box.Revision + 1, Claim = reservation };
        Assert.True(await db.CompareExchangeAgentMailboxAsync(box.Revision, next, CodexAgentMailboxOperation.Claim, Ct));
        return (next, input);
    }

    [Fact]
    public async Task NamedSpawnWakeAndTerminalResultSurviveRealDatabaseReopen()
    {
        RequireMac();
        using var fixture = new Fixture();
        CodexNamedAgentSpawnRequest request;
        await using (var db = await fixture.Open(create: true))
        {
            var store = Store(db);
            request = await Prepare(store);
            var child = await store.SpawnNamedAgentAsync(request, Ct);
            Assert.Equal(Child, child.ThreadId);
        }
        CodexThreadDocument finished;
        await using (var db = await fixture.Open())
        {
            var store = Store(db);
            var identity = await store.InspectNamedAgentSpawnAsync(request, Ct);
            Assert.NotNull(identity);
            Assert.Equal("/root/worker", identity.Path);
            var mailbox = await store.ReadAgentMailboxAsync(Root, Child, Ct);
            Assert.Equal(request.InitialMessage, Assert.Single(mailbox.Pending));
            Assert.Equal(1L, (await store.ReadAsync(Child, Ct)).Revision);
            var requests = new List<JsonElement>();
            // No grant means no accepted claim, no journal turn, and no provider request.
            await Assert.ThrowsAsync<CodexThreadConflictException>(() => Wake(store, requests));
            Assert.True(await db.AuthorizeWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            Assert.Equal(CodexAgentWakeGrantState.Granted, await db.ReadWakeAuthorizationAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            Assert.Null(await db.ReadWakeAuthorizationAsync(Root, Child, Claim, Turn, "wrong-owner", Ct));
            await Assert.ThrowsAsync<CodexThreadConflictException>(() => Wake(store, requests, "wrong-owner"));
            Assert.Empty(requests);
            var run = Assert.IsType<CodexThreadAgentResult>(await Wake(store, requests));
            finished = run.Thread;
            Assert.Equal("completed", Assert.Single(finished.Turns).Status);
            Assert.Single(requests);
            var settled = await store.ReadAgentMailboxAsync(Root, Child, Ct);
            Assert.Equal(request.InitialMessage.Id, Assert.Single(settled.Receipts).Id);
            Assert.Empty(settled.Pending);
            Assert.Null(settled.Claim);
            Assert.False(await db.FenceUnadmittedWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            var delivered = await store.DeliverNamedAgentCompletedResultAsync(Root, Child, Turn, finished.Revision, 0, Ct);
            Assert.Equal("done", Assert.Single(delivered.Pending).Content);
            Assert.False(delivered.HasWakeRequest);
        }
        await using (var db = await fixture.Open())
        {
            var store = Store(db);
            var result = await store.ReadAgentMailboxAsync(Root, Root, Ct);
            Assert.Equal(CodexAgentCompletedResult.DeliveryId(Child, Turn), Assert.Single(result.Pending).Id);
            Assert.Equal(CodexAgentWakeGrantState.Consumed, await db.ReadWakeAuthorizationAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            var retry = await store.DeliverNamedAgentCompletedResultAsync(Root, Child, Turn, finished.Revision, result.Revision, Ct);
            Assert.Equal(result.Revision, retry.Revision);
            Assert.Single(retry.Pending);
            Assert.Equal("completed", Assert.Single((await store.ReadAsync(Child, Ct)).Turns).Status);
        }
    }

    [Fact]
    public async Task IndependentConnectionsRaceForExactlyOneSpawnAndWakeGrant()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var first = await fixture.Open(create: true);
        await using var second = await fixture.Open();
        var left = Store(first);
        var right = Store(second);
        var request = await Prepare(left);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Spawn(CodexThreadStore store, CodexNamedAgentSpawnRequest candidate)
        {
            await start.Task;
            try { await store.SpawnNamedAgentAsync(candidate, Ct); return true; }
            catch (CodexThreadConflictException) { return false; }
        }
        var one = Spawn(left, request);
        var two = Spawn(right, request with { ChildThreadId = Id(3), TaskName = "other" });
        start.SetResult();
        var winners = await Task.WhenAll(one, two);
        Assert.Single(winners, won => won);
        var directory = await right.ReadAgentIdentitiesAsync(Root, Ct);
        var child = Assert.Single(directory.RestorableDescendants).ThreadId;
        var losingChild = child == Child ? Id(3) : Child;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => left.ReadAsync(losingChild, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => right.ReadAgentMailboxAsync(Root, losingChild, Ct));
        var grants = await Task.WhenAll(
            first.AuthorizeWakeAsync(Root, child, Id(31), Id(30), ChildOwner, Ct),
            second.AuthorizeWakeAsync(Root, child, Id(33), Id(32), "other-owner", Ct));
        Assert.Single(grants, won => won);
    }

    [Fact]
    public async Task ReopenAfterJournalAdmissionSettlesExistingClaimWithoutDispatch()
    {
        RequireMac();
        using var fixture = new Fixture();
        long revision;
        await using (var db = await fixture.Open(create: true))
        {
            var store = Store(db);
            await store.SpawnNamedAgentAsync(await Prepare(store), Ct);
            Assert.True(await db.AuthorizeWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            var (box, input) = await Reserve(db, Claim, Turn);
            revision = box.Revision;
            await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.StartTurnAsync(Child, 1, Turn,
                ChildOwner, [Json("""{"type":"message","role":"user","content":"forged"}""")], RootTurn, Ct,
                turnTrigger: CodexAgentMailWakeAdmission.TurnTrigger(Claim)));
            var admitted = await store.StartTurnAsync(Child, 1, Turn, ChildOwner, input, RootTurn, Ct,
                turnTrigger: CodexAgentMailWakeAdmission.TurnTrigger(Claim));
            Assert.Equal(Turn, Assert.Single(admitted.Turns).Id);
            Assert.Equal(Claim, (await store.ReadAgentMailboxAsync(Root, Child, Ct)).Claim?.Id);
        }
        await using (var db = await fixture.Open())
        {
            var store = Store(db);
            Assert.False(await db.FenceUnadmittedWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
            Assert.True(await store.ReconcileAgentMailboxWakeAsync(Root, Child, revision, Claim, ChildOwner, Ct));
            var settled = await store.ReadAgentMailboxAsync(Root, Child, Ct);
            Assert.Empty(settled.Pending);
            Assert.Single(settled.Receipts);
            Assert.Null(settled.Claim);
            Assert.Empty(Assert.Single((await store.ReadAsync(Child, Ct)).Turns).Dispatches);
            Assert.False(await db.AuthorizeWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
        }
    }

    [Fact]
    public async Task RevokedClaimCannotAdmitAndGraphCloseFencesNewMail()
    {
        RequireMac();
        using var fixture = new Fixture();
        await using var first = await fixture.Open(create: true);
        await using var second = await fixture.Open();
        var store = Store(first);
        await store.SpawnNamedAgentAsync(await Prepare(store), Ct);
        Assert.True(await first.AuthorizeWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
        var (box, input) = await Reserve(first, Claim, Turn);
        var other = Store(second);
        var graph = await other.ReadSpawnGraphAsync(Root, Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => other.SetSpawnEdgeStatusAsync(Root,
            graph.Revision, Child, CodexThreadSpawnEdgeStatus.Closed, Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => other.ReleaseAgentMailAsync(Root, Child,
            box.Revision, Claim, ChildOwner, Ct));
        Assert.True(await second.FenceUnadmittedWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
        Assert.Equal(CodexAgentWakeGrantState.Fenced, await first.ReadWakeAuthorizationAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.StartTurnAsync(Child, 1, Turn, ChildOwner,
            input, RootTurn, Ct, turnTrigger: CodexAgentMailWakeAdmission.TurnTrigger(Claim)));
        var released = await other.ReleaseAgentMailAsync(Root, Child, box.Revision, Claim, ChildOwner, Ct);
        Assert.Null(released.Claim);
        Assert.Single(released.Pending);
        Assert.False(await first.AuthorizeWakeAsync(Root, Child, Claim, Turn, ChildOwner, Ct));
        await other.SetSpawnEdgeStatusAsync(Root, graph.Revision, Child, CodexThreadSpawnEdgeStatus.Closed, Ct);
        Assert.Empty((await store.ReadAgentIdentitiesAsync(Root, Ct)).RestorableDescendants);
        Assert.False(await first.AuthorizeWakeAsync(Root, Child, Id(33), Id(32), ChildOwner, Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.EnqueueAgentMailAsync(Root, Child,
            released.Revision, new(Id(21), Root, CodexAgentMailMode.Followup, "not allowed"), Ct));
        Assert.Empty((await other.ReadAsync(Child, Ct)).Turns);
    }

    [Fact]
    public async Task GraphOnlyReservationSurvivesReopenAndCrossTreeReparentIsRefused()
    {
        RequireMac();
        using var fixture = new Fixture();
        var otherRoot = Id(40);
        await using (var db = await fixture.Open(create: true))
        {
            var store = Store(db);
            await store.CreateAsync(Root, Json("{}"), Ct);
            await store.CreateAsync(otherRoot, Json("{}"), Ct);
            var graph = await store.ReadSpawnGraphAsync(Root, Ct);
            await store.UpsertSpawnEdgeAsync(Root, graph.Revision, new(Root, Child, CodexThreadSpawnEdgeStatus.Open), Ct);
        }
        await using (var db = await fixture.Open())
        {
            var store = Store(db);
            Assert.Equal(Child, Assert.Single(await store.ListSpawnChildrenAsync(Root, cancellationToken: Ct)));
            Assert.Empty((await store.ReadAgentIdentitiesAsync(Root, Ct)).RestorableDescendants);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAsync(Child, Ct));
            var graph = await store.ReadSpawnGraphAsync(otherRoot, Ct);
            await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.UpsertSpawnEdgeAsync(otherRoot,
                graph.Revision, new(otherRoot, Child, CodexThreadSpawnEdgeStatus.Open), Ct));
            await store.CreateAsync(Child, Json("{}"), Ct);
            Assert.Equal(0L, (await store.ReadAgentMailboxAsync(Root, Child, Ct)).Revision);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAgentMailboxAsync(otherRoot, Child, Ct));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open(account: "foreign"));
        await Assert.ThrowsAsync<IOException>(() => fixture.Open(create: true));
    }
}
