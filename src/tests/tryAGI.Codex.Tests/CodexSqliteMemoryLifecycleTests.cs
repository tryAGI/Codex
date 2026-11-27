using System.Diagnostics;

namespace tryAGI.Codex.Tests;

public sealed class CodexSqliteMemoryLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Account = "memory-account";
    private static readonly CodexMemoryScope V1 = new(Account, CodexMemoryVersion.V1);
    private static readonly CodexMemoryScope V2 = new(Account, CodexMemoryVersion.V2);
    private const string Summary = "v1\n## User Profile\nUser\n## User preferences\nPreference\n## General Tips\nTip\n## What's in Memory\nSource\n";
    private const string NoteName = "2026-11-27T12-00-00-test.md";
    private const string NotePath = "extensions/ad_hoc/notes/" + NoteName;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static void RequireMac() => Assert.SkipUnless(OperatingSystem.IsMacOS(), "Actual system SQLite requires macOS.");
    private static CodexMemoryJobKey Key(CodexMemoryScope scope, Guid? thread = null) => new(scope, thread);
    private static CodexMemoryJobs Jobs(CodexMacOsSqliteMemoryJobStorage storage, CodexMemoryScope scope, Clock clock) =>
        new(scope, storage, (_, _) => ValueTask.CompletedTask, clock);
    private static CodexMemoryBackend Backend(CodexMacOsSqliteMemoryJobStorage storage, CodexMemoryScope scope, Guid thread) =>
        new(storage, scope, thread, () => new(true, true, true, scope.Version), (_, _) => ValueTask.CompletedTask);

    private static async Task<CodexMemoryJobChange> Claim(CodexMemoryJobs jobs, CodexMemoryJobKey key)
    {
        var plan = jobs.PrepareClaim(await jobs.ReadAsync(key, Ct), Guid.NewGuid(), Guid.NewGuid(), Lease);
        Assert.Equal(CodexMemoryJobClaimOutcome.Prepared, plan.Outcome);
        var change = Assert.IsType<CodexMemoryJobChange>(plan.Change);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(change, Ct)).Settlement);
        return change;
    }

    private static async Task<CodexMemoryJobChange> Extract(CodexMacOsSqliteMemoryJobStorage storage, CodexMemoryScope scope,
        Clock clock, Guid thread, long generation = 1, int ageDays = 1, string text = "output")
    {
        Assert.True(await storage.PublishSourceAsync(new(thread, generation, true,
            new(clock.GetUtcNow().AddDays(-ageDays), "cli")), Ct));
        var jobs = Jobs(storage, scope, clock);
        var claim = await Claim(jobs, Key(scope, thread));
        var change = jobs.PrepareStage1Success(await jobs.ReadAsync(claim.Key, Ct), claim.Next.WorkerId!.Value,
            claim.OperationId, Guid.NewGuid(), new(generation, scope.Version == CodexMemoryVersion.V1 ? text : "", text, "summary"));
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(change, Ct)).Settlement);
        return change;
    }

    private static CodexMemorySamplingAdmission Admission(CodexMemoryJobChange claim) =>
        new(claim.Key, claim.Next.WorkerId!.Value, claim.OperationId, claim.Next.ClaimedWatermark!.Value, Guid.NewGuid());

    private static async Task<CodexMemoryConsolidationOutput> Publish(CodexMacOsSqliteMemoryJobStorage storage,
        CodexMemoryScope scope, Clock clock, IReadOnlyList<CodexMemorySelectedSnapshot> selected)
    {
        var jobs = Jobs(storage, scope, clock);
        var claim = Admission(await Claim(jobs, Key(scope)));
        Assert.True(await storage.TryAdmitSamplingAsync(claim, Ct));
        var output = await storage.StageMemoryArtifactAsync(claim, selected,
            [new("memory_summary.md", Summary), new("MEMORY.md", "artifact")], Ct);
        var change = jobs.PreparePhase2Success(await jobs.ReadAsync(claim.Key, Ct), claim.WorkerId,
            claim.OwnershipToken, Guid.NewGuid(), output);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(change, Ct)).Settlement);
        return output;
    }

    [Fact]
    public async Task AccountResetFencesBothNamespacesAndPreservesModesAndReceipts()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var source = Guid.NewGuid();
        var consumer = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        var running = Guid.NewGuid();
        var notYetClaimed = Guid.NewGuid();
        var reset = Guid.NewGuid();
        CodexMemoryJobChange oldCompletion;
        CodexMemoryJobChange prepared;
        CodexMemoryJobChange oldExtraction;
        await using (var current = await fixture.Open(V2, clock, true))
        await using (var legacy = await fixture.Open(V1, clock, true))
        {
            oldExtraction = await Extract(current, V2, clock, source);
            await Extract(legacy, V1, clock, source);
            foreach (var (store, scope) in new[] { (current, V2), (legacy, V1) })
            {
                await Publish(store, scope, clock, [new(source, 1)]);
                Assert.True(await store.PublishMemoryIoPolicyAsync(new(consumer, 1, true, true, true), Ct));
                await Backend(store, scope, consumer).AddAdHocNoteAsync(NoteName, "note", Ct);
                Assert.True(await store.PublishSourceAsync(new(disabled, 1, true), Ct));
            }

            var lifecycle = current.CreateLocalLifecycleStorage(legacy);
            var rpc = new CodexMemoryRpc(Account, (_, _) => ValueTask.FromResult<CodexMemoryBackend?>(null),
                (_, _) => ValueTask.CompletedTask, lifecycle);
            Assert.True((await rpc.StatusAsync(1, Ct)).V2Ready);
            await rpc.SetThreadModeAsync(disabled, CodexThreadMemoryMode.Disabled, Guid.NewGuid(), Ct);
            var jobs = Jobs(current, V2, clock);
            Assert.True(await current.PublishSourceAsync(new(running, 1, true), Ct));
            var claim = await Claim(jobs, Key(V2, running));
            Assert.True(await current.TryAdmitSamplingAsync(Admission(claim), Ct));
            oldCompletion = jobs.PrepareStage1Success(await jobs.ReadAsync(claim.Key, Ct), claim.Next.WorkerId!.Value,
                claim.OperationId, Guid.NewGuid(), new(1, "", "late result", "late"));
            Assert.True(await current.PublishSourceAsync(new(notYetClaimed, 1, true), Ct));
            prepared = jobs.PrepareClaim(await jobs.ReadAsync(Key(V2, notYetClaimed), Ct), Guid.NewGuid(), Guid.NewGuid(), Lease).Change!;

            await using var snapshot = await legacy.OpenReadAsync(new(V1, consumer, "read", "MEMORY.md"), Ct);
            Assert.Equal("artifact", (await snapshot.ReadAsync("MEMORY.md", 100, Ct)).Content);
            await rpc.ResetAsync(reset, Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadAsync("MEMORY.md", 100, Ct));
            Assert.False((await rpc.StatusAsync(1, Ct)).V2Ready);
            foreach (var (store, scope) in new[] { (current, V2), (legacy, V1) })
            {
                Assert.Equal(1L, (await store.ReadAsync(Key(scope), Ct)).ResetEpoch);
                Assert.Null((await store.ReadAsync(Key(scope, source), Ct)).State);
                Assert.Empty(await store.ListEligibleOutputsAsync(cancellationToken: Ct));
                Assert.Equal(CodexThreadMemoryMode.Disabled, await store.ReadThreadMemoryModeAsync(disabled, Ct));
                Assert.False((await store.ReadAsync(Key(scope, disabled), Ct)).SourceEnabled);
                Assert.Empty((await Backend(store, scope, consumer).ListAsync(cancellationToken: Ct)).Entries);
                Assert.Null(await Backend(store, scope, consumer).ReadRpcSummaryAsync(Ct));
                Assert.Equal(new CodexMemoryConsolidationState(null, 0), await store.ReadConsolidationAsync(Ct));
            }
            Assert.Equal(CodexMemoryJobSettlement.Conflict, (await current.ApplyAsync(oldCompletion, Ct)).Settlement);
            Assert.Equal(CodexMemoryJobSettlement.Conflict, (await current.ApplyAsync(prepared, Ct)).Settlement);
            // Historical receipts remain reconcilable, but do not recreate jobs or outputs.
            Assert.Equal(CodexMemoryJobSettlement.Applied, (await current.ApplyAsync(oldExtraction, Ct)).Settlement);
            Assert.Null(await current.ReadExtractionAsync(new(source, 1), Ct));
            var fresh = await Claim(jobs, Key(V2, notYetClaimed));
            Assert.Equal(1L, fresh.ResetEpoch);
            Assert.True(await current.TryAdmitSamplingAsync(Admission(fresh), Ct));
        }

        await using var reopened = await fixture.Open(V2, clock);
        await using var old = await fixture.Open(V1, clock);
        var restored = reopened.CreateLocalLifecycleStorage(old);
        var resetAccess = new CodexMemoryRpcAccess(Account, "memory/reset", OperationId: reset);
        Assert.Equal(CodexMemoryLifecycleSettlement.Applied, (await restored.ApplyAsync(resetAccess, Ct)).Settlement);
        Assert.Equal(1L, (await reopened.ReadAsync(Key(V2), Ct)).ResetEpoch);
        Assert.NotNull((await reopened.ReadAsync(Key(V2, notYetClaimed), Ct)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await restored.ApplyAsync(
            new(Account, "thread/memoryMode/set", disabled, CodexThreadMemoryMode.Enabled, reset), Ct));
    }

    [Fact]
    public async Task SecondNamespaceFailureRollsBackTheEntireReset()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        await using var current = await fixture.Open(V2, clock, true);
        await using var legacy = await fixture.Open(V1, clock, true);
        await Extract(current, V2, clock, thread);
        await Extract(legacy, V1, clock, thread);
        await fixture.ExecuteFixtureSql(V1,
            "CREATE TRIGGER fixture_reset_failure BEFORE DELETE ON job BEGIN SELECT RAISE(ABORT,'fixture failure'); END;");
        var lifecycle = current.CreateLocalLifecycleStorage(legacy);
        var access = new CodexMemoryRpcAccess(Account, "memory/reset", OperationId: Guid.NewGuid());
        await Assert.ThrowsAsync<IOException>(async () => await lifecycle.ApplyAsync(access, Ct));
        foreach (var (store, scope) in new[] { (current, V2), (legacy, V1) })
        {
            Assert.Equal(0L, (await store.ReadAsync(Key(scope), Ct)).ResetEpoch);
            Assert.NotNull(await store.ReadExtractionAsync(new(thread, 1), Ct));
            Assert.Equal(CodexMemoryJobStatus.Succeeded, (await store.ReadAsync(Key(scope, thread), Ct)).State!.Status);
        }
        await fixture.ExecuteFixtureSql(V1, "DROP TRIGGER fixture_reset_failure;");
        Assert.Equal(CodexMemoryLifecycleSettlement.Applied, (await lifecycle.ApplyAsync(access, Ct)).Settlement);
        Assert.Null(await current.ReadExtractionAsync(new(thread, 1), Ct));
        Assert.Null(await legacy.ReadExtractionAsync(new(thread, 1), Ct));
    }

    [Fact]
    public async Task MemoryModeFencesStalePlansWithoutOverridingHostSourceEligibility()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var thread = Guid.NewGuid();
        var archived = Guid.NewGuid();
        await using var current = await fixture.Open(V2, clock, true);
        var lifecycle = current.CreateLocalLifecycleStorage();
        Assert.True((await lifecycle.ReadReadinessAsync(Account, Ct)).HasV1Workspace == false);
        Assert.True(CodexMemoryReadiness.Evaluate(await lifecycle.ReadReadinessAsync(Account, Ct)).V2Ready);
        Assert.True(await current.PublishSourceAsync(new(thread, 1, true), Ct));
        Assert.True(await current.PublishSourceAsync(new(archived, 1, true, new(clock.GetUtcNow(), "cli", true)), Ct));
        var jobs = Jobs(current, V2, clock);
        var before = jobs.PrepareClaim(await jobs.ReadAsync(Key(V2, thread), Ct), Guid.NewGuid(), Guid.NewGuid(), Lease).Change!;
        var disable = new CodexMemoryRpcAccess(Account, "thread/memoryMode/set", thread, CodexThreadMemoryMode.Disabled, Guid.NewGuid());
        Assert.Equal(CodexMemoryLifecycleSettlement.Applied, (await lifecycle.ApplyAsync(disable, Ct)).Settlement);
        Assert.Equal(CodexThreadMemoryMode.Disabled, await current.ReadThreadMemoryModeAsync(thread, Ct));
        Assert.True(await current.PublishSourceAsync(new(thread, 2, true), Ct));
        Assert.Equal(CodexMemoryJobClaimOutcome.Disabled, jobs.PrepareClaim(await jobs.ReadAsync(Key(V2, thread), Ct),
            Guid.NewGuid(), Guid.NewGuid(), Lease).Outcome);
        Assert.True(await current.PublishSourceAsync(new(thread, 3, false), Ct));
        var enable = disable with { Mode = CodexThreadMemoryMode.Enabled, OperationId = Guid.NewGuid() };
        Assert.Equal(CodexMemoryLifecycleSettlement.Applied, (await lifecycle.ApplyAsync(enable, Ct)).Settlement);
        Assert.Equal(CodexThreadMemoryMode.Enabled, await current.ReadThreadMemoryModeAsync(thread, Ct));
        Assert.False((await current.ReadAsync(Key(V2, thread), Ct)).SourceEnabled);
        Assert.Equal(CodexMemoryJobSettlement.Conflict, (await current.ApplyAsync(before, Ct)).Settlement);
        Assert.Equal(CodexMemoryLifecycleSettlement.NotFound, (await lifecycle.ApplyAsync(
            disable with { ThreadId = archived, OperationId = Guid.NewGuid() }, Ct)).Settlement);
        Assert.Equal(CodexMemoryLifecycleSettlement.NotFound, (await lifecycle.ApplyAsync(
            disable with { ThreadId = Guid.NewGuid(), OperationId = Guid.NewGuid() }, Ct)).Settlement);

        // An exact source generation still cannot revive a prepared claim across a mode toggle.
        Assert.True(await current.PublishSourceAsync(new(thread, 4, true), Ct));
        var stale = jobs.PrepareClaim(await jobs.ReadAsync(Key(V2, thread), Ct), Guid.NewGuid(), Guid.NewGuid(), Lease).Change!;
        await lifecycle.ApplyAsync(disable with { OperationId = Guid.NewGuid() }, Ct);
        await lifecycle.ApplyAsync(enable with { OperationId = Guid.NewGuid() }, Ct);
        Assert.Equal(CodexMemoryJobSettlement.Conflict, (await current.ApplyAsync(stale, Ct)).Settlement);
        Assert.Equal(4L, (await Claim(jobs, Key(V2, thread))).Next.ClaimedWatermark);
        Assert.False(File.Exists(fixture.DatabasePath(V1)));
    }

    [Fact]
    public async Task RetentionRanksActualUsageAndPreservesSelectedOutputsAndJobWatermarks()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var used = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var oldest = Guid.NewGuid();
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var blank = Guid.NewGuid();
        await using var storage = await fixture.Open(V2, clock, true);
        await Extract(storage, V2, clock, used, ageDays: 40);
        await Extract(storage, V2, clock, selected, ageDays: 50);
        await Extract(storage, V2, clock, oldest, ageDays: 70);
        await Extract(storage, V2, clock, old, ageDays: 60);
        await Extract(storage, V2, clock, recent, ageDays: 2);
        await Extract(storage, V2, clock, blank, ageDays: 1, text: "   ");
        var artifact = await Publish(storage, V2, clock, [new(used, 1), new(selected, 1)]);
        var cutoff = clock.GetUtcNow().AddDays(-30);
        Assert.Equal(recent, Assert.Single(await storage.SelectPhase2InputsAsync(cutoff, 1, Ct)).ThreadId);
        var report = new CodexMemoryUsageReport(Guid.NewGuid(), artifact.ArtifactRevision, 0,
            [new(used, 1), new(old, 1)]);
        Assert.Equal(1, await storage.RecordMemoryUsageAsync(report, Ct));
        Assert.Equal(1, await storage.RecordMemoryUsageAsync(report with { Selected = report.Selected.Reverse().ToArray() }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.RecordMemoryUsageAsync(
            report with { Selected = [new(selected, 1)] }, Ct));
        Assert.Equal(used, Assert.Single(await storage.SelectPhase2InputsAsync(cutoff, 1, Ct)).ThreadId);
        Assert.DoesNotContain(await storage.SelectPhase2InputsAsync(cutoff, 100, Ct), x => x.ThreadId == blank);
        Assert.Equal(1, await storage.PruneUnusedExtractionsAsync(cutoff, 1, Ct));
        Assert.Null(await storage.ReadExtractionAsync(new(oldest, 1), Ct));
        Assert.NotNull(await storage.ReadExtractionAsync(new(old, 1), Ct));
        Assert.Equal(1, await storage.PruneUnusedExtractionsAsync(cutoff, 100, Ct));
        Assert.Null(await storage.ReadExtractionAsync(new(old, 1), Ct));
        Assert.NotNull(await storage.ReadExtractionAsync(new(selected, 1), Ct));
        Assert.NotNull(await storage.ReadExtractionAsync(new(used, 1), Ct));
        var jobs = Jobs(storage, V2, clock);
        var observed = await jobs.ReadAsync(Key(V2, oldest), Ct);
        Assert.Equal(1L, observed.State!.LastSuccessWatermark);
        Assert.Equal(CodexMemoryJobClaimOutcome.UpToDate, jobs.PrepareClaim(observed,
            Guid.NewGuid(), Guid.NewGuid(), Lease).Outcome);
        Assert.Equal(0, await storage.PruneUnusedExtractionsAsync(cutoff, 100, Ct));
    }

    [Fact]
    public async Task ArtifactRetentionPreservesCurrentPointerAndLiveStaging()
    {
        RequireMac();
        using var fixture = new Fixture();
        var clock = new Clock();
        var source = Guid.NewGuid();
        var unused = Guid.NewGuid();
        await using var storage = await fixture.Open(V2, clock, true);
        await Extract(storage, V2, clock, source);
        await Extract(storage, V2, clock, unused, ageDays: 60);
        var first = await Publish(storage, V2, clock, [new(source, 1)]);
        clock.Advance(TimeSpan.FromHours(7));
        var jobs = Jobs(storage, V2, clock);
        var admission = Admission(await Claim(jobs, Key(V2)));
        Assert.True(await storage.TryAdmitSamplingAsync(admission, Ct));
        var staged = await storage.StageMemoryArtifactAsync(admission, [new(source, 1)],
            [new("memory_summary.md", Summary), new("MEMORY.md", "updated")], Ct);
        var cutoff = clock.GetUtcNow().AddSeconds(1);
        Assert.Equal(0, await storage.PruneUnpublishedArtifactsAsync(cutoff, 100, Ct));
        Assert.Equal(0, await storage.PruneUnusedExtractionsAsync(clock.GetUtcNow().AddDays(-30), 100, Ct));
        var complete = jobs.PreparePhase2Success(await jobs.ReadAsync(Key(V2), Ct), admission.WorkerId,
            admission.OwnershipToken, Guid.NewGuid(), staged);
        Assert.Equal(CodexMemoryJobSettlement.Applied, (await jobs.ApplyAsync(complete, Ct)).Settlement);
        Assert.Equal(1, await storage.PruneUnpublishedArtifactsAsync(cutoff, 1, Ct));
        Assert.Equal(staged.ArtifactRevision, (await storage.ReadLocalArtifactStatusAsync(Ct)).ArtifactRevision);
        Assert.Equal(0, await storage.RecordMemoryUsageAsync(new(Guid.NewGuid(), first.ArtifactRevision, 0, [new(source, 1)]), Ct));
        Assert.Equal(1, await storage.PruneUnusedExtractionsAsync(clock.GetUtcNow().AddDays(-30), 100, Ct));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-memory-lifecycle-").FullName);
        internal string DatabasePath(CodexMemoryScope scope) => Path.Combine(root,
            scope.Version == CodexMemoryVersion.V1 ? "legacy.sqlite" : "current.sqlite");
        internal Task<CodexMacOsSqliteMemoryJobStorage> Open(CodexMemoryScope scope, Clock clock, bool create = false) =>
            CodexMacOsSqliteMemoryJobStorage.OpenAsync(DatabasePath(scope), scope, (_, _) => ValueTask.CompletedTask, create, clock, Ct);

        internal async Task ExecuteFixtureSql(CodexMemoryScope scope, string sql)
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/sqlite3")
            {
                ArgumentList = { "-batch", "-bail", DatabasePath(scope), sql },
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
}
