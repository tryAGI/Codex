using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class ThreadDeletionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement Args(Guid id) => Json(new JsonObject { ["threadId"] = id.ToString() }.ToJsonString());

    // Models the host's atomic graph, revision, writer and reference checks in one transaction.
    private sealed class Storage : ICodexThreadDeletionStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        private readonly Dictionary<Guid, Guid> parents = [];
        private readonly HashSet<Guid> tombstones = [];
        private readonly HashSet<Guid> foreignWriters = [];
        private long graphRevision;
        internal int DeleteCalls;
        internal Exception? LostAcknowledgement;
        internal Action? BeforeCommit;
        internal Func<CodexThreadDeletionPlan, CodexThreadDeletionPlan>? TransformPlan;

        internal void Spawn(Guid parent, Guid child)
        {
            lock (gate)
            {
                parents.Add(child, parent);
                graphRevision++;
            }
        }

        internal void ClaimForeign(Guid id)
        {
            lock (gate) foreignWriters.Add(id);
        }

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
                if (tombstones.Contains(id)) return Task.FromResult(false);
                long? current = rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (current != revision) return Task.FromResult(false);
                if (revision is null)
                {
                    if (body.TryGetProperty("forkedFromId", out var source) && source.ValueKind == JsonValueKind.String
                        && !rows.ContainsKey(source.GetGuid())) return Task.FromResult(false);
                    graphRevision++;
                }
                rows[id] = body.Clone();
                return Task.FromResult(true);
            }
        }

        private CodexThreadDeletionPlan Plan(string account, Guid root)
        {
            if (!rows.ContainsKey(root) && !parents.Values.Contains(root)) throw new KeyNotFoundException();
            var targets = new List<CodexThreadDeletionTarget>();
            void Visit(Guid id, Guid? parent)
            {
                foreach (var child in parents.Where(edge => edge.Value == id).Select(edge => edge.Key).Order())
                    Visit(child, id);
                long? revision = null;
                if (rows.TryGetValue(id, out var row))
                {
                    if (row.GetProperty("accountId").GetString() != account) throw new InvalidOperationException("Foreign account.");
                    revision = row.GetProperty("revision").GetInt64();
                }
                targets.Add(new(id, parent, revision));
            }
            Visit(root, null);
            var ids = targets.Select(target => target.ThreadId).ToHashSet();
            if (foreignWriters.Overlaps(ids)) throw new InvalidOperationException("Foreign writer.");
            if (rows.Any(pair => !ids.Contains(pair.Key) && pair.Value.TryGetProperty("forkedFromId", out var source)
                && source.ValueKind == JsonValueKind.String && ids.Contains(source.GetGuid())))
                throw new InvalidOperationException("External fork still references the subtree.");
            return new(account, root, graphRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), targets.ToArray());
        }

        public Task<CodexThreadDeletionPlan> ReadDeletionPlanAsync(string account, Guid root, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                var plan = Plan(account, root);
                return Task.FromResult(TransformPlan?.Invoke(plan) ?? plan);
            }
        }

        public Task<bool> CompareDeleteAsync(CodexThreadDeletionPlan plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                DeleteCalls++;
                BeforeCommit?.Invoke();
                CodexThreadDeletionPlan actual;
                try { actual = Plan(plan.AccountId, plan.RootThreadId); }
                catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException) { return Task.FromResult(false); }
                if (actual.GraphRevision != plan.GraphRevision || !actual.Targets.SequenceEqual(plan.Targets))
                    return Task.FromResult(false);
                foreach (var target in plan.Targets)
                {
                    rows.Remove(target.ThreadId);
                    parents.Remove(target.ThreadId);
                    tombstones.Add(target.ThreadId);
                }
                graphRevision++;
                if (LostAcknowledgement is { } lost) throw lost;
                return Task.FromResult(true);
            }
        }
    }

    private sealed class Fixture
    {
        internal Storage Database { get; } = new();
        internal CodexThreadStore Store { get; }
        internal List<Guid> Deleted { get; } = [];
        internal List<string> Events { get; } = [];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Fixture() => Store = new(Database, "account");

        internal CodexThreadRpc Host(bool runTurns = false)
            => new(Store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]), Json(new JsonObject
            {
                ["model"] = "test", ["modelProvider"] = "openai", ["cwd"] = Path.GetTempPath(),
                ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["approvalsReviewer"] = "user"
            }.ToJsonString()), async (run, started, ct) =>
            {
                if (!runTurns) throw new InvalidOperationException("Unexpected execution.");
                var turn = await Store.StartTurnAsync(run.Thread.Id, run.Thread.Revision, run.TurnId, run.OwnerId,
                    run.Input, cancellationToken: ct);
                await started(turn);
                Entered.TrySetResult();
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                return await Store.CompleteAsync(run.Thread.Id, turn.Revision, run.TurnId, run.OwnerId,
                    "interrupted", [], CancellationToken.None);
            }, (method, value) =>
            {
                Events.Add(method);
                if (method == "thread/deleted") Deleted.Add(Guid.Parse(value.GetProperty("threadId").GetString()!));
                return Task.CompletedTask;
            });

        internal Task<CodexThreadDocument> Create() => Store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
    }

    [Fact]
    public async Task AuthorizedDeletionRemovesCompleteSubtreeChildFirstIncludingGraphOnlyMember()
    {
        var f = new Fixture();
        var root = await f.Create();
        var child = await f.Create();
        var graphOnly = Guid.NewGuid();
        var unrelated = await f.Create();
        f.Database.Spawn(root.Id, child.Id);
        f.Database.Spawn(child.Id, graphOnly);
        await f.Store.ArchiveAsync(child.Id, child.Revision, Ct);
        var host = f.Host();
        CodexThreadDeletionPlan? approved = null;
        host.EnableThreadDeletion((plan, _) =>
        {
            approved = plan;
            return Task.FromResult(true);
        });

        var response = await host.InvokeAsync("thread/delete", Args(root.Id), Ct);
        Assert.Empty(response.EnumerateObject());
        Assert.NotNull(approved);
        Assert.Equal("account", approved.AccountId);
        Assert.Equal(new[] { graphOnly, child.Id, root.Id }, approved.Targets.Select(target => target.ThreadId));
        Assert.Null(approved.Targets[0].Revision);
        Assert.Equal(new[] { graphOnly, child.Id, root.Id }, f.Deleted);
        Assert.Equal(1, f.Database.DeleteCalls);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.ReadAsync(root.Id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.ReadAsync(child.Id, Ct));
        Assert.Equal(unrelated.Id, (await f.Store.ReadAsync(unrelated.Id, Ct)).Id);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => f.Store.CreateAsync(root.Id, Json("{}"), Ct));
    }

    [Fact]
    public async Task MissingOrDeniedAuthorizationDoesNotWriteOrNotify()
    {
        var f = new Fixture();
        var root = await f.Create();
        var host = f.Host();
        var missing = await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        Assert.Equal(-32601, missing.Code);
        host.EnableThreadDeletion((_, _) => Task.FromResult(false));
        var denied = await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        Assert.Equal(-32000, denied.Code);
        Assert.Equal(root.Revision, (await f.Store.ReadAsync(root.Id, Ct)).Revision);
        Assert.Empty(f.Deleted);
        Assert.Equal(0, f.Database.DeleteCalls);
    }

    [Fact]
    public async Task ExternalForkAndForeignWriterPreventSubtreeDeletionBeforeAuthorization()
    {
        var f = new Fixture();
        var root = await f.Create();
        var child = await f.Create();
        f.Database.Spawn(root.Id, child.Id);
        var fork = await f.Store.ForkAsync(child.Id, child.Revision, Guid.NewGuid(), cancellationToken: Ct);
        var host = f.Host();
        var approvals = 0;
        host.EnableThreadDeletion((_, _) => { approvals++; return Task.FromResult(true); });
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        Assert.Equal(0, approvals);
        await host.InvokeAsync("thread/delete", Args(fork.Id), Ct);
        f.Database.ClaimForeign(child.Id);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        Assert.Equal(1, approvals);
        Assert.Equal(1, f.Database.DeleteCalls);
        Assert.Equal(new[] { fork.Id }, f.Deleted);
        Assert.Equal(root.Id, (await f.Store.ReadAsync(root.Id, Ct)).Id);
        Assert.Equal(child.Id, (await f.Store.ReadAsync(child.Id, Ct)).Id);
    }

    [Fact]
    public async Task ChangedApprovedRevisionAndNewSpawnRequireFreshApproval()
    {
        var f = new Fixture();
        var root = await f.Create();
        var host = f.Host();
        host.EnableThreadDeletion(async (_, ct) =>
        {
            await f.Store.SetNameAsync(root.Id, root.Revision, "new metadata", ct);
            f.Database.Spawn(root.Id, Guid.NewGuid());
            return true;
        });
        var changed = await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        Assert.Equal(-32009, changed.Code);
        Assert.Equal(0, f.Database.DeleteCalls);
        Assert.Empty(f.Deleted);
        Assert.Equal("new metadata", (await f.Store.ReadAsync(root.Id, Ct)).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionRechecksForeignWriterAndUnknownCommitDoesNotReplay(bool nonIoFailure)
    {
        var f = new Fixture();
        var root = await f.Create();
        var host = f.Host();
        host.EnableThreadDeletion((_, _) => Task.FromResult(true));
        f.Database.BeforeCommit = () => f.Database.ClaimForeign(root.Id);
        var raced = await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        Assert.Equal(-32009, raced.Code);
        Assert.Equal(1, f.Database.DeleteCalls);
        Assert.Empty(f.Deleted);
        Assert.Equal(root.Revision, (await f.Store.ReadAsync(root.Id, Ct)).Revision);

        var other = await f.Create();
        f.Database.BeforeCommit = null;
        f.Database.LostAcknowledgement = nonIoFailure
            ? new InvalidOperationException("Lost durable commit acknowledgement.")
            : new IOException("Lost durable commit acknowledgement.");
        var uncertain = await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(other.Id), Ct));
        Assert.Equal(-32010, uncertain.Code);
        Assert.Equal(2, f.Database.DeleteCalls);
        Assert.Empty(f.Deleted);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.ReadAsync(other.Id, Ct));
    }

    [Fact]
    public async Task RejectsMalformedScopeAndOpenRealtimeWithoutDeletingAnything()
    {
        var f = new Fixture();
        var root = await f.Create();
        var unrelated = await f.Create();
        var host = f.Host();
        host.EnableThreadDeletion((_, _) => Task.FromResult(true));
        f.Database.TransformPlan = plan => plan with
        {
            Targets = [new(unrelated.Id, null, unrelated.Revision), .. plan.Targets]
        };
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        f.Database.TransformPlan = null;
        await f.Store.CreateDirectRealtimeSessionAsync(root.Id, root.Revision, "voice", "v3", "owner", Ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/delete", Args(root.Id), Ct));
        Assert.Empty(f.Deleted);
        Assert.Equal(0, f.Database.DeleteCalls);
    }

    [Fact]
    public async Task LocalActiveTurnIsDrainedOnlyAfterAuthorizationAndForeignHostCannotDelete()
    {
        var f = new Fixture();
        var host = f.Host(runTurns: true);
        var start = await host.InvokeAsync("thread/start", Json("{}"), Ct);
        var id = Guid.Parse(start.GetProperty("thread").GetProperty("id").GetString()!);
        await host.InvokeAsync("turn/start", Json(new JsonObject
        {
            ["threadId"] = id.ToString(),
            ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "test" })
        }.ToJsonString()), Ct);
        await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var foreign = f.Host();
        foreign.EnableThreadDeletion((_, _) => Task.FromResult(true));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => foreign.InvokeAsync("thread/delete", Args(id), Ct));
        Assert.Equal("inProgress", Assert.Single((await f.Store.ReadAsync(id, Ct)).Turns).Status);
        Assert.Equal(0, f.Database.DeleteCalls);

        host.EnableThreadDeletion((_, _) => Task.FromResult(true));
        await host.InvokeAsync("thread/delete", Args(id), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(new[] { id }, f.Deleted);
        Assert.Contains("turn/completed", f.Events);
        Assert.True(f.Events.IndexOf("turn/completed") < f.Events.IndexOf("thread/deleted"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.ReadAsync(id, Ct));
        Assert.Equal(1, f.Database.DeleteCalls);
    }
}
