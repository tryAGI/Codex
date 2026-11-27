using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class ThreadGoalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement Args(Guid id, string fields = "{}")
    {
        var body = JsonNode.Parse(fields)!.AsObject();
        body["threadId"] = id.ToString();
        return Json(body.ToJsonString());
    }

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly Dictionary<Guid, JsonElement> rows = [];
        private readonly object gate = new();
        internal bool LoseNextAcknowledgment;

        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(rows.TryGetValue(id, out var row) ? (JsonElement?)row.Clone() : null);
        }

        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? actual = rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (actual != revision) return Task.FromResult(false);
                rows[id] = body.Clone();
                if (LoseNextAcknowledgment)
                {
                    LoseNextAcknowledgment = false;
                    throw new IOException("Lost goal settlement acknowledgment.");
                }
                return Task.FromResult(true);
            }
        }
    }

    private sealed class GoalClock : TimeProvider
    {
        private long ticks;
        private DateTimeOffset utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => utc;

        internal void Advance(TimeSpan elapsed)
        {
            ticks += elapsed.Ticks;
            utc += elapsed;
        }

        internal void ShiftUtc(TimeSpan difference) => utc += difference;
    }

    private sealed class Fixture
    {
        private readonly Storage storage = new();
        internal CodexThreadStore Store { get; }
        internal List<(string Method, JsonElement Value)> Events { get; } = [];
        internal List<CodexThreadGoalMutation> Authorizations { get; } = [];

        internal GoalClock Clock { get; } = new();
        internal Fixture() => Store = new(storage, "account", Clock);
        internal void LoseNextAcknowledgment() => storage.LoseNextAcknowledgment = true;
        internal CodexThreadStore Reopen() => new(storage, "account", Clock);
        internal CodexThreadRpc Host(bool enabled = true, long? max = null, bool allow = true)
        {
            var host = new CodexThreadRpc(Store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
                Json("{}"), (_, _, _) => throw new InvalidOperationException("No automatic execution authorized."),
                (method, value) =>
                {
                    Events.Add((method, value.Clone()));
                    return Task.CompletedTask;
                });
            if (enabled)
                host.EnableThreadGoals((request, _) =>
                {
                    Authorizations.Add(request);
                    return Task.FromResult(allow);
                }, max);
            return host;
        }
        internal Task<CodexThreadDocument> Create(string settings = "{}") => Store.CreateAsync(Guid.NewGuid(), Json(settings), Ct);
        internal async Task<CodexThreadDocument> Finish(Guid id, string status, string text)
        {
            var before = await Store.ReadAsync(id, Ct);
            var turn = Guid.NewGuid();
            var started = await Store.StartTurnAsync(id, before.Revision, turn, "owner", [], cancellationToken: Ct);
            var message = Json(new JsonObject
            {
                ["type"] = "message", ["role"] = "assistant", ["id"] = Guid.NewGuid().ToString(),
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
            }.ToJsonString());
            return await Store.CompleteAsync(id, started.Revision, turn, "owner", status, [message], Ct);
        }
    }

    private static async Task<JsonElement> Goal(CodexThreadRpc host, Guid id, string patch)
        => (await host.InvokeAsync("thread/goal/set", Args(id, patch), Ct)).GetProperty("goal");

    private static async Task<JsonElement> Get(CodexThreadRpc host, Guid id)
        => (await host.InvokeAsync("thread/goal/get", Args(id), Ct)).GetProperty("goal");

    [Fact]
    public async Task GoalRequiresExplicitHostAuthorityAndDoesNotTreatOriginAsConsent()
    {
        var f = new Fixture();
        var thread = await f.Create();
        var disabled = f.Host(enabled: false);
        Assert.Equal(-32601, (await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            Goal(disabled, thread.Id, """{"origin":"user","objective":"work"}"""))).Code);
        var denied = f.Host(allow: false);
        Assert.Equal(-32000, (await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            Goal(denied, thread.Id, """{"origin":"user","objective":"work"}"""))).Code);
        Assert.Equal("user", Assert.Single(f.Authorizations).Origin);
        Assert.Null((await f.Store.ReadAsync(thread.Id, Ct)).Goal);
        Assert.Equal(thread.Revision, (await f.Store.ReadAsync(thread.Id, Ct)).Revision);

        var cold = f.Host();
        Assert.Equal(JsonValueKind.Null, (await Get(cold, thread.Id)).ValueKind);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Goal(cold, thread.Id, """{"status":"active"}"""));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Goal(cold, thread.Id, """{"origin":"unknown","objective":"work"}"""));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Goal(cold, thread.Id, """{"objective":"   "}"""));
        var goal = await Goal(cold, thread.Id, """{"objective":"  Finish task  "}""");
        Assert.Equal("Finish task", goal.GetProperty("objective").GetString());
        Assert.Equal("active", goal.GetProperty("status").GetString());
        Assert.Null(f.Authorizations[^1].Origin);
        Assert.Equal(thread.AccountId, f.Authorizations[^1].AccountId);
        Assert.Equal(thread.Revision, f.Authorizations[^1].ExpectedRevision);
        Assert.Single(f.Events, e => e.Method == "thread/goal/updated");
    }

    [Fact]
    public async Task GoalEmptyContinuationCircuitBreakerPersistsAndExplicitRestartResetsIt()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work"}""");
        var current = await f.Store.ReadAsync(thread.Id, Ct);
        var goalId = current.Goal!.Id;
        var context = Json("""{"role":"user","content":[{"type":"input_text","text":"goal context"}]}""");
        for (var count = 1; count <= 3; count++)
        {
            var id = Guid.NewGuid();
            var start = await f.Store.StartTurnAsync(thread.Id, current.Revision, id, "owner", [context],
                cancellationToken: Ct, clientUserMessageIds: [null], turnTrigger: "native:goal", goalContinuationId: goalId);
            current = await f.Store.CompleteAsync(thread.Id, start.Revision, id, "owner", "completed", [], Ct);
            Assert.Equal(count, current.Goal!.EmptyContinuationCount);
            Assert.Equal(count == 3 ? "blocked" : "active", current.Goal.Status);
            current = await f.Reopen().ReadAsync(thread.Id, Ct);
        }
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.Store.StartTurnAsync(thread.Id, current.Revision,
            Guid.NewGuid(), "owner", [context], cancellationToken: Ct, clientUserMessageIds: [null],
            turnTrigger: "native:goal", goalContinuationId: goalId));
        await Goal(host, thread.Id, """{"status":"active","origin":"user"}""");
        current = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.Null(current.Goal!.EmptyContinuationCount);
        var explicitTurn = Guid.NewGuid();
        current = await f.Store.StartTurnAsync(thread.Id, current.Revision, explicitTurn, "owner", [context], cancellationToken: Ct);
        Assert.Null(current.Turns[^1].GoalContinuation);
    }

    [Fact]
    public async Task GoalContinuationRejectsNewDispatchAfterPauseOrBudgetLimit()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work","tokenBudget":3}""");
        var current = await f.Store.ReadAsync(thread.Id, Ct);
        var goalId = current.Goal!.Id;
        var id = Guid.NewGuid();
        var context = Json("""{"role":"user","content":[{"type":"input_text","text":"goal context"}]}""");
        var started = await f.Store.StartTurnAsync(thread.Id, current.Revision, id, "owner", [context],
            cancellationToken: Ct, clientUserMessageIds: [null], turnTrigger: "native:goal", goalContinuationId: goalId);
        var admitted = await f.Store.AdmitAsync(thread.Id, started.Revision, id, "owner", "request", "inference", Json("{}"), Ct);
        current = await f.Store.SettleAsync(thread.Id, admitted.Thread.Revision, id, "owner", "request", "succeeded",
            Json("""{"id":"response","usage":{"input_tokens":2,"output_tokens":1}}"""), Ct);
        Assert.Equal("budgetLimited", current.Goal!.Status);
        foreach (var kind in new[] { "inference", "tool" })
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Store.AdmitAsync(thread.Id, current.Revision, id, "owner", "late-" + kind, kind, Json("{}"), Ct));
        Assert.Equal(current.Revision, (await f.Store.ReadAsync(thread.Id, Ct)).Revision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DetachedGoalUsageAndOriginalTerminalShareOneCas(bool incomplete, bool missingInput)
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work","tokenBudget":7}""");
        var before = await f.Finish(thread.Id, "completed", "answer");
        var terminal = incomplete
            ? missingInput
                ? Json("""{"type":"response.incomplete","response":{"id":"response","usage":{"output_tokens":4}}}""")
                : Json("""{"type":"response.incomplete","response":{"id":"response","usage":{"input_tokens":15,"input_tokens_details":{"cached_tokens":10},"output_tokens":4}}}""")
            : Json("""{"id":"response","usage":{"input_tokens":15,"input_tokens_details":{"cached_tokens":10},"output_tokens":4}}""");
        var accounting = new CodexStoredInferenceAccounting(before.Id, Guid.NewGuid(), before.AccountId,
            new string('a', 64), "model", CodexRetainedJournalMetadata.ItemHash(terminal))
        { RequestKind = "compaction" };
        if (incomplete)
            accounting = accounting with
            {
                AccountingKind = CodexAbnormalInferenceAccounting.IncompleteObservation,
                TerminalEventType = "response.incomplete", ObservedAt = f.Clock.GetUtcNow()
            };
        var usageId = Guid.NewGuid();
        var receipt = new CodexStoredDetachedCompactionReceipt("request", new string('b', 64), "response", before.Revision, accounting, terminal)
        { GoalInference = new(before.Id, before.Goal!.Id, usageId) };
        f.Clock.Advance(TimeSpan.FromHours(2));
        f.LoseNextAcknowledgment();
        await Assert.ThrowsAsync<IOException>(() => f.Store.RecordDetachedCompactionAccountingAsync(before.Id, receipt, Ct));
        var cold = f.Reopen();
        var saved = await cold.ReadAsync(before.Id, Ct);
        Assert.Equal(before.Revision + 1, saved.Revision);
        Assert.Equal(usageId, Assert.Single(saved.DetachedCompactionReceipts!).GoalInference!.UsageId);
        Assert.Equal(missingInput ? 0L : 9L, saved.Goal!.TokensUsed);
        Assert.Equal(missingInput ? "active" : "budgetLimited", saved.Goal.Status);
        Assert.Equal(0, saved.Goal.TimeUsedSeconds);
        Assert.True(CodexDetachedCompactionAccounting.OnlyReceiptsAppended(before, saved));
        Assert.False(CodexDetachedCompactionAccounting.OnlyReceiptsAppended(before,
            saved with { Goal = saved.Goal with { Objective = "different objective" } }));
        var replay = await cold.RecordDetachedCompactionAccountingAsync(before.Id, receipt, Ct);
        Assert.Equal(saved.Revision, replay.Revision);
        Assert.Equal(saved.Goal.TokensUsed, replay.Goal!.TokensUsed);
        var forged = receipt with { RequestId = "different", Terminal = Json("""{"id":"response","usage":{"input_tokens":100,"output_tokens":100}}""") };
        await Assert.ThrowsAsync<InvalidDataException>(() => cold.RecordDetachedCompactionAccountingAsync(before.Id, forged, Ct));
        var fork = await cold.ForkAsync(before.Id, saved.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.DetachedCompactionReceipts);
        Assert.Equal(saved.Goal.TokensUsed, fork.Goal!.TokensUsed);
    }

    [Fact]
    public async Task GoalSteeringContextIsDurableAndOnlyReinjectedWhenProjectionNeedsIt()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"original"}""");
        var current = await f.Store.ReadAsync(thread.Id, Ct);
        var turnId = Guid.NewGuid();
        current = await f.Store.StartTurnAsync(thread.Id, current.Revision, turnId, "owner", [], cancellationToken: Ct);
        var snapshot = CodexGoalSteeringSnapshot.From(current.Goal);
        var capture = CodexGoalSteeringContext.Capture(current, snapshot, [], 0)!;
        current = await f.Store.SynchronizeOutputCoreAsync(thread.Id, current.Revision, turnId, "owner",
            [capture.Message], null, Ct, capture);
        var saved = await f.Reopen().ReadAsync(thread.Id, Ct);
        Assert.True(CodexGoalSteeringContext.IsRecorded(saved.Turns[^1], 0, capture.Message));
        Assert.Null(CodexGoalSteeringContext.Capture(saved, snapshot, [capture.Message], 1));
        Assert.NotNull(CodexGoalSteeringContext.Capture(saved, snapshot, [], 1));
        await Goal(host, thread.Id, """{"objective":"updated"}""");
        current = await f.Store.ReadAsync(thread.Id, Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.Store.AdmitSteeredInferenceAsync(
            thread.Id, current.Revision, turnId, "owner", "stale", Json("{}"), snapshot.Key, Ct));
        Assert.Empty((await f.Store.ReadAsync(thread.Id, Ct)).Turns[^1].Dispatches);
        var updated = CodexGoalSteeringContext.Capture(current, CodexGoalSteeringSnapshot.From(current.Goal), [capture.Message], 1)!;
        Assert.NotEqual(capture.Transition.StateSha256, updated.Transition.StateSha256);
        current = await f.Store.SynchronizeOutputCoreAsync(thread.Id, current.Revision, turnId, "owner",
            [capture.Message, updated.Message], null, Ct, updated);
        var admitted = await f.Store.AdmitSteeredInferenceAsync(thread.Id, current.Revision, turnId, "owner",
            "current", Json("{}"), updated.Key, Ct);
        Assert.True(admitted.Admitted);
        Assert.Equal(2, admitted.Thread.Turns[^1].GoalSteeringTransitions!.Length);
    }

    [Fact]
    public async Task GoalTimeUsesMonotonicIntervalsAndExcludesPauseAndIdle()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work"}""");
        var current = await f.Store.ReadAsync(thread.Id, Ct);
        f.Clock.Advance(TimeSpan.FromHours(2));
        var turn = Guid.NewGuid();
        current = await f.Store.StartTurnAsync(thread.Id, current.Revision, turn, "owner", [], cancellationToken: Ct);
        f.Clock.Advance(TimeSpan.FromMilliseconds(750));
        f.Clock.ShiftUtc(TimeSpan.FromDays(1));
        var admitted = await f.Store.AdmitAsync(thread.Id, current.Revision, turn, "owner", "request", "inference", Json("{}"), Ct);
        Assert.Equal(0, admitted.Thread.Goal!.TimeUsedSeconds);
        Assert.Equal(TimeSpan.FromMilliseconds(750).Ticks, admitted.Thread.Goal.ElapsedRemainderTicks);
        f.Clock.Advance(TimeSpan.FromMilliseconds(750));
        current = await f.Store.SettleAsync(thread.Id, admitted.Thread.Revision, turn, "owner", "request", "succeeded",
            Json("""{"id":"response","usage":{"input_tokens":2,"output_tokens":1}}"""), Ct);
        Assert.Equal(1, current.Goal!.TimeUsedSeconds);
        Assert.Equal(TimeSpan.FromMilliseconds(500).Ticks, current.Goal.ElapsedRemainderTicks);
        f.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await Goal(host, thread.Id, """{"status":"paused"}""");
        current = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.Equal(2, current.Goal!.TimeUsedSeconds);
        Assert.Null(current.Goal.ElapsedRemainderTicks);
        Assert.Null(current.GoalExecutionClock!.GoalId);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await Goal(host, thread.Id, """{"status":"active"}""");
        f.Clock.Advance(TimeSpan.FromMilliseconds(1250));
        current = await f.Store.ReadAsync(thread.Id, Ct);
        current = await f.Store.CompleteAsync(thread.Id, current.Revision, turn, "owner", "completed", [], Ct);
        Assert.Equal(3, current.Goal!.TimeUsedSeconds);
        Assert.Equal(TimeSpan.FromMilliseconds(250).Ticks, current.Goal.ElapsedRemainderTicks);
        Assert.Null(current.GoalExecutionClock);
        var fork = await f.Store.ForkAsync(thread.Id, current.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Equal(3, fork.Goal!.TimeUsedSeconds);
        Assert.Null(fork.Goal.ElapsedRemainderTicks);
        Assert.Null(fork.GoalExecutionClock);
    }

    [Fact]
    public async Task GoalTimeAcknowledgementLossAndRecoveryPreserveOnlyConfirmedIntervals()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work"}""");
        var current = await f.Store.ReadAsync(thread.Id, Ct);
        var turn = Guid.NewGuid();
        current = await f.Store.StartTurnAsync(thread.Id, current.Revision, turn, "owner", [], cancellationToken: Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        var admitted = await f.Store.AdmitAsync(thread.Id, current.Revision, turn, "owner", "request", "inference", Json("{}"), Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.LoseNextAcknowledgment();
        await Assert.ThrowsAsync<IOException>(() => f.Store.SettleAsync(thread.Id, admitted.Thread.Revision, turn,
            "owner", "request", "succeeded", Json("""{"id":"response","usage":{"input_tokens":2,"output_tokens":1}}"""), Ct));
        var saved = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.Equal(5, saved.Goal!.TimeUsedSeconds);
        Assert.Equal(3, saved.Goal.TokensUsed);
        f.Clock.Advance(TimeSpan.FromHours(9));
        var cold = f.Reopen();
        current = await cold.RecoverInterruptedAsync(thread.Id, saved.Revision, turn, "owner", Ct);
        Assert.Equal(5, current.Goal!.TimeUsedSeconds);
        Assert.Null(current.GoalExecutionClock);
        var next = Guid.NewGuid();
        current = await cold.StartTurnAsync(thread.Id, current.Revision, next, "new-owner", [], cancellationToken: Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        current = await cold.CompleteAsync(thread.Id, current.Revision, next, "new-owner", "completed", [], Ct);
        Assert.Equal(6, current.Goal!.TimeUsedSeconds);
    }

    [Fact]
    public async Task GoalTimeStopsAtBudgetAndDoesNotChargeAReplacement()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work","tokenBudget":3}""");
        var current = await f.Store.ReadAsync(thread.Id, Ct);
        var turn = Guid.NewGuid();
        current = await f.Store.StartTurnAsync(thread.Id, current.Revision, turn, "owner", [], cancellationToken: Ct);
        var admitted = await f.Store.AdmitAsync(thread.Id, current.Revision, turn, "owner", "request", "inference", Json("{}"), Ct);
        f.Clock.Advance(TimeSpan.FromSeconds(4));
        current = await f.Store.SettleAsync(thread.Id, admitted.Thread.Revision, turn, "owner", "request", "succeeded",
            Json("""{"id":"response","usage":{"input_tokens":2,"output_tokens":1}}"""), Ct);
        Assert.Equal("budgetLimited", current.Goal!.Status);
        Assert.Equal(4, current.Goal.TimeUsedSeconds);
        f.Clock.Advance(TimeSpan.FromSeconds(8));
        await host.InvokeAsync("thread/goal/clear", Args(thread.Id), Ct);
        await Goal(host, thread.Id, """{"objective":"new goal"}""");
        current = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.Equal(0, current.Goal!.TimeUsedSeconds);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        current = await f.Store.CompleteAsync(thread.Id, current.Revision, turn, "owner", "completed", [], Ct);
        Assert.Equal(2, current.Goal!.TimeUsedSeconds);
    }

    [Fact]
    public async Task GoalInferenceUsageCommitsWithTheOriginalDispatchAndCountsOnlyUncachedTokens()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work","tokenBudget":10}""");
        var before = await f.Store.ReadAsync(thread.Id, Ct);
        var turnId = Guid.NewGuid();
        var started = await f.Store.StartTurnAsync(thread.Id, before.Revision, turnId, "owner", [], cancellationToken: Ct);
        var admitted = await f.Store.AdmitAsync(thread.Id, started.Revision, turnId, "owner", "request-1", "inference", Json("{}"), Ct);
        var binding = Assert.Single(admitted.Thread.Turns[^1].Dispatches).GoalInference!;
        Assert.Equal(before.Goal!.Id, binding.GoalId);
        Assert.Equal(thread.Id, binding.OriginThreadId);
        var terminal = Json("""{"id":"resp_1","status":"completed","usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":95},"output_tokens":6,"output_tokens_details":{"reasoning_tokens":4},"total_tokens":106}}""");
        var settled = await f.Store.SettleAsync(thread.Id, admitted.Thread.Revision, turnId, "owner", "request-1", "succeeded", terminal, Ct);
        Assert.Equal(admitted.Thread.Revision + 1, settled.Revision);
        Assert.Equal(11, settled.Goal!.TokensUsed);
        Assert.Equal("budgetLimited", settled.Goal.Status);
        var receipt = Assert.Single(settled.Goal.UsageReceipts!);
        Assert.Equal(binding.UsageId, receipt.Id);
        Assert.Equal(11, receipt.Tokens);
        Assert.Equal("succeeded", Assert.Single(settled.Turns[^1].Dispatches).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Store.SettleAsync(thread.Id, settled.Revision, turnId, "owner", "request-1", "succeeded", terminal, Ct));
        var cold = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.Equal(settled.Revision, cold.Revision);
        Assert.Equal(11, cold.Goal!.TokensUsed);

        var uncharged = await f.Store.AdmitAsync(thread.Id, cold.Revision, turnId, "owner", "request-2", "inference", Json("{}"), Ct);
        Assert.Null(uncharged.Thread.Turns[^1].Dispatches[^1].GoalInference);
        var next = await f.Store.SettleAsync(thread.Id, uncharged.Thread.Revision, turnId, "owner", "request-2", "succeeded", terminal, Ct);
        Assert.Equal(11, next.Goal!.TokensUsed);
    }

    [Fact]
    public async Task GoalInferenceLostSettlementAcknowledgmentCannotChargeTwiceAfterRestart()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work","tokenBudget":10}""");
        var before = await f.Store.ReadAsync(thread.Id, Ct);
        var turnId = Guid.NewGuid();
        var started = await f.Store.StartTurnAsync(thread.Id, before.Revision, turnId, "owner", [], cancellationToken: Ct);
        var admitted = await f.Store.AdmitAsync(thread.Id, started.Revision, turnId, "owner", "lost-ack", "inference", Json("{}"), Ct);
        var terminal = Json("""{"id":"resp","usage":{"input_tokens":6,"output_tokens":5}}""");
        f.LoseNextAcknowledgment();
        await Assert.ThrowsAsync<IOException>(() => f.Store.SettleAsync(thread.Id, admitted.Thread.Revision,
            turnId, "owner", "lost-ack", "succeeded", terminal, Ct));
        var cold = f.Reopen();
        var saved = await cold.ReadAsync(thread.Id, Ct);
        Assert.Equal("succeeded", Assert.Single(saved.Turns[^1].Dispatches).State);
        Assert.Equal(11, saved.Goal!.TokensUsed);
        Assert.Equal("budgetLimited", saved.Goal.Status);
        Assert.Single(saved.Goal.UsageReceipts!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cold.SettleAsync(thread.Id, saved.Revision,
            turnId, "owner", "lost-ack", "succeeded", terminal, Ct));
        var recovered = await cold.RecoverInterruptedAsync(thread.Id, saved.Revision, turnId, "owner", Ct);
        Assert.Equal(11, recovered.Goal!.TokensUsed);
        Assert.Single(recovered.Goal.UsageReceipts!);
    }

    [Fact]
    public async Task GoalInferenceLateResponseDoesNotChargeReplacementGenerationOrInheritedReceipt()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"old"}""");
        var before = await f.Store.ReadAsync(thread.Id, Ct);
        var turnId = Guid.NewGuid();
        var started = await f.Store.StartTurnAsync(thread.Id, before.Revision, turnId, "owner", [], cancellationToken: Ct);
        var admitted = await f.Store.AdmitAsync(thread.Id, started.Revision, turnId, "owner", "original", "inference", Json("{}"), Ct);
        var originalGoal = admitted.Thread.Goal!.Id;
        await host.InvokeAsync("thread/goal/clear", Args(thread.Id), Ct);
        await Goal(host, thread.Id, """{"objective":"replacement"}""");
        var replacement = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.NotEqual(originalGoal, replacement.Goal!.Id);
        var terminal = Json("""{"id":"resp_1","usage":{"input_tokens":12,"output_tokens":3}}""");
        var settled = await f.Store.SettleAsync(thread.Id, replacement.Revision, turnId, "owner", "original", "succeeded", terminal, Ct);
        Assert.Equal(0, settled.Goal!.TokensUsed);
        var next = await f.Store.AdmitAsync(thread.Id, settled.Revision, turnId, "owner", "inherited", "inference", Json("{}"), Ct);
        var unknown = await f.Store.SettleAsync(thread.Id, next.Thread.Revision, turnId, "owner", "inherited", "unknown", cancellationToken: Ct);
        var recovered = await f.Store.RecoverInterruptedAsync(thread.Id, unknown.Revision, turnId, "owner", Ct);
        var fork = await f.Store.ForkAsync(thread.Id, recovered.Revision, Guid.NewGuid(), cancellationToken: Ct);
        var child = await f.Store.SettleAsync(fork.Id, fork.Revision, turnId, "owner", "inherited", "succeeded", terminal, Ct);
        Assert.Equal(0, child.Goal!.TokensUsed);
        var original = await f.Store.SettleAsync(thread.Id, recovered.Revision, turnId, "owner", "inherited", "succeeded", terminal, Ct);
        Assert.Equal(15, original.Goal!.TokensUsed);
        Assert.Equal(0, (await f.Store.ReadAsync(fork.Id, Ct)).Goal!.TokensUsed);
    }

    [Fact]
    public async Task GoalInferencePartialUsageAndToolOutputsDoNotInventTokenDeltas()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host();
        await Goal(host, thread.Id, """{"objective":"work"}""");
        var before = await f.Store.ReadAsync(thread.Id, Ct);
        var turnId = Guid.NewGuid();
        var current = await f.Store.StartTurnAsync(thread.Id, before.Revision, turnId, "owner", [], cancellationToken: Ct);
        foreach (var (call, kind, evidence) in new[]
        {
            ("missing-output", "inference", """{"id":"r1","usage":{"input_tokens":100,"total_tokens":110}}"""),
            ("tool", "tool", """{"id":"fake","usage":{"input_tokens":1000,"output_tokens":1000}}""")
        })
        {
            var admitted = await f.Store.AdmitAsync(thread.Id, current.Revision, turnId, "owner", call, kind, Json("{}"), Ct);
            current = await f.Store.SettleAsync(thread.Id, admitted.Thread.Revision, turnId, "owner", call, "succeeded", Json(evidence), Ct);
        }
        Assert.Equal(0, current.Goal!.TokensUsed);
        Assert.Null(current.Goal.UsageReceipts);
    }

    [Fact]
    public async Task GoalBudgetAndUsageAreFencedByGenerationAndStableReceipt()
    {
        var f = new Fixture(); var thread = await f.Create(); var host = f.Host(max: 200);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Goal(host, thread.Id, """{"objective":"work","tokenBudget":201}"""));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Goal(host, thread.Id, """{"objective":"work","tokenBudget":0}"""));
        var wire = await Goal(host, thread.Id, """{"origin":"user","objective":"work","tokenBudget":100}""");
        Assert.Equal(100L, wire.GetProperty("tokenBudget").GetInt64());
        var before = await f.Store.ReadAsync(thread.Id, Ct);
        var goalId = before.Goal!.Id;
        var receiptId = Guid.NewGuid();
        var accounted = await f.Store.RecordGoalUsageAsync(thread.Id, before.Revision, goalId, receiptId,
            110, 7, CodexGoalAccountingMode.ActiveOnly, Ct);
        Assert.Equal("budgetLimited", accounted.Goal!.Status);
        Assert.Equal(110, accounted.Goal.TokensUsed);
        Assert.Equal(7, accounted.Goal.TimeUsedSeconds);
        var duplicate = await f.Store.RecordGoalUsageAsync(thread.Id, accounted.Revision, goalId, receiptId,
            110, 7, CodexGoalAccountingMode.ActiveOnly, Ct);
        Assert.Equal(accounted.Revision, duplicate.Revision);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.RecordGoalUsageAsync(thread.Id, duplicate.Revision,
            goalId, receiptId, 111, 7, CodexGoalAccountingMode.ActiveOnly, Ct));

        var cold = f.Host(max: 200);
        var patched = await Goal(cold, thread.Id, """{"objective":"new objective","status":"paused","tokenBudget":null}""");
        Assert.Equal("new objective", patched.GetProperty("objective").GetString());
        Assert.Equal("budgetLimited", patched.GetProperty("status").GetString());
        Assert.Equal(200L, patched.GetProperty("tokenBudget").GetInt64());
        var resumed = await Goal(cold, thread.Id, """{"status":"active","origin":"user"}""");
        Assert.Equal("active", resumed.GetProperty("status").GetString());
        Assert.Equal(110L, resumed.GetProperty("tokensUsed").GetInt64());
        var paused = await Goal(cold, thread.Id, """{"status":"paused"}""");
        Assert.Equal("paused", paused.GetProperty("status").GetString());
        var snapshot = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.Equal(goalId, snapshot.Goal!.Id);
        Assert.Equal(before.Goal.CreatedAt, snapshot.Goal.CreatedAt);
        Assert.Equal(thread.UpdatedAt, snapshot.UpdatedAt);
        var stopped = await f.Store.RecordGoalUsageAsync(thread.Id, snapshot.Revision, goalId, Guid.NewGuid(),
            100, 5, CodexGoalAccountingMode.ActiveOrStopped, Ct);
        Assert.Equal("budgetLimited", stopped.Goal!.Status);
        Assert.Equal(210, stopped.Goal.TokensUsed);
        await Goal(cold, thread.Id, """{"status":"complete"}""");
        var completed = await f.Store.ReadAsync(thread.Id, Ct);
        var final = await f.Store.RecordGoalUsageAsync(thread.Id, completed.Revision, goalId, Guid.NewGuid(),
            5, 2, CodexGoalAccountingMode.ActiveOrComplete, Ct);
        Assert.Equal("complete", final.Goal!.Status);
        Assert.Equal(215, final.Goal.TokensUsed);

        Assert.True((await cold.InvokeAsync("thread/goal/clear", Args(thread.Id, """{"origin":"user"}"""), Ct)).GetProperty("cleared").GetBoolean());
        Assert.False((await cold.InvokeAsync("thread/goal/clear", Args(thread.Id), Ct)).GetProperty("cleared").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await Get(cold, thread.Id)).ValueKind);
        await Goal(cold, thread.Id, """{"objective":"next"}""");
        var replacement = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.NotEqual(goalId, replacement.Goal!.Id);
        var ignored = await f.Store.RecordGoalUsageAsync(thread.Id, replacement.Revision, goalId, Guid.NewGuid(),
            20, 2, CodexGoalAccountingMode.ActiveOnly, Ct);
        Assert.Equal(replacement.Revision, ignored.Revision);
        Assert.Equal(0, ignored.Goal!.TokensUsed);
        Assert.Single(f.Events, e => e.Method == "thread/goal/cleared");
    }

    [Fact]
    public async Task GoalForkCopiesDurableSnapshotButNotEphemeralAndSuppressesIntermediateAttention()
    {
        var f = new Fixture(); var host = f.Host(); var thread = await f.Create();
        await Goal(host, thread.Id, """{"objective":"work","tokenBudget":100}""");
        var active = await f.Finish(thread.Id, "completed", "Intermediate progress");
        Assert.Null(active.ReadReceipt);
        var failed = await f.Finish(thread.Id, "failed", "Failure needs attention");
        Assert.Equal(failed.Turns[^1].Id.ToString(), failed.ReadReceipt!.FirstUnreadTurn);
        var fork = await f.Store.ForkAsync(thread.Id, failed.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Equal(failed.Goal!.Id, fork.Goal!.Id);
        Assert.Equal(failed.Goal.Objective, fork.Goal.Objective);
        Assert.Null(fork.ReadReceipt);
        var ephemeral = await f.Store.ForkAsync(thread.Id, failed.Revision, Guid.NewGuid(), cancellationToken: Ct,
            effectiveSettings: Json("""{"ephemeral":true}"""));
        Assert.Null(ephemeral.Goal);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Goal(host, ephemeral.Id, """{"objective":"no"}"""));

        await Goal(host, fork.Id, """{"status":"complete"}""");
        var terminal = await f.Finish(fork.Id, "completed", "Done");
        Assert.Equal(terminal.Turns[^1].Id.ToString(), terminal.ReadReceipt!.FirstUnreadTurn);
        Assert.Equal("active", (await f.Store.ReadAsync(thread.Id, Ct)).Goal!.Status);
        var archived = await f.Store.ArchiveAsync(fork.Id, terminal.Revision, Ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Get(host, archived.Id));
    }

    [Fact]
    public async Task GoalAuthorityCannotCommitAgainstChangedSnapshot()
    {
        var f = new Fixture(); var thread = await f.Create();
        var host = f.Host(enabled: false);
        host.EnableThreadGoals(async (_, ct) =>
        {
            await f.Store.SetNameAsync(thread.Id, thread.Revision, "changed", ct);
            return true;
        });
        var error = await Assert.ThrowsAsync<CodexThreadRpcException>(() => Goal(host, thread.Id, """{"objective":"work"}"""));
        Assert.Equal(-32009, error.Code);
        var saved = await f.Store.ReadAsync(thread.Id, Ct);
        Assert.Equal("changed", saved.Name);
        Assert.Null(saved.Goal);
        Assert.Empty(f.Events);
    }
}
