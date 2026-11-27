using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class AgentMailboxExecutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static Guid Root => Id(1);
    private static Guid Child => Id(2);
    private static Guid Grandchild => Id(3);
    private static Guid Claim => Id(20);
    private static Guid Turn => Id(30);
    private const string Owner = "runner";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private const string Success = """{"type":"response.completed","response":{"id":"reasoning-response","model":"model","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}}""";

    // A single lock models the host's shared graph/mailbox/journal transaction boundary.
    // No provider, process, or filesystem effects are required by this adapter.
    private sealed class Storage : ICodexAgentMailboxExecutionStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> documents = [];
        internal CodexAgentMailbox Mailbox { get; private set; } = new("account", Root, Child, 0, [], []);
        internal string GraphRevision { get; private set; } = "graph-1";
        internal bool LoseClaimReply { get; set; }
        internal bool LoseTurnReply { get; set; }
        internal bool FailAck { get; set; }
        internal bool LoseAckReply { get; set; }
        internal bool ChangeGraphAfterClaim { get; set; }
        internal Guid? FencedClaim { get; set; }
        internal int Admitted { get; private set; }
        internal bool LoseRootUsageReply { get; set; }
        internal bool FailRootUsageCommit { get; set; }
        internal int RootUsageCommits { get; private set; }
        internal Func<CancellationToken, Task>? BeforeResponse { get; set; }
        internal string Terminal { get; set; } = Success;

        private CodexThreadSpawnGraph Graph => new("account", Root, GraphRevision,
            [new(Root, Child, CodexThreadSpawnEdgeStatus.Open),
             new(Child, Grandchild, CodexThreadSpawnEdgeStatus.Open)]);
        private static CodexNamedAgentIdentity[] Identities =>
            [new(Child, Root, Root, "/root/worker"), new(Grandchild, Child, Root, "/root/worker/helper")];

        private CodexThreadDocument Decode(JsonElement body) => new CodexThreadStore(this, "account").ValidateSnapshot(body);

        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(documents.TryGetValue(threadId, out var value) ? (JsonElement?)value : null);
        }

        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var previous = documents.TryGetValue(threadId, out var existing) ? Decode(existing) : null;
                if (previous?.Revision != expectedRevision) return Task.FromResult(false);
                var proposed = Decode(document);
                var turn = proposed.Turns.LastOrDefault();
                var newTurn = proposed.Turns.Length > (previous?.Turns.Length ?? 0);
                // A new fork copies historical turns; only a fresh execution admission consumes mail.
                if (newTurn && (previous is not null || proposed.ForkedFromId is null) &&
                    turn?.TurnTrigger?.StartsWith("native:agent-mail:", StringComparison.Ordinal) == true)
                {
                    if (threadId != Child || Mailbox.Claim is not { WakeAdmission: { } binding } claim ||
                        binding.GraphRevision != GraphRevision || claim.Id == FencedClaim || !binding.Matches(claim, turn))
                        return Task.FromResult(false);
                    Admitted++;
                }
                var rootUsage = threadId == Root && (proposed.Goal?.UsageReceipts?.Length ?? 0) >
                    (previous?.Goal?.UsageReceipts?.Length ?? 0) &&
                    proposed.Goal!.UsageReceipts!.Any(receipt => receipt.Mode == CodexGoalAccountingMode.AttributedDescendant);
                if (rootUsage && FailRootUsageCommit)
                {
                    FailRootUsageCommit = false;
                    throw new IOException("Root usage commit failed before write.");
                }
                documents[threadId] = document.Clone();
                if (rootUsage)
                {
                    RootUsageCommits++;
                    if (LoseRootUsageReply)
                    {
                        LoseRootUsageReply = false;
                        throw new IOException("Root usage acknowledgement lost.");
                    }
                }
                if (newTurn && LoseTurnReply)
                {
                    LoseTurnReply = false;
                    throw new IOException("Turn admission acknowledgement lost.");
                }
                return Task.FromResult(true);
            }
        }

        public Task<CodexThreadSpawnGraph> ReadSpawnGraphAsync(string accountId, Guid rootThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(Graph);
        }

        public Task<bool> CompareExchangeSpawnEdgeAsync(string accountId, Guid rootThreadId, string expectedGraphRevision,
            CodexThreadSpawnEdge edge, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected graph write.");

        public Task<CodexAgentIdentitySnapshot> ReadAgentIdentitySnapshotAsync(string accountId, Guid rootThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (accountId != "account" || rootThreadId != Root) throw new UnauthorizedAccessException();
            lock (gate) return Task.FromResult(new CodexAgentIdentitySnapshot(Graph, Identities));
        }

        public Task<CodexAgentMailbox> ReadAgentMailboxAsync(string accountId, Guid rootThreadId, Guid recipientThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (accountId != "account" || rootThreadId != Root || recipientThreadId != Child)
                throw new UnauthorizedAccessException();
            lock (gate) return Task.FromResult(Mailbox);
        }

        public Task<bool> CompareExchangeAgentMailboxAsync(long expectedRevision, CodexAgentMailbox next,
            CodexAgentMailboxOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (Mailbox.Revision != expectedRevision || next.Revision != expectedRevision + 1) return Task.FromResult(false);
                var child = Decode(documents[Child]);
                if (operation == CodexAgentMailboxOperation.Enqueue)
                {
                    var mail = next.Pending[^1];
                    if (mail.SenderThreadId != Root && mail.SenderThreadId != Grandchild ||
                        mail.Mode == CodexAgentMailMode.Result && mail.SenderThreadId != Grandchild)
                        return Task.FromResult(false);
                }
                if (operation == CodexAgentMailboxOperation.Claim)
                {
                    if (next.Claim is not { WakeAdmission: { } binding } claim || binding.GraphRevision != GraphRevision ||
                        claim.OwnerId != Owner || child.Turns.Any(turn => turn.Id == claim.TurnId || turn.Status == "inProgress"))
                        return Task.FromResult(false);
                    var identities = Identities.Prepend(new CodexNamedAgentIdentity(Root, null, Root, "/root")).ToDictionary(i => i.ThreadId);
                    var projected = next.Pending.Take(claim.MessageCount).Select(mail =>
                        CodexAgentMailWakeAdmission.Project(identities[mail.SenderThreadId], identities[Child], mail));
                    if (binding.InputFingerprint != CodexAgentMailWakeAdmission.Fingerprint(projected)) return Task.FromResult(false);
                }
                if (operation == CodexAgentMailboxOperation.Acknowledge)
                {
                    if (Mailbox.Claim is not { WakeAdmission: { } binding } claim ||
                        child.Turns.SingleOrDefault(turn => turn.Id == claim.TurnId) is not { } turn ||
                        !binding.Matches(claim, turn)) return Task.FromResult(false);
                    if (FailAck) throw new IOException("Acknowledgement failed before commit.");
                }
                if (operation == CodexAgentMailboxOperation.Release &&
                    (Mailbox.Claim is not { } outstanding || outstanding.Id != FencedClaim ||
                     child.Turns.Any(turn => turn.Id == outstanding.TurnId)))
                    return Task.FromResult(false);
                Mailbox = next;
                if (operation == CodexAgentMailboxOperation.Claim)
                {
                    if (ChangeGraphAfterClaim) GraphRevision = "graph-2";
                    if (LoseClaimReply) throw new IOException("Claim acknowledgement lost.");
                }
                if (operation == CodexAgentMailboxOperation.Acknowledge && LoseAckReply)
                    throw new IOException("Acknowledgement lost after commit.");
                return Task.FromResult(true);
            }
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

    private sealed class Handler(Storage storage, List<JsonElement> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(storage.Mailbox.Claim);
            Assert.NotEmpty(storage.Mailbox.Receipts);
            requests.Add(Json(await request.Content!.ReadAsStringAsync(ct)));
            if (storage.BeforeResponse is { } before) await before(ct);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + storage.Terminal + "\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class Fixture
    {
        internal Storage Storage { get; } = new();
        internal CodexThreadStore Store { get; }
        internal List<JsonElement> Requests { get; } = [];
        internal Fixture() => Store = new CodexThreadStore(Storage, "account");

        internal Task<CodexThreadDocument> CreateAsync() => Store.CreateAsync(Child, Json("{}"), Ct);

        internal CodexThreadRpc GoalHost()
        {
            var host = new CodexThreadRpc(Store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
                Json("{}"), (_, _, _) => throw new InvalidOperationException("No automatic goal execution."),
                (_, _) => Task.CompletedTask);
            host.EnableThreadGoals((_, _) => Task.FromResult(true));
            return host;
        }

        internal async Task<CodexThreadDocument> CreateRootGoalAsync(long budget = 10)
        {
            await Store.CreateAsync(Root, Json("{}"), Ct);
            var host = GoalHost();
            await host.InvokeAsync("thread/goal/set", Json($$"""{"threadId":"{{Root}}","objective":"root task","tokenBudget":{{budget}}}"""), Ct);
            Storage.Terminal = Success.Replace("\"output\":[", "\"usage\":{\"input_tokens\":100,\"input_tokens_details\":{\"cached_tokens\":95},\"output_tokens\":6,\"total_tokens\":106},\"output\":[", StringComparison.Ordinal);
            return await Store.ReadAsync(Root, Ct);
        }

        internal async Task SendAsync(int id, string content, CodexAgentMailMode mode = CodexAgentMailMode.QueueOnly,
            bool encrypted = false, Guid? sender = null)
        {
            var before = await Store.ReadAgentMailboxAsync(Root, Child, Ct);
            await Store.EnqueueAgentMailAsync(Root, Child, before.Revision,
                new(Id(id), sender ?? Root, mode, content, encrypted), Ct);
        }

        internal Task<CodexThreadAgentResult?> WakeAsync(CodexResponseOptions? options = null)
        {
            var agent = new CodexThreadAgent(Store, new Credentials(), new Budget(),
                clientFactory: (credentials, usage) => new CodexClient(credentials,
                    handler: new Handler(Storage, Requests), usageObserver: usage));
            options ??= new("model", "instructions")
            {
                Identity = new("session", Child.ToString(), Turn.ToString()) { ParentThreadId = Root.ToString() },
            };
            return Store.RunAgentMailboxWakeAsync(agent, Root, Child, Storage.Mailbox.Revision, 1, Claim, Turn, Owner,
                options, (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        }

        internal Task<bool> ReconcileAsync(CodexThreadStore? reopened = null)
            => (reopened ?? Store).ReconcileAgentMailboxWakeAsync(Root, Child, Storage.Mailbox.Revision, Claim, Owner, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedDescendantUsageReconcilesExactRootReceiptAfterRestart(bool committed)
    {
        var f = new Fixture();
        var child = await f.CreateAsync();
        var root = await f.CreateRootGoalAsync();
        var directory = await f.Store.ReadAgentIdentitiesAsync(Root, Ct);
        var attribution = await f.Store.CaptureDescendantGoalAttributionAsync(directory, directory.GetActive(Child), Ct);
        Assert.NotNull(attribution);
        var terminal = Json("""{"id":"compact-response","usage":{"input_tokens":15,"input_tokens_details":{"cached_tokens":10},"output_tokens":6}}""");
        var accounting = new CodexStoredInferenceAccounting(Child, Guid.NewGuid(), "account", new string('a', 64),
            "model", CodexRetainedJournalMetadata.ItemHash(terminal)) { RequestKind = "compaction" };
        var receipt = new CodexStoredDetachedCompactionReceipt("compact", new string('b', 64), "compact-response", child.Revision, accounting, terminal)
        {
            GoalInference = new(Child, root.Goal!.Id, Guid.NewGuid()) { GoalThreadId = Root },
            DescendantGoal = attribution
        };
        child = await f.Store.RecordDetachedCompactionAccountingAsync(Child, receipt, Ct);
        f.Storage.LoseRootUsageReply = committed;
        f.Storage.FailRootUsageCommit = !committed;
        await Assert.ThrowsAsync<IOException>(() => f.Store.AccountDetachedDescendantGoalAsync(child, receipt.RequestId, Ct));
        var cold = new CodexThreadStore(f.Storage, "account");
        Assert.Equal(committed ? 11 : 0, (await cold.ReadAsync(Root, Ct)).Goal!.TokensUsed);
        Assert.Equal(committed ? CodexDescendantGoalUsageOutcome.AlreadyApplied : CodexDescendantGoalUsageOutcome.Applied,
            await cold.ReconcileDetachedDescendantGoalUsageAsync(Child, child.Revision, receipt.RequestId, Ct));
        Assert.Equal(CodexDescendantGoalUsageOutcome.AlreadyApplied,
            await cold.ReconcileDetachedDescendantGoalUsageAsync(Child, child.Revision, receipt.RequestId, Ct));
        var charged = await cold.ReadAsync(Root, Ct);
        Assert.Equal(11, charged.Goal!.TokensUsed);
        Assert.Equal("budgetLimited", charged.Goal.Status);
        Assert.Equal(receipt.GoalInference!.UsageId, Assert.Single(charged.Goal.UsageReceipts!).Id);
        var fork = await cold.ForkAsync(Child, child.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Equal(CodexDescendantGoalUsageOutcome.NoEvidence,
            await cold.ReconcileDetachedDescendantGoalUsageAsync(fork.Id, fork.Revision, receipt.RequestId, Ct));
        Assert.Equal(1, f.Storage.RootUsageCommits);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task DescendantUsageChargesOriginalRootGoalExactlyOnceThroughMailboxRunner()
    {
        var f = new Fixture();
        await f.CreateAsync();
        var root = await f.CreateRootGoalAsync();
        await f.SendAsync(10, "follow-up", CodexAgentMailMode.Followup);
        var result = Assert.IsType<CodexThreadAgentResult>(await f.WakeAsync());
        var child = result.Thread;
        var turn = Assert.Single(child.Turns);
        Assert.Equal(root.Goal!.Id, turn.DescendantGoal!.GoalId);
        Assert.Equal(root.Revision, turn.DescendantGoal.RootRevision);
        Assert.Equal(Root, turn.DescendantGoal.RootThreadId);
        Assert.Equal(Child, turn.DescendantGoal.OriginThreadId);
        var inference = Assert.Single(turn.Dispatches, item => item.Kind == "inference");
        Assert.Equal(Root, inference.GoalInference!.GoalThreadId);
        var charged = await f.Store.ReadAsync(Root, Ct);
        Assert.Equal(11, charged.Goal!.TokensUsed);
        Assert.Equal("budgetLimited", charged.Goal.Status);
        Assert.Equal(inference.GoalInference.UsageId, Assert.Single(charged.Goal.UsageReceipts!).Id);
        Assert.Null(child.Goal);
        Assert.Equal(1, f.Storage.RootUsageCommits);
        Assert.Equal(CodexDescendantGoalUsageOutcome.AlreadyApplied, await f.Store.ReconcileDescendantGoalUsageAsync(
            Child, child.Revision, turn.Id, inference.CallId, Ct));
        Assert.Equal(1, f.Storage.RootUsageCommits);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task DescendantLateResponseNeverChargesReplacementRootGeneration()
    {
        var f = new Fixture();
        await f.CreateAsync();
        var root = await f.CreateRootGoalAsync();
        f.Storage.BeforeResponse = async ct =>
        {
            var host = f.GoalHost();
            await host.InvokeAsync("thread/goal/clear", Json($$"""{"threadId":"{{Root}}"}"""), ct);
            await host.InvokeAsync("thread/goal/set", Json($$"""{"threadId":"{{Root}}","objective":"replacement"}"""), ct);
        };
        await f.SendAsync(10, "follow-up", CodexAgentMailMode.Followup);
        var child = Assert.IsType<CodexThreadAgentResult>(await f.WakeAsync()).Thread;
        var turn = Assert.Single(child.Turns);
        Assert.Equal(root.Goal!.Id, turn.DescendantGoal!.GoalId);
        var current = await f.Store.ReadAsync(Root, Ct);
        Assert.NotEqual(root.Goal.Id, current.Goal!.Id);
        Assert.Equal("replacement", current.Goal.Objective);
        Assert.Equal(0, current.Goal.TokensUsed);
        var receipt = Assert.Single(turn.Dispatches, item => item.Kind == "inference");
        Assert.Equal(CodexDescendantGoalUsageOutcome.StaleGoal, await f.Store.ReconcileDescendantGoalUsageAsync(
            Child, child.Revision, turn.Id, receipt.CallId, Ct));
        Assert.Equal(0, f.Storage.RootUsageCommits);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DescendantRootWriteFailureRecoversSameReceiptWithoutAnotherInference(bool committed)
    {
        var f = new Fixture();
        await f.CreateAsync();
        var root = await f.CreateRootGoalAsync();
        f.Storage.LoseRootUsageReply = committed;
        f.Storage.FailRootUsageCommit = !committed;
        await f.SendAsync(10, "follow-up", CodexAgentMailMode.Followup);
        await Assert.ThrowsAsync<IOException>(() => f.WakeAsync());
        var cold = new CodexThreadStore(f.Storage, "account");
        var child = await cold.ReadAsync(Child, Ct);
        var turn = Assert.Single(child.Turns);
        Assert.Equal("failed", turn.Status);
        var receipt = Assert.Single(turn.Dispatches, item => item.Kind == "inference");
        Assert.NotNull(receipt.InferenceCompletion!.Accounting);
        Assert.Equal(root.Goal!.Id, receipt.GoalInference!.GoalId);
        Assert.Equal("succeeded", receipt.State);
        Assert.Equal(committed ? 11 : 0, (await cold.ReadAsync(Root, Ct)).Goal!.TokensUsed);
        Assert.Equal(committed ? CodexDescendantGoalUsageOutcome.AlreadyApplied : CodexDescendantGoalUsageOutcome.Applied,
            await cold.ReconcileDescendantGoalUsageAsync(Child, child.Revision, turn.Id, receipt.CallId, Ct));
        Assert.Equal(CodexDescendantGoalUsageOutcome.AlreadyApplied,
            await cold.ReconcileDescendantGoalUsageAsync(Child, child.Revision, turn.Id, receipt.CallId, Ct));
        var fork = await cold.ForkAsync(Child, child.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Equal(CodexDescendantGoalUsageOutcome.InheritedReceipt,
            await cold.ReconcileDescendantGoalUsageAsync(fork.Id, fork.Revision, turn.Id, receipt.CallId, Ct));
        Assert.Equal(11, (await cold.ReadAsync(Root, Ct)).Goal!.TokensUsed);
        Assert.Equal(1, f.Storage.RootUsageCommits);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task QueueOnlyDoesNotWakeAndFollowupUsesExactRustAgentMessagesInOneExistingRunner()
    {
        var f = new Fixture();
        await f.CreateAsync();
        await f.SendAsync(10, "old note");
        await f.SendAsync(11, "child result", CodexAgentMailMode.Result, sender: Grandchild);
        Assert.Null(await f.WakeAsync());
        Assert.Empty(f.Requests);
        Assert.Null(f.Storage.Mailbox.Claim);
        await f.SendAsync(12, "opaque-ciphertext", CodexAgentMailMode.Followup, encrypted: true);
        await f.SendAsync(13, "later note");
        var result = Assert.IsType<CodexThreadAgentResult>(await f.WakeAsync());
        var turn = Assert.Single(result.Thread.Turns);
        Assert.Equal(Turn, turn.Id);
        Assert.Equal("completed", turn.Status);
        Assert.Equal(CodexAgentMailWakeAdmission.TurnTrigger(Claim), turn.TurnTrigger);
        Assert.Equal(3, turn.Input.Length);
        Assert.Equal("Message Type: MESSAGE\nTask name: /root/worker\nSender: /root\nPayload:\nold note",
            turn.Input[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Message Type: FINAL_ANSWER\nTask name: /root/worker\nSender: /root/worker/helper\nPayload:\nchild result",
            turn.Input[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Message Type: NEW_TASK\nTask name: /root/worker\nSender: /root\nPayload:\n",
            turn.Input[2].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("opaque-ciphertext", turn.Input[2].GetProperty("content")[1].GetProperty("encrypted_content").GetString());
        var request = Assert.Single(f.Requests);
        var mail = request.GetProperty("input").EnumerateArray().Where(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "agent_message").ToArray();
        Assert.Equal(3, mail.Length);
        Assert.Equal(new[] { Id(10), Id(11), Id(12) }.Select(id => "amsg_" + id.ToString("D")),
            mail.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(Id(13), Assert.Single(f.Storage.Mailbox.Pending).Id);
        Assert.Equal(3, f.Storage.Mailbox.Receipts.Count);
        Assert.Equal(1, f.Storage.Admitted);
        var sampled = Assert.Single(turn.Dispatches, item => item.MailSampling is not null);
        Assert.Equal(3, sampled.MailSampling!.Occurrences.Length);
    }

    [Fact]
    public async Task LostClaimReplyNeverRunsAndReleaseRequiresPositiveOwnerFence()
    {
        var f = new Fixture();
        await f.CreateAsync();
        await f.SendAsync(10, "follow-up", CodexAgentMailMode.Followup);
        f.Storage.LoseClaimReply = true;
        await Assert.ThrowsAsync<IOException>(() => f.WakeAsync());
        Assert.Empty(f.Requests);
        Assert.Equal(Claim, f.Storage.Mailbox.Claim?.Id);
        Assert.False(await f.ReconcileAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.WakeAsync());
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.Store.ReleaseAgentMailAsync(Root, Child,
            f.Storage.Mailbox.Revision, Claim, Owner, Ct));
        f.Storage.FencedClaim = Claim;
        var released = await f.Store.ReleaseAgentMailAsync(Root, Child, f.Storage.Mailbox.Revision, Claim, Owner, Ct);
        Assert.Null(released.Claim);
        Assert.Single(released.Pending);
        Assert.Empty(released.Receipts);
        Assert.Equal(0, f.Storage.Admitted);
    }

    [Fact]
    public async Task LostTurnReplyReconcilesExistingAdmissionAfterRestartWithoutReplay()
    {
        var f = new Fixture();
        await f.CreateAsync();
        await f.SendAsync(10, "follow-up", CodexAgentMailMode.Followup);
        f.Storage.LoseTurnReply = true;
        await Assert.ThrowsAsync<IOException>(() => f.WakeAsync());
        Assert.Equal(1, f.Storage.Admitted);
        Assert.Empty(f.Requests);
        await f.SendAsync(11, "later");
        var restarted = new CodexThreadStore(f.Storage, "account");
        Assert.True(await f.ReconcileAsync(restarted));
        Assert.Equal(Id(11), Assert.Single(f.Storage.Mailbox.Pending).Id);
        Assert.Single(f.Storage.Mailbox.Receipts);
        Assert.Null(f.Storage.Mailbox.Claim);
        Assert.Equal(Turn, Assert.Single((await restarted.ReadAsync(Child, Ct)).Turns).Id);
        Assert.Empty(f.Requests);
        Assert.Equal(1, f.Storage.Admitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousMailboxAcknowledgementDoesNotReachProvider(bool committed)
    {
        var f = new Fixture();
        await f.CreateAsync();
        await f.SendAsync(10, "follow-up", CodexAgentMailMode.Followup);
        f.Storage.FailAck = !committed;
        f.Storage.LoseAckReply = committed;
        await Assert.ThrowsAsync<IOException>(() => f.WakeAsync());
        Assert.Empty(f.Requests);
        Assert.Equal(1, f.Storage.Admitted);
        var thread = await f.Store.ReadAsync(Child, Ct);
        Assert.Equal(Turn, Assert.Single(thread.Turns).Id);
        if (!committed)
        {
            f.Storage.FailAck = false;
            Assert.True(await f.ReconcileAsync());
        }
        Assert.Null(f.Storage.Mailbox.Claim);
        Assert.Empty(f.Storage.Mailbox.Pending);
        Assert.Single(f.Storage.Mailbox.Receipts);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task ChangedGraphAndWrongParentCannotAdmitWake()
    {
        var f = new Fixture();
        await f.CreateAsync();
        await f.SendAsync(10, "follow-up", CodexAgentMailMode.Followup);
        var wrong = new CodexResponseOptions("model", "instructions")
        {
            Identity = new("session", Child.ToString(), Turn.ToString()) { ParentThreadId = Grandchild.ToString() },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => f.WakeAsync(wrong));
        Assert.Null(f.Storage.Mailbox.Claim);
        f.Storage.ChangeGraphAfterClaim = true;
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.WakeAsync());
        Assert.Equal(Claim, f.Storage.Mailbox.Claim?.Id);
        Assert.False(await f.ReconcileAsync());
        Assert.Equal(0, f.Storage.Admitted);
        Assert.Empty(f.Requests);
    }
}
