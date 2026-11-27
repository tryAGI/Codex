using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class ThreadCompactionCheckpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static JsonElement User(string text) => Json(new JsonObject { ["role"] = "user", ["content"] = text }.ToJsonString());
    private static readonly JsonElement Encrypted = Json("""{"type":"reasoning","encrypted_content":"original-opaque","summary":[]}""");
    private static readonly JsonElement ToolCall = Json("""{"type":"function_call","call_id":"executed","name":"read","arguments":"{}"}""");
    private static readonly JsonElement ToolOutput = Json("""{"type":"function_call_output","call_id":"executed","output":"receipt"}""");
    private static readonly JsonElement Checkpoint = Json("""{"type":"compaction","id":"checkpoint","encrypted_content":"compacted-opaque","future_metadata":{"key":3}}""");

    private static CodexResponseOptions Settings(Guid thread, Guid turn) => new("model", "instructions")
    { Identity = new("session", thread.ToString(), turn.ToString()) };

    private static async Task<CodexThreadDocument> CloseTurn(CodexThreadStore store, CodexThreadDocument thread, bool interrupted = false)
    {
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", [User("original user input")], cancellationToken: Ct);
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "owner", "executed", "tool", ToolCall, Ct)).Thread;
        if (interrupted)
            return await store.RecoverInterruptedAsync(thread.Id, thread.Revision, turn, "owner", Ct);
        thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "owner", "executed", "succeeded", ToolOutput, Ct);
        return await store.CompleteAsync(thread.Id, thread.Revision, turn, "owner", "completed", [Encrypted, ToolCall, ToolOutput], Ct);
    }

    private static async Task<(RealtimeHistoryTests.Storage Storage, CodexThreadStore Store, CodexThreadDocument Thread)> Setup(bool interrupted = false)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        return (storage, store, await CloseTurn(store, thread, interrupted));
    }

    private static CodexCompactionResult Result(CodexThreadCompactionCandidate candidate) =>
        new(candidate.Model, "response-id", Checkpoint, Json("""{"input_tokens":15,"output_tokens":3}"""),
            candidate.History, candidate.ExecutedCallIds);

    [Fact]
    public async Task CasInstallRestartContinuationAndForkPreserveOriginalHistory()
    {
        var (storage, store, original) = await Setup();
        var settings = Settings(original.Id, Guid.NewGuid());
        var candidate = await store.CaptureCompactionAsync(original.Id, original.Revision, settings, Ct);
        var installed = await store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct);
        Assert.Equal(original.Revision + 1, installed.Revision);
        Assert.Equal(original.Turns[0].Output.Select(x => x.GetRawText()), installed.Turns[0].Output.Select(x => x.GetRawText()));
        Assert.Equal("executed", Assert.Single(CodexThreadStore.DispatchedToolCallIds(installed)));
        var restored = new CodexThreadStore(storage, "account");
        var thread = await restored.ReadAsync(original.Id, Ct);
        Assert.Equal("response-id", thread.CompactionCheckpoint!.ResponseId);
        Assert.Equal("compacted-opaque", thread.CompactionCheckpoint.Projection.Last().GetProperty("encrypted_content").GetString());
        var next = await restored.StartTurnAsync(thread.Id, thread.Revision, Guid.Parse(settings.Identity!.TurnId), "owner",
            [User("new")], cancellationToken: Ct);
        var projected = CodexThreadCompaction.Project(next, next.CompactionCheckpoint, settings.Model,
            CodexThreadCompaction.ScopeHash(settings), next.Turns.Length, currentTurnId: next.Turns[^1].Id);
        Assert.Equal(3, projected.Count);
        Assert.Equal("compaction", projected[1].GetProperty("type").GetString());
        Assert.Equal("new", projected[2].GetProperty("content").GetString());
        Assert.Single(CodexThreadStore.DispatchedToolCallIds(next));
        var fork = await restored.ForkAsync(next.Id, next.Revision, Guid.NewGuid(), lastTurnId: original.Turns[0].Id, cancellationToken: Ct);
        Assert.Null(fork.CompactionCheckpoint);
        Assert.Equal(3, fork.Turns[0].Output.Length);
    }

    [Fact]
    public async Task ConcurrentTurnAndSteerRejectStaleBackgroundCandidate()
    {
        var (_, store, thread) = await Setup();
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, Settings(thread.Id, Guid.NewGuid()), Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", [User("new")], cancellationToken: Ct);
        thread = await store.AppendInputAsync(thread.Id, thread.Revision, turn, "owner", [User("steer")], Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct));
        var after = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal(thread.Revision, after.Revision);
        Assert.Null(after.CompactionCheckpoint);
        Assert.Equal(2, after.Turns[^1].Input.Length);
    }

    [Fact]
    public async Task CancelledInvalidAndAmbiguousWritesNeverAuthorizeAnotherInference()
    {
        var (storage, store, thread) = await Setup();
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, Settings(thread.Id, Guid.NewGuid()), Ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => store.InstallCompactionAsync(candidate,
            Result(candidate) with { Item = Json("""{"type":"compaction","encrypted_content":""}""") }, cancellationToken: Ct));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: cancelled.Token));
        Assert.Null((await store.ReadAsync(thread.Id, Ct)).CompactionCheckpoint);
        storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() => store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct));
        var observed = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal("response-id", observed.CompactionCheckpoint!.ResponseId);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct));
    }

    [Fact]
    public async Task LateReceiptChangesCoveredPrefixAndInvalidatesOnlyTheProjection()
    {
        var (_, store, thread) = await Setup(interrupted: true);
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, Settings(thread.Id, Guid.NewGuid()), Ct);
        thread = await store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct);
        Assert.NotNull(thread.CompactionCheckpoint);
        thread = await store.SettleAsync(thread.Id, thread.Revision, thread.Turns[0].Id, "owner", "executed", "succeeded", ToolOutput, Ct);
        Assert.Null(thread.CompactionCheckpoint);
        Assert.Equal("succeeded", Assert.Single(thread.Turns[0].Dispatches).State);
        Assert.Equal("executed", Assert.Single(CodexThreadStore.DispatchedToolCallIds(thread)));
    }

    [Fact]
    public async Task CarryoverAtCheckpointBoundaryRemainsInNextSeedExactlyOnce()
    {
        var (_, store, thread, attachment) = await RealtimeHistoryTests.Setup();
        thread = await CloseTurn(store, thread);
        var settings = Settings(thread.Id, Guid.NewGuid());
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        thread = await store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct);
        thread = await RealtimeHistoryTests.Observe(store, thread, attachment, "late",
            """{"type":"input_transcript.added","item":{"text":"new voice transcript"}}""");
        var next = await store.StartTurnAsync(thread.Id, thread.Revision, Guid.Parse(settings.Identity!.TurnId), "owner",
            [User("next")], cancellationToken: Ct);
        var seed = CodexThreadCompaction.Project(next, next.CompactionCheckpoint, settings.Model,
            CodexThreadCompaction.ScopeHash(settings), next.Turns.Length, currentTurnId: next.Turns[^1].Id);
        Assert.NotNull(next.CompactionCheckpoint);
        Assert.Single(seed, item => item.GetRawText().Contains("new voice transcript", StringComparison.Ordinal));
        Assert.Equal("next", seed[^1].GetProperty("content").GetString());
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CodexCredentials("test", "account"));
    }

    private sealed class Budget : ICodexUsageObserver
    {
        internal int Reserved, Completed, Incomplete;
        public Task ReserveAsync(string id, string model, CancellationToken ct) { Reserved++; return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) { Completed++; return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Incomplete++; return Task.CompletedTask; }
    }

    private sealed class Server(bool incomplete = false)
    {
        internal List<JsonElement> Requests { get; } = [];
        internal HttpMessageHandler Handler() => new Transport(this);
        private sealed class Transport(Server owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var payload = Json(await request.Content!.ReadAsStringAsync(ct));
                owner.Requests.Add(payload);
                var compacting = payload.GetProperty("input").EnumerateArray().Any(item =>
                    item.TryGetProperty("type", out var type) && type.GetString() == "compaction_trigger");
                string response;
                if (compacting && owner.Incomplete)
                    response = """{"type":"response.incomplete","response":{"id":"incomplete","usage":{"output_tokens":2}}}""";
                else if (compacting)
                    response = "{\"type\":\"response.completed\",\"response\":{\"id\":\"compact-response\",\"output\":[" + Checkpoint.GetRawText() + "]}}";
                else
                    response = """{"type":"response.completed","response":{"id":"reasoning-response","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}}""";
                return new(HttpStatusCode.OK) { Content = new StringContent("data: " + response + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
        }
        private bool Incomplete => incomplete;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThresholdCompactionRunsBeforeNewTurnAndRecordsUsageEvenOnFailure(bool incomplete)
    {
        var (_, store, thread) = await Setup();
        var budget = new Budget();
        var server = new Server(incomplete);
        var agent = new CodexThreadAgent(store, new Credentials(), budget,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: server.Handler(), usageObserver: usage));
        var turn = Guid.NewGuid();
        var options = Settings(thread.Id, turn) with { AutomaticCompaction = new() { TokenThreshold = 1 } };
        Task<CodexThreadAgentResult> Run() => agent.RunAsync(thread.Id, thread.Revision, turn, "owner", options, [User("next")],
            (_, _) => throw new InvalidOperationException("Never replay a prior tool."), cancellationToken: Ct);
        if (incomplete)
        {
            await Assert.ThrowsAsync<CodexProtocolException>(Run);
            Assert.Equal(1, budget.Incomplete);
            Assert.Equal(0, budget.Completed);
            Assert.Null((await store.ReadAsync(thread.Id, Ct)).CompactionCheckpoint);
            Assert.Single(server.Requests);
        }
        else
        {
            var result = await Run();
            Assert.Equal(2, budget.Completed);
            Assert.Equal(2, server.Requests.Count);
            var continuation = server.Requests[1].GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal("compaction", continuation[^2].GetProperty("type").GetString());
            Assert.Equal("next", continuation[^1].GetProperty("content").GetString());
            Assert.Equal("compact-response", result.Thread.CompactionCheckpoint!.ResponseId);
            Assert.Equal(2, result.Thread.Turns.Length);
            Assert.Equal("original-opaque", result.Thread.Turns[0].Output[0].GetProperty("encrypted_content").GetString());
        }
    }
}
