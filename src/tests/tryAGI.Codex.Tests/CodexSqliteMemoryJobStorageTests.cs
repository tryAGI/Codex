using System.Diagnostics;

namespace tryAGI.Codex.Tests;

public sealed class CodexSqliteMemoryJobStorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly CodexMemoryScope Scope = new("memory-account", CodexMemoryVersion.V2);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static void RequireMac() => Assert.SkipUnless(OperatingSystem.IsMacOS(), "Actual system SQLite requires macOS.");
    private static CodexMemoryJobKey Key(Guid? thread = null) => new(Scope, thread);
    private static CodexMemoryJobs Jobs(ICodexMemoryJobStorage storage, Clock clock) =>
        new(Scope, storage, (_, _) => ValueTask.CompletedTask, clock);

    private static async Task<CodexMemoryJobChange> Claim(CodexMemoryJobs jobs, CodexMemoryJobKey key, Guid worker,
        int cap = 10)
    {
        var observed = await jobs.ReadAsync(key, Ct);
        var plan = jobs.PrepareClaim(observed, worker, Guid.NewGuid(), Lease, cap);
        Assert.Equal(CodexMemoryJobClaimOutcome.Prepared, plan.Outcome);
        var change = Assert.IsType<CodexMemoryJobChange>(plan.Change);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(change, Ct)).Settlement);
        return change;
    }

    private static async Task<CodexMemoryJobChange> Extract(CodexMacOsSqliteMemoryJobStorage storage, Clock clock,
        Guid thread, long watermark, string memory)
    {
        Assert.True(await storage.PublishSourceAsync(new(thread, watermark, true), Ct));
        var jobs = Jobs(storage, clock);
        var worker = Guid.NewGuid();
        var claim = await Claim(jobs, Key(thread), worker);
        var observed = await jobs.ReadAsync(Key(thread), Ct);
        var change = jobs.PrepareStage1Success(observed, worker, claim.OperationId, Guid.NewGuid(),
            new(watermark, memory, "rollout summary"));
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(change, Ct)).Settlement);
        return change;
    }

    [Fact]
    public async Task ExtractionAndGlobalEnqueueSurviveReopenAndExactReceiptReplay()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        CodexMemoryJobChange change;
        long globalRevision;
        await using (var first = await fixture.Open(clock, true))
        {
            change = await Extract(first, clock, thread, 17, "durable result");
            var global = await first.ReadAsync(Key(), Ct);
            Assert.Equal(17L, global.State!.InputWatermark);
            Assert.Equal(CodexMemoryJobStatus.Pending, global.State.Status);
            globalRevision = global.State.Revision;
        }

        await using var second = await fixture.Open(clock);
        var observed = await second.ReadAsync(Key(thread), Ct);
        Assert.Equal(17L, observed.OutputWatermark);
        Assert.Equal(17L, observed.State!.LastSuccessWatermark);
        var snapshots = await second.ListEligibleOutputsAsync(cancellationToken: Ct);
        var selected = Assert.Single(snapshots);
        Assert.Equal(new CodexMemorySelectedSnapshot(thread, 17), selected);
        Assert.Equal("durable result", (await second.ReadExtractionAsync(selected, Ct))!.RawMemory);
        Assert.Null(await second.ReadExtractionAsync(selected with { SourceWatermark = 16 }, Ct));

        var receipt = await second.ReadReceiptAsync(Key(thread), change.OperationId, Ct);
        Assert.NotNull(receipt);
        Assert.Equal(CodexMemoryJobSettlement.Applied, receipt.Settlement);
        Assert.Equal(receipt, await second.ApplyAsync(change, Ct));
        Assert.Equal(globalRevision, (await second.ReadAsync(Key(), Ct)).State!.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.ApplyAsync(
            change with { Extraction = change.Extraction! with { RawMemory = "changed" } }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.ReadReceiptAsync(Key(Guid.NewGuid()), change.OperationId, Ct));
    }

    [Fact]
    public async Task IndependentConnectionsSerializeClaimsAndGlobalRunningCap()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        await using var first = await fixture.Open(clock, true);
        await using var second = await fixture.Open(clock);
        Assert.True(await first.PublishSourceAsync(new(one, 1, true), Ct));
        Assert.True(await first.PublishSourceAsync(new(two, 1, true), Ct));
        var a = Jobs(first, clock);
        var b = Jobs(second, clock);
        var claim1 = a.PrepareClaim(await a.ReadAsync(Key(one), Ct), Guid.NewGuid(), Guid.NewGuid(), Lease, 1).Change!;
        var claim2 = b.PrepareClaim(await b.ReadAsync(Key(two), Ct), Guid.NewGuid(), Guid.NewGuid(), Lease, 1).Change!;
        var results = await Task.WhenAll(a.ApplyAsync(claim1, Ct).AsTask(), b.ApplyAsync(claim2, Ct).AsTask());
        Assert.Single(results, r => r.Settlement == CodexMemoryJobSettlement.Applied);
        var rejected = Assert.Single(results, r => r.Settlement == CodexMemoryJobSettlement.Capacity);
        Assert.Equal(rejected, await second.ReadReceiptAsync(rejected.Key, rejected.OperationId, Ct));

        clock.Advance(Lease);
        var stale = results[0].Settlement == CodexMemoryJobSettlement.Applied ? claim1 : claim2;
        var key = stale.Key;
        var firstView = await a.ReadAsync(key, Ct);
        var secondView = await b.ReadAsync(key, Ct);
        var takeoverA = a.PrepareClaim(firstView, Guid.NewGuid(), Guid.NewGuid(), Lease, 1).Change!;
        var takeoverB = b.PrepareClaim(secondView, Guid.NewGuid(), Guid.NewGuid(), Lease, 1).Change!;
        var race = await Task.WhenAll(a.ApplyAsync(takeoverA, Ct).AsTask(), b.ApplyAsync(takeoverB, Ct).AsTask());
        Assert.Single(race, r => r.Settlement == CodexMemoryJobSettlement.Applied);
        Assert.Single(race, r => r.Settlement == CodexMemoryJobSettlement.Conflict);
    }

    [Fact]
    public async Task RevocationAndNewGenerationFenceACompletionPreparedBeforePolicyChange()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var first = await fixture.Open(clock, true);
        await using var second = await fixture.Open(clock);
        Assert.True(await first.PublishSourceAsync(new(thread, 10, true), Ct));
        var jobs = Jobs(first, clock);
        var worker = Guid.NewGuid();
        var claim = await Claim(jobs, Key(thread), worker);
        var prepared = jobs.PrepareStage1Success(await jobs.ReadAsync(Key(thread), Ct), worker,
            claim.OperationId, Guid.NewGuid(), new(10, "must not publish", "summary"));
        Assert.True(await second.PublishSourceAsync(new(thread, 11, false), Ct));
        Assert.False(await second.PublishSourceAsync(new(thread, 10, true), Ct));
        Assert.False(await second.PublishSourceAsync(new(thread, 11, true), Ct));
        Assert.True(await second.PublishSourceAsync(new(thread, 12, true), Ct));

        var rejected = await jobs.ApplyAsync(prepared, Ct);
        Assert.Equal(CodexMemoryJobSettlement.Denied, rejected.Settlement);
        Assert.Equal(rejected, await second.ReadReceiptAsync(Key(thread), prepared.OperationId, Ct));
        Assert.Empty(await second.ListEligibleOutputsAsync(cancellationToken: Ct));
        Assert.Null((await second.ReadAsync(Key(), Ct)).State);
        clock.Advance(Lease);
        var updated = await Claim(Jobs(second, clock), Key(thread), Guid.NewGuid());
        Assert.Equal(12L, updated.Next.ClaimedWatermark);
    }

    [Fact]
    public async Task ExpiredLeaseCannotSettleEvenWithoutATakeover()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        Assert.True(await storage.PublishSourceAsync(new(thread, 2, true), Ct));
        var jobs = Jobs(storage, clock);
        var worker = Guid.NewGuid();
        var claim = await Claim(jobs, Key(thread), worker);
        var completion = jobs.PrepareStage1Success(await jobs.ReadAsync(Key(thread), Ct), worker,
            claim.OperationId, Guid.NewGuid(), new(2, "expired", "summary"));
        clock.Advance(Lease);
        Assert.Equal(CodexMemoryJobSettlement.Conflict, (await jobs.ApplyAsync(completion, Ct)).Settlement);
        Assert.Null((await storage.ReadAsync(Key(thread), Ct)).OutputWatermark);
    }

    [Fact]
    public async Task ConsolidationRejectsStaleSelectionAndRevocationHidesPublishedArtifact()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        await Extract(storage, clock, a, 1, "first");
        await Extract(storage, clock, b, 2, "second");
        var jobs = Jobs(storage, clock);
        var worker = Guid.NewGuid();
        var phase2 = await Claim(jobs, Key(), worker);
        var selected = await storage.ListEligibleOutputsAsync(cancellationToken: Ct);
        Assert.Equal(2, selected.Count);
        await Extract(storage, clock, a, 3, "updated");
        var latestGlobal = await jobs.ReadAsync(Key(), Ct);
        Assert.True(latestGlobal.State!.InputWatermark > phase2.Next.ClaimedWatermark);
        Assert.Equal(phase2.OperationId, latestGlobal.State.OwnershipToken);
        var stale = jobs.PreparePhase2Success(latestGlobal, worker, phase2.OperationId,
            Guid.NewGuid(), new("artifact-stale", selected));
        Assert.Equal(CodexMemoryJobSettlement.Conflict, (await jobs.ApplyAsync(stale, Ct)).Settlement);
        Assert.Null((await storage.ReadConsolidationAsync(Ct)).ArtifactRevision);

        // A fresh selection must come from a new consolidation claim at the new watermark.
        clock.Advance(Lease);
        var freshWorker = Guid.NewGuid();
        var fresh = await Claim(jobs, Key(), freshWorker);
        var exact = await storage.ListEligibleOutputsAsync(cancellationToken: Ct);
        var complete = jobs.PreparePhase2Success(await jobs.ReadAsync(Key(), Ct), freshWorker, fresh.OperationId,
            Guid.NewGuid(), new("artifact-verified", exact));
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(complete, Ct)).Settlement);
        Assert.Equal(new CodexMemoryConsolidationState("artifact-verified", 2), await storage.ReadConsolidationAsync(Ct));
        Assert.True(await storage.PublishSourceAsync(new(a, 4, false), Ct));
        Assert.Equal(new CodexMemoryConsolidationState(null, 2), await storage.ReadConsolidationAsync(Ct));
        Assert.Null(await storage.ReadExtractionAsync(new(a, 3), Ct));
        Assert.Equal(b, Assert.Single(await storage.ListEligibleOutputsAsync(cancellationToken: Ct)).ThreadId);
    }

    [Fact]
    public async Task NoOutputCompletionRemovesPriorExtractionAndEnqueuesExactlyOnce()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        await Extract(storage, clock, thread, 3, "old");
        Assert.True(await storage.PublishSourceAsync(new(thread, 4, true), Ct));
        var jobs = Jobs(storage, clock);
        var worker = Guid.NewGuid();
        var claim = await Claim(jobs, Key(thread), worker);
        var completion = jobs.PrepareStage1Success(await jobs.ReadAsync(Key(thread), Ct), worker,
            claim.OperationId, Guid.NewGuid(), null);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(completion, Ct)).Settlement);
        var global = await storage.ReadAsync(Key(), Ct);
        Assert.Equal(4L, global.State!.InputWatermark);
        Assert.Null((await storage.ReadAsync(Key(thread), Ct)).OutputWatermark);
        Assert.Empty(await storage.ListEligibleOutputsAsync(cancellationToken: Ct));
        await jobs.ApplyAsync(completion, Ct);
        Assert.Equal(global.State.Revision, (await storage.ReadAsync(Key(), Ct)).State!.Revision);
    }

    [Fact]
    public async Task ReopenResolvesLostAcknowledgmentWithoutRepeatingEffects()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        var worker = Guid.NewGuid();
        CodexMemoryJobChange change;
        await using (var storage = await fixture.Open(clock, true))
        {
            Assert.True(await storage.PublishSourceAsync(new(thread, 9, true), Ct));
            var jobs = Jobs(storage, clock);
            var claim = await Claim(jobs, Key(thread), worker);
            change = jobs.PrepareStage1Success(await jobs.ReadAsync(Key(thread), Ct), worker,
                claim.OperationId, Guid.NewGuid(), new(9, "committed", "summary"));
            var lossy = Jobs(new LoseAcknowledgment(storage), clock);
            await Assert.ThrowsAsync<IOException>(async () => await lossy.ApplyAsync(change, Ct));
        }
        await using var reopened = await fixture.Open(clock);
        var receipt = await reopened.ReadReceiptAsync(Key(thread), change.OperationId, Ct);
        Assert.NotNull(receipt);
        Assert.Equal(CodexMemoryJobSettlement.Applied, receipt.Settlement);
        Assert.Equal("committed", (await reopened.ReadExtractionAsync(new(thread, 9), Ct))!.RawMemory);
        Assert.Equal(receipt, await reopened.ApplyAsync(change, Ct));
        Assert.Equal(1L, (await reopened.ReadAsync(Key(), Ct)).State!.Revision);
    }

    [Fact]
    public async Task ExactPlannerAndAuthorityRejectFabricatedNextState()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        var seen = new List<CodexMemoryJobStorageAccess>();
        await using var storage = await fixture.Open(clock, true, (access, _) =>
        {
            seen.Add(access);
            if (access.Operation == "read_output") throw new UnauthorizedAccessException();
            return ValueTask.CompletedTask;
        });
        Assert.True(await storage.PublishSourceAsync(new(thread, 1, true), Ct));
        var jobs = Jobs(storage, clock);
        var prepared = jobs.PrepareClaim(await jobs.ReadAsync(Key(thread), Ct),
            Guid.NewGuid(), Guid.NewGuid(), Lease).Change!;
        var forged = prepared with { Next = prepared.Next with { RetryRemaining = 999 } };
        Assert.Equal(CodexMemoryJobSettlement.Conflict, (await storage.ApplyAsync(forged, Ct)).Settlement);
        Assert.Null((await storage.ReadAsync(Key(thread), Ct)).State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.ReadExtractionAsync(new(thread, 1), Ct));
        Assert.Contains(seen, x => x.Operation == "apply" && x.Change!.OperationId == forged.OperationId);
        var count = seen.Count;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await storage.ReadAsync(
            new(new("foreign", CodexMemoryVersion.V2), thread), Ct));
        Assert.Equal(count, seen.Count);
    }

    [Fact]
    public async Task NamespaceAndSchemaMismatchDoNotInitializeOrOverwriteExistingDatabase()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open(clock));
        await using (var initialized = await fixture.Open(clock, true))
        {
            Assert.Null((await initialized.ReadAsync(Key(), Ct)).State);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open(clock, scope: new("foreign", CodexMemoryVersion.V2)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open(clock, scope: new(Scope.AccountId, CodexMemoryVersion.V1)));
        await Assert.ThrowsAsync<IOException>(() => fixture.Open(clock, true));
    }

    private static CodexMemorySamplingAdmission Admission(CodexMemoryJobChange claim) =>
        new(claim.Key, claim.Next.WorkerId!.Value, claim.OperationId, claim.Next.ClaimedWatermark!.Value, Guid.NewGuid());

    [Fact]
    public async Task SamplingAdmissionIsOneShotAcrossConnectionsAndReopen()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        CodexMemorySamplingAdmission admission;
        await using (var first = await fixture.Open(clock, true))
        await using (var second = await fixture.Open(clock))
        {
            Assert.True(await first.PublishSourceAsync(new(thread, 1, true), Ct));
            admission = Admission(await Claim(Jobs(first, clock), Key(thread), Guid.NewGuid()));
            var race = await Task.WhenAll(first.TryAdmitSamplingAsync(admission, Ct).AsTask(),
                second.TryAdmitSamplingAsync(admission with { AttemptId = Guid.NewGuid() }, Ct).AsTask());
            Assert.Single(race, result => result);
            Assert.False(await first.TryAdmitSamplingAsync(admission, Ct));
        }
        await using var reopened = await fixture.Open(clock);
        Assert.False(await reopened.TryAdmitSamplingAsync(admission with { AttemptId = Guid.NewGuid() }, Ct));
        clock.Advance(Lease);
        var takeover = Admission(await Claim(Jobs(reopened, clock), Key(thread), Guid.NewGuid()));
        Assert.False(await reopened.TryAdmitSamplingAsync(admission, Ct));
        Assert.True(await reopened.TryAdmitSamplingAsync(takeover, Ct));
    }

    [Fact]
    public async Task RevokedSourceCannotAdmitSampling()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        Assert.True(await storage.PublishSourceAsync(new(thread, 1, true), Ct));
        var admission = Admission(await Claim(Jobs(storage, clock), Key(thread), Guid.NewGuid()));
        Assert.True(await storage.PublishSourceAsync(new(thread, 2, false), Ct));
        Assert.False(await storage.TryAdmitSamplingAsync(admission, Ct));
    }

    [Fact]
    public async Task WorkerExtractsAndConsolidatesExactlyOnce()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        Assert.True(await storage.PublishSourceAsync(new(thread, 3, true), Ct));
        var jobs = Jobs(storage, clock);
        var worker = new CodexMemorySamplingWorker(jobs, storage, clock);
        var admission = Admission(await Claim(jobs, Key(thread), Guid.NewGuid()));
        var calls = 0;
        Task<string?> Sample(CodexMemorySamplingAdmission _, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>("{\"rollout_summary\":\"private-secret\",\"rollout_slug\":\"sample\"}");
        }

        var operation = Guid.NewGuid();
        var result = await worker.RunStage1Async(admission, operation, Sample,
            value => value.Replace("private-secret", "[redacted]", StringComparison.Ordinal), cancellationToken: Ct);
        Assert.Equal(CodexMemorySamplingOutcome.Completed, result.Outcome);
        Assert.Equal(CodexMemoryJobSettlement.Applied, result.Receipt!.Settlement);
        var extraction = await storage.ReadExtractionAsync(new(thread, 3), Ct);
        Assert.Equal("[redacted]", extraction!.RolloutSummary);
        Assert.Empty(extraction.RawMemory);
        Assert.Equal(CodexMemorySamplingOutcome.NotAdmitted,
            (await worker.RunStage1Async(admission, operation, Sample, value => value, cancellationToken: Ct)).Outcome);
        Assert.Equal(1, calls);

        var phase2 = Admission(await Claim(jobs, Key(), Guid.NewGuid()));
        var consolidationCalls = 0;
        async Task<CodexMemoryConsolidationOutput> Consolidate(CodexMemorySamplingAdmission _, CancellationToken ct)
        {
            consolidationCalls++;
            return new("verified-artifact", await storage.ListEligibleOutputsAsync(cancellationToken: ct));
        }
        var phase2Operation = Guid.NewGuid();
        var consolidated = await worker.RunPhase2Async(phase2, phase2Operation, Consolidate, cancellationToken: Ct);
        Assert.Equal(CodexMemorySamplingOutcome.Completed, consolidated.Outcome);
        Assert.Equal(new CodexMemoryConsolidationState("verified-artifact", 1), await storage.ReadConsolidationAsync(Ct));
        Assert.Equal(CodexMemorySamplingOutcome.NotAdmitted,
            (await worker.RunPhase2Async(phase2, Guid.NewGuid(), Consolidate, cancellationToken: Ct)).Outcome);
        Assert.Equal(1, consolidationCalls);
    }

    [Fact]
    public async Task LostSamplingAdmissionNeverDispatchesAfterRecovery()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        CodexMemorySamplingAdmission admission;
        var operation = Guid.NewGuid();
        var calls = 0;
        Task<string?> Sample(CodexMemorySamplingAdmission _, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult<string?>(null);
        }
        await using (var storage = await fixture.Open(clock, true))
        {
            Assert.True(await storage.PublishSourceAsync(new(thread, 1, true), Ct));
            var jobs = Jobs(storage, clock);
            admission = Admission(await Claim(jobs, Key(thread), Guid.NewGuid()));
            var worker = new CodexMemorySamplingWorker(jobs, new LoseSamplingAcknowledgment(storage), clock);
            await Assert.ThrowsAsync<IOException>(() => worker.RunStage1Async(admission, operation, Sample,
                value => value, cancellationToken: Ct));
        }
        await using var reopened = await fixture.Open(clock);
        var recovered = new CodexMemorySamplingWorker(Jobs(reopened, clock), reopened, clock);
        Assert.Equal(CodexMemorySamplingOutcome.NotAdmitted,
            (await recovered.RunStage1Async(admission, operation, Sample, value => value, cancellationToken: Ct)).Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LostWorkerSettlementReconcilesExactMutationWithoutResampling()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        CodexMemoryJobChange change;
        CodexMemorySamplingAdmission admission;
        var operation = Guid.NewGuid();
        var calls = 0;
        Task<string?> Sample(CodexMemorySamplingAdmission _, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult<string?>("{\"rollout_summary\":\"durable\",\"rollout_slug\":\"summary\"}");
        }
        await using (var storage = await fixture.Open(clock, true))
        {
            Assert.True(await storage.PublishSourceAsync(new(thread, 4, true), Ct));
            admission = Admission(await Claim(Jobs(storage, clock), Key(thread), Guid.NewGuid()));
            var worker = new CodexMemorySamplingWorker(Jobs(new LoseAcknowledgment(storage), clock), storage, clock);
            var error = await Assert.ThrowsAsync<CodexMemorySamplingSettlementException>(() =>
                worker.RunStage1Async(admission, operation, Sample, value => value, cancellationToken: Ct));
            change = error.Change;
            Assert.Equal(operation, change.OperationId);
        }
        await using var reopened = await fixture.Open(clock);
        var recovered = new CodexMemorySamplingWorker(Jobs(reopened, clock), reopened, clock);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await reopened.ReadReceiptAsync(Key(thread), operation, Ct))!.Settlement);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await reopened.ApplyAsync(change, Ct)).Settlement);
        Assert.Equal(CodexMemorySamplingOutcome.NotAdmitted,
            (await recovered.RunStage1Async(admission, operation, Sample, value => value, cancellationToken: Ct)).Outcome);
        Assert.Equal(1, calls);
        Assert.Equal("durable", (await reopened.ReadExtractionAsync(new(thread, 4), Ct))!.RolloutSummary);
    }

    [Fact]
    public async Task WorkerRejectsRevokedResultAndRecordsOnlySafeFailureClassification()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        Assert.True(await storage.PublishSourceAsync(new(thread, 1, true), Ct));
        var jobs = Jobs(storage, clock);
        var worker = new CodexMemorySamplingWorker(jobs, storage, clock);
        var admission = Admission(await Claim(jobs, Key(thread), Guid.NewGuid()));
        var result = await worker.RunStage1Async(admission, Guid.NewGuid(), async (_, ct) =>
        {
            Assert.True(await storage.PublishSourceAsync(new(thread, 2, false), ct));
            return "{\"rollout_summary\":\"do not retain\",\"rollout_slug\":\"sample\"}";
        }, value => value, cancellationToken: Ct);
        Assert.Equal(CodexMemorySamplingOutcome.Superseded, result.Outcome);
        Assert.Null(await storage.ReadExtractionAsync(new(thread, 1), Ct));

        clock.Advance(Lease);
        Assert.True(await storage.PublishSourceAsync(new(thread, 3, true), Ct));
        var next = Admission(await Claim(jobs, Key(thread), Guid.NewGuid()));
        var failed = await worker.RunStage1Async(next, Guid.NewGuid(),
            (_, _) => throw new IOException("provider-secret"), value => value, cancellationToken: Ct);
        Assert.Equal(CodexMemorySamplingOutcome.Failed, failed.Outcome);
        var state = (await storage.ReadAsync(Key(thread), Ct)).State!;
        Assert.Equal("sampling_failed", state.LastError);
        Assert.Equal(CodexMemoryJobStatus.Failed, state.Status);
    }

    [Fact]
    public async Task LegacySchemaUpgradePreservesOutputsAndReceiptsAndRefusesActiveClaims()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var completedThread = Guid.NewGuid();
        var runningThread = Guid.NewGuid();
        CodexMemoryJobChange completed;
        CodexMemorySamplingAdmission oldClaim;
        await using (var storage = await fixture.Open(clock, true))
        {
            completed = await Extract(storage, clock, completedThread, 7, "legacy output");
            Assert.True(await storage.PublishSourceAsync(new(runningThread, 8, true), Ct));
            oldClaim = Admission(await Claim(Jobs(storage, clock), Key(runningThread), Guid.NewGuid()));
        }

        // Reconstruct the exact schema-1 shape in this disposable fixture only.
        await fixture.MakeLegacySchemaAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open(clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Upgrade(clock));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open(clock));

        clock.Advance(Lease);
        await using (var upgraded = await fixture.Upgrade(clock))
        {
            Assert.Equal("legacy output", (await upgraded.ReadExtractionAsync(new(completedThread, 7), Ct))!.RawMemory);
            Assert.Equal(CodexMemoryJobSettlement.Applied,
                (await upgraded.ReadReceiptAsync(Key(completedThread), completed.OperationId, Ct))!.Settlement);
            Assert.False(await upgraded.TryAdmitSamplingAsync(oldClaim, Ct));
            var fresh = Admission(await Claim(Jobs(upgraded, clock), Key(runningThread), Guid.NewGuid()));
            Assert.True(await upgraded.TryAdmitSamplingAsync(fresh, Ct));
        }
        await using var reopened = await fixture.Open(clock);
        Assert.Equal(7L, (await reopened.ReadAsync(Key(completedThread), Ct)).OutputWatermark);
    }

    private static CodexMemoryStartupPolicy StartupPolicy(int scanLimit = 20, int maximumClaims = 8) =>
        new(["cli", "vscode"], TimeSpan.FromDays(30), TimeSpan.FromHours(1), maximumClaims, scanLimit);

    private static CodexMemorySourceUpdate Discover(Clock clock, Guid thread, long generation,
        double ageHours, string kind = "cli", bool enabled = true, bool archived = false) =>
        new(thread, generation, enabled, new(clock.GetUtcNow().AddHours(-ageHours), kind, archived));

    [Fact]
    public async Task StartupFiltersBeforeScanLimitAndProbesOnlySelectedCandidates()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var current = Guid.NewGuid();
        var upToDate = Guid.NewGuid();
        var older = Guid.NewGuid();
        var accesses = new List<CodexMemoryJobStorageAccess>();
        await using var storage = await fixture.Open(clock, true, (access, _) =>
        {
            accesses.Add(access);
            return ValueTask.CompletedTask;
        });
        await Extract(storage, clock, upToDate, 1, "already extracted");
        Assert.True(await storage.PublishSourceAsync(Discover(clock, upToDate, 1, 2), Ct));
        Assert.True(await storage.PublishSourceAsync(Discover(clock, older, 2, 3, "vscode"), Ct));
        Assert.True(await storage.PublishSourceAsync(Discover(clock, current, 1, 1.1), Ct));
        Assert.True(await storage.PublishSourceAsync(Discover(clock, Guid.NewGuid(), 1, 0.5), Ct));
        Assert.True(await storage.PublishSourceAsync(Discover(clock, Guid.NewGuid(), 1, 1.2, "subagent"), Ct));
        Assert.True(await storage.PublishSourceAsync(Discover(clock, Guid.NewGuid(), 1, 1.3, enabled: false), Ct));
        Assert.True(await storage.PublishSourceAsync(Discover(clock, Guid.NewGuid(), 1, 1.4, archived: true), Ct));
        Assert.True(await storage.PublishSourceAsync(Discover(clock, Guid.NewGuid(), 1, 31 * 24), Ct));
        Assert.True(await storage.PublishSourceAsync(new(Guid.NewGuid(), 1, true), Ct));

        accesses.Clear();
        var startup = new CodexMemoryStartup(Scope, Jobs(storage, clock), storage, clock);
        var first = await startup.ClaimAsync(current, StartupPolicy(1), Ct);
        Assert.Equal(1, first.Scanned);
        Assert.Equal(1, first.Probed);
        Assert.Empty(first.Claims);
        Assert.Equal(upToDate, Assert.Single(accesses, x => x.Operation == "read").Key.ThreadId);

        accesses.Clear();
        var next = await startup.ClaimAsync(current, StartupPolicy(2), Ct);
        Assert.Equal(2, next.Scanned);
        Assert.Equal(2, next.Probed);
        var granted = Assert.Single(next.Claims);
        Assert.Equal(older, granted.Candidate.ThreadId);
        Assert.Equal(current, granted.Change.Next.WorkerId);
        Assert.Equal(2L, granted.CreateAdmission(Guid.NewGuid()).ClaimedWatermark);
        Assert.Equal(2, accesses.Count(x => x.Operation == "read"));
        Assert.DoesNotContain(accesses, x => x.Operation == "read_output");
    }

    [Fact]
    public async Task StartupCandidateMetadataIsGenerationFencedAndSurvivesReopen()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        var current = Guid.NewGuid();
        var source = Discover(clock, thread, 8, 2);
        var scan = new CodexMemoryStartupScan(Scope, current, clock.GetUtcNow().AddDays(-30),
            clock.GetUtcNow().AddHours(-1), ["cli"], 10);
        await using (var first = await fixture.Open(clock, true))
        {
            Assert.True(await first.PublishSourceAsync(source, Ct));
            Assert.True(await first.PublishSourceAsync(source, Ct));
            Assert.True(await first.PublishSourceAsync(source with { Discovery = null }, Ct));
            Assert.False(await first.PublishSourceAsync(source with { Discovery = source.Discovery! with { Archived = true } }, Ct));
            Assert.Equal(thread, Assert.Single(await first.ScanStartupAsync(scan, Ct)).ThreadId);
        }
        await using var second = await fixture.Open(clock);
        Assert.Equal(8L, Assert.Single(await second.ScanStartupAsync(scan, Ct)).Watermark);
        Assert.True(await second.PublishSourceAsync(source with { Watermark = 9, Discovery = null }, Ct));
        Assert.Empty(await second.ScanStartupAsync(scan, Ct));
        Assert.False(await second.PublishSourceAsync(source, Ct));
        Assert.True(await second.PublishSourceAsync(source with { Watermark = 9 }, Ct));
        Assert.Single(await second.ScanStartupAsync(scan, Ct));
        Assert.True(await second.PublishSourceAsync(source with { Watermark = 10, Enabled = false }, Ct));
        Assert.Empty(await second.ScanStartupAsync(scan, Ct));
    }

    [Fact]
    public async Task SourceGenerationChangedAfterDiscoveryCannotBeClaimed()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        await using var publisher = await fixture.Open(clock);
        var original = Discover(clock, thread, 1, 2);
        Assert.True(await publisher.PublishSourceAsync(original, Ct));
        var source = new AfterScan(storage, async ct =>
        {
            Assert.True(await publisher.PublishSourceAsync(original with { Watermark = 2 }, ct));
        });
        var startup = new CodexMemoryStartup(Scope, Jobs(storage, clock), source, clock);
        var result = await startup.ClaimAsync(Guid.NewGuid(), StartupPolicy(), Ct);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Probed);
        Assert.Empty(result.Claims);
        Assert.Null((await storage.ReadAsync(Key(thread), Ct)).State);
    }

    [Fact]
    public async Task ConcurrentStartupsRespectGlobalCapAndDoNotReadOriginalHistory()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        await using var first = await fixture.Open(clock, true);
        await using var second = await fixture.Open(clock);
        for (var i = 0; i < 4; i++)
            Assert.True(await first.PublishSourceAsync(Discover(clock, Guid.NewGuid(), 1, 2 + i), Ct));
        var a = new CodexMemoryStartup(Scope, Jobs(first, clock), first, clock);
        var b = new CodexMemoryStartup(Scope, Jobs(second, clock), second, clock);
        var policy = StartupPolicy(maximumClaims: 2);
        var results = await Task.WhenAll(a.ClaimAsync(Guid.NewGuid(), policy, Ct), b.ClaimAsync(Guid.NewGuid(), policy, Ct));
        var claims = results.SelectMany(x => x.Claims).ToArray();
        Assert.Equal(2, claims.Length);
        Assert.Equal(2, claims.Select(x => x.Candidate.ThreadId).Distinct().Count());
    }

    [Fact]
    public async Task ZeroStartupLimitsAndUnapprovedSourcesRequireNoStorageProbe()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var accesses = new List<CodexMemoryJobStorageAccess>();
        await using var storage = await fixture.Open(clock, true, (access, _) =>
        {
            accesses.Add(access);
            return ValueTask.CompletedTask;
        });
        var startup = new CodexMemoryStartup(Scope, Jobs(storage, clock), storage, clock);
        Assert.Equal(0, (await startup.ClaimAsync(Guid.NewGuid(), StartupPolicy(0), Ct)).Scanned);
        Assert.Equal(0, (await startup.ClaimAsync(Guid.NewGuid(), StartupPolicy(maximumClaims: 0), Ct)).Scanned);
        Assert.Equal(0, (await startup.ClaimAsync(Guid.NewGuid(), StartupPolicy() with { AllowedSources = [] }, Ct)).Scanned);
        Assert.Empty(accesses);
    }

    [Fact]
    public async Task Schema2UpgradeBackfillsDiscoveryOnlyFromAuthoritativeMetadata()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        CodexMemoryJobChange completed;
        await using (var storage = await fixture.Open(clock, true))
            completed = await Extract(storage, clock, thread, 9, "legacy extraction");
        await fixture.MakeLegacySchemaAsync(2);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open(clock));
        await using var upgraded = await fixture.Upgrade(clock);
        var scan = new CodexMemoryStartupScan(Scope, Guid.NewGuid(), clock.GetUtcNow().AddDays(-30),
            clock.GetUtcNow().AddHours(-1), ["cli"], 10);
        Assert.Empty(await upgraded.ScanStartupAsync(scan, Ct));
        Assert.Equal("legacy extraction", (await upgraded.ReadExtractionAsync(new(thread, 9), Ct))!.RawMemory);
        Assert.Equal(CodexMemoryJobSettlement.Applied,
            (await upgraded.ReadReceiptAsync(Key(thread), completed.OperationId, Ct))!.Settlement);
        Assert.True(await upgraded.PublishSourceAsync(Discover(clock, thread, 9, 2), Ct));
        Assert.Equal(thread, Assert.Single(await upgraded.ScanStartupAsync(scan, Ct)).ThreadId);
    }

    private const string LocalSummary = "v1\n## User Profile\nUser\n## User preferences\nPreference\n## General Tips\nTip\n## What's in Memory\nSource\n";

    private static CodexMemoryBackend LocalBackend(CodexMacOsSqliteMemoryJobStorage storage, Guid thread) =>
        new(storage, Scope, thread, () => new(true, true, true, CodexMemoryVersion.V2), (_, _) => ValueTask.CompletedTask);

    [Fact]
    public async Task LocalArtifactIsVisibleOnlyAfterExactPhase2SettlementAndSurvivesReopen()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var consumer = Guid.NewGuid();
        var firstSource = Guid.NewGuid();
        var secondSource = Guid.NewGuid();
        CodexMemoryConsolidationOutput output;
        await using (var storage = await fixture.Open(clock, true))
        {
            await Extract(storage, clock, firstSource, 1, "first");
            await Extract(storage, clock, secondSource, 2, "second");
            Assert.True(await storage.PublishMemoryIoPolicyAsync(new(consumer, 1, true, true, true), Ct));
            var backend = LocalBackend(storage, consumer);
            Assert.Null(await backend.ReadRpcSummaryAsync(Ct));
            var jobs = Jobs(storage, clock);
            var admission = Admission(await Claim(jobs, Key(), Guid.NewGuid()));
            var selected = await storage.ListEligibleOutputsAsync(cancellationToken: Ct);
            CodexMemoryFile[] files =
            [
                new("memory_summary.md", LocalSummary),
                new("MEMORY.md", "overview"),
                new("rollout_summaries/z.md", "needle Z"),
                new("rollout_summaries/a.md", "needle A"),
                new("skills/empty.md", ""),
                new("extensions/ad_hoc/instructions.md", "notes guide"),
            ];
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.StageMemoryArtifactAsync(admission, selected, files, Ct));
            Assert.True(await storage.TryAdmitSamplingAsync(admission, Ct));
            output = await storage.StageMemoryArtifactAsync(admission, selected, files, Ct);
            var repeated = await storage.StageMemoryArtifactAsync(admission, selected.Reverse().ToArray(), files.Reverse().ToArray(), Ct);
            Assert.Equal(output.ArtifactRevision, repeated.ArtifactRevision);
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.StageMemoryArtifactAsync(admission,
                selected, [.. files, new("extra.md", "different")], Ct));

            Assert.Null(await backend.ReadRpcSummaryAsync(Ct));
            Assert.Empty((await backend.ListAsync(cancellationToken: Ct)).Entries);
            Assert.False((await storage.ReadLocalArtifactStatusAsync(Ct)).HasValidV2Summary);

            var observed = await jobs.ReadAsync(Key(), Ct);
            var wrongSelection = jobs.PreparePhase2Success(observed, admission.WorkerId, admission.OwnershipToken,
                Guid.NewGuid(), output with { Selected = [selected[0]] });
            Assert.Equal(CodexMemoryJobSettlement.Conflict, (await jobs.ApplyAsync(wrongSelection, Ct)).Settlement);
            var forged = jobs.PreparePhase2Success(observed, admission.WorkerId, admission.OwnershipToken,
                Guid.NewGuid(), output with { ArtifactRevision = "sqlite-memory-v1:missing" });
            Assert.Equal(CodexMemoryJobSettlement.Conflict, (await jobs.ApplyAsync(forged, Ct)).Settlement);
            var complete = jobs.PreparePhase2Success(observed, admission.WorkerId, admission.OwnershipToken,
                Guid.NewGuid(), output);
            Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(complete, Ct)).Settlement);
            Assert.Equal(new CodexMemoryLocalArtifactStatus(output.ArtifactRevision, 2, true),
                await storage.ReadLocalArtifactStatusAsync(Ct));
        }

        await using var reopened = await fixture.Open(clock);
        var reader = LocalBackend(reopened, consumer);
        Assert.Equal(LocalSummary, await reader.ReadRpcSummaryAsync(Ct));
        Assert.Equal("", (await reader.ReadAsync("skills/empty.md", cancellationToken: Ct)).Content);
        var page = await reader.ListAsync("rollout_summaries", maxResults: 1, cancellationToken: Ct);
        Assert.Equal(new CodexMemoryEntry("rollout_summaries/a.md", false), Assert.Single(page.Entries));
        Assert.NotNull(page.NextCursor);
        var next = await reader.ListAsync("rollout_summaries", page.NextCursor, 1, Ct);
        Assert.Equal(new CodexMemoryEntry("rollout_summaries/z.md", false), Assert.Single(next.Entries));
        Assert.Null(next.NextCursor);
        var request = new CodexMemorySearchRequest(["needle"], Path: "rollout_summaries", MaxResults: 1);
        var search = await reader.SearchAsync(request, Ct);
        Assert.Equal("rollout_summaries/a.md", Assert.Single(search.Matches).Path);
        Assert.NotNull(search.NextCursor);
        var rest = await reader.SearchAsync(request with { Cursor = search.NextCursor }, Ct);
        Assert.Equal("rollout_summaries/z.md", Assert.Single(rest.Matches).Path);
        Assert.Null(rest.NextCursor);
        await using var snapshot = await reopened.OpenReadAsync(new(Scope, consumer, "read", "MEMORY.md"), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await snapshot.ReadAsync("memory_summary.md", 1024, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await snapshot.ReadAsync("MEMORY.md", 2, Ct));
        Assert.Equal("overview", (await snapshot.ReadAsync("MEMORY.md", 1024, Ct)).Content);
    }

    [Fact]
    public async Task NoteCreationAndPolicyChangesFenceOpenSnapshotsWithoutOverwriting()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        var accesses = new List<CodexMemoryJobStorageAccess>();
        await using var storage = await fixture.Open(clock, true, (access, _) =>
        {
            accesses.Add(access);
            return ValueTask.CompletedTask;
        });
        await using var other = await fixture.Open(clock);
        var backend = LocalBackend(storage, thread);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => backend.ReadRpcSummaryAsync(Ct));
        Assert.True(await storage.PublishMemoryIoPolicyAsync(new(thread, 1, true, false, false), Ct));
        Assert.Null(await backend.ReadRpcSummaryAsync(Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => backend.ListAsync(cancellationToken: Ct));
        Assert.False(await storage.PublishMemoryIoPolicyAsync(new(thread, 1, true, true, true), Ct));
        Assert.True(await storage.PublishMemoryIoPolicyAsync(new(thread, 2, true, true, true), Ct));
        await using var before = await storage.OpenReadAsync(new(Scope, thread, "list", ""), Ct);
        Assert.Empty(await before.ListAsync("", null, 10, Ct));

        const string filename = "2026-11-27T12-00-00-preference.md";
        const string path = "extensions/ad_hoc/notes/" + filename;
        var note = new CodexMemoryAccess(Scope, thread, "add_ad_hoc_note", path);
        Assert.True(await storage.CreateNoteAsync(note, "original", Ct));
        var authorized = Assert.Single(accesses, access => access.Operation == "memory_create_note");
        Assert.Equal(note, authorized.MemoryAccess);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("original"u8)), authorized.NoteSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await before.ListAsync("", null, 10, Ct));
        await using var current = await storage.OpenReadAsync(new(Scope, thread, "read", path), Ct);
        Assert.False(await other.CreateNoteAsync(note, "replacement", Ct));
        Assert.Equal("original", (await current.ReadAsync(path, 100, Ct)).Content);
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.CreateNoteAsync(note with { Path = "extensions/ad_hoc/notes/invalid.md" }, "bad", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await storage.CreateNoteAsync(note with { Path = "MEMORY.md" }, "bad", Ct));

        Assert.True(await other.PublishMemoryIoPolicyAsync(new(thread, 3, false, false, false), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await current.ReadAsync(path, 100, Ct));
        Assert.False(await other.PublishMemoryIoPolicyAsync(new(thread, 2, true, true, true), Ct));
        Assert.True(await other.PublishMemoryIoPolicyAsync(new(thread, 4, true, true, true), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await current.ReadAsync(path, 100, Ct));
        Assert.Equal("original", (await LocalBackend(other, thread).ReadAsync(path, cancellationToken: Ct)).Content);
        var roots = await LocalBackend(other, thread).ListAsync(cancellationToken: Ct);
        Assert.Equal(new CodexMemoryEntry("extensions", true), Assert.Single(roots.Entries));
    }

    [Fact]
    public async Task SourceRevocationHidesPublishedArtifactAndFencesSnapshots()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var source = Guid.NewGuid();
        var consumer = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        await using var publisher = await fixture.Open(clock);
        await Extract(storage, clock, source, 1, "revocable");
        Assert.True(await storage.PublishMemoryIoPolicyAsync(new(consumer, 1, true, true, false), Ct));
        var jobs = Jobs(storage, clock);
        var admission = Admission(await Claim(jobs, Key(), Guid.NewGuid()));
        Assert.True(await storage.TryAdmitSamplingAsync(admission, Ct));
        var selected = await storage.ListEligibleOutputsAsync(cancellationToken: Ct);
        var output = await storage.StageMemoryArtifactAsync(admission, selected,
            [new("memory_summary.md", LocalSummary), new("MEMORY.md", "revocable")], Ct);
        var complete = jobs.PreparePhase2Success(await jobs.ReadAsync(Key(), Ct), admission.WorkerId,
            admission.OwnershipToken, Guid.NewGuid(), output);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(complete, Ct)).Settlement);
        await using var snapshot = await storage.OpenReadAsync(new(Scope, consumer, "read", "MEMORY.md"), Ct);
        Assert.Equal("revocable", (await snapshot.ReadAsync("MEMORY.md", 100, Ct)).Content);
        Assert.True(await publisher.PublishSourceAsync(new(source, 2, false), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadAsync("MEMORY.md", 100, Ct));
        Assert.Null(await LocalBackend(storage, consumer).ReadRpcSummaryAsync(Ct));
        Assert.Equal(new CodexMemoryLocalArtifactStatus(null, 1, false), await storage.ReadLocalArtifactStatusAsync(Ct));
    }

    [Fact]
    public async Task StagedArtifactCannotCrossClaimsAndInvalidPathsNeverBecomeVisible()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var source = Guid.NewGuid();
        await using var storage = await fixture.Open(clock, true);
        await Extract(storage, clock, source, 1, "source");
        var jobs = Jobs(storage, clock);
        var admission = Admission(await Claim(jobs, Key(), Guid.NewGuid()));
        Assert.True(await storage.TryAdmitSamplingAsync(admission, Ct));
        var selected = await storage.ListEligibleOutputsAsync(cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.StageMemoryArtifactAsync(admission, selected,
            [new("memory_summary.md", "invalid")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.StageMemoryArtifactAsync(admission, selected,
            [new("memory_summary.md", LocalSummary), new("../escape.md", "bad")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.StageMemoryArtifactAsync(admission, selected,
            [new("memory_summary.md", LocalSummary), new("extensions/ad_hoc/notes/a.md", "bad")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.StageMemoryArtifactAsync(admission, selected,
            [new("memory_summary.md", LocalSummary), new("a", "file"), new("a/b", "conflict")], Ct));
        var output = await storage.StageMemoryArtifactAsync(admission, selected, [new("memory_summary.md", LocalSummary)], Ct);
        clock.Advance(Lease);
        var fresh = Admission(await Claim(jobs, Key(), Guid.NewGuid()));
        Assert.True(await storage.TryAdmitSamplingAsync(fresh, Ct));
        var oldOutput = jobs.PreparePhase2Success(await jobs.ReadAsync(Key(), Ct), fresh.WorkerId,
            fresh.OwnershipToken, Guid.NewGuid(), output);
        Assert.Equal(CodexMemoryJobSettlement.Conflict, (await jobs.ApplyAsync(oldOutput, Ct)).Settlement);
        Assert.Null((await storage.ReadLocalArtifactStatusAsync(Ct)).ArtifactRevision);
    }

    [Fact]
    public async Task Schema3UpgradePreservesDiscoveryAndRequiresExplicitIoPermission()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        var update = Discover(clock, thread, 7, 2);
        await using (var storage = await fixture.Open(clock, true))
        {
            await Extract(storage, clock, thread, 7, "legacy");
            Assert.True(await storage.PublishSourceAsync(update, Ct));
        }
        await fixture.MakeLegacySchemaAsync(3);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open(clock));
        await using var upgraded = await fixture.Upgrade(clock);
        Assert.Equal("legacy", (await upgraded.ReadExtractionAsync(new(thread, 7), Ct))!.RawMemory);
        var scan = new CodexMemoryStartupScan(Scope, Guid.NewGuid(), clock.GetUtcNow().AddDays(-30),
            clock.GetUtcNow().AddHours(-1), ["cli"], 10);
        Assert.Equal(thread, Assert.Single(await upgraded.ScanStartupAsync(scan, Ct)).ThreadId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => LocalBackend(upgraded, thread).ReadRpcSummaryAsync(Ct));
        Assert.True(await upgraded.PublishMemoryIoPolicyAsync(new(thread, 1, true, true, false), Ct));
        Assert.Null(await LocalBackend(upgraded, thread).ReadRpcSummaryAsync(Ct));
    }

    [Fact]
    public async Task Schema4UpgradeBackfillsRetentionWithoutInventingSourceTimestamps()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        CodexMemoryJobChange completed;
        await using (var storage = await fixture.Open(clock, true))
        {
            completed = await Extract(storage, clock, known, 7, "known");
            Assert.True(await storage.PublishSourceAsync(Discover(clock, known, 7, 24 * 40), Ct));
            await Extract(storage, clock, unknown, 8, "unknown time");
        }
        await fixture.MakeLegacySchemaAsync(4);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open(clock));
        await using var upgraded = await fixture.Upgrade(clock);
        Assert.Equal(0L, (await upgraded.ReadAsync(Key(), Ct)).ResetEpoch);
        Assert.Equal(unknown, Assert.Single(await upgraded.SelectPhase2InputsAsync(clock.GetUtcNow().AddDays(-30), cancellationToken: Ct)).ThreadId);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await upgraded.ApplyAsync(completed, Ct)).Settlement);
        Assert.Equal("known", (await upgraded.ReadExtractionAsync(new(known, 7), Ct))!.RawMemory);
    }

    private sealed class AfterScan(ICodexMemoryStartupSource inner, Func<CancellationToken, Task> change) : ICodexMemoryStartupSource
    {
        public async ValueTask<IReadOnlyList<CodexMemoryStartupCandidate>> ScanStartupAsync(
            CodexMemoryStartupScan scan, CancellationToken ct)
        {
            var result = await inner.ScanStartupAsync(scan, ct);
            await change(ct);
            return result;
        }
    }

    private sealed class LoseSamplingAcknowledgment(ICodexMemorySamplingStorage inner) : ICodexMemorySamplingStorage
    {
        public async ValueTask<bool> TryAdmitSamplingAsync(CodexMemorySamplingAdmission admission, CancellationToken ct)
        {
            if (await inner.TryAdmitSamplingAsync(admission, ct))
                throw new IOException("Lost sampling admission acknowledgment.");
            return false;
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-memory-sqlite-").FullName);
        private string DatabasePath => Path.Combine(root, "memory.sqlite");
        internal Task<CodexMacOsSqliteMemoryJobStorage> Open(Clock clock, bool create = false,
            Func<CodexMemoryJobStorageAccess, CancellationToken, ValueTask>? authorize = null, CodexMemoryScope? scope = null) =>
            CodexMacOsSqliteMemoryJobStorage.OpenAsync(DatabasePath, scope ?? Scope,
                authorize ?? ((_, _) => ValueTask.CompletedTask), create, clock, Ct);
        internal Task<CodexMacOsSqliteMemoryJobStorage> Upgrade(Clock clock) =>
            CodexMacOsSqliteMemoryJobStorage.UpgradeLegacyAsync(DatabasePath, Scope,
                (_, _) => ValueTask.CompletedTask, clock, Ct);

        internal async Task MakeLegacySchemaAsync(int version = 1)
        {
            const string removeMaintenance = "BEGIN IMMEDIATE; DROP TABLE memory_usage_receipt; " +
                "DROP TABLE memory_lifecycle_receipt; DROP TABLE memory_retention; DROP TABLE memory_mode; DROP TABLE memory_epoch; ";
            const string removeArtifacts = removeMaintenance + "DROP TRIGGER memory_pointer_changed; " +
                "DROP TABLE memory_artifact_source; DROP TABLE memory_entry; DROP TABLE memory_manifest; " +
                "DROP TABLE memory_io_policy; DROP TABLE memory_view; ";
            var sql = version switch
            {
                1 => removeArtifacts + "DROP TABLE discovery; DROP TABLE sampling; PRAGMA user_version=1; COMMIT;",
                2 => removeArtifacts + "DROP TABLE discovery; PRAGMA user_version=2; COMMIT;",
                3 => removeArtifacts + "PRAGMA user_version=3; COMMIT;",
                4 => removeMaintenance + "PRAGMA user_version=4; COMMIT;",
                _ => throw new ArgumentOutOfRangeException(nameof(version)),
            };
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/sqlite3")
            {
                ArgumentList = { "-batch", "-bail", DatabasePath, sql },
                UseShellExecute = false,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("SQLite fixture process failed to start.");
            try
            {
                var error = await process.StandardError.ReadToEndAsync(Ct);
                await process.WaitForExitAsync(Ct);
                Assert.True(process.ExitCode == 0, error);
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        public void Dispose() => Directory.Delete(root, true);
    }

    private sealed class LoseAcknowledgment(ICodexMemoryJobStorage inner) : ICodexMemoryJobStorage
    {
        public ValueTask<CodexMemoryJobObservation> ReadAsync(CodexMemoryJobKey key, CancellationToken ct) =>
            inner.ReadAsync(key, ct);
        public async ValueTask<CodexMemoryJobReceipt> ApplyAsync(CodexMemoryJobChange change, CancellationToken ct)
        {
            var receipt = await inner.ApplyAsync(change, ct);
            if (receipt.Settlement == CodexMemoryJobSettlement.Applied) throw new IOException("Lost commit acknowledgment.");
            return receipt;
        }
        public ValueTask<CodexMemoryJobReceipt?> ReadReceiptAsync(CodexMemoryJobKey key, Guid id, CancellationToken ct) =>
            inner.ReadReceiptAsync(key, id, ct);
    }
}
