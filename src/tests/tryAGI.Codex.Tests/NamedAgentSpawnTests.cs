using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class NamedAgentSpawnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static Guid Root => Id(1);
    private static Guid ParentTurn => Id(10);
    private static Guid RootTurn => Id(11);
    private const string Owner = "parent-owner";
    private const string ChildOwner = "child-owner";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private const string Success = """{"type":"response.completed","response":{"id":"child-response","model":"model","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}}""";

    // Models the embedding host's required transaction using the existing thread journal.
    // A shared lock covers the graph, metadata, mailbox and document CAS together.
    private sealed class Storage : ICodexNamedAgentSpawnStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> documents = [];
        private readonly Dictionary<Guid, CodexThreadSpawnEdge> edges = [];
        private readonly Dictionary<Guid, CodexNamedAgentIdentity> identities = [];
        private readonly Dictionary<Guid, CodexAgentMailbox> mailboxes = [];
        private long graphRevision;
        internal bool LoseNextSpawnReply { get; set; }
        internal bool FailNextSpawn { get; set; }
        internal Action? BeforeSpawn { get; set; }
        internal int SpawnCommits { get; private set; }
        internal int WakeAdmissions { get; private set; }
        private string Revision => graphRevision.ToString(CultureInfo.InvariantCulture);
        private CodexThreadDocument Decode(JsonElement body) => new CodexThreadStore(this, "account").ValidateSnapshot(body);
        private CodexThreadSpawnGraph Graph => new("account", Root, Revision, edges.Values.ToArray());
        private static void CheckTree(string account, Guid root)
        {
            if (account != "account" || root != Root) throw new UnauthorizedAccessException();
        }

        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(documents.TryGetValue(threadId, out var value) ? (JsonElement?)value : null);
        }

        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var old = documents.TryGetValue(threadId, out var existing) ? Decode(existing) : null;
                if (old?.Revision != expectedRevision) return Task.FromResult(false);
                var next = Decode(document);
                if (next.Id != threadId || next.AccountId != "account") return Task.FromResult(false);
                var last = next.Turns.LastOrDefault();
                if (last is not null && next.Turns.Length > (old?.Turns.Length ?? 0) &&
                    last.TurnTrigger?.StartsWith("native:agent-mail:", StringComparison.Ordinal) == true)
                {
                    if (!edges.TryGetValue(threadId, out var edge) || edge.Status != CodexThreadSpawnEdgeStatus.Open ||
                        !mailboxes.TryGetValue(threadId, out var box) ||
                        box.Claim is not { WakeAdmission: { } binding } claim ||
                        binding.GraphRevision != Revision || !binding.Matches(claim, last))
                        return Task.FromResult(false);
                    if (identities[threadId].SpawnOrigin is { } origin &&
                        box.Pending.Take(claim.MessageCount).Any(mail => mail.Id == origin.InitialMessageId) &&
                        last.RootTurnId != origin.RootTurnId) return Task.FromResult(false);
                    WakeAdmissions++;
                }
                documents[threadId] = document.Clone();
                return Task.FromResult(true);
            }
        }

        public Task<CodexThreadSpawnGraph> ReadSpawnGraphAsync(string accountId, Guid rootThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckTree(accountId, rootThreadId);
            lock (gate) return Task.FromResult(Graph);
        }

        public Task<bool> CompareExchangeSpawnEdgeAsync(string accountId, Guid rootThreadId, string expectedGraphRevision,
            CodexThreadSpawnEdge edge, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Spawn must use the atomic creation transaction.");

        public Task<CodexAgentIdentitySnapshot> ReadAgentIdentitySnapshotAsync(string accountId, Guid rootThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckTree(accountId, rootThreadId);
            lock (gate) return Task.FromResult(new CodexAgentIdentitySnapshot(Graph, identities.Values.ToArray()));
        }

        public Task<CodexAgentMailbox> ReadAgentMailboxAsync(string accountId, Guid rootThreadId, Guid recipientThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckTree(accountId, rootThreadId);
            lock (gate) return Task.FromResult(mailboxes[recipientThreadId]);
        }

        public Task<bool> CompareExchangeAgentMailboxAsync(long expectedRevision, CodexAgentMailbox next,
            CodexAgentMailboxOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckTree(next.AccountId, next.RootThreadId);
            lock (gate)
            {
                if (!mailboxes.TryGetValue(next.RecipientThreadId, out var previous) ||
                    previous.Revision != expectedRevision || next.Revision != expectedRevision + 1 ||
                    edges[next.RecipientThreadId].Status != CodexThreadSpawnEdgeStatus.Open)
                    return Task.FromResult(false);
                var thread = Decode(documents[next.RecipientThreadId]);
                if (operation == CodexAgentMailboxOperation.Claim &&
                    (next.Claim is not { WakeAdmission: { } binding } claim || binding.GraphRevision != Revision ||
                     claim.OwnerId != ChildOwner || thread.Turns.Any(turn => turn.Id == claim.TurnId || turn.Status == "inProgress")))
                    return Task.FromResult(false);
                if (operation == CodexAgentMailboxOperation.Acknowledge &&
                    (previous.Claim is not { WakeAdmission: { } admission } reserved ||
                     thread.Turns.SingleOrDefault(turn => turn.Id == reserved.TurnId) is not { } turn ||
                     !admission.Matches(reserved, turn)))
                    return Task.FromResult(false);
                if (operation is not (CodexAgentMailboxOperation.Claim or CodexAgentMailboxOperation.Acknowledge))
                    throw new InvalidOperationException("Unexpected mailbox mutation.");
                mailboxes[next.RecipientThreadId] = next;
                return Task.FromResult(true);
            }
        }

        public Task<bool> CompareCreateNamedAgentAsync(CodexNamedAgentSpawnPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckTree(plan.AccountId, plan.Identity.RootThreadId);
            lock (gate)
            {
                BeforeSpawn?.Invoke();
                if (FailNextSpawn)
                {
                    FailNextSpawn = false;
                    return Task.FromResult(false);
                }
                var identity = plan.Identity;
                if (identity.SpawnOrigin is not { } origin || origin.GraphRevision != Revision ||
                    identity.ParentThreadId is not { } parentId || !documents.TryGetValue(parentId, out var parentBody) ||
                    documents.ContainsKey(identity.ThreadId) || edges.ContainsKey(identity.ThreadId) ||
                    identities.Values.Any(item => item.ParentThreadId == parentId &&
                        item.SpawnOrigin?.ParentTurnId == origin.ParentTurnId &&
                        item.SpawnOrigin.SourceDispatchId == origin.SourceDispatchId) ||
                    identities.Values.Any(item => item.Path == identity.Path &&
                        edges[item.ThreadId].Status == CodexThreadSpawnEdgeStatus.Open))
                    return Task.FromResult(false);
                var parent = Decode(parentBody);
                if (parent.Revision != origin.ParentRevision || parent.ArchivedAt is not null ||
                    parent.Turns.LastOrDefault() is not { Status: "inProgress" } turn ||
                    turn.Id != origin.ParentTurnId || turn.OwnerId != origin.ParentOwnerId || turn.RootTurnId != origin.RootTurnId ||
                    !turn.Dispatches.Any(dispatch => dispatch.CallId == origin.SourceDispatchId &&
                        dispatch.Kind == "tool" && dispatch.State == "dispatching" && dispatch.RequestSha256 == origin.SourceDispatchSha256))
                    return Task.FromResult(false);
                if (parentId != Root && (!edges.TryGetValue(parentId, out var incoming) ||
                    incoming.Status != CodexThreadSpawnEdgeStatus.Open)) return Task.FromResult(false);
                var parentPath = parentId == Root ? CodexAgentPath.Root : identities[parentId].Path;
                var child = Decode(plan.Document);
                if (identity.Path != CodexAgentPath.Join(parentPath, CodexAgentPath.Name(identity.Path)) ||
                    identity.Path.Count(c => c == '/') - 1 > plan.MaximumDepth || child.Id != identity.ThreadId ||
                    child.AccountId != plan.AccountId || child.Revision != 1 || child.Turns.Length != 0 ||
                    child.ForkedFromId is not null || child.Goal is not null || child.RealtimeSessions is not null ||
                    child.QueuedSubmissions is not null ||
                    CodexRetainedJournalMetadata.ItemHash(child.Settings) != origin.InitialSettingsSha256 ||
                    plan.Mailbox.AccountId != plan.AccountId || plan.Mailbox.RootThreadId != Root ||
                    plan.Mailbox.RecipientThreadId != identity.ThreadId || plan.Mailbox.Revision != 1 ||
                    plan.Mailbox.Claim is not null || plan.Mailbox.Receipts.Count != 0 ||
                    plan.Mailbox.Pending.Count != 1 || plan.Mailbox.Pending[0].Id != origin.InitialMessageId ||
                    plan.Mailbox.Pending[0].SenderThreadId != parentId ||
                    plan.Mailbox.Pending[0].Mode != CodexAgentMailMode.Followup)
                    return Task.FromResult(false);

                documents.Add(child.Id, plan.Document.Clone());
                edges.Add(child.Id, new(parentId, child.Id, CodexThreadSpawnEdgeStatus.Open));
                identities.Add(child.Id, identity);
                mailboxes.Add(child.Id, plan.Mailbox);
                graphRevision++;
                SpawnCommits++;
                if (LoseNextSpawnReply)
                {
                    LoseNextSpawnReply = false;
                    throw new IOException("Spawn acknowledgement lost after commit.");
                }
                return Task.FromResult(true);
            }
        }

        internal void Close(Guid child)
        {
            lock (gate)
            {
                edges[child] = edges[child] with { Status = CodexThreadSpawnEdgeStatus.Closed };
                graphRevision++;
            }
        }

        internal void AdvanceGraph()
        {
            lock (gate) graphRevision++;
        }
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

    private sealed class Fixture
    {
        internal Storage Database { get; } = new();
        internal CodexThreadStore Store { get; }
        internal Fixture() => Store = new CodexThreadStore(Database, "account");

        internal async Task<CodexNamedAgentSpawnRequest> PrepareAsync()
        {
            var parent = await Store.CreateAsync(Root, Json("""{"model":"parent","threadSource":"user"}"""), Ct);
            parent = await Store.StartTurnAsync(Root, parent.Revision, ParentTurn, Owner,
                [Json("""{"role":"user","content":"delegate"}""")], RootTurn, Ct);
            var admitted = await Store.AdmitAsync(Root, parent.Revision, ParentTurn, Owner, "spawn-call", "tool",
                Json("""{"task_name":"worker","message":"do work"}"""), Ct);
            var graph = await Store.ReadAgentIdentitiesAsync(Root, Ct);
            return new(Root, Root, admitted.Thread.Revision, graph.Graph.Revision, ParentTurn, Owner, "spawn-call",
                Id(2), "worker", Json("""{"model":"child","sandbox":"read-only","source":"appServer","threadSource":"user"}"""),
                new(Id(20), Root, CodexAgentMailMode.Followup, "do work"), 2, "worker", "Ada");
        }

        internal CodexThreadAgent Agent(List<JsonElement> requests)
            => new(Store, new Credentials(), new Budget(), clientFactory: (credentials, usage) =>
                new CodexClient(credentials, handler: new Handler(requests), usageObserver: usage));
    }

    [Fact]
    public async Task FreshSpawnCommitsNamedGraphJournalAndInitialMailThenRunsExistingAgent()
    {
        var f = new Fixture();
        var request = await f.PrepareAsync();
        Assert.True(f.Store.SupportsNamedAgentSpawn);
        var child = await f.Store.SpawnNamedAgentAsync(request, Ct);
        Assert.Equal(request.ChildThreadId, child.ThreadId);
        Assert.Equal("/root/worker", child.Path);
        Assert.Equal("worker", child.Role);
        Assert.Equal("Ada", child.Nickname);
        Assert.Equal(ParentTurn, child.SpawnOrigin?.ParentTurnId);
        Assert.Equal(RootTurn, child.SpawnOrigin?.RootTurnId);
        Assert.Equal(child, (await f.Store.ReadAgentIdentitiesAsync(Root, Ct)).Resolve(Root, "worker"));
        var document = await f.Store.ReadAsync(child.ThreadId, Ct);
        Assert.Equal(1L, document.Revision);
        Assert.Empty(document.Turns);
        Assert.Null(document.ForkedFromId);
        Assert.Equal("subagent", document.Settings.GetProperty("threadSource").GetString());
        Assert.Equal("child", document.Settings.GetProperty("model").GetString());
        Assert.False(CodexThreadStore.ReadStateEligible(document));
        var mailbox = await f.Store.ReadAgentMailboxAsync(Root, child.ThreadId, Ct);
        Assert.True(mailbox.HasWakeRequest);
        Assert.Equal(request.InitialMessage, Assert.Single(mailbox.Pending));
        Assert.Equal(request.ExpectedParentRevision, (await f.Store.ReadAsync(Root, Ct)).Revision);

        var requests = new List<JsonElement>();
        var agent = f.Agent(requests);
        var turnId = Id(30);
        var claimId = Id(31);
        var options = new CodexResponseOptions("child", "instructions")
        {
            Identity = new("session", child.ThreadId.ToString(), turnId.ToString())
            {
                ParentThreadId = Root.ToString(), ParentTurnId = ParentTurn.ToString(), RootTurnId = RootTurn.ToString(),
            },
        };
        Task<CodexThreadAgentResult?> Wake(CodexResponseOptions effective)
            => f.Store.RunAgentMailboxWakeAsync(agent, Root, child.ThreadId, mailbox.Revision, document.Revision,
                claimId, turnId, ChildOwner, effective, (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => Wake(options with
        {
            Identity = options.Identity! with { ParentTurnId = Id(999).ToString() },
        }));
        Assert.Null((await f.Store.ReadAgentMailboxAsync(Root, child.ThreadId, Ct)).Claim);
        var result = Assert.IsType<CodexThreadAgentResult>(await Wake(options));
        var turn = Assert.Single(result.Thread.Turns);
        Assert.Equal(turnId, turn.Id);
        Assert.Equal(RootTurn, turn.RootTurnId);
        Assert.Equal("completed", turn.Status);
        Assert.Equal("Message Type: NEW_TASK\nTask name: /root/worker\nSender: /root\nPayload:\ndo work",
            Assert.Single(turn.Input).GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Single(requests);
        Assert.Equal(1, f.Database.WakeAdmissions);
        Assert.Equal(1, f.Database.SpawnCommits);
        var settled = await f.Store.ReadAgentMailboxAsync(Root, child.ThreadId, Ct);
        Assert.Empty(settled.Pending);
        Assert.Equal(request.InitialMessage.Id, Assert.Single(settled.Receipts).Id);
        Assert.Equal(child, await f.Store.InspectNamedAgentSpawnAsync(request, Ct));
    }

    [Fact]
    public async Task LostCommitReplyIsInspectedByOriginalIdAndCannotSpawnAgain()
    {
        var f = new Fixture();
        var request = await f.PrepareAsync();
        f.Database.LoseNextSpawnReply = true;
        await Assert.ThrowsAsync<IOException>(() => f.Store.SpawnNamedAgentAsync(request, Ct));
        var reopened = new CodexThreadStore(f.Database, "account");
        var accepted = Assert.IsType<CodexNamedAgentIdentity>(await reopened.InspectNamedAgentSpawnAsync(request, Ct));
        Assert.Equal(request.ChildThreadId, accepted.ThreadId);
        Assert.Equal(1, f.Database.SpawnCommits);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => reopened.SpawnNamedAgentAsync(request, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.InspectNamedAgentSpawnAsync(request with
        {
            InitialMessage = request.InitialMessage with { Content = "changed" },
        }, Ct));
        f.Database.Close(accepted.ThreadId);
        Assert.Equal(accepted, await reopened.InspectNamedAgentSpawnAsync(request, Ct));
        var freshGraph = await reopened.ReadAgentIdentitiesAsync(Root, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SpawnNamedAgentAsync(request with
        {
            ExpectedGraphRevision = freshGraph.Graph.Revision, ChildThreadId = Id(3), TaskName = "another",
        }, Ct));
        Assert.Equal(1, f.Database.SpawnCommits);
    }

    [Fact]
    public async Task GraphConflictAndRejectedTransactionPublishNothing()
    {
        var f = new Fixture();
        var request = await f.PrepareAsync();
        f.Database.FailNextSpawn = true;
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.Store.SpawnNamedAgentAsync(request, Ct));
        Assert.Null(await f.Store.InspectNamedAgentSpawnAsync(request, Ct));
        Assert.Empty((await f.Store.ReadAgentIdentitiesAsync(Root, Ct)).Graph.Edges);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.ReadAsync(request.ChildThreadId, Ct));
        f.Database.BeforeSpawn = f.Database.AdvanceGraph;
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.Store.SpawnNamedAgentAsync(request, Ct));
        Assert.Null(await f.Store.InspectNamedAgentSpawnAsync(request, Ct));
        Assert.Equal(0, f.Database.SpawnCommits);
    }

    [Fact]
    public async Task AuthorityIdentityAndInitialInputAreValidatedBeforePublication()
    {
        var f = new Fixture();
        var request = await f.PrepareAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SpawnNamedAgentAsync(request with
        {
            ParentOwnerId = "foreign-owner",
        }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SpawnNamedAgentAsync(request with
        {
            SourceDispatchId = "not-admitted",
        }, Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.Store.SpawnNamedAgentAsync(request with
        {
            ExpectedParentRevision = request.ExpectedParentRevision - 1,
        }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.SpawnNamedAgentAsync(request with
        {
            EffectiveSettings = Json("""{"ephemeral":true}"""),
        }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.SpawnNamedAgentAsync(request with
        {
            InitialMessage = request.InitialMessage with { Mode = CodexAgentMailMode.QueueOnly },
        }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.SpawnNamedAgentAsync(request with
        {
            TaskName = "../foreign",
        }, Ct));
        Assert.Empty((await f.Store.ReadAgentIdentitiesAsync(Root, Ct)).Graph.Edges);
        Assert.Equal(0, f.Database.SpawnCommits);
    }

    [Fact]
    public async Task ConcurrentSpawnRequestsForSameDispatchCannotPublishTwoChildren()
    {
        var f = new Fixture();
        var request = await f.PrepareAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Attempt(CodexNamedAgentSpawnRequest candidate)
        {
            await start.Task;
            try { await f.Store.SpawnNamedAgentAsync(candidate, Ct); return true; }
            catch (CodexThreadConflictException) { return false; }
        }
        var first = Attempt(request);
        var second = Attempt(request with { ChildThreadId = Id(3), TaskName = "other" });
        start.SetResult();
        Assert.Single(await Task.WhenAll(first, second), accepted => accepted);
        Assert.Equal(1, f.Database.SpawnCommits);
        Assert.Single((await f.Store.ReadAgentIdentitiesAsync(Root, Ct)).RestorableDescendants);
    }
}
