using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task ConnectPerformsInitializeHandshakeAndRequestsRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeServer();
        server.Respond = request => request.GetProperty("method").GetString() switch
        {
            "initialize" => """{"userAgent":"codex/0.165.0"}""",
            "thread/start" => """{"thread":{"id":"t1"}}""",
            _ => null,
        };

        await using var client = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions { ClientVersion = "1.2.3" }, server, ct);

        Assert.Equal("codex/0.165.0", client.InitializeResult.GetProperty("userAgent").GetString());
        var init = server.Sent[0];
        Assert.Equal("initialize", init.GetProperty("method").GetString());
        Assert.Equal("1.2.3", init.GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.True(init.GetProperty("params").GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean());
        Assert.Equal("initialized", server.Sent[1].GetProperty("method").GetString());
        Assert.False(server.Sent[1].TryGetProperty("id", out _));

        var result = await client.RequestAsync("thread/start", Json("""{"cwd":"/tmp"}"""), ct);
        Assert.Equal("t1", result.GetProperty("thread").GetProperty("id").GetString());
        Assert.Equal("/tmp", server.Sent[2].GetProperty("params").GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task ErrorResponsesSurfaceCodeAndMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeServer();
        server.Respond = request => request.GetProperty("method").GetString() == "initialize" ? "{}" : null;
        server.RespondError = _ => """{"code":-32602,"message":"bad params","data":{"field":"cwd"}}""";
        await using var client = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions(), server, ct);

        var ex = await Assert.ThrowsAsync<CodexAppServerException>(() => client.RequestAsync("thread/start", null, ct));
        Assert.Equal(-32602, ex.Code);
        Assert.Equal("bad params", ex.Message);
        Assert.Equal("cwd", ex.ErrorData?.GetProperty("field").GetString());
    }

    [Fact]
    public async Task OverloadedRequestsAreRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeServer();
        var attempts = 0;
        server.Respond = request => request.GetProperty("method").GetString() == "initialize" || ++attempts > 1 ? "{}" : null;
        server.RespondError = _ => """{"code":-32001,"message":"overloaded"}""";
        await using var client = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions(), server, ct);

        await client.RequestAsync("model/list", null, ct);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task NotificationsAreStreamedInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeServer();
        server.Respond = _ => "{}";
        await using var client = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions(), server, ct);

        server.Push("""{"method":"turn/started","params":{"turnId":"u1"}}""");
        server.Push("not json");
        server.Push("""{"method":"turn/completed","params":{"turnId":"u1"}}""");

        var first = await client.Notifications.ReadAsync(ct);
        var second = await client.Notifications.ReadAsync(ct);
        Assert.Equal("turn/started", first.Method);
        Assert.Equal("turn/completed", second.Method);
        Assert.Equal("u1", second.Params?.GetProperty("turnId").GetString());
    }

    [Fact]
    public async Task ServerRequestsAreAnsweredByHandlerOrMethodNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeServer();
        server.Respond = _ => "{}";
        var options = new CodexAppServerOptions
        {
            ServerRequestHandler = (request, _) => request.Method == "item/commandExecution/requestApproval"
                ? ValueTask.FromResult(Json("""{"decision":"decline"}"""))
                : throw new CodexAppServerException(CodexAppServerException.MethodNotFoundCode, "unsupported"),
        };
        await using var client = await CodexAppServerClient.ConnectAsync(options, server, ct);

        server.Push("""{"id":7,"method":"item/commandExecution/requestApproval","params":{}}""");
        var approval = await server.NextSentAsync(ct);
        Assert.Equal(7, approval.GetProperty("id").GetInt32());
        Assert.Equal("decline", approval.GetProperty("result").GetProperty("decision").GetString());

        server.Push("""{"id":"x","method":"item/tool/requestUserInput","params":{}}""");
        var rejected = await server.NextSentAsync(ct);
        Assert.Equal("x", rejected.GetProperty("id").GetString());
        Assert.Equal(-32601, rejected.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task TransportEndFailsPendingRequestsAndCompletesNotifications()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeServer();
        server.Respond = request => request.GetProperty("method").GetString() == "initialize" ? "{}" : null;
        server.RespondError = _ => null;
        await using var client = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions(), server, ct);

        var request = client.RequestAsync("turn/start", null, ct);
        await server.NextSentAsync(ct);
        server.End();

        await Assert.ThrowsAsync<CodexAppServerException>(() => request);
        await client.Completion;
        Assert.False(await client.Notifications.WaitToReadAsync(ct));
    }

    [Fact]
    public void EndpointsAreValidatedAndProcessArgumentsBuilt()
    {
        Assert.Equal(["app-server", "--disable", "hooks", "--listen", "stdio://"],
            ProcessAppServerTransport.BuildArguments(new CodexAppServerOptions { DisabledFeatures = ["hooks"] }));
        Assert.Equal(["app-server", "proxy", "--sock", "/run/codex.sock"],
            ProcessAppServerTransport.BuildArguments(new CodexAppServerOptions { Endpoint = "unix:///run/codex.sock" }));
        Assert.Equal(CodexAppServerTransportKind.WebSocket, new CodexAppServerOptions { Endpoint = "ws://127.0.0.1:4500" }.ResolveKind());
        Assert.Equal(CodexAppServerTransportKind.WebSocket, new CodexAppServerOptions { Endpoint = "wss://codex.example/rpc" }.ResolveKind());
        Assert.Throws<ArgumentException>(() => new CodexAppServerOptions { Endpoint = "ws://codex.example:4500" }.ResolveKind());
        Assert.Throws<ArgumentException>(() => new CodexAppServerOptions { Endpoint = "http://localhost" }.ResolveKind());

        var start = ProcessAppServerTransport.BuildStartInfo(new CodexAppServerOptions { ExecutablePath = "/opt/codex", CodexHome = "/tmp/home" }, isWindows: false);
        Assert.Equal("/opt/codex", start.FileName);
        Assert.Equal("/tmp/home", start.Environment["CODEX_HOME"]);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeServer : ICodexAppServerTransport
    {
        private readonly Channel<string> inbound = Channel.CreateUnbounded<string>();
        private readonly Channel<JsonElement> sent = Channel.CreateUnbounded<JsonElement>();

        public List<JsonElement> Sent { get; } = [];
        public Func<JsonElement, string?> Respond { get; set; } = _ => "{}";
        public Func<JsonElement, string?> RespondError { get; set; } = _ => """{"code":-32601,"message":"unknown"}""";

        public void Push(string line) => inbound.Writer.TryWrite(line);

        public void End() => inbound.Writer.TryComplete();

        public async Task<JsonElement> NextSentAsync(CancellationToken ct) => await sent.Reader.ReadAsync(ct);

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            var element = Json(message);
            lock (Sent) Sent.Add(element);
            if (element.TryGetProperty("id", out var id) && element.TryGetProperty("method", out _))
            {
                var idJson = id.GetRawText();
                if (Respond(element) is { } result) Push($$"""{"id":{{idJson}},"result":{{result}}}""");
                else if (RespondError(element) is { } error) Push($$"""{"id":{{idJson}},"error":{{error}}}""");
            }

            if (Sent.Count > 2) sent.Writer.TryWrite(element);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadMessagesAsync(CancellationToken cancellationToken) => inbound.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            End();
            return ValueTask.CompletedTask;
        }
    }
}
