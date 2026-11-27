using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class NativeBackingArtifactTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => RealtimeHistoryTests.Json(value);
    private static CodexThreadRpc Rpc(CodexThreadStore store) => new(store,
        _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]), Json("{}"),
        (_, _, _) => throw new InvalidOperationException("No execution allowed by artifact reads."),
        (_, _) => Task.CompletedTask);
    private static JsonObject[] Promotions(CodexThreadDocument document) => CodexThreadStore.ProjectRealtimeItems(document)
        .Where(x => (string?)x["type"] == "bemItemPromoted").ToArray();

    [Fact]
    public async Task ImageAndSubagentPresentationPreservesIdentityAcrossCompletionAndVoiceClosure()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", "request", "inference", Json("{}"), Ct)).Thread;
        thread = (await store.RecordRealtimeObservationAsync(thread.Id, thread.Revision, "session", "owner", attachment, "voice-before",
            RealtimeHistoryTests.Event("""{"type":"output_transcript.added","item":{"text":"Before image"}}"""), Ct)).Thread;
        async Task Observe(CodexNativeArtifactStage stage, string wire) => thread = (await store.ObserveNativeBackingArtifactAsync(
            thread.Id, thread.Revision, turn, "runner", "request", stage, Json(wire), Ct)).Thread;
        await Observe(CodexNativeArtifactStage.Started, """{"type":"imageGeneration","id":"image","status":"in_progress","result":""}""");
        var promotion = Assert.Single(Promotions(thread));
        Assert.Equal("image", (string?)promotion["item_id"]);
        var items = CodexThreadStore.ProjectRealtimeItems(thread);
        Assert.Equal(["realtimeSessionStarted", "frame", "bemItemPromoted"], items.Select(x => (string?)x["type"]).ToArray());
        await Observe(CodexNativeArtifactStage.Completed, """{"type":"imageGeneration","id":"image","status":"completed","fileId":"file_observed"}""");
        Assert.Equal(promotion.ToJsonString(), Assert.Single(Promotions(thread)).ToJsonString());
        await Observe(CodexNativeArtifactStage.Started, """{"type":"subAgentActivity","id":"child","kind":"started","agentThreadId":"child-thread","agentPath":"root/child"}""");
        Assert.Single(Promotions(thread));
        await Observe(CodexNativeArtifactStage.Completed, """{"type":"subAgentActivity","id":"child","kind":"started","agentThreadId":"child-thread","agentPath":"root/child"}""");
        await Observe(CodexNativeArtifactStage.Completed, """{"type":"subAgentActivity","id":"interaction","kind":"interacted","agentThreadId":"child-thread","agentPath":"root/child"}""");
        Assert.Equal(["image", "child"], Promotions(thread).Select(x => (string?)x["item_id"]).ToArray());
        var end = Guid.NewGuid();
        thread = (await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", attachment, end, Ct)).Thread;
        thread = await store.CompleteRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", end, Ct);
        await Observe(CodexNativeArtifactStage.Completed, """{"type":"imageGeneration","id":"late-image","status":"completed","fileId":"late-file"}""");
        var after = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(["image", "child", "late-image"], Promotions(after).Select(x => (string?)x["item_id"]).ToArray());
        Assert.All(Promotions(after), item => Assert.Equal("session", (string?)item["realtimeSessionId"]));
        Assert.Empty(after.Turns.Single().Output);

        var rpc = Rpc(store);
        var page = await rpc.InvokeAsync("thread/items/list", Json(new JsonObject
        {
            ["threadId"] = thread.Id.ToString(), ["turnId"] = turn.ToString(), ["sortDirection"] = "asc",
        }.ToJsonString()), Ct);
        var rows = page.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(["image", "child", "interaction", "late-image"], rows.Select(r => r.GetProperty("item").GetProperty("id").GetString()).ToArray());
        Assert.Equal("file_observed", rows[0].GetProperty("item").GetProperty("fileId").GetString());
        Assert.Equal(JsonValueKind.Number, rows[0].GetProperty("startedAtMs").ValueKind);
        Assert.Equal(JsonValueKind.Number, rows[0].GetProperty("completedAtMs").ValueKind);
        var read = await rpc.InvokeAsync("thread/items/read", Json(new JsonObject
        {
            ["threadId"] = thread.Id.ToString(), ["turnId"] = turn.ToString(),
            ["itemIds"] = new JsonArray(JsonValue.Create("image")),
        }.ToJsonString()), Ct);
        Assert.True(JsonElement.DeepEquals(rows[0], Assert.Single(read.GetProperty("data").EnumerateArray())));
    }

    [Fact]
    public async Task OnlyCompletedCodexAppMcpCallPromotesAndReplacesItsExactRawCallProjection()
    {
        var (_, store, thread, _) = await RealtimeHistoryTests.Setup();
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "runner", [], cancellationToken: Ct);
        string[] ids = ["app", "other", "failed"];
        JsonElement Call(string id) => Json(new JsonObject
        {
            ["type"] = "function_call", ["id"] = id + "-item", ["call_id"] = id, ["name"] = "lookup", ["arguments"] = "{}",
        }.ToJsonString());
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "runner", ids.Select(Call).ToArray(), Ct);
        JsonElement Item(string id, string server, string status) => Json(new JsonObject
        {
            ["type"] = "mcpToolCall", ["id"] = id + "-item", ["server"] = server,
            ["tool"] = "lookup", ["status"] = status, ["arguments"] = new JsonObject(),
        }.ToJsonString());
        foreach (var id in ids)
        {
            var server = id == "other" ? "other_server" : "codex_app";
            thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "runner", id, "tool", Call(id), Ct)).Thread;
            thread = (await store.ObserveNativeBackingArtifactAsync(thread.Id, thread.Revision, turn, "runner", id,
                CodexNativeArtifactStage.Started, Item(id, server, "inProgress"), Ct)).Thread;
            thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "runner", id, "succeeded",
                Json(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = "observed" }.ToJsonString()), Ct);
            var terminal = Item(id, server, id == "failed" ? "failed" : "completed");
            thread = (await store.ObserveNativeBackingArtifactAsync(thread.Id, thread.Revision, turn, "runner", id,
                CodexNativeArtifactStage.Completed, terminal, Ct)).Thread;
            var replay = await store.ObserveNativeBackingArtifactAsync(thread.Id, thread.Revision, turn, "runner", id,
                CodexNativeArtifactStage.Completed, terminal, Ct);
            Assert.False(replay.Admitted);
            Assert.Equal(thread.Revision, replay.Thread.Revision);
        }
        Assert.Equal("app-item", (string?)Assert.Single(Promotions(thread))["item_id"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ObserveNativeBackingArtifactAsync(
            thread.Id, thread.Revision, turn, "wrong-owner", "app", CodexNativeArtifactStage.Completed, Item("app", "codex_app", "completed"), Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ObserveNativeBackingArtifactAsync(
            thread.Id, thread.Revision, turn, "runner", "other", CodexNativeArtifactStage.Completed, Item("app", "codex_app", "completed"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ObserveNativeBackingArtifactAsync(
            thread.Id, thread.Revision, turn, "runner", "app", CodexNativeArtifactStage.Completed, Item("app", "codex_app", "failed"), Ct));
        var rpc = Rpc(store);
        var page = await rpc.InvokeAsync("thread/items/list", Json(new JsonObject
        {
            ["threadId"] = thread.Id.ToString(), ["turnId"] = turn.ToString(), ["sortDirection"] = "asc",
        }.ToJsonString()), Ct);
        var projected = page.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("item")).ToArray();
        Assert.Equal(ids.Select(id => id + "-item"), projected.Select(i => i.GetProperty("id").GetString()));
        Assert.All(projected, item => Assert.Equal("mcpToolCall", item.GetProperty("type").GetString()));
        Assert.Equal(3, (await store.ReadAsync(thread.Id, Ct)).Turns.Single().Output.Length);
    }
}
