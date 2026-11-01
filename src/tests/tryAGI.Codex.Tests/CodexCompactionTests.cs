using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using tryAGI.Codex;

namespace tryAGI.Codex.Tests;

public sealed class CodexCompactionTests
{
    private const string Checkpoint = """{"type":"compaction","id":"compact-1","encrypted_content":"opaque","internal_chat_message_metadata_passthrough":{"key":"preserved"}}""";
    private const string Call = """{"type":"function_call","call_id":"already-executed","name":"action","arguments":"{}"}""";
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static string Done(string item, int index = 0) => "data: " + new JsonObject
    { ["type"] = "response.output_item.done", ["output_index"] = index, ["item"] = JsonNode.Parse(item) }.ToJsonString() + "\n\n";
    private static string Complete(params string[] items) => "data: " + new JsonObject
    { ["type"] = "response.completed", ["response"] = new JsonObject { ["id"] = "response-1", ["status"] = "completed",
        ["usage"] = new JsonObject { ["input_tokens"] = 5, ["output_tokens"] = 2 },
        ["output"] = new JsonArray(items.Select(item => JsonNode.Parse(item)).ToArray()) } }.ToJsonString() + "\n\n";
    private sealed class Credentials : ICodexCredentialProvider
    { public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) => ValueTask.FromResult(new CodexCredentials("test", "test")); }
    private sealed class Handler(Func<JsonElement, string> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.EndsWith("/responses", (request.RequestUri ?? throw new InvalidDataException("Required data was null.")).AbsolutePath);
            using var body = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(respond(body.RootElement), Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class Accounting(bool deny = false, bool failComplete = false) : ICodexUsageObserver
    {
        public List<string> Events { get; } = [];
        public Task ReserveAsync(string id, string model, CancellationToken ct)
        { Events.Add("reserved"); if (deny) throw new InvalidOperationException("budget"); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        { Events.Add("complete"); if (failComplete) throw new InvalidOperationException("persistence"); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Events.Add("unknown"); return Task.CompletedTask; }
    }
    private sealed class DelayedAccounting : ICodexUsageObserver
    {
        public TaskCompletionSource Completing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReserveAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public async Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        { Completing.TrySetResult(); await Release.Task.WaitAsync(ct); }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task CheckpointWaitsForDurableFinalUsage()
    {
        var accounting = new DelayedAccounting();
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Complete(Checkpoint)), usageObserver: accounting);
        var compaction = client.CompactAsync(new("test", "test"), [], TestContext.Current.CancellationToken);
        await accounting.Completing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(compaction.IsCompleted);
        accounting.Release.SetResult();
        Assert.Equal("response-1", (await compaction).ResponseId);
    }

    [Fact]
    public async Task UsesRustTriggerAndRetainsOriginalHistoryAndIndependentExecutedIds()
    {
        var history = new[] { Json("""{"role":"user","content":"Remember input"}"""), Json(Call),
            Json("""{"type":"function_call_output","call_id":"already-executed","output":"returned"}""") };
        var original = history.Select(item => item.GetRawText()).ToArray();
        var accounting = new Accounting();
        using var client = new CodexClient(new Credentials(), usageObserver: accounting, handler: new Handler(body =>
        {
            Assert.Equal("compaction_trigger", body.GetProperty("input")[3].GetProperty("type").GetString());
            Assert.True(body.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.False(body.TryGetProperty("text", out _));
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.Single(body.GetProperty("tools").EnumerateArray());
            return Done(Call) + Done(Checkpoint, 1) + Complete(Call, Checkpoint);
        }));
        var result = await client.CompactAsync(new("test", "test") { OutputSchema = Json("{\"type\":\"object\"}"),
            Tools = [Json("""{"type":"function","name":"action","parameters":{"type":"object"}}""")] }, history, previouslyExecutedCallIds: ["older-call"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "reserved", "complete" }, accounting.Events);
        Assert.Equal("response-1", result.ResponseId);
        Assert.Equal(5, (result.Usage ?? throw new InvalidDataException("Required data was null.")).GetProperty("input_tokens").GetInt32());
        Assert.Equal(original, result.OriginalHistory.Select(item => item.GetRawText()));
        Assert.Equal(original, history.Select(item => item.GetRawText()));
        Assert.Equal(new[] { "already-executed", "older-call" }, result.ExecutedCallIds);
        Assert.Equal("preserved", result.Item.GetProperty("internal_chat_message_metadata_passthrough").GetProperty("key").GetString());
        var projected = result.CreatePrompt([history[0]]);
        Assert.Equal(2, projected.Count);
        Assert.Equal("compaction", projected[1].GetProperty("type").GetString());
        Assert.Throws<ArgumentException>(() => result.CreatePrompt([history[1]]));
    }

    [Fact]
    public async Task CompactedHistoryCannotReplayAnEarlierCall()
    {
        using var compactClient = new CodexClient(new Credentials(), handler: new Handler(_ => Complete(Checkpoint)));
        var compacted = await compactClient.CompactAsync(new("test", "test"), [Json(Call)], TestContext.Current.CancellationToken);
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Complete(Call)));
        var executed = false;
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(new("test", "test")
            { Tools = [Json("""{"type":"function","name":"action","parameters":{"type":"object"}}""")] }, compacted.CreatePrompt([]), (_, _) => { executed = true; return Task.FromResult("not allowed"); }, previouslyExecutedCallIds: compacted.ExecutedCallIds, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(executed);
    }

    [Fact]
    public async Task ContinuationSeesCheckpointAndPreservesIdsInItsResult()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(body =>
        {
            Assert.Equal("opaque", body.GetProperty("input")[0].GetProperty("encrypted_content").GetString());
            return Complete("""{"type":"message","content":[{"type":"output_text","text":"answer"}]}""");
        }));
        var result = await new CodexAgent(client).RunAsync(new("test", "test"), [Json(Checkpoint)], (_, _) => throw new InvalidOperationException(), previouslyExecutedCallIds: ["earlier"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "earlier" }, result.ExecutedCallIds);
        Assert.Equal("answer", result.Text);
    }

    [Theory]
    [InlineData("{\"type\":\"compaction_summary\",\"encrypted_content\":\"opaque\"}")]
    [InlineData(Checkpoint)]
    public async Task AcceptsReviewedAliasAndDoneOnlyTerminal(string item)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(_ => Done(item) + Complete()));
        var result = await client.CompactAsync(new("test", "test"), [], TestContext.Current.CancellationToken);
        Assert.Equal("compaction", result.Item.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("different_type")]
    [InlineData("conflict")]
    [InlineData("gap")]
    [InlineData("missing_terminal")]
    [InlineData("missing_id")]
    [InlineData("incomplete_status")]
    public async Task InvalidCheckpointDoesNotInstallOrRetry(string failure)
    {
        var response = failure switch
        {
            "none" => Complete(Call),
            "duplicate" => Complete(Checkpoint, Checkpoint),
            "empty" => Complete("""{"type":"compaction","encrypted_content":" "}"""),
            "different_type" => Complete("""{"type":"context_compaction","encrypted_content":"opaque"}"""),
            "conflict" => Done(Checkpoint) + Complete(Checkpoint.Replace("opaque", "other")),
            "gap" => Done(Checkpoint, 1) + Complete(),
            "missing_terminal" => Done(Checkpoint),
            "missing_id" => Complete(Checkpoint).Replace("\"id\":\"response-1\",", ""),
            _ => Complete(Checkpoint).Replace("\"status\":\"completed\"", "\"status\":\"incomplete\""),
        };
        var handler = new Handler(_ => response);
        using var client = new CodexClient(new Credentials(), handler: handler);
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.CompactAsync(new("test", "test"), [], TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BudgetAndAccountingFailurePreventCheckpointPublication(bool deny, bool failComplete)
    {
        var accounting = new Accounting(deny, failComplete);
        var handler = new Handler(_ => Complete(Checkpoint));
        using var client = new CodexClient(new Credentials(), handler: handler, usageObserver: accounting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompactAsync(new("test", "test"), [], TestContext.Current.CancellationToken));
        Assert.Equal(deny ? 0 : 1, handler.Requests);
        Assert.Equal(deny ? new[] { "reserved" } : new[] { "reserved", "complete", "unknown" }, accounting.Events);
    }

    [Fact]
    public async Task TriggerAndInvalidExecutedIdsAreRejectedBeforeAdmission()
    {
        var accounting = new Accounting();
        var handler = new Handler(_ => Complete(Checkpoint));
        using var client = new CodexClient(new Credentials(), handler: handler, usageObserver: accounting);
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompactAsync(new("test", "test"), [Json("{\"type\":\"compaction_trigger\"}")], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompactAsync(new("test", "test"), [], previouslyExecutedCallIds: [" "], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
        Assert.Empty(accounting.Events);
    }

    [Fact]
    public async Task CompactedInputRequiresExplicitSeparateCallLedgerBeforeNetwork()
    {
        var accounting = new Accounting();
        var handler = new Handler(_ => Complete(Checkpoint));
        using var client = new CodexClient(new Credentials(), handler: handler, usageObserver: accounting);
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompactAsync(new("test", "test"), [Json(Checkpoint)], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => new CodexAgent(client).RunAsync(new("test", "test"), [Json(Checkpoint)], (_, _) => throw new InvalidOperationException(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(accounting.Events);
        Assert.Equal(0, handler.Requests);
        var second = await client.CompactAsync(new("test", "test"), [Json(Checkpoint)], previouslyExecutedCallIds: [], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(second.ExecutedCallIds);
    }
}
