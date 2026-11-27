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

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-memory-sqlite-").FullName);
        internal Task<CodexMacOsSqliteMemoryJobStorage> Open(Clock clock, bool create = false,
            Func<CodexMemoryJobStorageAccess, CancellationToken, ValueTask>? authorize = null, CodexMemoryScope? scope = null) =>
            CodexMacOsSqliteMemoryJobStorage.OpenAsync(Path.Combine(root, "memory.sqlite"), scope ?? Scope,
                authorize ?? ((_, _) => ValueTask.CompletedTask), create, clock, Ct);
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
