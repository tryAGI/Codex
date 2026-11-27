using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class ThreadAgentMailboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static Guid Root => Id(1);
    private static Guid Child => Id(2);
    private const string Owner = "authorized-worker";

    private sealed class Storage : ICodexAgentMailboxStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, CodexAgentMailbox> mailboxes = [];
        private readonly Dictionary<Guid, Guid> parents = new() { [Id(2)] = Id(1), [Id(3)] = Id(2) };
        internal bool LoseNextAcknowledgement { get; set; }
        internal bool ConflictOnWrite { get; set; }
        internal int Writes { get; private set; }
        internal Guid? AdmittedClaim { get; set; }
        internal Guid? ProvenUnadmittedClaim { get; set; }
        internal Func<CodexAgentMailbox, CodexAgentMailbox>? TransformRead { get; set; }

        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Mailbox reads must not load thread documents.");

        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Mailbox mutations must not rewrite thread documents.");

        private bool Known(Guid id) => id == Root || parents.ContainsKey(id);

        private CodexAgentMailbox Current(string account, Guid root, Guid recipient)
        {
            if (account != "account" || root != Root || !Known(recipient))
                throw new UnauthorizedAccessException("Unknown agent tree or recipient.");
            return mailboxes.TryGetValue(recipient, out var current) ? current : new(account, root, recipient, 0, [], []);
        }

        public Task<CodexAgentMailbox> ReadAgentMailboxAsync(string accountId, Guid rootThreadId, Guid recipientThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var current = Current(accountId, rootThreadId, recipientThreadId);
                return Task.FromResult(TransformRead?.Invoke(current) ?? current);
            }
        }

        public Task<bool> CompareExchangeAgentMailboxAsync(long expectedRevision, CodexAgentMailbox next,
            CodexAgentMailboxOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                Writes++;
                var current = Current(next.AccountId, next.RootThreadId, next.RecipientThreadId);
                if (ConflictOnWrite || current.Revision != expectedRevision || next.Revision != expectedRevision + 1)
                    return Task.FromResult(false);
                if (operation == CodexAgentMailboxOperation.Enqueue)
                {
                    var mail = next.Pending[^1];
                    if (!Known(mail.SenderThreadId) || mail.Mode == CodexAgentMailMode.Result &&
                        parents.GetValueOrDefault(mail.SenderThreadId) != next.RecipientThreadId)
                        return Task.FromResult(false);
                }
                if (operation == CodexAgentMailboxOperation.Claim && next.Claim?.OwnerId != Owner)
                    return Task.FromResult(false);
                if (operation == CodexAgentMailboxOperation.Acknowledge &&
                    (current.Claim is null || current.Claim.Id != AdmittedClaim))
                    return Task.FromResult(false);
                if (operation == CodexAgentMailboxOperation.Release &&
                    (current.Claim is null || current.Claim.Id != ProvenUnadmittedClaim || current.Claim.Id == AdmittedClaim))
                    return Task.FromResult(false);
                mailboxes[next.RecipientThreadId] = next;
                if (LoseNextAcknowledgement)
                {
                    LoseNextAcknowledgement = false;
                    throw new IOException("Simulated lost mailbox commit acknowledgement.");
                }
                return Task.FromResult(true);
            }
        }
    }

    private static CodexAgentMail Mail(int id, string text, CodexAgentMailMode mode = CodexAgentMailMode.QueueOnly,
        Guid? sender = null, bool encrypted = false) => new(Id(id), sender ?? Root, mode, text, encrypted);

    private static async Task<CodexAgentMailbox> Send(CodexThreadStore store, CodexAgentMail mail, Guid? recipient = null)
    {
        var target = recipient ?? Child;
        var current = await store.ReadAgentMailboxAsync(Root, target, Ct);
        return await store.EnqueueAgentMailAsync(Root, target, current.Revision, mail, Ct);
    }

    [Fact]
    public async Task QueueOnlyMailSurvivesRecreationAndFollowupReservesExactPrefix()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        Assert.True(store.SupportsAgentMailbox);
        await Send(store, Mail(10, "earlier"));
        var queued = await Send(store, Mail(11, "while unloaded"));
        Assert.False(queued.HasWakeRequest);
        var noWake = await store.ClaimAgentMailAsync(Root, Child, queued.Revision, Id(20), Owner, Id(30), true,
            cancellationToken: Ct);
        Assert.Equal(queued.Revision, noWake.Revision);

        store = new CodexThreadStore(storage, "account");
        await Send(store, Mail(12, "follow-up", CodexAgentMailMode.Followup));
        var before = await Send(store, Mail(13, "later"));
        Assert.True(before.HasWakeRequest);
        var claimed = await store.ClaimAgentMailAsync(Root, Child, before.Revision, Id(20), Owner, Id(30), true,
            cancellationToken: Ct);
        Assert.Equal(3, claimed.Claim?.MessageCount);
        Assert.False(claimed.HasWakeRequest);
        Assert.Equal(new[] { Id(10), Id(11), Id(12), Id(13) }, claimed.Pending.Select(mail => mail.Id));
        var appended = await Send(store, Mail(14, "during claim", CodexAgentMailMode.Followup));
        var recovered = await new CodexThreadStore(storage, "account").ReadAgentMailboxAsync(Root, Child, Ct);
        Assert.Equal(claimed.Claim, recovered.Claim);
        Assert.Equal(appended.Revision, recovered.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimAgentMailAsync(Root, Child,
            recovered.Revision, Id(21), Owner, Id(31), true, cancellationToken: Ct));

        storage.AdmittedClaim = Id(20);
        var settled = await store.AcknowledgeAgentMailAsync(Root, Child, recovered.Revision, Id(20), Owner, Ct);
        Assert.Null(settled.Claim);
        Assert.True(settled.HasWakeRequest);
        Assert.Equal(new[] { Id(13), Id(14) }, settled.Pending.Select(mail => mail.Id));
        Assert.Equal(new[] { Id(10), Id(11), Id(12) }, settled.Receipts.Select(receipt => receipt.Id));
    }

    [Fact]
    public async Task DuplicateIdsAreNoOpsBeforeAndAfterConsumptionButChangedIntentFails()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        var message = Mail(10, "opaque", encrypted: true);
        var first = await Send(store, message);
        var duplicate = await Send(store, message);
        Assert.Equal(first.Revision, duplicate.Revision);
        await Assert.ThrowsAsync<ArgumentException>(() => Send(store, message with { IsEncrypted = false }));
        var claim = await store.ClaimAgentMailAsync(Root, Child, first.Revision, Id(20), Owner, Id(30), false,
            cancellationToken: Ct);
        storage.AdmittedClaim = Id(20);
        var settled = await store.AcknowledgeAgentMailAsync(Root, Child, claim.Revision, Id(20), Owner, Ct);
        var replay = await Send(store, message);
        Assert.Equal(settled.Revision, replay.Revision);
        Assert.Empty(replay.Pending);
        await Assert.ThrowsAsync<ArgumentException>(() => Send(store, message with { Content = "changed" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Send(store, message with { Mode = CodexAgentMailMode.Followup }));
        Assert.Equal(3, storage.Writes);
    }

    [Fact]
    public async Task UncertainCommitCanBeInspectedWithoutReplay()
    {
        var storage = new Storage { LoseNextAcknowledgement = true };
        var store = new CodexThreadStore(storage, "account");
        var mail = Mail(10, "one");
        await Assert.ThrowsAsync<IOException>(() => store.EnqueueAgentMailAsync(Root, Child, 0, mail, Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.EnqueueAgentMailAsync(Root, Child, 0, mail, Ct));
        var recovered = await Send(store, mail);
        Assert.Single(recovered.Pending);
        Assert.Equal(1, storage.Writes);

        storage.LoseNextAcknowledgement = true;
        await Assert.ThrowsAsync<IOException>(() => store.ClaimAgentMailAsync(Root, Child, recovered.Revision,
            Id(20), Owner, Id(30), false, cancellationToken: Ct));
        recovered = await store.ReadAgentMailboxAsync(Root, Child, Ct);
        Assert.Equal(Id(20), recovered.Claim?.Id);
        storage.AdmittedClaim = Id(20);
        storage.LoseNextAcknowledgement = true;
        await Assert.ThrowsAsync<IOException>(() => store.AcknowledgeAgentMailAsync(Root, Child, recovered.Revision, Id(20), Owner, Ct));
        recovered = await new CodexThreadStore(storage, "account").ReadAgentMailboxAsync(Root, Child, Ct);
        Assert.Null(recovered.Claim);
        Assert.Empty(recovered.Pending);
        Assert.Equal(Id(10), Assert.Single(recovered.Receipts).Id);
        Assert.Equal(3, storage.Writes);
    }

    [Fact]
    public async Task ReleaseRequiresNonAdmissionAndPreservesOrder()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        await Send(store, Mail(10, "first"));
        var pending = await Send(store, Mail(11, "second"));
        var claim = await store.ClaimAgentMailAsync(Root, Child, pending.Revision, Id(20), Owner, Id(30), false, 1, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcknowledgeAgentMailAsync(Root, Child,
            claim.Revision, Id(20), "foreign-owner", Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.AcknowledgeAgentMailAsync(Root, Child,
            claim.Revision, Id(20), Owner, Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.ReleaseAgentMailAsync(Root, Child,
            claim.Revision, Id(20), Owner, Ct));
        storage.ProvenUnadmittedClaim = Id(20);
        var released = await store.ReleaseAgentMailAsync(Root, Child, claim.Revision, Id(20), Owner, Ct);
        Assert.Null(released.Claim);
        Assert.Equal(new[] { Id(10), Id(11) }, released.Pending.Select(mail => mail.Id));
        Assert.Empty(released.Receipts);
    }

    [Fact]
    public async Task RootFollowupForeignSendersAndNonparentResultsAreRejected()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        await Assert.ThrowsAsync<ArgumentException>(() => Send(store,
            Mail(10, "forbidden", CodexAgentMailMode.Followup, Child), Root));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => Send(store, Mail(11, "foreign", sender: Id(99))));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => Send(store,
            Mail(12, "wrong parent", CodexAgentMailMode.Result, Id(3)), Root));
        var result = await Send(store, Mail(13, "child done", CodexAgentMailMode.Result, Child), Root);
        Assert.False(result.HasWakeRequest);
        Assert.Equal(Child, Assert.Single(result.Pending).SenderThreadId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadAgentMailboxAsync(Id(99), Child, Ct));
    }

    [Fact]
    public async Task ConcurrentEnqueueAtOneRevisionHasExactlyOneWinner()
    {
        var storage = new Storage();
        var left = new CodexThreadStore(storage, "account");
        var right = new CodexThreadStore(storage, "account");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Attempt(CodexThreadStore store, CodexAgentMail mail)
        {
            await start.Task;
            try
            {
                await store.EnqueueAgentMailAsync(Root, Child, 0, mail, Ct);
                return true;
            }
            catch (CodexThreadConflictException) { return false; }
        }
        var first = Attempt(left, Mail(10, "first"));
        var second = Attempt(right, Mail(11, "second"));
        start.SetResult();
        Assert.Single((await Task.WhenAll(first, second)).Where(won => won));
        var current = await right.ReadAgentMailboxAsync(Root, Child, Ct);
        Assert.Equal(1L, current.Revision);
        Assert.Single(current.Pending);
    }

    [Fact]
    public async Task BoundsRejectInvalidEncodingAndOversizeWithoutDiscardingOlderMail()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        var first = await Send(store, Mail(10, "valid", sender: Child));
        await Assert.ThrowsAsync<ArgumentException>(() => Send(store, Mail(11, new string((char)0xD800, 1))));
        await Assert.ThrowsAsync<ArgumentException>(() => Send(store, Mail(11, new string('x', 65_537))));
        var current = await store.ReadAgentMailboxAsync(Root, Child, Ct);
        Assert.Equal(first.Revision, current.Revision);
        Assert.Equal(Id(10), Assert.Single(current.Pending).Id);
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task StaleRevisionAndMalformedSnapshotsCannotOverwriteMail()
    {
        var storage = new Storage { ConflictOnWrite = true };
        var store = new CodexThreadStore(storage, "account");
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => Send(store, Mail(10, "conflict")));
        storage.ConflictOnWrite = false;
        var first = await Send(store, Mail(10, "valid"));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.EnqueueAgentMailAsync(Root, Child, 0, Mail(11, "stale"), Ct));
        storage.TransformRead = box => box with { RootThreadId = Id(99) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentMailboxAsync(Root, Child, Ct));
        storage.TransformRead = box => box with { Pending = [.. box.Pending, box.Pending[0]] };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentMailboxAsync(Root, Child, Ct));
        storage.TransformRead = box => box with { Claim = new(Id(20), Owner, Id(30), 2, false) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentMailboxAsync(Root, Child, Ct));
        storage.TransformRead = null;
        var recovered = await store.ReadAgentMailboxAsync(Root, Child, Ct);
        Assert.Equal(first.Revision, recovered.Revision);
        Assert.Single(recovered.Pending);
    }
}
