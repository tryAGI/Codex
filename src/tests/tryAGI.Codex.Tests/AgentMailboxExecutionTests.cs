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
                if (newTurn && turn?.TurnTrigger?.StartsWith("native:agent-mail:", StringComparison.Ordinal) == true)
                {
                    if (threadId != Child || Mailbox.Claim is not { WakeAdmission: { } binding } claim ||
                        binding.GraphRevision != GraphRevision || claim.Id == FencedClaim || !binding.Matches(claim, turn))
                        return Task.FromResult(false);
                    Admitted++;
                }
                documents[threadId] = document.Clone();
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
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + Success + "\n\n", Encoding.UTF8, "text/event-stream"),
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
