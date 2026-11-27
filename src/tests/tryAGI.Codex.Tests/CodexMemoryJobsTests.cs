namespace tryAGI.Codex.Tests;

public sealed class CodexMemoryJobsTests
{
    private static readonly CodexMemoryScope Scope = new("account", CodexMemoryVersion.V2);
    private static readonly Guid Thread = Guid.Parse("96dc06d7-7454-4936-9c57-6c71867c7c33");
    private static readonly Guid Worker = Guid.Parse("7786b4c7-cc6d-458e-a895-538f17a77d81");
    private static readonly Guid OtherWorker = Guid.Parse("a99a64fc-8b5f-4d91-9d1f-c822e243fb2c");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly CodexMemoryJobKey Stage1 = new(Scope, Thread);
    private static readonly CodexMemoryJobKey Phase2 = new(Scope, null);

    private static CodexMemoryJobs Create(Storage storage, Clock clock) =>
        new(Scope, storage, (_, _) => ValueTask.CompletedTask, clock);

    [Fact]
    public void DisabledAndUpToDateSourcesAreNotClaimed()
    {
        var jobs = Create(new(), new());
        var initial = new CodexMemoryJobObservation(Stage1, null, false, 10, null);
        Assert.Equal(CodexMemoryJobClaimOutcome.Disabled, jobs.PrepareClaim(initial, Worker, Guid.NewGuid(), Lease).Outcome);
        Assert.Equal(CodexMemoryJobClaimOutcome.UpToDate, jobs.PrepareClaim(
            initial with { SourceEnabled = true, OutputWatermark = 10 }, Worker, Guid.NewGuid(), Lease).Outcome);
    }

    [Fact]
    public void ExpiredLeaseMayBeTakenOverButOldOwnerCannotComplete()
    {
        var clock = new Clock();
        var jobs = Create(new(), clock);
        var observed = new CodexMemoryJobObservation(Stage1, null, true, 10, null);
        var token = Guid.NewGuid();
        var claim = jobs.PrepareClaim(observed, Worker, token, Lease).Change!;
        observed = observed with { State = claim.Next };
        Assert.Equal(CodexMemoryJobClaimOutcome.Running,
            jobs.PrepareClaim(observed, OtherWorker, Guid.NewGuid(), Lease).Outcome);
        clock.Advance(Lease);
        var takeover = jobs.PrepareClaim(observed, OtherWorker, Guid.NewGuid(), Lease).Change!;
        Assert.Equal(claim.Next.Revision, takeover.ExpectedRevision);
        Assert.Equal(OtherWorker, takeover.Next.WorkerId);
        Assert.NotEqual(token, takeover.Next.OwnershipToken);
        Assert.Throws<InvalidOperationException>(() => jobs.PrepareStage1Success(
            observed, Worker, token, Guid.NewGuid(), new(10, "memory", "summary")));
    }

