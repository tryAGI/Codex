using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexMcpClientTests
{
    private sealed class Transport : ICodexMcpTransport
    {
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> outbound = Channel.CreateUnbounded<JsonElement>();
        public ValueTask SendAsync(JsonElement message, CancellationToken cancellationToken) =>
            outbound.Writer.WriteAsync(message.Clone(), cancellationToken);
        public async ValueTask<JsonElement?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (await inbound.Reader.WaitToReadAsync(cancellationToken) &&
                inbound.Reader.TryRead(out var value)) return value;
            return null;
        }
        public Task<JsonElement> NextAsync() =>
            outbound.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        public bool HasSent => outbound.Reader.TryPeek(out _);
        public void Incoming(string json) => inbound.Writer.TryWrite(Json(json));
        public void Result(JsonElement request, string json)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = request.GetProperty("id").GetInt64(),
                ["result"] = JsonNode.Parse(json),
            };
            Incoming(response.ToJsonString());
        }
        public ValueTask DisposeAsync()
        {
            inbound.Writer.TryComplete();
            outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
    private static string Method(JsonElement request) => request.GetProperty("method").GetString()!;
    private static ValueTask<bool> Allow(CodexMcpRequest request, CancellationToken ct) => ValueTask.FromResult(true);

    private static async Task Initialize(CodexMcpClient client, Transport transport)
    {
        var start = client.InitializeAsync("test", "1", TestContext.Current.CancellationToken);
        var request = await transport.NextAsync();
        Assert.Equal("initialize", Method(request));
        Assert.Equal(CodexMcpClient.LegacyProtocolVersion,
            request.GetProperty("params").GetProperty("protocolVersion").GetString());
        transport.Result(request, """{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":true},"resources":{},"prompts":{}},"serverInfo":{"name":"fake","version":"1"}}""");
        Assert.Equal("notifications/initialized", Method(await transport.NextAsync()));
        await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(CodexMcpState.Ready, client.State);
    }

    [Fact]
    public async Task NegotiatesOnceAndCorrelatesConcurrentResponsesOutOfOrder()
    {
        var transport = new Transport();
        await using var client = new CodexMcpClient(transport, Allow);
        Assert.Equal(CodexMcpState.Created, client.State);
        Assert.Throws<InvalidOperationException>(() => { _ = client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken); });
        await Initialize(client, transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync("test", "1", TestContext.Current.CancellationToken));
        var a = client.ListResourcesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var b = client.ListPromptsAsync("next", TestContext.Current.CancellationToken);
        var first = await transport.NextAsync();
        var second = await transport.NextAsync();
        Assert.Equal("resources/list", Method(first));
        Assert.Equal("prompts/list", Method(second));
        Assert.Equal("next", second.GetProperty("params").GetProperty("cursor").GetString());
        transport.Result(second, """{"prompts":[{"name":"prompt"}]}""");
        transport.Result(first, """{"resources":[{"uri":"resource"}]}""");
        Assert.Equal("resource", (await a).GetProperty("resources")[0].GetProperty("uri").GetString());
        Assert.Equal("prompt", (await b).GetProperty("prompts")[0].GetProperty("name").GetString());
        await client.DisposeAsync();
        Assert.Equal(CodexMcpState.Closed, client.State);
    }

    [Fact]
    public async Task AuthorizationIsIndependentOfToolDiscoveryAndPreservesExactArguments()
    {
        var transport = new Transport();
        var approvals = new List<CodexMcpRequest>();
        await using var client = new CodexMcpClient(transport, (request, _) =>
        {
            approvals.Add(request);
            return ValueTask.FromResult(request.Method != "tools/call" ||
                request.Parameters.GetProperty("name").GetString() == "allowed");
        });
        await Initialize(client, transport);
        var list = client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        transport.Result(await transport.NextAsync(),
            """{"tools":[{"name":"denied","inputSchema":{"type":"object"}}]}""");
        await list;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.CallToolAsync("denied", Json("{}"), TestContext.Current.CancellationToken));
        Assert.False(transport.HasSent);
        var call = client.CallToolAsync("allowed", Json("""{"value":"exact","future":true}"""), TestContext.Current.CancellationToken);
        var request = await transport.NextAsync();
        Assert.Equal("tools/call", Method(request));
        Assert.Equal("exact", request.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString());
        transport.Result(request, """{"isError":true,"content":[{"type":"text","text":"tool error"}]}""");
        Assert.True((await call).GetProperty("isError").GetBoolean());
        Assert.Equal("allowed", approvals[^1].Parameters.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CancellationCorrelatesWireIdAndLateResultCannotCompleteAnotherRequest()
    {
        var transport = new Transport();
        await using var client = new CodexMcpClient(transport, Allow);
        await Initialize(client, transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = client.CallToolAsync("a", Json("{}"), cancellation.Token);
        var original = await transport.NextAsync();
        cancellation.Cancel();
        var cancel = await transport.NextAsync();
        Assert.Equal("notifications/cancelled", Method(cancel));
        Assert.Equal(original.GetProperty("id").GetInt64(),
            cancel.GetProperty("params").GetProperty("requestId").GetInt64());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var next = client.CallToolAsync("b", cancellationToken: TestContext.Current.CancellationToken);
        var nextRequest = await transport.NextAsync();
        transport.Result(original, """{"content":[{"type":"text","text":"late"}]}""");
        transport.Result(nextRequest, """{"content":[{"type":"text","text":"current"}]}""");
        Assert.Equal("current", (await next).GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(transport.HasSent);
    }

    [Fact]
    public async Task RefreshUsesPaginationAndInvalidationInTransportOrder()
    {
        var transport = new Transport();
        await using var client = new CodexMcpClient(transport, Allow);
        await Initialize(client, transport);
        var refresh = client.RefreshToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        transport.Result(await transport.NextAsync(),
            """{"tools":[{"name":"one","inputSchema":{"type":"object"}}],"nextCursor":"page2"}""");
        var second = await transport.NextAsync();
        Assert.Equal("page2", second.GetProperty("params").GetProperty("cursor").GetString());
        transport.Result(second, """{"tools":[{"name":"two","inputSchema":{"type":"object"},"future":true}]}""");
        var snapshot = await refresh;
        Assert.Equal(2, snapshot.Tools.Count);
        Assert.True(snapshot.Tools[1].GetProperty("future").GetBoolean());
        Assert.True(client.IsToolCatalogCurrent);

        var stale = client.RefreshToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var request = await transport.NextAsync();
        transport.Incoming("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""");
        transport.Result(request, """{"tools":[]}""");
        await Assert.ThrowsAsync<CodexProtocolException>(() => stale);
        Assert.Equal(1, client.ToolListRevision);
        Assert.False(client.IsToolCatalogCurrent);
        Assert.Same(snapshot, client.ToolCatalog);
    }

    [Fact]
    public async Task RefreshRejectsDuplicatesAndUnknownServerRequestsWithoutExecuting()
    {
        var transport = new Transport();
        await using var client = new CodexMcpClient(transport, Allow);
        await Initialize(client, transport);
        var refresh = client.RefreshToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        transport.Result(await transport.NextAsync(),
            """{"tools":[{"name":"same","inputSchema":{}},{"name":"same","inputSchema":{}}]}""");
        await Assert.ThrowsAsync<CodexProtocolException>(() => refresh);
        Assert.Null(client.ToolCatalog);
        transport.Incoming("""{"jsonrpc":"2.0","id":"ask","method":"elicitation/create","params":{}}""");
        var rejected = await transport.NextAsync();
        Assert.Equal("ask", rejected.GetProperty("id").GetString());
        Assert.Equal(-32601, rejected.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ResourcesAndPromptsRetainRawResultsAndRpcErrors()
    {
        var transport = new Transport();
        await using var client = new CodexMcpClient(transport, Allow);
        await Initialize(client, transport);
        var read = client.ReadResourceAsync("resource://host/item", TestContext.Current.CancellationToken);
        var request = await transport.NextAsync();
        Assert.Equal("resources/read", Method(request));
        Assert.Equal("resource://host/item", request.GetProperty("params").GetProperty("uri").GetString());
        transport.Result(request, """{"contents":[{"uri":"resource://host/item","text":"data","future":1}]}""");
        Assert.Equal(1, (await read).GetProperty("contents")[0].GetProperty("future").GetInt32());

        var prompt = client.GetPromptAsync("prompt", Json("""{"arg":"value"}"""), TestContext.Current.CancellationToken);
        request = await transport.NextAsync();
        Assert.Equal("prompts/get", Method(request));
        Assert.Equal("value", request.GetProperty("params").GetProperty("arguments").GetProperty("arg").GetString());
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = request.GetProperty("id").GetInt64(),
            ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "bad args", ["data"] = new JsonObject { ["detail"] = true } },
        };
        transport.Incoming(response.ToJsonString());
        var error = await Assert.ThrowsAsync<CodexMcpRpcException>(() => prompt);
        Assert.Equal(-32602, error.Code);
        Assert.True(error.DataValue!.Value.GetProperty("detail").GetBoolean());
        Assert.Equal(CodexMcpState.Ready, client.State);
    }

    [Fact]
    public async Task RepeatedCursorDoesNotPublishPartialCatalog()
    {
        var transport = new Transport();
        await using var client = new CodexMcpClient(transport, Allow);
        await Initialize(client, transport);
        var refresh = client.RefreshToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        transport.Result(await transport.NextAsync(), """{"tools":[],"nextCursor":"same"}""");
        transport.Result(await transport.NextAsync(), """{"tools":[],"nextCursor":"same"}""");
        await Assert.ThrowsAsync<CodexProtocolException>(() => refresh);
        Assert.Null(client.ToolCatalog);
    }

    [Fact]
    public async Task UnsupportedHandshakeFailsClosedWithoutInitializedOrToolCalls()
    {
        var transport = new Transport();
        await using var client = new CodexMcpClient(transport, Allow);
        var initialize = client.InitializeAsync("test", "1", TestContext.Current.CancellationToken);
        transport.Result(await transport.NextAsync(),
            """{"protocolVersion":"unknown","capabilities":{"tools":{}},"serverInfo":{}}""");
        await Assert.ThrowsAsync<CodexProtocolException>(() => initialize);
        Assert.Equal(CodexMcpState.Faulted, client.State);
        Assert.False(transport.HasSent);
        Assert.Throws<InvalidOperationException>(() => { _ = client.CallToolAsync("any", cancellationToken: TestContext.Current.CancellationToken); });
    }
}
