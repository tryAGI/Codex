using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

// A bounded synthetic Responses source. Rust uses HTTP on isolated loopback;
// managed Codex uses an injected handler with the identical event generator.
// Neither path receives real credentials or reaches a real provider.
internal sealed class RustSyntheticTurnProvider : IAsyncDisposable
{
    internal const string FirstPrompt = "Synthetic parity prompt one.";
    internal const string SecondPrompt = "Synthetic parity prompt two.";
    internal const string FirstAnswer = "Synthetic parity answer one.";
    internal const string SecondAnswer = "Synthetic parity answer two.";
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task serving;
    private int rustCount;
    private int nativeCount;
    internal ConcurrentQueue<JsonElement> RustRequests { get; } = new();
    internal ConcurrentQueue<JsonElement> NativeRequests { get; } = new();
    internal ConcurrentQueue<string> Rejections { get; } = new();
    internal string BaseUrl { get; }

    internal RustSyntheticTurnProvider()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        serving = ServeAsync();
    }

    private static string Events(int index)
    {
        var id = $"synthetic-response-{index}";
        var item = new JsonObject
        {
            ["type"] = "message", ["role"] = "assistant", ["id"] = $"synthetic-message-{index}",
            ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "output_text", ["text"] = index == 1 ? FirstAnswer : SecondAnswer,
            }),
        };
        var events = new[]
        {
            new JsonObject { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = id } },
            new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item.DeepClone() },
            new JsonObject
            {
                ["type"] = "response.completed",
                ["response"] = new JsonObject
                {
                    ["id"] = id, ["model"] = RustThreadModelDifferentialFixture.Recommended,
                    ["status"] = "completed", ["output"] = new JsonArray(item.DeepClone()),
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = 5, ["input_tokens_details"] = null,
                        ["output_tokens"] = 3, ["output_tokens_details"] = null, ["total_tokens"] = 8,
                    },
                },
            },
        };
        return string.Concat(events.Select(e => "data: " + e.ToJsonString() + "\n\n"));
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(lifetime.Token);
                try
                {
                    if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != "/v1/responses")
                    {
                        Rejections.Enqueue("Unexpected Rust request");
                        context.Response.StatusCode = 404;
                        continue;
                    }

                    // The fixture accepts only the exact local endpoint and two
                    // response requests. Capture synthetic request bodies, not headers.
                    var index = Interlocked.Increment(ref rustCount);
                    if (index > 2)
                    {
                        Rejections.Enqueue("Rust response budget exceeded");
                        context.Response.StatusCode = 429;
                        continue;
                    }
                    var encoding = context.Request.Headers["Content-Encoding"];
                    if (encoding is not (null or "" or "gzip"))
                    {
                        Rejections.Enqueue("Unsupported Rust request encoding");
                        context.Response.StatusCode = 415;
                        continue;
                    }
                    using var gzip = encoding == "gzip"
                        ? new GZipStream(context.Request.InputStream, CompressionMode.Decompress, leaveOpen: true) : null;
                    using var reader = new StreamReader(gzip ?? context.Request.InputStream, Encoding.UTF8, leaveOpen: true);
                    var request = await reader.ReadToEndAsync(lifetime.Token);
                    if (request.Length > 2_000_000)
                        throw new InvalidDataException("Unexpected synthetic request size.");
                    RustRequests.Enqueue(RustThreadModelDifferentialFixture.Json(request));
                    var bytes = Encoding.UTF8.GetBytes(Events(index));
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/event-stream";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, lifetime.Token);
                }
                finally { context.Response.Close(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (HttpListenerException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
    }

    private sealed class NativeHandler(RustSyntheticTurnProvider owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/responses", request.RequestUri?.AbsolutePath);
            var index = Interlocked.Increment(ref owner.nativeCount);
            Assert.InRange(index, 1, 2);
            var content = request.Content ?? throw new InvalidDataException("Missing synthetic request body.");
            owner.NativeRequests.Enqueue(RustThreadModelDifferentialFixture.Json(await content.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(Events(index), Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CodexCredentials("synthetic-never-transmitted", "synthetic-account"));
        }
    }

    private sealed class Budget : ICodexUsageObserver
    {
        private int count;
        public Task ReserveAsync(string id, string model, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.InRange(Interlocked.Increment(ref count), 1, 2);
            Assert.Equal(RustThreadModelDifferentialFixture.Recommended, model);
            return Task.CompletedTask;
        }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) => Task.CompletedTask;
        public Task IncompleteAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
    }

    private readonly Budget budget = new();
    internal async Task<CodexThreadDocument> RunNativeAsync(CodexThreadStore store, CodexThreadRun run,
        Func<CodexThreadDocument, Task> started, CancellationToken ct)
    {
        var agent = new CodexThreadAgent(store, new Credentials(), budget,
            clientFactory: (credentials, observer) => new CodexClient(credentials,
                handler: new NativeHandler(this), usageObserver: observer));
        return (await agent.RunAsync(run.Thread.Id, run.Thread.Revision, run.TurnId, run.OwnerId,
            run.Options, run.Input,
            (_, _) => throw new InvalidOperationException("Synthetic text fixture does not admit tools."),
            maximumRequests: 1, turnStarted: (document, _) => started(document),
            cancellationToken: ct, control: run.Control, validatedItem: run.ValidatedItem,
            observeAsync: run.ObserveProvider)).Thread;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Close();
        try { await serving; }
        finally { lifetime.Dispose(); }
    }
}

[Trait("Category", "RustDifferential")]
public sealed class RustTurnLifecycleDifferentialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Input(string threadId, string prompt) => new()
    {
        ["threadId"] = threadId,
        ["input"] = new JsonArray(new JsonObject
        {
            ["type"] = "text", ["text"] = prompt, ["text_elements"] = new JsonArray(),
        }),
    };

    private static string TurnId(JsonElement result) =>
        result.GetProperty("turn").GetProperty("id").GetString()
        ?? throw new InvalidDataException("Missing turn identity.");

    private static bool IsThread(JsonElement payload, string threadId) =>
        payload.TryGetProperty("threadId", out var id) && id.GetString() == threadId;

    private static bool IsTerminal(string method, JsonElement payload, string threadId, string turnId) =>
        method == "turn/completed" && IsThread(payload, threadId) && TurnId(payload) == turnId;

    private static void AssertLifecycle(IReadOnlyList<(string Method, JsonElement Payload)> events,
        string threadId, string turnId, string answer)
    {
        var relevant = events.Where(e => IsThread(e.Payload, threadId)).ToArray();
        var started = Assert.Single(relevant, e => e.Method == "turn/started" && TurnId(e.Payload) == turnId);
        var terminal = Assert.Single(relevant, e => IsTerminal(e.Method, e.Payload, threadId, turnId));
        Assert.Equal("completed", terminal.Payload.GetProperty("turn").GetProperty("status").GetString());
        var message = Assert.Single(relevant, e => e.Method == "item/completed"
            && e.Payload.TryGetProperty("turnId", out var id) && id.GetString() == turnId
            && e.Payload.GetProperty("item").GetProperty("type").GetString() == "agentMessage");
        Assert.Equal(answer, message.Payload.GetProperty("item").GetProperty("text").GetString());
        var itemId = message.Payload.GetProperty("item").GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(itemId));
        var messageStarted = Assert.Single(relevant, e => e.Method == "item/started"
            && e.Payload.TryGetProperty("turnId", out var id) && id.GetString() == turnId
            && e.Payload.GetProperty("item").GetProperty("id").GetString() == itemId);
        Assert.True(Array.IndexOf(relevant, started) < Array.IndexOf(relevant, messageStarted));
        Assert.True(Array.IndexOf(relevant, messageStarted) < Array.IndexOf(relevant, message));
        Assert.True(Array.IndexOf(relevant, message) < Array.IndexOf(relevant, terminal));
    }

    private static async Task RunPairAsync(RustThreadModelDifferentialFixture f, (string Native, string Rust) ids,
        string prompt, string answer)
    {
        var native = await f.CallNative("turn/start", Input(ids.Native, prompt)).WaitAsync(TimeSpan.FromSeconds(25), Ct);
        var rust = await f.CallRust("turn/start", Input(ids.Rust, prompt));
        var nativeTurn = TurnId(native);
        var rustTurn = TurnId(rust);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        var rustEvents = new List<(string Method, JsonElement Payload)>();
        var terminal = false;
        while (!terminal && await f.Rust.Notifications.WaitToReadAsync(deadline.Token))
        {
            while (f.Rust.Notifications.TryRead(out var n))
            {
                if (n.Params is not { } payload || !IsThread(payload, ids.Rust)) continue;
                rustEvents.Add((n.Method, payload));
                if (IsTerminal(n.Method, payload, ids.Rust, rustTurn))
                {
                    terminal = true;
                    break;
                }
            }
        }
        Assert.True(terminal, "Rust terminal notification missing.");
        while (!f.NativeNotifications.Any(e => IsTerminal(e.Method, e.Payload, ids.Native, nativeTurn)))
            await Task.Delay(20, deadline.Token);
        AssertLifecycle(f.NativeNotifications.ToArray(), ids.Native, nativeTurn, answer);
        AssertLifecycle(rustEvents, ids.Rust, rustTurn, answer);
    }

    private static string[] PublicHistory(JsonElement result)
    {
        var history = new List<string>();
        foreach (var turn in result.GetProperty("thread").GetProperty("turns").EnumerateArray())
        {
            Assert.Equal("completed", turn.GetProperty("status").GetString());
            foreach (var item in turn.GetProperty("items").EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                if (type == "userMessage")
                    history.Add("user:" + string.Concat(item.GetProperty("content").EnumerateArray()
                        .Where(c => c.GetProperty("type").GetString() == "text")
                        .Select(c => c.GetProperty("text").GetString())));
                else if (type == "agentMessage")
                    history.Add("assistant:" + item.GetProperty("text").GetString());
            }
        }
        return history.ToArray();
    }

    private static void AssertRequestHistory(JsonElement request, params string[] expected)
    {
        Assert.Equal(RustThreadModelDifferentialFixture.Recommended, request.GetProperty("model").GetString());
        var messages = request.GetProperty("input").EnumerateArray()
            .Where(item => item.TryGetProperty("role", out var role) && role.GetString() is "user" or "assistant")
            .Select(item => item.GetProperty("role").GetString() + ":" +
                string.Concat(item.GetProperty("content").EnumerateArray()
                    .Where(c => c.TryGetProperty("text", out _)).Select(c => c.GetProperty("text").GetString())))
            .Where(text => text.StartsWith("user:Synthetic parity prompt", StringComparison.Ordinal)
                        || text.StartsWith("assistant:Synthetic parity answer", StringComparison.Ordinal)).ToArray();
        Assert.Equal(expected, messages);
    }

    [Fact]
    public async Task TwoCompletedTurnsRetainOrderedHistoryAcrossRestart()
    {
        await using var f = await RustThreadModelDifferentialFixture.CreateAsync(enableSyntheticTurns: true);
        var ids = await f.StartAsync();
        await RunPairAsync(f, ids, RustSyntheticTurnProvider.FirstPrompt, RustSyntheticTurnProvider.FirstAnswer);
        await f.RestartAsync();
        await f.CallNative("thread/resume", new JsonObject { ["threadId"] = ids.Native, ["excludeTurns"] = true });
        await f.CallRust("thread/resume", new JsonObject { ["threadId"] = ids.Rust, ["excludeTurns"] = true });
        await RunPairAsync(f, ids, RustSyntheticTurnProvider.SecondPrompt, RustSyntheticTurnProvider.SecondAnswer);

        var nativeHistory = PublicHistory(await f.CallNative("thread/read", new JsonObject
            { ["threadId"] = ids.Native, ["includeTurns"] = true }));
        var rustHistory = PublicHistory(await f.CallRust("thread/read", new JsonObject
            { ["threadId"] = ids.Rust, ["includeTurns"] = true }));
        var expected = new[]
        {
            "user:" + RustSyntheticTurnProvider.FirstPrompt, "assistant:" + RustSyntheticTurnProvider.FirstAnswer,
            "user:" + RustSyntheticTurnProvider.SecondPrompt, "assistant:" + RustSyntheticTurnProvider.SecondAnswer,
        };
        Assert.Equal(expected, nativeHistory);
        Assert.Equal(expected, rustHistory);
        var provider = f.SyntheticTurns ?? throw new InvalidOperationException("Missing synthetic provider.");
        Assert.Empty(provider.Rejections);
        foreach (var requests in new[] { provider.NativeRequests.ToArray(), provider.RustRequests.ToArray() })
        {
            Assert.Equal(2, requests.Length);
            AssertRequestHistory(requests[0], expected[0]);
            AssertRequestHistory(requests[1], expected[0], expected[1], expected[2]);
        }
    }
}
