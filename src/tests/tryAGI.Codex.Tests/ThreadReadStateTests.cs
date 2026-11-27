using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class ThreadReadStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement Args(Guid id) => Json(new JsonObject { ["threadId"] = id.ToString() }.ToJsonString());

    private static JsonElement Mark(Guid id, string revision, string type) => Json(new JsonObject
    {
        ["threadId"] = id.ToString(), ["expectedRevision"] = revision,
        ["operation"] = new JsonObject { ["type"] = type }
    }.ToJsonString());

    private static JsonElement Assistant(string text) => Json(new JsonObject
    {
        ["type"] = "message", ["id"] = Guid.NewGuid().ToString(), ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
    }.ToJsonString());

    private static string Revision(JsonElement state) => state.GetProperty("revision").GetString()!;
    private static string? FirstType(JsonElement state) => state.GetProperty("firstUnread") is { ValueKind: JsonValueKind.Object } first
        ? first.GetProperty("type").GetString() : null;

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        internal bool RaceWithMetadata;
        internal int Writes;

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
                long? actual = rows.TryGetValue(id, out var previous) ? previous.GetProperty("revision").GetInt64() : null;
                if (revision != actual) return Task.FromResult(false);
                if (RaceWithMetadata && actual is { } value && body.TryGetProperty("readReceipt", out _))
                {
                    RaceWithMetadata = false;
                    var metadata = JsonNode.Parse(previous.GetRawText())!.AsObject();
                    metadata["name"] = "concurrent metadata";
                    metadata["revision"] = value + 1;
                    rows[id] = Json(metadata.ToJsonString());
                    Writes++;
                    return Task.FromResult(false);
                }
                rows[id] = body.Clone();
                Writes++;
                return Task.FromResult(true);
            }
        }

        internal Task<IReadOnlyList<CodexThreadDocument>> List(CodexThreadStore store)
        {
            lock (gate) return Task.FromResult<IReadOnlyList<CodexThreadDocument>>(rows.Values.Select(store.ValidateSnapshot).ToArray());
        }
    }

    private sealed class Fixture
    {
        internal Storage Database { get; } = new();
        internal CodexThreadStore Store { get; }
        internal List<(string Method, JsonElement Value)> Events { get; } = [];

        internal Fixture() => Store = new(Database, "account");

        internal CodexThreadRpc Host(bool enable = true)
        {
            var host = new CodexThreadRpc(Store, _ => Database.List(Store), Json("{}"),
                (_, _, _) => throw new InvalidOperationException("Unexpected execution."), (method, value) =>
                {
                    Events.Add((method, value.Clone()));
                    return Task.CompletedTask;
                });
            if (enable) host.EnableThreadReadState();
            return host;
        }

        internal Task<CodexThreadDocument> Create(string settings = "{}") => Store.CreateAsync(Guid.NewGuid(), Json(settings), Ct);

        internal async Task<CodexThreadDocument> Finish(Guid id, string status, string? text = null)
        {
            var thread = await Store.ReadAsync(id, Ct);
            var turnId = Guid.NewGuid();
            var running = await Store.StartTurnAsync(id, thread.Revision, turnId, "owner", [], cancellationToken: Ct);
            return await Store.CompleteAsync(id, running.Revision, turnId, "owner", status, text is null ? [] : [Assistant(text)], Ct);
        }
    }

    private static async Task<JsonElement> Read(CodexThreadRpc host, Guid id)
        => (await host.InvokeAsync("thread/read", Args(id), Ct)).GetProperty("readState");

    private static async Task<JsonElement> Set(CodexThreadRpc host, Guid id, string revision, string operation)
        => (await host.InvokeAsync("thread/readState/update", Mark(id, revision, operation), Ct)).GetProperty("readState");

    [Fact]
    public async Task ExperimentalSurfaceUsesThreadBoundRevisionsAndDoesNotMaterializeOnRead()
    {
        var f = new Fixture();
        var one = await f.Create();
        var other = await f.Create();
        var host = f.Host(enable: false);
        Assert.False((await host.InvokeAsync("thread/read", Args(one.Id), Ct)).TryGetProperty("readState", out _));
        Assert.False((await host.InvokeAsync("thread/list", Json("{}"), Ct)).TryGetProperty("readStates", out _));
        Assert.Equal(-32601, (await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            host.InvokeAsync("thread/readState/update", Mark(one.Id, "initial:" + one.Id, "unread"), Ct))).Code);
        host.EnableThreadReadState();

        var initial = await Read(host, one.Id);
        var another = await Read(host, other.Id);
        Assert.NotEqual(Revision(initial), Revision(another));
        Assert.Null(FirstType(initial));
        Assert.Equal(2, f.Database.Writes);
        Assert.Null((await f.Store.ReadAsync(one.Id, Ct)).ReadReceipt);
        var crossThread = await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            Set(host, other.Id, Revision(initial), "unread"));
        Assert.Equal(-32600, crossThread.Code);
        Assert.Equal("readStateConflict", crossThread.ErrorData!.Value.GetProperty("reason").GetString());
        Assert.True(JsonElement.DeepEquals(another, crossThread.ErrorData.Value.GetProperty("readState")));

        var marked = await Set(host, one.Id, Revision(initial), "unread");
        Assert.Equal("threadStart", FirstType(marked));
        Assert.NotEqual(Revision(initial), Revision(marked));
        var snapshot = await f.Store.ReadAsync(one.Id, Ct);
        Assert.Equal(one.UpdatedAt, snapshot.UpdatedAt);
        var listed = await host.InvokeAsync("thread/list", Json("""{"limit":1}"""), Ct);
        var entry = Assert.Single(listed.GetProperty("data").EnumerateArray());
        var receipts = listed.GetProperty("readStates");
        Assert.Single(receipts.EnumerateObject());
        Assert.Equal(entry.GetProperty("id").GetString(), Assert.Single(receipts.EnumerateObject()).Name);

        var renamed = await f.Store.SetNameAsync(one.Id, snapshot.Revision, "renamed", Ct);
        var cold = f.Host();
        Assert.True(JsonElement.DeepEquals(marked, await Read(cold, one.Id)));
        f.Database.RaceWithMetadata = true;
        var markedAgain = await Set(cold, one.Id, Revision(marked), "unread");
        Assert.NotEqual(Revision(marked), Revision(markedAgain));
        Assert.Equal("concurrent metadata", (await f.Store.ReadAsync(one.Id, Ct)).Name);
        Assert.True((await f.Store.ReadAsync(one.Id, Ct)).Revision > renamed.Revision);
        foreach (var action in new[] { "read", "unread" })
        {
            var stale = await Assert.ThrowsAsync<CodexThreadRpcException>(() => Set(cold, one.Id, Revision(marked), action));
            Assert.Equal(-32600, stale.Code);
            Assert.True(JsonElement.DeepEquals(markedAgain, stale.ErrorData!.Value.GetProperty("readState")));
        }
        var read = await Set(cold, one.Id, Revision(markedAgain), "read");
        Assert.Null(FirstType(read));
        Assert.Equal(3, f.Events.Count(e => e.Method == "thread/readState/changed"));
    }

    [Fact]
    public async Task TerminalReceiptsKeepEarliestUnreadAndSuppressHiddenResultsAndReplay()
    {
        var f = new Fixture();
        var original = await f.Create();
        var host = f.Host();
        var first = await f.Finish(original.Id, "completed", "Ready");
        var readState = await Read(host, original.Id);
        var firstTurn = first.Turns[^1].Id;
        Assert.Equal(firstTurn.ToString(), readState.GetProperty("firstUnread").GetProperty("turnId").GetString());
        var second = await f.Finish(original.Id, "completed", "Another result");
        var secondReceipt = await Read(host, original.Id);
        Assert.Equal(firstTurn.ToString(), secondReceipt.GetProperty("firstUnread").GetProperty("turnId").GetString());
        Assert.NotEqual(Revision(readState), Revision(secondReceipt));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Set(host, original.Id, Revision(readState), "read"));
        var marked = await Set(host, original.Id, Revision(secondReceipt), "read");
        Assert.Null(FirstType(marked));
        Assert.Equal(second.Turns[^1].Id, (await f.Store.ReadAsync(original.Id, Ct)).ReadReceipt!.LastPublishedTurnId);

        await f.Finish(original.Id, "completed", "<decision>DONT_NOTIFY</decision>");
        await f.Finish(original.Id, "completed", "   ");
        await f.Finish(original.Id, "interrupted");
        Assert.True(JsonElement.DeepEquals(marked, await Read(host, original.Id)));

        var failed = await f.Finish(original.Id, "failed");
        var failureReceipt = await Read(host, original.Id);
        Assert.Equal(failed.Turns[^1].Id.ToString(), failureReceipt.GetProperty("firstUnread").GetProperty("turnId").GetString());
        var running = await f.Store.StartTurnAsync(original.Id, failed.Revision, Guid.NewGuid(), "lost-owner", [], cancellationToken: Ct);
        var recovered = await f.Store.RecoverInterruptedAsync(original.Id, running.Revision, running.Turns[^1].Id, "lost-owner", Ct);
        Assert.True(JsonElement.DeepEquals(failureReceipt, await Read(host, original.Id)));
        var fork = await f.Store.ForkAsync(original.Id, recovered.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.ReadReceipt);
        Assert.Null(FirstType(await Read(host, fork.Id)));
    }

    [Fact]
    public async Task IneligibleSourcesAreExcludedAndArchivedThreadsRetainReceipts()
    {
        var f = new Fixture();
        var host = f.Host();
        foreach (var settings in new[] { """{"ephemeral":true}""", """{"threadSource":"subagent"}""", """{"threadSource":"guardianReview"}""", """{"source":"internal"}""" })
        {
            var thread = await f.Create(settings);
            Assert.False((await host.InvokeAsync("thread/read", Args(thread.Id), Ct)).TryGetProperty("readState", out _));
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => Set(host, thread.Id, "initial:" + thread.Id, "unread"));
            var terminal = await f.Finish(thread.Id, "completed", "Internal result");
            Assert.Null(terminal.ReadReceipt);
        }
        var original = await f.Create();
        var before = await Set(host, original.Id, "initial:" + original.Id, "unread");
        var saved = await f.Store.ReadAsync(original.Id, Ct);
        await f.Store.ArchiveAsync(original.Id, saved.Revision, Ct);
        var archived = await host.InvokeAsync("thread/list", Json("""{"archived":true}"""), Ct);
        Assert.True(JsonElement.DeepEquals(before, archived.GetProperty("readStates").GetProperty(original.Id.ToString())));
        Assert.Null(FirstType(await Set(host, original.Id, Revision(before), "read")));
    }

    [Fact]
    public async Task NewOwnedTerminalPublishesCommittedReadStateNotification()
    {
        var f = new Fixture();
        var thread = await f.Create();
        var host = f.Host();
        var turnId = Guid.NewGuid();
        await host.RunOwnedAsync(thread, turnId, new CodexResponseOptions("test", ""), [],
            async (run, started, ct) =>
            {
                var running = await f.Store.StartTurnAsync(run.Thread.Id, run.Thread.Revision, run.TurnId, run.OwnerId, run.Input, cancellationToken: ct);
                await started(running);
                return await f.Store.CompleteAsync(run.Thread.Id, running.Revision, run.TurnId, run.OwnerId, "completed", [Assistant("Ready")], ct);
            }, Ct);
        var receipt = await Read(host, thread.Id);
        var notification = Assert.Single(f.Events, e => e.Method == "thread/readState/changed").Value;
        Assert.Equal(thread.Id.ToString(), notification.GetProperty("threadId").GetString());
        Assert.True(JsonElement.DeepEquals(receipt, notification.GetProperty("readState")));
        Assert.Equal(turnId.ToString(), receipt.GetProperty("firstUnread").GetProperty("turnId").GetString());
        Assert.Equal(0, host.NotificationFailures);
    }
}
