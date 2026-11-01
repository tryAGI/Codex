using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class ThreadAgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static JsonElement Input(string text) => Json(new JsonObject { ["role"] = "user", ["content"] = text }.ToJsonString());
    private static readonly JsonElement ReadTool = Json("""{"type":"function","name":"read","parameters":{"type":"object","properties":{"key":{"type":"string"}},"required":["key"],"additionalProperties":false}}""");
    private static string Call(string id, string key) => new JsonObject { ["type"] = "function_call", ["name"] = "read", ["call_id"] = id,
        ["arguments"] = new JsonObject { ["key"] = key }.ToJsonString() }.ToJsonString();
    private static string Text(string text) => new JsonObject { ["type"] = "message", ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }.ToJsonString();
    private static CodexResponseOptions Options(Guid thread, Guid turn, bool parallel = false) => new("test", "host instructions")
    { Identity = new("host-session", thread.ToString(), turn.ToString()), Tools = [ReadTool], ParallelToolCalls = parallel };

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        internal Func<JsonElement, bool>? RejectOnce;
        internal bool AmbiguousInferenceAdmission;
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
                long? before = rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (before != revision) return Task.FromResult(false);
                if (RejectOnce?.Invoke(body) == true) { RejectOnce = null; throw new IOException("Injected write rejection."); }
                rows[id] = body.Clone();
                var turns = body.GetProperty("turns");
                if (AmbiguousInferenceAdmission && turns.GetArrayLength() > 0 && turns.EnumerateArray().Last().GetProperty("dispatches")
                    .EnumerateArray().Any(d => d.GetProperty("kind").GetString() == "inference" && d.GetProperty("state").GetString() == "dispatching"))
                { AmbiguousInferenceAdmission = false; throw new IOException("Commit acknowledgment lost."); }
                return Task.FromResult(true);
            }
        }
    }
    private sealed class Credentials : ICodexCredentialProvider
    {
        internal string Account = "account";
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) => ValueTask.FromResult(new CodexCredentials("test", Account));
    }
    private sealed class Budget : ICodexUsageObserver
    {
        internal int Reserved, Completed, Unknown, NotSent;
        internal Action? Reserve;
        public Task ReserveAsync(string id, string model, CancellationToken ct) { Reserved++; Reserve?.Invoke(); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) { Completed++; return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Unknown++; return Task.CompletedTask; }
        public Task NotDispatchedAsync(string id, string model, CancellationToken ct) { NotSent++; return Task.CompletedTask; }
    }
    private sealed class Server
    {
        internal readonly Queue<string> Replies = [];
        internal readonly List<JsonElement> Requests = [];
        internal Func<Task>? BeforeResponse;
        internal HttpMessageHandler Handler() => new Transport(this);
        private sealed class Transport(Server server) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                using var body = JsonDocument.Parse(await (request.Content ?? throw new InvalidOperationException()).ReadAsStringAsync(ct));
                server.Requests.Add(body.RootElement.Clone());
                if (server.BeforeResponse is not null) await server.BeforeResponse();
                var output = JsonNode.Parse("[" + server.Replies.Dequeue() + "]");
                var terminal = new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject
                { ["id"] = "resp_test_" + server.Requests.Count, ["status"] = "completed", ["model"] = "test", ["output"] = output,
                    ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 } } };
                return new(HttpStatusCode.OK) { Content = new StringContent("data: " + terminal.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
        }
    }
    private static async Task<(Storage Storage, CodexThreadStore Store, CodexThreadDocument Thread, Credentials Credentials, Budget Budget, Server Server)> Setup()
    {
        var storage = new Storage(); var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{\"model\":\"test\"}"), Ct);
        return (storage, store, thread, new(), new(), new());
    }
    private static CodexThreadAgent Agent(CodexThreadStore store, Credentials auth, Budget budget, Server server) =>
        new(store, auth, budget, clientFactory: (credentials, observer) => new(credentials, handler: server.Handler(), usageObserver: observer));
    private static Task<CodexToolOutput> NoTools(CodexToolCall call, CancellationToken ct) => throw new InvalidOperationException("Unexpected tool.");

    [Fact]
    public async Task CompletedTurnsAndPixelsContinueAfterReloadAndFork()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        server.Replies.Enqueue(Call("read_once", "image")); server.Replies.Enqueue(Text("one")); server.Replies.Enqueue(Text("two"));
        server.BeforeResponse = async () =>
        {
            var current = await store.ReadAsync(thread.Id, Ct);
            var pending = current.Turns.Last().Dispatches.Single(d => d.Kind == "inference" && d.State == "dispatching");
            var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(server.Requests.Last().GetRawText()));
            Assert.Equal(Convert.ToHexStringLower(digest), pending.RequestSha256);
        };
        var firstTurn = Guid.NewGuid();
        var first = await Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, firstTurn, "owner", Options(thread.Id, firstTurn),
            [Input("look")], (_, _) => Task.FromResult(new CodexToolOutput("observed")
            { Images = [new(new byte[] { 1, 2, 3 }, "image/png")] }), cancellationToken: Ct);
        Assert.Equal("completed", first.Thread.Turns[0].Status);
        Assert.All(first.Thread.Turns[0].Dispatches, d => Assert.Equal("succeeded", d.State));
        Assert.Contains("data:image/png;base64,AQID", server.Requests[1].GetRawText());
        var loaded = await store.ReadAsync(thread.Id, Ct);
        var fork = await store.ForkAsync(loaded.Id, loaded.Revision, Guid.NewGuid(), cancellationToken: Ct);
        server.BeforeResponse = null;
        var nextTurn = Guid.NewGuid();
        var second = await Agent(store, auth, budget, server).RunAsync(fork.Id, fork.Revision, nextTurn, "other-owner", Options(fork.Id, nextTurn),
            [Input("continue")], NoTools, cancellationToken: Ct);
        Assert.Equal(2, second.Thread.Turns.Length);
        Assert.Contains("data:image/png;base64,AQID", server.Requests[2].GetRawText());
        Assert.Equal(3, budget.Completed); Assert.Equal(0, budget.NotSent);
        Assert.Single((await store.ReadAsync(thread.Id, Ct)).Turns);
    }

    [Fact]
    public async Task LostAdmissionAcknowledgmentNeverReachesHttpAndReleasesKnownNondispatch()
    {
        var (storage, store, thread, auth, budget, server) = await Setup();
        storage.AmbiguousInferenceAdmission = true;
        var turn = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner",
            Options(thread.Id, turn), [Input("hello")], NoTools, cancellationToken: Ct));
        Assert.Empty(server.Requests); Assert.Equal(1, budget.NotSent); Assert.Equal(0, budget.Unknown);
        var saved = (await store.ReadAsync(thread.Id, Ct)).Turns.Single();
        Assert.Equal("failed", saved.Status);
        var receipt = Assert.Single(saved.Dispatches);
        Assert.Equal("failed", receipt.State); Assert.True(receipt.Result?.GetProperty("notDispatched").GetBoolean());
    }

    [Fact]
    public async Task ReturnedEffectSurvivesReceiptWriteFailureAndIsNeverReplayed()
    {
        var (storage, store, thread, auth, budget, server) = await Setup();
        server.Replies.Enqueue(Call("effect_once", "a")); server.Replies.Enqueue(Call("effect_once", "a"));
        storage.RejectOnce = body => body.GetProperty("turns").EnumerateArray().Last().GetProperty("dispatches").EnumerateArray()
            .Any(d => d.GetProperty("kind").GetString() == "tool" && d.GetProperty("state").GetString() == "succeeded");
        var effects = 0;
        Task<CodexToolOutput> Effect(CodexToolCall call, CancellationToken ct) { effects++; return Task.FromResult(new CodexToolOutput("known result")); }
        var turn = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner",
            Options(thread.Id, turn), [Input("do it")], Effect, cancellationToken: Ct));
        var saved = await store.ReadAsync(thread.Id, Ct);
        Assert.Equal("succeeded", saved.Turns[0].Dispatches.Single(d => d.Kind == "tool").State);
        Assert.Equal("failed", saved.Turns[0].Status);
        var next = Guid.NewGuid();
        await Assert.ThrowsAsync<CodexProtocolException>(() => Agent(store, auth, budget, server).RunAsync(thread.Id, saved.Revision, next, "next-owner",
            Options(thread.Id, next), [Input("inspect")], Effect, cancellationToken: Ct));
        Assert.Equal(1, effects);
        Assert.Contains("Prior incomplete turn", server.Requests[1].GetRawText());
    }

    [Fact]
    public async Task ParallelHostApprovedReadsPersistReceiptsWithoutReorderingHistory()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        server.Replies.Enqueue(Call("read_a", "a") + "," + Call("read_b", "b")); server.Replies.Enqueue(Text("done"));
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        async Task<CodexToolOutput> Read(CodexToolCall call, CancellationToken ct)
        {
            if (Interlocked.Increment(ref count) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (call.Id == "read_a") await Task.Delay(20, ct);
            return new(call.Id);
        }
        var turn = Guid.NewGuid();
        var result = await Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner", Options(thread.Id, turn, true),
            [Input("read both")], Read, canExecuteConcurrently: _ => true, cancellationToken: Ct);
        Assert.Equal(2, count);
        var outputs = result.Thread.Turns[0].Output.Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "function_call_output").ToArray();
        Assert.Equal(new[] { "read_a", "read_b" }, outputs.Select(x => x.GetProperty("call_id").GetString()));
        Assert.All(result.Thread.Turns[0].Dispatches, d => Assert.Equal("succeeded", d.State));
    }

    [Fact]
    public async Task AccountReplacementAfterBudgetAdmissionPreventsHttp()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        budget.Reserve = () => auth.Account = "other-account";
        var turn = Guid.NewGuid();
        await Assert.ThrowsAsync<CodexProtocolException>(() => Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner",
            Options(thread.Id, turn), [Input("hello")], NoTools, cancellationToken: Ct));
        Assert.Empty(server.Requests); Assert.Equal(1, budget.NotSent);
        Assert.Equal("failed", (await store.ReadAsync(thread.Id, Ct)).Turns.Single().Status);
    }

    [Fact]
    public async Task CancellationAfterReturnedEffectRetainsKnownReceipt()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        server.Replies.Enqueue(Call("effect", "a"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var turn = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner",
            Options(thread.Id, turn), [Input("do it")], (_, _) => { stop.Cancel(); return Task.FromResult(new CodexToolOutput("returned")); }, cancellationToken: stop.Token));
        var saved = (await store.ReadAsync(thread.Id, Ct)).Turns.Single();
        Assert.Equal("interrupted", saved.Status);
        Assert.Equal("succeeded", saved.Dispatches.Single(d => d.Kind == "tool").State);
    }

    [Fact]
    public async Task OutputPrefixCannotBeRewritten()
    {
        var (_, store, thread, _, _, _) = await Setup(); var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", [Input("hello")], cancellationToken: Ct);
        thread = await store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "owner", [Json(Text("one"))], Ct);
        Assert.Equal(2, thread.Format);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SynchronizeOutputAsync(thread.Id, thread.Revision, turn, "owner", [Json(Text("two"))], Ct));
        Assert.Equal("one", (await store.ReadAsync(thread.Id, Ct)).Turns[0].Output[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ConcurrentInputMutationStopsExecutionInsteadOfSilentlyAdoptingIt()
    {
        var (_, store, thread, auth, budget, server) = await Setup(); var turn = Guid.NewGuid();
        server.Replies.Enqueue(Text("answer"));
        server.BeforeResponse = async () =>
        {
            var current = await store.ReadAsync(thread.Id, Ct);
            await store.AppendInputAsync(thread.Id, current.Revision, turn, "owner", [Input("concurrent input")], Ct);
        };
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner",
            Options(thread.Id, turn), [Input("hello")], NoTools, cancellationToken: Ct));
        Assert.Single(server.Requests);
        var saved = (await store.ReadAsync(thread.Id, Ct)).Turns.Single();
        Assert.Equal("failed", saved.Status); Assert.Equal(2, saved.Input.Length);
        Assert.Equal("succeeded", Assert.Single(saved.Dispatches).State);
    }

    [Fact]
    public async Task CustomToolInputAndOutputAreRetainedWithoutFunctionArgumentConversion()
    {
        var (_, store, thread, auth, budget, server) = await Setup(); var turn = Guid.NewGuid();
        server.Replies.Enqueue("""{"type":"custom_tool_call","call_id":"patch_once","name":"patch","input":"literal patch"}""");
        server.Replies.Enqueue(Text("done"));
        var options = Options(thread.Id, turn) with { Tools = [Json("""{"type":"custom","name":"patch","format":{"type":"text"}}""")] };
        var result = await Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner", options, [Input("patch")],
            (call, _) =>
            {
                Assert.Equal(CodexToolCallKind.Custom, call.Kind); Assert.Equal("literal patch", call.Input);
                return Task.FromResult(new CodexToolOutput("applied"));
            }, cancellationToken: Ct);
        var receipt = result.Thread.Turns[0].Dispatches.Single(d => d.Kind == "tool");
        Assert.Equal("custom_tool_call_output", receipt.Result?.GetProperty("type").GetString());
        Assert.Contains("custom_tool_call_output", server.Requests[1].GetRawText());
    }

    [Fact]
    public async Task DurableTurnReceiptCanReleasePendingBeforeProviderAdmission()
    {
        var (_, store, thread, auth, budget, server) = await Setup(); var turn = Guid.NewGuid();
        server.Replies.Enqueue(Text("done"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner", Options(thread.Id, turn),
            [Input("hello")], NoTools, turnStarted: (snapshot, _) =>
            {
                Assert.Equal("inProgress", snapshot.Turns.Single().Status);
                snapshot.Turns[0].Input[0] = Input("mutated callback copy");
                started.TrySetResult(); return release.Task;
            }, cancellationToken: Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Empty(server.Requests); Assert.Equal(0, budget.Reserved);
        Assert.Equal("inProgress", (await store.ReadAsync(thread.Id, Ct)).Turns.Single().Status);
        release.TrySetResult();
        await run;
        Assert.Equal("hello", server.Requests.Single().GetProperty("input")[0].GetProperty("content").GetString());
    }
}
