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
                return Task.FromResult(true);
            }
        }
    }

    private sealed class Fixture
    {
        private readonly Storage storage = new();
        internal CodexThreadStore Store { get; }
        internal List<(string Method, JsonElement Value)> Events { get; } = [];
        internal List<CodexThreadGoalMutation> Authorizations { get; } = [];

        internal Fixture() => Store = new(storage, "account");
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
