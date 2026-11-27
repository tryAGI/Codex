namespace tryAGI.Codex.Tests;

public sealed class CodexApprovalStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CodexApprovalAction Action() => new(CodexApprovalKind.Tool, "connection", "execution",
        CodexApprovalAction.Fingerprint("exact tool, args and policy"u8), "thread", "turn", "item", "environment");
    private static ValueTask Authorize(CodexApprovalAuthorization authorization, CancellationToken ct) =>
        authorization.ActorId == "reviewer" ? ValueTask.CompletedTask : throw new UnauthorizedAccessException();
    private static CodexApprovalStore Store(Storage storage, Clock clock, string account = "account") =>
        new(storage, account, Authorize, clock);

    [Fact]
    public async Task ExplicitApprovalCanBeConsumedOnlyOnceAfterRestart()
    {
        var storage = new Storage();
        var clock = new Clock();
        var id = Guid.NewGuid();
        var action = Action();
        var deadline = clock.GetUtcNow().AddMinutes(1);
        var store = Store(storage, clock);
        var request = await store.RequestAsync(id, action, deadline, Ct);
        Assert.Equal(CodexApprovalState.Pending, request.State);
        Assert.Null(await store.TryConsumeAsync(id, action, Ct));
        var approved = await store.DecideAsync(id, action, request.Revision, true, "reviewer", Ct);
        Assert.Equal(CodexApprovalState.Approved, approved.State);
        Assert.Equal("reviewer", approved.ActorId);

        var restarted = Store(storage, clock);
        var admissions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Store(storage, clock).TryConsumeAsync(id, action, Ct)));
        var consumed = Assert.Single(admissions.OfType<CodexApprovalRecord>());
        Assert.Equal(CodexApprovalState.Consumed, consumed.State);
        Assert.Equal(3, consumed.Revision);
        Assert.Equal(deadline, consumed.ExpiresAt);
        Assert.Equal(consumed, await restarted.ReadAsync(id, Ct));
        Assert.Null(await restarted.TryConsumeAsync(id, action, Ct));
    }

    [Fact]
    public async Task DuplicateRequestNeverResurrectsOrRebindsAuthority()
    {
        var storage = new Storage();
        var clock = new Clock();
        var store = Store(storage, clock);
        var id = Guid.NewGuid();
        var action = Action();
        var deadline = clock.GetUtcNow().AddMinutes(1);
        var records = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.RequestAsync(id, action, deadline, Ct)));
        Assert.All(records, x => Assert.Equal(records[0], x));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RequestAsync(id, action with { ActionSha256 = CodexApprovalAction.Fingerprint("changed args"u8) }, deadline, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RequestAsync(id, action, deadline.AddSeconds(1), Ct));
        var denied = await store.DecideAsync(id, action, 1, false, "reviewer", Ct);
        Assert.Equal(denied, await Store(storage, clock).RequestAsync(id, action, deadline, Ct));
        Assert.Equal(CodexApprovalState.Denied, denied.State);
        Assert.Null(await store.TryConsumeAsync(id, action, Ct));
    }

    [Fact]
    public async Task MismatchedOwnerScopeAndStaleDecisionAreRejected()
    {
        var storage = new Storage();
        var clock = new Clock();
        var store = Store(storage, clock);
        var id = Guid.NewGuid();
        var action = Action();
        var request = await store.RequestAsync(id, action, clock.GetUtcNow().AddMinutes(1), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            store.DecideAsync(id, action, request.Revision, true, "model", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DecideAsync(id, action with { ItemId = "other-item" }, request.Revision, true, "reviewer", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DecideAsync(id, action, 99, true, "reviewer", Ct));
        Assert.Equal(request, await store.ReadAsync(id, Ct));
        var approved = await store.DecideAsync(id, action, request.Revision, true, "reviewer", Ct);
        Assert.Null(await store.TryConsumeAsync(id, action with { ConnectionId = "foreign" }, Ct));
        Assert.Null(await store.TryConsumeAsync(id, action with { EnvironmentId = "other-env" }, Ct));
        Assert.Null(await store.TryConsumeAsync(id, action with { ActionId = "other-attempt" }, Ct));
        Assert.Null(await store.TryConsumeAsync(id, action with { Kind = CodexApprovalKind.Command }, Ct));
        var otherAccount = Store(storage, clock, "other-account");
        Assert.Null(await otherAccount.ReadAsync(id, Ct));
        Assert.Null(await otherAccount.TryConsumeAsync(id, action, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DecideAsync(id, action, request.Revision, false, "reviewer", Ct));
        Assert.Equal(approved, await store.ReadAsync(id, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpirationIsPersistedForPendingAndApprovedRequests(bool approved)
    {
        var storage = new Storage();
        var clock = new Clock();
        var store = Store(storage, clock);
        var id = Guid.NewGuid();
        var action = Action();
        var request = await store.RequestAsync(id, action, clock.GetUtcNow().AddMinutes(1), Ct);
        if (approved) request = await store.DecideAsync(id, action, request.Revision, true, "reviewer", Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        var expired = await Store(storage, clock).ReadAsync(id, Ct);
        Assert.Equal(CodexApprovalState.Expired, expired!.State);
        Assert.Equal(request.Revision + 1, expired.Revision);
        Assert.Equal(expired, storage.Record("account", id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DecideAsync(id, action, request.Revision, true, "reviewer", Ct));
        Assert.Null(await store.TryConsumeAsync(id, action, Ct));
    }

    [Fact]
    public async Task CancellationAndRevocationDoNotGrantOrRestoreAuthority()
    {
        var storage = new Storage();
        var clock = new Clock();
        var store = Store(storage, clock);
        var action = Action();
        var deadline = clock.GetUtcNow().AddMinutes(1);
        var id = Guid.NewGuid();
        var request = await store.RequestAsync(id, action, deadline, Ct);
        var cancelled = await store.CancelAsync(id, action, request.Revision, "reviewer", Ct);
        Assert.Equal(CodexApprovalState.Cancelled, cancelled.State);
        Assert.Null(await store.TryConsumeAsync(id, action, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DecideAsync(id, action, cancelled.Revision, true, "reviewer", Ct));

        var next = Guid.NewGuid();
        var otherAction = action with { ActionId = "next" };
        await store.RequestAsync(next, otherAction, deadline, Ct);
        var approved = await store.DecideAsync(next, otherAction, 1, true, "reviewer", Ct);
        var revoked = await store.RevokeAsync(next, otherAction, approved.Revision, "reviewer", Ct);
        Assert.Equal(CodexApprovalState.Revoked, revoked.State);
        Assert.Null(await store.TryConsumeAsync(next, otherAction, Ct));
    }

    [Fact]
    public async Task RevocationRacingConsumptionHasOnlyOneCasWinner()
    {
        var storage = new Storage();
        var clock = new Clock();
        var store = Store(storage, clock);
        var id = Guid.NewGuid();
        var action = Action();
        await store.RequestAsync(id, action, clock.GetUtcNow().AddMinutes(1), Ct);
        var approved = await store.DecideAsync(id, action, 1, true, "reviewer", Ct);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        storage.BeforeWrite = async (record, _) =>
        {
            if (record.State is not (CodexApprovalState.Revoked or CodexApprovalState.Consumed)) return;
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await gate.Task.WaitAsync(Ct);
        };
        var consuming = store.TryConsumeAsync(id, action, Ct);
        var revoking = store.RevokeAsync(id, action, approved.Revision, "reviewer", Ct);
        var consumed = await consuming;
        CodexApprovalRecord? revoked = null;
        try { revoked = await revoking; }
        catch (InvalidOperationException) { }
        Assert.True((consumed is not null) ^ (revoked is not null));
        Assert.Equal(consumed ?? revoked, await store.ReadAsync(id, Ct));
    }

    [Fact]
    public async Task AmbiguousConsumedCommitCannotAuthorizeReplay()
    {
        var storage = new Storage();
        var clock = new Clock();
        var store = Store(storage, clock);
        var id = Guid.NewGuid();
        var action = Action();
        await store.RequestAsync(id, action, clock.GetUtcNow().AddMinutes(1), Ct);
        await store.DecideAsync(id, action, 1, true, "reviewer", Ct);
        storage.ThrowAfterCommit = CodexApprovalState.Consumed;
        await Assert.ThrowsAsync<IOException>(() => store.TryConsumeAsync(id, action, Ct));
        storage.ThrowAfterCommit = null;
        var restarted = Store(storage, clock);
        Assert.Equal(CodexApprovalState.Consumed, (await restarted.ReadAsync(id, Ct))!.State);
        Assert.Null(await restarted.TryConsumeAsync(id, action, Ct));
    }

    [Fact]
    public async Task DelayedConsumptionCommitCannotExtendDeadline()
    {
        var storage = new Storage();
        var clock = new Clock();
        var store = Store(storage, clock);
        var id = Guid.NewGuid();
        var action = Action();
        await store.RequestAsync(id, action, clock.GetUtcNow().AddMinutes(1), Ct);
        await store.DecideAsync(id, action, 1, true, "reviewer", Ct);
        storage.BeforeWrite = (record, _) =>
        {
            if (record.State == CodexApprovalState.Consumed) clock.Advance(TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        };
        Assert.Null(await store.TryConsumeAsync(id, action, Ct));
        Assert.Equal(CodexApprovalState.Consumed, (await store.ReadAsync(id, Ct))!.State);
        Assert.Null(await store.TryConsumeAsync(id, action, Ct));
    }

    [Fact]
    public async Task ReviewerAuthorizationDelayCannotApproveAfterExpiration()
    {
        var storage = new Storage();
        var clock = new Clock();
        var id = Guid.NewGuid();
        var action = Action();
        var store = new CodexApprovalStore(storage, "account", (_, _) =>
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return ValueTask.CompletedTask;
        }, clock);
        await store.RequestAsync(id, action, clock.GetUtcNow().AddMinutes(1), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DecideAsync(id, action, 1, true, "reviewer", Ct));
        Assert.Equal(CodexApprovalState.Expired, storage.Record("account", id)!.State);
        Assert.Null(await store.TryConsumeAsync(id, action, Ct));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class Storage : ICodexApprovalStorage
    {
        private readonly Dictionary<(string, Guid), CodexApprovalRecord> records = new();
        private readonly object gate = new();
        internal CodexApprovalState? ThrowAfterCommit { get; set; }
        internal Func<CodexApprovalRecord, CancellationToken, Task>? BeforeWrite { get; set; }

        internal CodexApprovalRecord? Record(string account, Guid id)
        {
            lock (gate) return records.GetValueOrDefault((account, id));
        }

        public Task<CodexApprovalRecord?> ReadAsync(string accountId, Guid requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Record(accountId, requestId));
        }

        public async Task<bool> CompareExchangeAsync(string accountId, Guid requestId, long? expectedRevision,
            CodexApprovalRecord record, CancellationToken cancellationToken)
        {
            if (BeforeWrite is { } before) await before(record, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var key = (accountId, requestId);
                if (records.GetValueOrDefault(key)?.Revision != expectedRevision) return false;
                records[key] = record;
                if (ThrowAfterCommit == record.State) throw new IOException("Synthetic ambiguous commit.");
                return true;
            }
        }
    }
}