    [Fact]
    public void NewSourceWatermarkResetsBackoffAndExhaustedBudget()
    {
        var clock = new Clock();
        var jobs = Create(new(), clock);
        var observed = new CodexMemoryJobObservation(Stage1, null, true, 10, null);
        var token = Guid.NewGuid();
        var claim = jobs.PrepareClaim(observed, Worker, token, Lease).Change!;
        observed = observed with { State = claim.Next with { RetryRemaining = 1 } };
        var failure = jobs.PrepareFailure(observed, Worker, token, Guid.NewGuid(), "transient", TimeSpan.FromHours(1));
        observed = observed with { State = failure.Next };
        Assert.Equal(0, failure.Next.RetryRemaining);
        Assert.Equal(CodexMemoryJobClaimOutcome.RetryBackoff, jobs.PrepareClaim(observed, Worker, Guid.NewGuid(), Lease).Outcome);
        var refreshed = jobs.PrepareClaim(observed with { SourceWatermark = 11 }, Worker, Guid.NewGuid(), Lease).Change!;
        Assert.Equal(3, refreshed.Next.RetryRemaining);
        Assert.Equal(11, refreshed.Next.ClaimedWatermark);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CodexMemoryJobClaimOutcome.RetryExhausted, jobs.PrepareClaim(observed, Worker, Guid.NewGuid(), Lease).Outcome);
        Assert.Equal(CodexMemoryJobClaimOutcome.StaleSource, jobs.PrepareClaim(
            observed with { SourceWatermark = 9 }, Worker, Guid.NewGuid(), Lease).Outcome);
    }

    [Fact]
    public void ExtractionCompletionFencesSourceVersionAndPermission()
    {
        var jobs = Create(new(), new());
        var observed = new CodexMemoryJobObservation(Stage1, null, true, 10, null);
        var token = Guid.NewGuid();
        var claim = jobs.PrepareClaim(observed, Worker, token, Lease).Change!;
        observed = observed with { State = claim.Next };
        var result = new CodexMemoryExtractionOutput(10, "memory", "summary", "slug");
        var completed = jobs.PrepareStage1Success(observed, Worker, token, Guid.NewGuid(), result);
        Assert.Equal(CodexMemoryJobChangeKind.CompleteStage1, completed.Kind);
        Assert.Equal(10, completed.ExpectedSourceWatermark);
        Assert.Equal(token, completed.ExpectedOwnershipToken);
        Assert.Equal(Worker, completed.ExpectedWorkerId);
        Assert.Equal(result, completed.Extraction);
        Assert.Equal(10, completed.Next.LastSuccessWatermark);
        Assert.Null(completed.Next.LeaseUntil);
        var noOutput = jobs.PrepareStage1Success(observed, Worker, token, Guid.NewGuid(), null);
        Assert.Null(noOutput.Extraction);
        Assert.Equal(CodexMemoryJobChangeKind.CompleteStage1, noOutput.Kind);
        Assert.Throws<InvalidOperationException>(() => jobs.PrepareStage1Success(
            observed with { SourceWatermark = 11 }, Worker, token, Guid.NewGuid(), result));
        Assert.Throws<InvalidOperationException>(() => jobs.PrepareStage1Success(
            observed with { SourceEnabled = false }, Worker, token, Guid.NewGuid(), result));
        Assert.Throws<ArgumentException>(() => jobs.PrepareStage1Success(
            observed, Worker, token, Guid.NewGuid(), result with { SourceWatermark = 11 }));
    }

    [Fact]
    public void EnqueueDuringConsolidationKeepsClaimedSnapshotAndCooldown()
    {
        var clock = new Clock();
        var jobs = Create(new(), clock);
        var observed = new CodexMemoryJobObservation(Phase2, null, false, null, null);
        var enqueue = jobs.PrepareEnqueue(observed, Guid.NewGuid(), 15);
        observed = observed with { State = enqueue.Next };
        var token = Guid.NewGuid();
        var claim = jobs.PrepareClaim(observed, Worker, token, Lease).Change!;
        observed = observed with { State = claim.Next };
        var advance = jobs.PrepareEnqueue(observed, Guid.NewGuid(), 14);
        Assert.Equal(16, advance.Next.InputWatermark);
        Assert.Equal(15, advance.Next.ClaimedWatermark);
        Assert.Equal(token, advance.Next.OwnershipToken);
        observed = observed with { State = advance.Next };
        var heartbeat = jobs.PrepareHeartbeat(observed, Worker, token, Guid.NewGuid(), TimeSpan.FromMinutes(10));
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(10), heartbeat.Next.LeaseUntil);
        observed = observed with { State = heartbeat.Next };

        var inputs = new List<CodexMemorySelectedSnapshot> { new(Thread, 10) };
        var completed = jobs.PreparePhase2Success(observed, Worker, token, Guid.NewGuid(), new("digest", inputs));
        inputs.Clear();
        Assert.Equal(10, Assert.Single(completed.Consolidation!.Selected).SourceWatermark);
        Assert.Equal(15, completed.Next.LastSuccessWatermark);
        Assert.Equal(16, completed.Next.InputWatermark);
        observed = observed with { State = completed.Next };
        Assert.Equal(CodexMemoryJobClaimOutcome.Cooldown, jobs.PrepareClaim(observed, Worker, Guid.NewGuid(), Lease).Outcome);
        clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(16, jobs.PrepareClaim(observed, Worker, Guid.NewGuid(), Lease).Change!.Next.ClaimedWatermark);
    }

    [Fact]
    public void Phase2ExhaustedRetryBudgetDoesNotPermanentlyBlockConsolidation()
    {
        var clock = new Clock();
        var jobs = Create(new(), clock);
        var observed = new CodexMemoryJobObservation(Phase2, null, false, null, null);
        var token = Guid.NewGuid();
        var claim = jobs.PrepareClaim(observed, Worker, token, Lease).Change!;
        observed = observed with { State = claim.Next with { RetryRemaining = 0 } };
        var failure = jobs.PrepareFailure(observed, Worker, token, Guid.NewGuid(), "retry", TimeSpan.FromMinutes(1));
        observed = observed with { State = failure.Next };
        Assert.Equal(CodexMemoryJobClaimOutcome.RetryBackoff, jobs.PrepareClaim(observed, Worker, Guid.NewGuid(), Lease).Outcome);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CodexMemoryJobClaimOutcome.Prepared, jobs.PrepareClaim(observed, Worker, Guid.NewGuid(), Lease).Outcome);
    }

    [Fact]
    public async Task LostCommitResponseUsesExactReceiptWithoutRefreshingCas()
    {
        var storage = new Storage { LoseNextResponse = true };
        var clock = new Clock();
        var jobs = Create(storage, clock);
        var ct = TestContext.Current.CancellationToken;
        var observed = await jobs.ReadAsync(Stage1, ct);
        var claim = jobs.PrepareClaim(observed, Worker, Guid.NewGuid(), Lease).Change!;
        await Assert.ThrowsAsync<IOException>(async () => await jobs.ApplyAsync(claim, ct));
        Assert.Equal(1, storage.Reads);
        Assert.Equal(1, storage.AppliedMutations);
        var restarted = Create(storage, clock);
        var receipt = await restarted.ReadReceiptAsync(Stage1, claim.OperationId, ct);
        Assert.NotNull(receipt);
        Assert.Equal(CodexMemoryJobSettlement.Applied, receipt.Settlement);
        Assert.Equal(claim.Next.Revision, receipt.CommittedRevision);
        Assert.Equal(receipt, await restarted.ApplyAsync(claim, ct));
        Assert.Equal(1, storage.Reads);
        Assert.Equal(1, storage.AppliedMutations);
        var competing = jobs.PrepareClaim(observed, OtherWorker, Guid.NewGuid(), Lease).Change!;
        Assert.Equal(CodexMemoryJobSettlement.Conflict, (await jobs.ApplyAsync(competing, ct)).Settlement);
        Assert.Equal(1, storage.AppliedMutations);
    }

    [Fact]
    public async Task ScopeAndAuthorizationAreCheckedBeforeStorage()
    {
        var storage = new Storage();
        var jobs = Create(storage, new());
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await jobs.ReadAsync(
            new(new("foreign", CodexMemoryVersion.V2), Thread), ct));
        var denied = new CodexMemoryJobs(Scope, storage, (_, _) => throw new UnauthorizedAccessException());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await denied.ReadAsync(Stage1, ct));
        Assert.Equal(0, storage.Reads);
        Assert.Equal(0, storage.AppliedMutations);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    // The fixture models an atomic host transaction; it does not certify any production storage adapter.
    private sealed class Storage : ICodexMemoryJobStorage
    {
        private CodexMemoryJobState? state;
        private readonly Dictionary<Guid, (CodexMemoryJobChange Change, CodexMemoryJobReceipt Receipt)> receipts = [];
        public int Reads { get; private set; }
        public int AppliedMutations { get; private set; }
        public bool LoseNextResponse { get; set; }

        public ValueTask<CodexMemoryJobObservation> ReadAsync(CodexMemoryJobKey key, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(new CodexMemoryJobObservation(key, state, true, 10, null));
        }

        public ValueTask<CodexMemoryJobReceipt> ApplyAsync(CodexMemoryJobChange change, CancellationToken cancellationToken)
        {
            if (receipts.TryGetValue(change.OperationId, out var previous))
            {
                if (previous.Change != change) throw new InvalidOperationException("Different operation contents.");
                return ValueTask.FromResult(previous.Receipt);
            }
            var applied = state?.Revision == change.ExpectedRevision;
            var receipt = new CodexMemoryJobReceipt(change.Key, change.OperationId,
                applied ? CodexMemoryJobSettlement.Applied : CodexMemoryJobSettlement.Conflict,
                applied ? change.Next.Revision : null);
            if (applied)
            {
                state = change.Next;
                AppliedMutations++;
            }
            receipts.Add(change.OperationId, (change, receipt));
            if (LoseNextResponse)
            {
                LoseNextResponse = false;
                throw new IOException("Commit response lost.");
            }
            return ValueTask.FromResult(receipt);
        }

        public ValueTask<CodexMemoryJobReceipt?> ReadReceiptAsync(CodexMemoryJobKey key, Guid operationId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                receipts.TryGetValue(operationId, out var value) && value.Receipt.Key == key ? value.Receipt : null);
    }
}
