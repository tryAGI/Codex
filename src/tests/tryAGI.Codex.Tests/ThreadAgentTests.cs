using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

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
        internal string[] PrefixEvents = [];
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
                var prefix = string.Concat(server.PrefixEvents.Select(value => "data: " + value + "\n\n"));
                return new(HttpStatusCode.OK) { Content = new StringContent(prefix + "data: " + terminal.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
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

    private sealed class ArtifactMcpTransport(Func<JsonElement, CancellationToken, Task> beforeTool, bool isError) : ICodexMcpTransport
    {
        private readonly System.Threading.Channels.Channel<JsonElement> incoming =
            System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        internal int ToolCalls;
        public async ValueTask SendAsync(JsonElement message, CancellationToken ct)
        {
            var method = message.GetProperty("method").GetString();
            if (!message.TryGetProperty("id", out var id)) return;
            JsonNode? result;
            if (method == "initialize")
                result = JsonNode.Parse("""{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"}}""");
            else
            {
                Assert.Equal("tools/call", method);
                Interlocked.Increment(ref ToolCalls);
                await beforeTool(message.GetProperty("params"), ct);
                result = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "actual result" }),
                    ["isError"] = isError, ["_meta"] = new JsonObject { ["private"] = "never publish this automatically" },
                };
            }
            await incoming.Writer.WriteAsync(Json(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result,
            }.ToJsonString()), ct);
        }
        public async ValueTask<JsonElement?> ReceiveAsync(CancellationToken ct) =>
            await incoming.Reader.WaitToReadAsync(ct) ? await incoming.Reader.ReadAsync(ct) : null;
        public ValueTask DisposeAsync() { incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData("codex_app", false)]
    [InlineData("other_server", false)]
    [InlineData("codex_app", true)]
    public async Task ActualNativeMcpInvocationProducesDurableBackingArtifactWithoutDuplicateDispatch(string serverName, bool isError)
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_mcp", "v3", "voice-owner", Ct);
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "voice-owner", Guid.NewGuid(), Ct);
        var turn = Guid.NewGuid();
        server.Replies.Enqueue(Call("mcp-call", "key")); server.Replies.Enqueue(Text("done"));
        var transport = new ArtifactMcpTransport(async (parameters, ct) =>
        {
            Assert.Equal("remote_read", parameters.GetProperty("name").GetString());
            Assert.Equal("key", parameters.GetProperty("arguments").GetProperty("key").GetString());
            var snapshot = await store.ReadAsync(thread.Id, ct);
            var backing = Assert.Single(snapshot.Turns.Single().NativeBackingArtifacts!);
            Assert.Equal(CodexNativeArtifactStage.Started, backing.Stage);
            Assert.Equal("mcp-call", backing.SourceDispatchId);
            Assert.Equal(serverName, backing.Item.GetProperty("server").GetString());
            Assert.Equal("dispatching", Assert.Single(snapshot.Turns.Single().Dispatches, d => d.CallId == "mcp-call").State);
        }, isError);
        var authorized = 0;
        await using var pool = new CodexMcpConnectionPool(async (_, ct) =>
        {
            var client = new CodexMcpClient(transport, (request, _) =>
            {
                if (request.Method == "tools/call") Interlocked.Increment(ref authorized);
                return ValueTask.FromResult(true);
            });
            await client.InitializeAsync("test", "1", ct);
            return client;
        });
        var scope = new CodexMcpConnectionScope("account", "connection", serverName, thread.Id.ToString());
        await pool.ConfigureAsync(new(scope, 1, new string('a', 64)), Ct);
        var executor = new CodexNativeMcpToolExecutor(pool, new(new(null, "read"), scope, 1, "remote_read"),
            (result, _) => ValueTask.FromResult(new CodexNativeMcpToolProjection(new("actual result"), Json(new JsonObject
            {
                ["content"] = JsonNode.Parse(result.GetProperty("content").GetRawText()),
            }.ToJsonString()))));
        CodexNativeToolArtifactContext? observation = null;
        var run = await Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turn, "owner",
            Options(thread.Id, turn), [Input("read")], async (call, ct) =>
            {
                observation = call.NativeArtifacts;
                Assert.NotNull(observation);
                var result = await executor.ExecuteAsync(call, ct);
                await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(call, ct));
                return result;
            }, cancellationToken: Ct);
        Assert.Equal(1, transport.ToolCalls);
        Assert.Equal(1, authorized);
        var artifact = Assert.Single(run.Thread.Turns.Single().NativeBackingArtifacts!);
        Assert.Equal(CodexNativeArtifactStage.Completed, artifact.Stage);
        Assert.Equal(isError ? "failed" : "completed", artifact.Item.GetProperty("status").GetString());
        Assert.False(artifact.Item.GetProperty("result").TryGetProperty("_meta", out _));
        Assert.Equal("actual result", artifact.Item.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        var promotions = CodexThreadStore.ProjectRealtimeItems(run.Thread)
            .Where(item => (string?)item["type"] == "bemItemPromoted").ToArray();
        Assert.Equal(serverName == "codex_app" && !isError ? 1 : 0, promotions.Length);
        if (promotions.Length != 0) Assert.Equal(artifact.ItemId, (string?)promotions[0]["item_id"]);
        Assert.NotNull(observation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => observation.ObserveAsync(
            CodexNativeArtifactStage.Completed, artifact.Item, Ct));
        Assert.Equal("completed", run.Thread.Turns.Single().Status);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task NativeStreamingArtifactIsQueryableBeforeTerminalOutputAndPromotedOnlyOnce()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_stream", "v3", "voice-owner", Ct);
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "voice-owner", Guid.NewGuid(), Ct);
        var turnId = Guid.NewGuid();
        server.PrefixEvents =
        [
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant","id":"streamed","content":[]}}""",
            """{"type":"response.output_text.delta","output_index":0,"content_index":0,"item_id":"streamed","delta":"::codex-realtime-"}""",
            """{"type":"response.output_text.delta","output_index":0,"content_index":0,"item_id":"streamed","delta":"inline{}\nPreview"}""",
            """{"type":"response.output_text.delta","output_index":0,"content_index":0,"item_id":"streamed","delta":" and final"}""",
        ];
        server.Replies.Enqueue("""{"type":"message","role":"assistant","id":"streamed","content":[{"type":"output_text","text":"::codex-realtime-inline{}\nPreview and final"}]}""");
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Json("{}"), (_, _, _) => throw new InvalidOperationException("Use the native agent."), (_, _) => Task.CompletedTask);
        async Task<JsonElement> Read(CancellationToken ct)
        {
            var response = await rpc.InvokeAsync("thread/items/read", Json(new JsonObject
            {
                ["threadId"] = thread.Id.ToString(), ["turnId"] = turnId.ToString(),
                ["itemIds"] = new JsonArray(JsonValue.Create("streamed")),
            }.ToJsonString()), ct);
            return Assert.Single(response.GetProperty("data").EnumerateArray()).GetProperty("item");
        }
        string? promotionId = null;
        var checks = 0;
        var result = await Agent(store, auth, budget, server).RunAsync(thread.Id, thread.Revision, turnId, "owner",
            Options(thread.Id, turnId), [Input("show")], NoTools, cancellationToken: Ct, observeAsync: async (item, ct) =>
            {
                if (item.Type != "response.output_text.delta") return;
                var current = await store.ReadAsync(thread.Id, ct);
                var entries = CodexThreadStore.ProjectRealtimeItems(current);
                if (CodexRealtimeProtocol.Text(item.Payload, "delta") == "::codex-realtime-")
                {
                    Assert.DoesNotContain(entries, x => x["type"]!.GetValue<string>() == "bemItemPromoted");
                    return;
                }
                Assert.Equal("inProgress", current.Turns.Single().Status);
                Assert.DoesNotContain(current.Turns.Single().Output, x => CodexRealtimeProtocol.Text(x, "id") == "streamed");
                var promotion = Assert.Single(entries, x => x["type"]!.GetValue<string>() == "bemItemPromoted");
                promotionId ??= promotion["id"]!.GetValue<string>();
                Assert.Equal(promotionId, promotion["id"]!.GetValue<string>());
                Assert.Equal("streamed", promotion["item_id"]!.GetValue<string>());
                var expectedPreview = "::codex-realtime-inline{}\nPreview" + (checks == 0 ? "" : " and final");
                Assert.Equal(expectedPreview, (await Read(ct)).GetProperty("text").GetString());
                checks++;
            });
        Assert.Equal(2, checks);
        Assert.Equal("completed", result.Thread.Turns.Single().Status);
        var finalPromotion = Assert.Single(CodexThreadStore.ProjectRealtimeItems(result.Thread),
            x => x["type"]!.GetValue<string>() == "bemItemPromoted");
        Assert.Equal(promotionId, finalPromotion["id"]!.GetValue<string>());
        Assert.Equal("::codex-realtime-inline{}\nPreview and final", (await Read(Ct)).GetProperty("text").GetString());
        Assert.Single(server.Requests);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRealtimeRpcStartsAppendsListsAndStopsWithoutAppServer(bool includeStartupContext)
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        var agent = Agent(store, auth, budget, server);
        using var socket = new DirectRealtimeTestSocket
        {
            FinalObservation = """{"type":"input_transcript.added","item":{"text":"last speech before close"}}""",
        };
        var incoming = socket.Incoming;
        var outgoing = socket.Outgoing;
        incoming.Writer.TryWrite("""{"type":"session.started","session":{"id":"provider-direct-session"}}""");
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Json("{}"), (_, _, _) => throw new InvalidOperationException("Use the native owned executor."),
            (method, body) => { notices.Writer.TryWrite((method, body)); return Task.CompletedTask; });
        var contextCalls = 0;
        rpc.EnableRealtime(async (request, ct) =>
        {
            Assert.Equal(thread.Id, request.Thread.Id);
            Assert.StartsWith("original prompt", request.SessionOptions.Instructions, StringComparison.Ordinal);
            Assert.Equal(includeStartupContext, request.SessionOptions.Instructions.Contains("recent owner project", StringComparison.Ordinal));
            Assert.Equal(includeStartupContext, request.SessionOptions.Instructions.Contains("explicit workspace", StringComparison.Ordinal));
            Assert.Equal("v3", request.SessionOptions.Protocol);
            Assert.True(request.CoordinatorOptions.DirectAudio);
            var session = await store.CreateDirectRealtimeSessionAsync(thread.Id, request.Thread.Revision,
                "voice", "v3", "voice-owner", ct);
            var controller = CodexRealtimeSessionController.CreateDirect(store, session.Id, "voice", "voice-owner",
                (attach, cancel) =>
                {
                    Assert.Equal(thread.Id, attach.ThreadId);
                    Assert.Equal("voice", attach.SessionId);
                    Assert.Equal("account", attach.AccountId);
                    Assert.Equal("v3", attach.Protocol);
                    return CodexClient.InitializeDirectRealtimeWebSocketAsync(socket, request.SessionOptions, cancellationToken: cancel);
                });
            return new(new CodexRealtimeCoordinator(store, agent, rpc, controller, session.Id,
                "voice", "voice-owner", "account", _ => Options(session.Id, Guid.NewGuid()), NoTools, request.CoordinatorOptions));
        }, (ownerThread, _) =>
        {
            Assert.Equal(thread.Id, ownerThread.Id);
            Interlocked.Increment(ref contextCalls);
            return Task.FromResult<CodexRealtimeStartupMetadata?>(new("recent owner project", "explicit workspace"));
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var id = thread.Id.ToString();
        async Task<JsonElement> Invoke(string operation, JsonObject? args = null)
        {
            args ??= new JsonObject();
            if (operation != "listVoices") args["threadId"] = id;
            Assert.True(CodexThreadRpc.Handles("thread/realtime/" + operation));
            return await rpc.InvokeAsync("thread/realtime/" + operation, Json(args.ToJsonString()), token);
        }
        var voices = await Invoke("listVoices");
        Assert.Contains(voices.GetProperty("voices").GetProperty("v3").EnumerateArray(), v => v.GetString() == CodexProtocolCatalog.GetDefaultVoice("v3"));
        Assert.Equal("unsupported", (await Invoke("list")).GetProperty("coverage").GetString());
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Invoke("start", new() { ["outputModality"] = "bad" }));
        await Invoke("start", new() { ["outputModality"] = "audio", ["version"] = "v3", ["realtimeSessionId"] = "voice",
            ["transport"] = new JsonObject { ["type"] = "websocket" }, ["includeStartupContext"] = includeStartupContext, ["prompt"] = "original prompt" });
        Assert.Equal(includeStartupContext ? 1 : 0, Volatile.Read(ref contextCalls));
        var started = await notices.Reader.ReadAsync(token);
        Assert.Equal("thread/realtime/started", started.Method);
        Assert.Equal("voice", started.Body.GetProperty("realtimeSessionId").GetString());
        var first = await Invoke("list", new() { ["order"] = "position", ["limit"] = 1 });
        Assert.Equal("ready", first.GetProperty("coverage").GetString());
        var snapshot = first.GetProperty("snapshotEnd").GetInt64();
        Assert.Equal("realtimeSessionStarted", first.GetProperty("data")[0].GetProperty("item").GetProperty("type").GetString());
        await Invoke("appendAudio", new() { ["audio"] = new JsonObject { ["data"] = "AAAAAA==", ["numChannels"] = 1,
            ["sampleRate"] = 24000, ["samplesPerChannel"] = 2 } });
        await Invoke("appendText", new() { ["text"] = "user text", ["role"] = "user" });
        await Invoke("appendSpeech", new() { ["text"] = "speak independently" });
        var audio = false; var text = false; var speech = false;
        while (!audio || !text || !speech)
        {
            var raw = await outgoing.Reader.ReadAsync(token);
            audio |= raw.Contains("input_audio.append", StringComparison.Ordinal);
            text |= raw.Contains("user text", StringComparison.Ordinal);
            speech |= raw.Contains("speak independently", StringComparison.Ordinal);
        }
        Assert.Empty(server.Requests);
        await incoming.Writer.WriteAsync("""{"type":"output_audio.delta","audio":"AAAAAA=="}""", token);
        while (true)
        {
            var notice = await notices.Reader.ReadAsync(token);
            if (notice.Method != "thread/realtime/outputAudio/delta") continue;
            Assert.Equal("AAAAAA==", notice.Body.GetProperty("audio").GetProperty("data").GetString());
            break;
        }
        async Task<JsonElement> NextNotice(string method, Func<JsonElement, bool>? predicate = null)
        {
            while (true)
            {
                var notice = await notices.Reader.ReadAsync(token);
                if (notice.Method == method && (predicate?.Invoke(notice.Body) ?? true)) return notice.Body;
            }
        }
        await incoming.Writer.WriteAsync("""{"type":"input_transcript.added","item":{"text":"live "}}""", token);
        var frameStart = (await NextNotice("thread/realtime/item/started")).GetProperty("item");
        var frameId = frameStart.GetProperty("id").GetString();
        Assert.Equal("", frameStart.GetProperty("channels").GetProperty("voice_transcript").GetProperty("text").GetString());
        var delta = await NextNotice("thread/realtime/item/transcript/delta");
        Assert.Equal(frameId, delta.GetProperty("itemId").GetString());
        Assert.Equal("live ", delta.GetProperty("delta").GetString());
        await incoming.Writer.WriteAsync("""{"type":"turn.done","turn":{"role":"user","transcript":"live transcript"}}""", token);
        var suffix = await NextNotice("thread/realtime/item/transcript/delta");
        Assert.Equal(frameId, suffix.GetProperty("itemId").GetString());
        Assert.Equal("transcript", suffix.GetProperty("delta").GetString());
        var completedFrame = (await NextNotice("thread/realtime/item/completed",
            value => value.GetProperty("item").GetProperty("id").GetString() == frameId)).GetProperty("item");
        var livePage = await Invoke("list", new() { ["order"] = "position" });
        var listed = Assert.Single(livePage.GetProperty("data").EnumerateArray()
            .Select(x => x.GetProperty("item")), x => x.GetProperty("id").GetString() == frameId);
        Assert.True(JsonElement.DeepEquals(completedFrame, listed));
        server.Replies.Enqueue(Text("::codex-realtime-inline{}\nnative direct answer"));
        await incoming.Writer.WriteAsync("""
            {"type":"delegation.created","item":{"type":"delegation","target":"client","id":"direct-handoff","content":[{"type":"input_text","text":"answer using backing model"}]}}
            """, token);
        while (true)
        {
            var wire = Json(await outgoing.Reader.ReadAsync(token));
            if (!wire.GetRawText().Contains("native direct answer", StringComparison.Ordinal)) continue;
            Assert.Equal("delegation.context.append", wire.GetProperty("type").GetString());
            Assert.Equal("direct-handoff", wire.GetProperty("delegation_item_id").GetString());
            break;
        }
        while (true)
        {
            var actual = await store.ReadAsync(thread.Id, token);
            if (actual.Turns.LastOrDefault()?.Status == "completed") break;
            await Task.Delay(10, token);
        }
        Assert.Single(server.Requests);
        var promoted = (await NextNotice("thread/realtime/item/completed", value =>
            value.GetProperty("item").GetProperty("type").GetString() == "bemItemPromoted")).GetProperty("item");
        Assert.Equal("inlineMarkdown", promoted.GetProperty("presentation").GetProperty("type").GetString());
        await Invoke("stop");
        var closed = (await NextNotice("thread/realtime/item/completed", value =>
            value.GetProperty("item").GetProperty("type").GetString() == "realtimeSessionClosed")).GetProperty("item");
        var end = await Invoke("list", new() { ["order"] = "position" });
        var data = end.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal("realtimeSessionClosed", data[^1].GetProperty("item").GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(closed, data[^1].GetProperty("item")));
        Assert.Single(data, x => x.GetProperty("item").GetProperty("id").GetString() == frameId);
        Assert.True(JsonElement.DeepEquals(promoted, Assert.Single(data.Select(x => x.GetProperty("item")),
            x => x.GetProperty("id").GetString() == promoted.GetProperty("id").GetString())));
        Assert.Contains("last speech before close", end.GetRawText());
        Assert.Equal(1, Volatile.Read(ref socket.CloseCommands));
        var old = await Invoke("list", new() { ["order"] = "position", ["snapshotEnd"] = snapshot });
        Assert.Single(old.GetProperty("data").EnumerateArray());
        var ended = (await store.ReadAsync(thread.Id, token)).RealtimeSessions!.Single();
        Assert.Equal("ended", ended.State);
        Assert.Equal(CodexRealtimeTransportKind.DirectWebSocket, ended.TransportKind);
        Assert.Null(ended.CallId);
        Assert.Single(ended.UsedAttachmentIds!);
        Assert.Equal("start", Assert.Single(ended.HandoffAdmissions!).State);
    }

    [Fact]
    public async Task RealtimeModeTransitionsAreDurableAcrossRequestsDetachEndAndReload()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        var agent = Agent(store, auth, budget, server);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_call", "v3", "voice-owner", Ct);
        thread = await store.ConfigureRealtimeModeInstructionsAsync(thread.Id, thread.Revision, "voice", "voice-owner",
            new("custom start", "custom end"), Ct);
        var attachment = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "voice-owner", attachment, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfigureRealtimeModeInstructionsAsync(thread.Id,
            thread.Revision, "voice", "voice-owner", new("wrong", "wrong"), Ct));
        server.Replies.Enqueue(Call("mode_read", "key"));
        server.Replies.Enqueue(Text("first"));
        server.Replies.Enqueue(Text("detached"));
        server.Replies.Enqueue(Text("ended"));
        server.Replies.Enqueue(Text("next"));
        async Task<CodexThreadDocument> Run(CodexThreadDocument current, string input)
        {
            var id = Guid.NewGuid();
            var result = await agent.RunAsync(current.Id, current.Revision, id, "owner", Options(current.Id, id),
                [Input(input)], (_, _) => Task.FromResult(new CodexToolOutput("result")), cancellationToken: Ct);
            return await store.ReadAsync(result.Thread.Id, Ct);
        }
        static string[] ModeMessages(JsonElement request) => request.GetProperty("input").EnumerateArray()
            .Where(CodexRealtimeModeContext.IsModeFragment).Select(x => x.GetProperty("content").GetString()!).ToArray();
        thread = await Run(thread, "first");
        const string start = "<realtime_conversation>\ncustom start\n</realtime_conversation>";
        const string end = "<realtime_conversation>\ncustom end\n</realtime_conversation>";
        Assert.Equal([start], ModeMessages(server.Requests[0]));
        Assert.Equal([start], ModeMessages(server.Requests[1]));
        Assert.True(Assert.Single(thread.Turns[0].RealtimeModeTransitions!).Active);
        thread = await store.DetachRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "voice-owner", attachment, Ct);
        thread = await Run(thread, "while detached");
        Assert.Equal([start], ModeMessages(server.Requests[2]));
        Assert.Null(thread.Turns[1].RealtimeModeTransitions);
        var endId = Guid.NewGuid();
        thread = (await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "voice", "voice-owner", null, endId, Ct)).Thread;
        thread = await store.CompleteRealtimeEndAsync(thread.Id, thread.Revision, "voice", "voice-owner", endId, Ct);
        thread = await Run(thread, "typed again");
        Assert.Equal([start, end], ModeMessages(server.Requests[3]));
        Assert.False(Assert.Single(thread.Turns[2].RealtimeModeTransitions!).Active);
        thread = await Run(thread, "typed later");
        Assert.Equal([start, end], ModeMessages(server.Requests[4]));
        Assert.Null(thread.Turns[3].RealtimeModeTransitions);
    }

    [Fact]
    public async Task QueuedStandaloneSpeechCannotRebindAfterUserTurn()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        var agent = Agent(store, auth, budget, server);
        thread = await store.CreateDirectRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "v3", "voice-owner", Ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var socket = new DirectRealtimeTestSocket
        {
            BeforeSend = async (text, ct) =>
            {
                if (!text.Contains("blocking control", StringComparison.Ordinal)) return;
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
        };
        socket.Incoming.Writer.TryWrite("""{"type":"session.started","session":{"id":"voice"}}""");
        var controller = CodexRealtimeSessionController.CreateDirect(store, thread.Id, "voice", "voice-owner",
            (request, ct) => CodexClient.InitializeDirectRealtimeWebSocketAsync(socket,
                new("test") { Protocol = request.Protocol }, cancellationToken: ct));
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Json("{}"), (_, _, _) => throw new InvalidOperationException("No reasoning expected."), (_, _) => Task.CompletedTask);
        await using var coordinator = new CodexRealtimeCoordinator(store, agent, rpc, controller, thread.Id,
            "voice", "voice-owner", "account", _ => Options(thread.Id, Guid.NewGuid()), NoTools, new() { DirectAudio = true });
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await coordinator.AttachAsync(token));
        var userFinal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = coordinator.RunAsync((item, _) =>
        {
            if (item.Turn is { Role: "user" }) userFinal.TrySetResult();
            return Task.CompletedTask;
        }, ct: token);
        await coordinator.PumpReady.WaitAsync(token);
        try
        {
            Assert.True(coordinator.TryAppendText("blocking control"));
            await entered.Task.WaitAsync(token);
            Assert.True(coordinator.TryAppendSpeech("stale queued speech"));
            await socket.Incoming.Writer.WriteAsync("""{"type":"turn.done","turn":{"role":"user","transcript":"new input"}}""", token);
            await userFinal.Task.WaitAsync(token);
            Assert.True(coordinator.TryAppendSpeech("fresh queued speech"));
        }
        finally { release.TrySetResult(); }
        while (true)
        {
            var wire = await socket.Outgoing.Reader.ReadAsync(token);
            Assert.DoesNotContain("stale queued speech", wire, StringComparison.Ordinal);
            if (wire.Contains("fresh queued speech", StringComparison.Ordinal)) break;
        }
        Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await coordinator.EndAsync(Guid.NewGuid(), ct: token));
        await pump;
    }

    [Fact]
    public async Task NativeRpcAudioLaneDoesNotFillSlowGeneralObserverQueue()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_call", "v3", "voice-owner", Ct);
        using var client = new CodexClient(auth);
        var incoming = Channel.CreateUnbounded<string>();
        var outgoing = Channel.CreateUnbounded<string>();
        var controller = new CodexRealtimeSessionController(store, thread.Id, "voice", "voice-owner",
            (request, ct) => client.AttachExistingRealtimeCallAsync(request.CallId,
                new CodexRealtimeAttachmentStream(incoming.Reader, outgoing.Writer), request.Protocol, request.StartBefore, ct));
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Json("{}"), (_, _, _) => throw new InvalidOperationException("No reasoning expected."), (_, _) => Task.CompletedTask);
        await using var coordinator = new CodexRealtimeCoordinator(store, Agent(store, auth, budget, server), rpc, controller,
            thread.Id, "voice", "voice-owner", "account", _ => Options(thread.Id, Guid.NewGuid()), NoTools);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await coordinator.AttachAsync(token));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = Channel.CreateUnbounded<int>();
        var audioCount = 0;
        var observerCalls = 0;
        var pump = coordinator.RunRpcAsync(async (_, ct) =>
        {
            Interlocked.Increment(ref observerCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }, (_, audio, _) =>
        {
            Assert.Equal(4, audio.Pcm16.Length);
            heard.Writer.TryWrite(Interlocked.Increment(ref audioCount));
            return Task.CompletedTask;
        }, token);
        await coordinator.PumpReady.WaitAsync(token);
        try
        {
            await incoming.Writer.WriteAsync("""{"type":"input_transcript.added","item":{"text":"blocked observer"}}""", token);
            await entered.Task.WaitAsync(token);
            for (var i = 1; i <= 300; i++)
            {
                await incoming.Writer.WriteAsync("""{"type":"output_audio.delta","audio":"AAAAAA=="}""", token);
                Assert.Equal(i, await heard.Reader.ReadAsync(token));
            }
            Assert.Equal(1, Volatile.Read(ref observerCalls));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(CodexRealtimeLifecycleStatus.Detached, await coordinator.DetachAsync(token));
        await pump;
        Assert.Equal(300, audioCount);
    }

    [Fact]
    public async Task OrdinaryReasoningTurnCanSpeakAndSteerWithoutRebindingInflightResponse()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        var agent = Agent(store, auth, budget, server);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_call", "v3", "voice-owner", Ct);
        using var client = new CodexClient(auth);
        var incoming = Channel.CreateUnbounded<string>();
        var outgoing = Channel.CreateUnbounded<string>();
        var controller = new CodexRealtimeSessionController(store, thread.Id, "voice", "voice-owner",
            (request, ct) => client.AttachExistingRealtimeCallAsync(request.CallId,
                new CodexRealtimeAttachmentStream(incoming.Reader, outgoing.Writer), request.Protocol, request.StartBefore, ct));
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Json("{}"), (_, _, _) => throw new InvalidOperationException("Use the native owned executor."), (_, _) => Task.CompletedTask);
        await using var coordinator = new CodexRealtimeCoordinator(store, agent, rpc, controller, thread.Id, "voice", "voice-owner", "account",
            _ => Options(thread.Id, Guid.NewGuid()), NoTools, new() { ChannelMode = CodexRealtimeReasoningChannelMode.BemTags });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await coordinator.AttachAsync(token));
        var pump = coordinator.RunAsync(ct: token);
        await coordinator.PumpReady.WaitAsync(token);
        async Task<CodexThreadDocument> RunOrdinary(CodexThreadDocument current, Guid id, string input)
            => await rpc.RunOwnedAsync(current, id, Options(current.Id, id), [Input(input)],
                async (native, started, ct) => (await agent.RunAsync(native.Thread.Id, native.Thread.Revision, native.TurnId,
                    native.OwnerId, native.Options, native.Input, NoTools, cancellationToken: ct, control: native.Control,
                    turnStarted: (snapshot, _) => started(snapshot), validatedItem: native.ValidatedItem,
                    observeAsync: native.ObserveProvider)).Thread, token);
        var before = await store.ReadAsync(thread.Id, token);
        server.Replies.Enqueue(Text("proactive ordinary answer"));
        var firstId = Guid.NewGuid();
        var first = await RunOrdinary(before, firstId, "host event, not a voice input");
        var standalone = Json(await outgoing.Reader.ReadAsync(token));
        Assert.Equal("session.context.append", standalone.GetProperty("type").GetString());
        Assert.Contains("proactive ordinary answer", standalone.GetRawText());
        Assert.False(standalone.TryGetProperty("delegation_item_id", out _));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Replies.Enqueue(Text("superseded answer"));
        server.Replies.Enqueue(Text("steered current answer"));
        var secondId = Guid.NewGuid();
        server.BeforeResponse = async () =>
        {
            if (server.Requests.Count != 2) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var second = RunOrdinary(first, secondId, "ordinary task");
        try
        {
            await entered.Task.WaitAsync(token);
            await incoming.Writer.WriteAsync("""
                {"type":"delegation.created","item":{"type":"delegation","target":"client","id":"new-handoff","content":[{"type":"input_text","text":"new instruction"}]}}
                """, token);
            while (true)
            {
                var actual = await store.ReadAsync(thread.Id, token);
                if (actual.RealtimeSessions!.Single().HandoffAdmissions?.Any(x => x.HandoffId == "new-handoff") == true) break;
                await Task.Delay(10, token);
            }
        }
        finally { release.TrySetResult(); }
        var completed = await second;
        Assert.Equal(2, completed.Turns.Length);
        Assert.Equal(secondId, completed.Turns.Last().Id);
        Assert.Equal(3, server.Requests.Count);
        var reply = Json(await outgoing.Reader.ReadAsync(token));
        Assert.Equal("delegation.context.append", reply.GetProperty("type").GetString());
        Assert.Equal("new-handoff", reply.GetProperty("delegation_item_id").GetString());
        Assert.Contains("steered current answer", reply.GetRawText());
        Assert.DoesNotContain("superseded answer", reply.GetRawText());
        Assert.False(outgoing.Reader.TryRead(out _));
        Assert.Equal(CodexRealtimeLifecycleStatus.Detached, await coordinator.DetachAsync(token));
        await pump;
    }

    [Fact]
    public async Task NativeCoordinatorKeepsAudioAndControlLiveDuringToolAndContinuesSameThread()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        var agent = Agent(store, auth, budget, server);
        var priorId = Guid.NewGuid();
        server.Replies.Enqueue(Text("prior answer"));
        thread = (await agent.RunAsync(thread.Id, thread.Revision, priorId, "prior",
            Options(thread.Id, priorId), [Input("prior question")], NoTools, cancellationToken: Ct)).Thread;
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_call", "v3", "voice-owner", Ct);
        using var client = new CodexClient(auth);
        var incoming = Channel.CreateUnbounded<string>();
        var outgoing = Channel.CreateUnbounded<string>();
        var controller = new CodexRealtimeSessionController(store, thread.Id, "voice", "voice-owner",
            (request, ct) => client.AttachExistingRealtimeCallAsync(request.CallId,
                new CodexRealtimeAttachmentStream(incoming.Reader, outgoing.Writer), request.Protocol, request.StartBefore, ct));
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Json("{}"), (_, _, _) => throw new InvalidOperationException("Use the native owned executor."), (_, _) => Task.CompletedTask);
        await using var coordinator = new CodexRealtimeCoordinator(store, agent, rpc, controller, thread.Id, "voice", "voice-owner", "account",
            _ => Options(thread.Id, Guid.NewGuid()), async (_, ct) =>
            {
                toolStarted.TrySetResult();
                await releaseTool.Task.WaitAsync(ct);
                return new CodexToolOutput("tool result");
            }, new() { DirectAudio = true, ChannelMode = CodexRealtimeReasoningChannelMode.Commentary, BackendReasoningStatus = true });
        server.Replies.Enqueue(Call("voice_read", "key"));
        server.Replies.Enqueue(Text("steered answer"));
        server.Replies.Enqueue(Text("third answer"));
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await coordinator.AttachAsync(Ct));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var pump = coordinator.RunAsync(ct: token);
        async Task Send(string id) => await incoming.Writer.WriteAsync(
            $$$"""{"type":"delegation.created","item":{"type":"delegation","target":"client","id":"{{{id}}}","content":[{"type":"input_text","text":"task {{{id}}}"}]}}""", token);
        async Task<CodexThreadDocument> Until(Func<CodexThreadDocument, bool> condition)
        {
            while (true)
            {
                if (coordinator.ReasoningFailure is { } error) throw new InvalidOperationException("Reasoning failed.", error);
                if (pump.IsCompleted) throw new InvalidOperationException("Voice pump ended.", pump.Exception);
                var snapshot = await store.ReadAsync(thread.Id, token);
                if (condition(snapshot)) return snapshot;
                await Task.Delay(10, token);
            }
        }
        async Task<string> ReadWire(Func<string, bool> predicate)
        {
            while (true)
            {
                var wire = await outgoing.Reader.ReadAsync(token);
                if (predicate(wire)) return wire;
            }
        }
        try
        {
            await Send("first");
            await toolStarted.Task.WaitAsync(token);
            await Send("second");
            var steered = await Until(d => d.Turns.Length == 2 && d.Turns.Last().Input.Length == 2);
            Assert.Equal("inProgress", steered.Turns.Last().Status);
            Assert.Equal(2, server.Requests.Count);
            Assert.True(coordinator.TryAppendAudio(new byte[] { 0, 0, 1, 0 }));
            Assert.True(coordinator.TryAppendText("keep talking"));
            Assert.True(coordinator.TryAppendReasoningStatus("working"));
            // Separate lanes are independent, so their relative wire order is unspecified.
            var heardAudio = false; var heardText = false;
            while (!heardAudio || !heardText)
            {
                var wire = await outgoing.Reader.ReadAsync(token);
                heardAudio |= wire.Contains("input_audio.append", StringComparison.Ordinal);
                heardText |= wire.Contains("keep talking", StringComparison.Ordinal);
            }
            Assert.False(releaseTool.Task.IsCompleted);
            releaseTool.TrySetResult();
            await Until(d => d.Turns.Length == 2 && d.Turns.Last().Status == "completed");
            await ReadWire(x => x.Contains("steered answer", StringComparison.Ordinal));
            await Send("third");
            var completed = await Until(d => d.Turns.Length == 3 && d.Turns.Last().Status == "completed");
            Assert.Equal(priorId, completed.Turns.First().Id);
            Assert.Equal(4, server.Requests.Count);
            Assert.Equal(3, completed.RealtimeSessions!.Single().HandoffAdmissions!.Length);
            Assert.Contains("prior answer", server.Requests.Last().GetRawText());
            await ReadWire(x => x.Contains("third answer", StringComparison.Ordinal));
            Assert.Equal(CodexRealtimeLifecycleStatus.Detached, await coordinator.DetachAsync(token));
            await pump;
        }
        finally { releaseTool.TrySetResult(); await deadline.CancelAsync(); }
    }

    [Fact]
    public async Task DurableRealtimeSteeringDuringToolRetainsExactlyOneOrderedInputAfterReload()
    {
        var (_, store, thread, auth, budget, server) = await Setup();
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "rtc_call", "v3", "voice-owner", Ct);
        var attachment = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "voice", "voice-owner", attachment, Ct);
        async Task<CodexThreadDocument> Observe(CodexThreadDocument current, string id)
        {
            var item = RealtimeHistoryTests.Event($$$"""{"type":"delegation.created","item":{"type":"delegation","target":"client","id":"{{{id}}}","content":[{"type":"input_text","text":"task {{{id}}}"}]}}""");
            return (await store.RecordRealtimeObservationAsync(current.Id, current.Revision, "voice", "voice-owner", attachment, "obs-" + id, item, Ct)).Thread;
        }
        thread = await Observe(thread, "one");
        var firstId = Guid.NewGuid();
        var firstText = CodexThreadStore.PrepareRealtimeHandoffInput(thread.RealtimeSessions!.Single(), "one")!.Text;
        var first = new CodexRealtimeHandoffRequest("voice", "voice-owner", attachment, "one", firstId, firstText);
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Replies.Enqueue(Call("read_realtime", "key"));
        server.Replies.Enqueue(Text("finished"));
        server.Replies.Enqueue(Text("continued"));
        using var control = new CodexAgentControl();
        var agent = Agent(store, auth, budget, server);
        var run = agent.RunRealtimeHandoffAsync(thread.Id, thread.Revision, "runner", first, Options(thread.Id, firstId), control,
            async (_, ct) =>
            {
                toolStarted.TrySetResult();
                await releaseTool.Task.WaitAsync(ct);
                return new CodexToolOutput("result");
            }, 8, null, null, null, Ct);
        try
        {
            await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var active = await store.ReadAsync(thread.Id, Ct);
            active = await Observe(active, "two");
            var secondText = CodexThreadStore.PrepareRealtimeHandoffInput(active.RealtimeSessions!.Single(), "two")!.Text;
            var second = new CodexRealtimeHandoffRequest("voice", "voice-owner", attachment, "two", Guid.NewGuid(), secondText);
            var admitted = await control.SteerRealtimeAsync(second, Ct);
            Assert.True(admitted.Admitted);
            Assert.Single(server.Requests);
            Assert.False((await control.SteerRealtimeAsync(second, Ct)).Admitted);
            Assert.Equal(2, admitted.Thread.Turns.Single().Input.Length);
        }
        finally { releaseTool.TrySetResult(); }
        var result = await run;
        Assert.Equal("completed", result.Thread.Turns.Single().Status);
        var next = await store.ReadAsync(thread.Id, Ct);
        var nextId = Guid.NewGuid();
        await agent.RunAsync(next.Id, next.Revision, nextId, "next", Options(next.Id, nextId), [Input("continue")], NoTools,
            cancellationToken: Ct);
        Assert.Equal(3, server.Requests.Count);
        var history = server.Requests[2].GetProperty("input").EnumerateArray().ToArray();
        var user = history.Where(x => CodexRealtimeProtocol.Text(x, "role") == "user").ToArray();
        Assert.Equal(3, user.Length);
        Assert.Contains("task two", user[1].GetProperty("content").GetString());
        Assert.True(Array.FindIndex(history, x => CodexRealtimeProtocol.Text(x, "type") == "function_call_output") <
            Array.FindIndex(history, x => JsonElement.DeepEquals(x, user[1])));
    }
}
