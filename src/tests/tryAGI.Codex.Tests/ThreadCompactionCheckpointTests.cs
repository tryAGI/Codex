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

    private sealed class Server(bool incomplete = false, bool midTurn = false, int toolRounds = 1)
    {
        internal List<JsonElement> Requests { get; } = [];
        internal TaskCompletionSource? CompactionEntered { get; init; }
        internal TaskCompletionSource? ReleaseCompaction { get; init; }
        private int reasoningCalls;
        private int compactionCalls;
        private int ToolRounds => toolRounds;
        internal HttpMessageHandler Handler() => new Transport(this);
        private sealed class Transport(Server owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var payload = Json(await request.Content!.ReadAsStringAsync(ct));
                owner.Requests.Add(payload);
                var compacting = payload.GetProperty("input").EnumerateArray().Any(item =>
                    item.TryGetProperty("type", out var type) && type.GetString() == "compaction_trigger");
                if (compacting && owner.ReleaseCompaction is { } release)
                {
                    owner.CompactionEntered?.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                string response;
                if (compacting && owner.Incomplete)
                    response = """{"type":"response.incomplete","response":{"id":"incomplete","usage":{"output_tokens":2}}}""";
                else if (compacting)
                {
                    var count = ++owner.compactionCalls;
                    var id = count == 1 ? "compact-response" : $"compact-response-{count}";
                    var checkpoint = count == 1 ? Checkpoint.GetRawText() :
                        Checkpoint.GetRawText().Replace("compacted-opaque", $"compacted-opaque-{count}", StringComparison.Ordinal);
                    response = "{\"type\":\"response.completed\",\"response\":{\"id\":\"" + id + "\",\"output\":[" + checkpoint + "]}}";
                }
                else if (owner.MidTurn && ++owner.reasoningCalls <= owner.ToolRounds)
                {
                    var count = owner.reasoningCalls;
                    var call = count == 1 ? ToolCall.GetRawText() :
                        ToolCall.GetRawText().Replace("executed", $"executed-{count}", StringComparison.Ordinal);
                    response = "{\"type\":\"response.completed\",\"response\":{\"id\":\"reasoning-tool-response-" + count + "\",\"output\":[" +
                        Encrypted.GetRawText() + "," + call + "]}}";
                }
                else
                    response = """{"type":"response.completed","response":{"id":"reasoning-response","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}}""";
                return new(HttpStatusCode.OK) { Content = new StringContent("data: " + response + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
        }
        private bool Incomplete => incomplete;
        private bool MidTurn => midTurn;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineCompactionPreservesOriginalToolJournalAndSettlesEveryRequest(bool incomplete)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var budget = new Budget();
        var server = new Server(incomplete, midTurn: true);
        var agent = new CodexThreadAgent(store, new Credentials(), budget,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: server.Handler(), usageObserver: usage));
        var turn = Guid.NewGuid();
        var options = Settings(thread.Id, turn) with
        {
            Tools = [Json("""{"type":"function","name":"read","description":"Read test data","parameters":{"type":"object","properties":{}}}""")],
            AutomaticCompaction = new() { TokenThreshold = 1 },
        };
        var calls = 0;
        Task<CodexThreadAgentResult> Run() => agent.RunAsync(thread.Id, thread.Revision, turn, "owner", options,
            [User("first input")], (_, _) =>
            {
                calls++;
                return Task.FromResult(new CodexToolOutput("receipt"));
            }, cancellationToken: Ct);
        if (incomplete)
        {
            await Assert.ThrowsAsync<CodexProtocolException>(Run);
            var failed = await store.ReadAsync(thread.Id, Ct);
            Assert.Null(failed.CompactionCheckpoint);
            Assert.Equal("failed", failed.Turns[^1].Status);
            Assert.Equal(2, budget.Reserved);
            Assert.Equal(1, budget.Completed);
            Assert.Equal(1, budget.Incomplete);
            Assert.Equal(3, failed.Turns[^1].Output.Length);
            Assert.Equal("receipt", failed.Turns[^1].Output[^1].GetProperty("output").GetString());
        }
        else
        {
            var result = await Run();
            Assert.Equal("done", result.Result.Text);
            Assert.Equal(3, budget.Completed);
            Assert.Equal(3, budget.Reserved);
            Assert.Equal(3, server.Requests.Count);
            var compacting = server.Requests[1].GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal("compaction_trigger", compacting[^1].GetProperty("type").GetString());
            Assert.Contains(compacting, item => JsonElement.DeepEquals(item, ToolOutput));
            var continuation = server.Requests[2].GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal("compacted-opaque", continuation[^1].GetProperty("encrypted_content").GetString());
            Assert.DoesNotContain(continuation, item => CodexRealtimeProtocol.Text(item, "call_id") == "executed");
            var stored = Assert.Single(result.Thread.Turns);
            Assert.Equal(4, stored.Output.Length);
            Assert.Equal("original-opaque", stored.Output[0].GetProperty("encrypted_content").GetString());
            Assert.Equal(3, stored.Dispatches.Count(receipt => receipt.Kind == "inference"));
            Assert.All(stored.Dispatches, receipt => Assert.Equal("succeeded", receipt.State));
            var restored = new CodexThreadStore(storage, "account");
            var observed = await restored.ReadAsync(thread.Id, Ct);
            Assert.Equal(turn, observed.CompactionCheckpoint!.ActiveBoundary!.TurnId);
            Assert.Equal(3, observed.CompactionCheckpoint.ActiveBoundary.OutputCount);
            Assert.Equal("compact-response", observed.CompactionCheckpoint.ResponseId);
            var next = Guid.NewGuid();
            observed = await restored.StartTurnAsync(thread.Id, observed.Revision, next, "owner", [User("next")], cancellationToken: Ct);
            var seed = CodexThreadCompaction.Project(observed, observed.CompactionCheckpoint, options.Model,
                CodexThreadCompaction.ScopeHash(options), observed.Turns.Length, currentTurnId: next);
            Assert.Equal("next", seed[^1].GetProperty("content").GetString());
            Assert.True(JsonElement.DeepEquals(stored.Output[^1], seed[^2]));
            Assert.Single(seed, item => CodexRealtimeProtocol.Text(item, "type") == "compaction");
            Assert.Equal("executed", Assert.Single(CodexThreadStore.DispatchedToolCallIds(observed)));
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InlineCandidateCannotInstallAfterConcurrentInput()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", [User("first")], cancellationToken: Ct);
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "owner", [Encrypted], Ct);
        var candidate = CodexThreadStore.CaptureActiveCompaction(thread, turn, "owner", Settings(thread.Id, turn))!;
        var result = Result(candidate);
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "owner", "compact", "inference", Json("{}"), Ct)).Thread;
        thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "owner", "compact", "succeeded",
            Json("""{"id":"response-id"}"""), Ct);
        thread = await store.AppendInputAsync(thread.Id, thread.Revision, turn, "owner", [User("new input")], Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.InstallActiveCompactionAsync(candidate, result, "compact", "owner", null, Ct));
        var observed = await store.ReadAsync(thread.Id, Ct);
        Assert.Null(observed.CompactionCheckpoint);
        Assert.Equal("succeeded", Assert.Single(observed.Turns[^1].Dispatches).State);
        Assert.Equal(2, observed.Turns[^1].Input.Length);
        Assert.True(JsonElement.DeepEquals(Encrypted, Assert.Single(observed.Turns[^1].Output)));
    }

    [Fact]
    public async Task MultipleInlineCheckpointsKeepAllOriginalOutputAndExecutedIds()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var budget = new Budget();
        var server = new Server(midTurn: true, toolRounds: 2);
        var agent = new CodexThreadAgent(store, new Credentials(), budget,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: server.Handler(), usageObserver: usage));
        var turn = Guid.NewGuid();
        var settings = Settings(thread.Id, turn) with
        {
            Tools = [Json("""{"type":"function","name":"read","parameters":{"type":"object","properties":{}}}""")],
            AutomaticCompaction = new() { TokenThreshold = 1, MaximumInlineCompactions = 2 },
        };
        var calls = 0;
        var result = await agent.RunAsync(thread.Id, thread.Revision, turn, "owner", settings, [User("first")],
            (_, _) => { calls++; return Task.FromResult(new CodexToolOutput("receipt")); }, cancellationToken: Ct);
        Assert.Equal(2, calls);
        Assert.Equal(5, budget.Completed);
        Assert.Equal(5, server.Requests.Count);
        var secondCompaction = server.Requests[3].GetProperty("input").EnumerateArray().ToArray();
        Assert.Single(secondCompaction, item => CodexRealtimeProtocol.Text(item, "type") == "compaction");
        Assert.DoesNotContain(secondCompaction, item => CodexRealtimeProtocol.Text(item, "call_id") == "executed");
        Assert.Equal(2, secondCompaction.Count(item => CodexRealtimeProtocol.Text(item, "call_id") == "executed-2"));
        var stored = Assert.Single(result.Thread.Turns);
        Assert.Equal(7, stored.Output.Length);
        Assert.Equal(new[] { "executed", "executed-2" }, CodexThreadStore.DispatchedToolCallIds(result.Thread));
        Assert.Equal("compact-response-2", result.Thread.CompactionCheckpoint!.ResponseId);
        Assert.Equal(6, result.Thread.CompactionCheckpoint.ActiveBoundary!.OutputCount);
    }

    [Fact]
    public async Task ActiveCheckpointAmbiguousCommitRemainsObservableButCannotBeReinstalled()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", [User("first")], cancellationToken: Ct);
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "owner", [Encrypted], Ct);
        var candidate = CodexThreadStore.CaptureActiveCompaction(thread, turn, "owner", Settings(thread.Id, turn))!;
        thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "owner", "compact", "inference", Json("{}"), Ct)).Thread;
        thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "owner", "compact", "succeeded", Json("""{"id":"response-id"}"""), Ct);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.InstallActiveCompactionAsync(candidate, Result(candidate), "compact", "owner", null, cancelled.Token));
        Assert.Null((await store.ReadAsync(thread.Id, Ct)).CompactionCheckpoint);
        storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() =>
            store.InstallActiveCompactionAsync(candidate, Result(candidate), "compact", "owner", null, Ct));
        var observed = await new CodexThreadStore(storage, "account").ReadAsync(thread.Id, Ct);
        Assert.Equal("response-id", observed.CompactionCheckpoint!.ResponseId);
        Assert.Equal("succeeded", Assert.Single(observed.Turns[^1].Dispatches).State);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() =>
            store.InstallActiveCompactionAsync(candidate, Result(candidate), "compact", "owner", null, Ct));
    }

    [Fact]
    public async Task BackgroundAdoptionPreservesAppendedClosedTurnsAndRejectsInterveningCheckpoint()
    {
        var (_, store, thread) = await Setup();
        var settings = Settings(thread.Id, Guid.NewGuid());
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", [User("appended")], cancellationToken: Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() =>
            store.InstallBackgroundCompactionAsync(candidate, Result(candidate), settings, thread.Revision, cancellationToken: Ct));
        thread = await store.CompleteAsync(thread.Id, thread.Revision, turn, "owner", "completed", [Encrypted], Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() =>
            store.InstallBackgroundCompactionAsync(candidate, Result(candidate), settings with { Instructions = "changed" },
                thread.Revision, cancellationToken: Ct));
        var installed = await store.InstallBackgroundCompactionAsync(candidate, Result(candidate), settings, thread.Revision, cancellationToken: Ct);
        Assert.Equal(2, installed.Turns.Length);
        Assert.Equal(1, installed.CompactionCheckpoint!.ThroughTurnCount);
        var projected = CodexThreadCompaction.Project(installed, installed.CompactionCheckpoint, settings.Model,
            CodexThreadCompaction.ScopeHash(settings), installed.Turns.Length);
        Assert.Equal("appended", projected[^2].GetProperty("content").GetString());
        Assert.True(JsonElement.DeepEquals(Encrypted, projected[^1]));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() =>
            store.InstallBackgroundCompactionAsync(candidate, Result(candidate), settings, installed.Revision, cancellationToken: Ct));
    }

    [Fact]
    public async Task BackgroundAdoptionRejectsChangedReceiptAndStoredSettings()
    {
        var (_, store, thread) = await Setup(interrupted: true);
        var settings = Settings(thread.Id, Guid.NewGuid());
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        thread = await store.SettleAsync(thread.Id, thread.Revision, thread.Turns[0].Id, "owner", "executed", "succeeded", ToolOutput, Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() =>
            store.InstallBackgroundCompactionAsync(candidate, Result(candidate), settings, thread.Revision, cancellationToken: Ct));
        candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        thread = await store.UpdateSettingsAsync(thread.Id, thread.Revision, Json("""{"model":"changed"}"""), Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() =>
            store.InstallBackgroundCompactionAsync(candidate, Result(candidate), settings, thread.Revision, cancellationToken: Ct));
    }

    [Fact]
    public async Task BackgroundCompactionDoesNotBlockCompletedTurnAndHardThresholdReusesPendingRequest()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var budget = new Budget();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server { CompactionEntered = entered, ReleaseCompaction = release };
        await using var scheduler = new CodexThreadCompactionScheduler();
        var agent = new CodexThreadAgent(store, new Credentials(), budget,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: server.Handler(), usageObserver: usage))
        { BackgroundCompactions = scheduler };
        var firstTurn = Guid.NewGuid();
        var options = Settings(thread.Id, firstTurn) with
        {
            AutomaticCompaction = new() { TokenThreshold = 256, BackgroundTokenThreshold = 1 },
        };
        var first = await agent.RunAsync(thread.Id, thread.Revision, firstTurn, "owner", options, [User("first")],
            (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        Assert.Equal("done", first.Result.Text);
        await entered.Task.WaitAsync(Ct);
        Assert.Equal(1, scheduler.Started);
        Assert.Equal(0, scheduler.Completed);
        Assert.Null(first.Thread.CompactionCheckpoint);
        var secondTurn = Guid.NewGuid();
        var secondOptions = options with { Identity = Settings(thread.Id, secondTurn).Identity };
        var second = agent.RunAsync(thread.Id, first.Thread.Revision, secondTurn, "owner", secondOptions,
            [User(new string('x', 3000))], (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        var result = await second;
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(3, budget.Completed);
        Assert.Equal(1, scheduler.Completed);
        Assert.Equal("compact-response", result.Thread.CompactionCheckpoint!.ResponseId);
        Assert.Single(server.Requests[2].GetProperty("input").EnumerateArray(), item => CodexRealtimeProtocol.Text(item, "type") == "compaction");
        Assert.Equal(2, result.Thread.Turns.Length);
    }

    [Fact]
    public async Task SchedulerDeduplicatesAndDiscardsChangedAuthOwner()
    {
        var (_, store, thread) = await Setup();
        var settings = Settings(thread.Id, Guid.NewGuid());
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        var budget = new Budget();
        var owner = new CodexThreadCompactionScheduler.AuthOwner("account", "user-a", false);
        await using var scheduler = new CodexThreadCompactionScheduler();
        Assert.True(scheduler.TrySchedule(store, budget, owner, candidate, _ => Task.FromResult(Result(candidate))));
        await scheduler.WaitForPendingAsync(thread.Id, Ct);
        Assert.Null(await scheduler.TakeAsync(store, budget, owner with { UserId = "user-b" }, thread, settings, true, Ct));
        Assert.Equal(1, scheduler.Discarded);
        Assert.False(scheduler.TrySchedule(store, budget, owner, candidate, _ => throw new InvalidOperationException("Duplicate")));
        Assert.Equal(1, scheduler.Started);
        Assert.Equal(1, scheduler.Completed);
    }

    [Fact]
    public async Task SchedulerShutdownCancelsAndAwaitsItsOwnedAttempt()
    {
        var (_, store, thread) = await Setup();
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, Settings(thread.Id, Guid.NewGuid()), Ct);
        var budget = new Budget();
        var owner = new CodexThreadCompactionScheduler.AuthOwner("account", null, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new CodexThreadCompactionScheduler();
        Assert.True(scheduler.TrySchedule(store, budget, owner, candidate, async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Result(candidate);
        }));
        await entered.Task.WaitAsync(Ct);
        await scheduler.DisposeAsync();
        Assert.Equal(1, scheduler.Failed);
        Assert.Equal(0, scheduler.Completed);
        Assert.False(scheduler.TrySchedule(store, budget, owner, candidate, _ => throw new InvalidOperationException("Disposed")));
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
