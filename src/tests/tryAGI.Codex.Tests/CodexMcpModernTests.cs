using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexMcpModernTests
{
    private static readonly Uri Endpoint = new("https://mcp.example.test/mcp");
    private const string Modern = CodexMcpClient.ModernProtocolVersion;
    private const string Legacy = CodexMcpClient.LegacyProtocolVersion;
    private const string Discovery = """
        {"resultType":"complete","supportedVersions":["2026-07-28"],"capabilities":{"tools":{},"resources":{}},
        "_meta":{"io.modelcontextprotocol/serverInfo":{"name":"modern-test","version":"1"}},"ttlMs":0,"cacheScope":"private"}
        """;
    private const string LegacyInfo = """
        {"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"legacy","version":"1"}}
        """;

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage Reply(JsonElement request, string value, bool error = false,
        HttpStatusCode status = HttpStatusCode.OK, bool sse = false, bool unrelated = false)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = unrelated ? -999 : request.GetProperty("id").GetInt64(),
            [error ? "error" : "result"] = JsonNode.Parse(value),
        }.ToJsonString();
        return new(status)
        {
            Content = new StringContent(sse ? "event: message\ndata: " + body + "\n\n" : body,
                Encoding.UTF8, sse ? "text/event-stream" : "application/json"),
        };
    }

    private static ValueTask<bool> Allow(CodexMcpRequest request, CancellationToken ct) => ValueTask.FromResult(true);

    private static CodexMcpClient Client(CodexMcpHttpTransport transport) =>
        new(transport, Allow, null, new CodexMcpProtocolOptions(CodexMcpProtocolMode.V20260728));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModernDiscoverUsesSelfContainedMetadataWithoutInitializeOrSession(bool sse)
    {
        var ct = TestContext.Current.CancellationToken;
        var methods = new List<string>();
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            var method = body.GetProperty("method").GetString()!;
            methods.Add(method);
            Assert.Equal(Modern, Assert.Single(http.Headers.GetValues("MCP-Protocol-Version")));
            Assert.Equal(method, Assert.Single(http.Headers.GetValues("Mcp-Method")));
            Assert.False(http.Headers.Contains("Mcp-Session-Id"));
            var meta = body.GetProperty("params").GetProperty("_meta");
            Assert.Equal(Modern, meta.GetProperty("io.modelcontextprotocol/protocolVersion").GetString());
            Assert.Equal("test", meta.GetProperty("io.modelcontextprotocol/clientInfo").GetProperty("name").GetString());
            Assert.Equal("1", meta.GetProperty("io.modelcontextprotocol/clientInfo").GetProperty("version").GetString());
            return method switch
            {
                "server/discover" => Reply(body, Discovery, sse: sse),
                "tools/list" => Reply(body, """{"resultType":"complete","tools":[]}""", sse: sse),
                "tools/call" => ToolCall(),
                _ => throw new InvalidOperationException("Unexpected lifecycle method."),
            };

            HttpResponseMessage ToolCall()
            {
                Assert.Equal("echo", Assert.Single(http.Headers.GetValues("Mcp-Name")));
                return Reply(body, """{"resultType":"complete","content":[{"type":"text","text":"ok"}]}""", sse: sse);
            }
        });
        await using var client = Client(transport);
        var server = await client.InitializeAsync("test", "1", ct);
        Assert.Equal(Modern, server.ProtocolVersion);
        Assert.Equal("modern-test", server.Raw.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString());
        Assert.Empty((await client.ListToolsAsync(cancellationToken: ct)).GetProperty("tools").EnumerateArray());
        var result = await client.CallToolAsync("echo", cancellationToken: ct);
        Assert.Equal("ok", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(new[] { "server/discover", "tools/list", "tools/call" }, methods);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("versions")]
    [InlineData("unsupported")]
    public async Task ExplicitLegacyEvidenceFallsBackOnceAndUsesNegotiatedHeaders(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var methods = new List<string>();
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            var method = body.GetProperty("method").GetString()!;
            methods.Add(method);
            switch (method)
            {
                case "server/discover":
                    return reason switch
                    {
                        "method" => Reply(body, """{"code":-32601,"message":"method not found"}""", error: true),
                        "versions" => Reply(body, """{"resultType":"complete","supportedVersions":["2025-11-25"],"capabilities":{}}"""),
                        _ => Reply(body, """{"code":-32022,"message":"unsupported","data":{"supported":["2025-06-18","2025-11-25"]}}""",
                            error: true, status: HttpStatusCode.BadRequest),
                    };
                case "initialize":
                    Assert.Equal(Legacy, body.GetProperty("params").GetProperty("protocolVersion").GetString());
                    Assert.False(body.GetProperty("params").TryGetProperty("_meta", out _));
                    return Reply(body, LegacyInfo.Replace(Legacy, "2025-11-25", StringComparison.Ordinal));
                case "notifications/initialized":
                    Assert.Equal("2025-11-25", Assert.Single(http.Headers.GetValues("MCP-Protocol-Version")));
                    Assert.False(http.Headers.Contains("Mcp-Method"));
                    return new(HttpStatusCode.Accepted);
                default: throw new InvalidOperationException("Unexpected lifecycle method.");
            }
        });
        await using var client = Client(transport);
        var server = await client.InitializeAsync("test", "1", ct);
        Assert.Equal("2025-11-25", server.ProtocolVersion);
        Assert.Equal(new[] { "server/discover", "initialize", "notifications/initialized" }, methods);
    }

    [Theory]
    [InlineData("""{"resultType":"complete","supportedVersions":[],"capabilities":{}}""", false, false)]
    [InlineData("""{"resultType":"complete","supportedVersions":["2025-06-18","2099-01-01"],"capabilities":{}}""", false, false)]
    [InlineData("""{"resultType":"complete","supportedVersions":["2025-06-18",42],"capabilities":{}}""", false, false)]
    [InlineData("""{"code":-32022,"message":"unsupported","data":{"supported":["2025-06-18",42]}}""", true, false)]
    [InlineData("""{"code":-32601,"message":"method not found"}""", true, true)]
    public async Task UnprovenOrUncorrelatedDowngradeDoesNotSendInitialize(string response, bool error, bool unrelated)
    {
        var ct = TestContext.Current.CancellationToken;
        var count = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            count++;
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            Assert.Equal("server/discover", body.GetProperty("method").GetString());
            return Reply(body, response, error, unrelated: unrelated);
        });
        await using var client = Client(transport);
        await Assert.ThrowsAnyAsync<IOException>(() => client.InitializeAsync("test", "1", ct));
        Assert.Equal(1, count);
        Assert.Equal(CodexMcpState.Faulted, client.State);
    }

    [Fact]
    public async Task AuthenticationFailureCannotDowngradeOrRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var count = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, (_, _) =>
        {
            count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        await using var client = Client(transport);
        var failure = await Assert.ThrowsAsync<CodexMcpHttpException>(() => client.InitializeAsync("test", "1", ct));
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Equal(1, count);
        Assert.Equal(CodexMcpState.Faulted, client.State);
    }

    [Fact]
    public async Task ModernInputRequiredDoesNotMasqueradeAsCompletedToolResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var count = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            count++;
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            return Reply(body, body.GetProperty("method").GetString() == "server/discover"
                ? Discovery : """{"resultType":"input_required","requestState":"opaque"}""");
        });
        await using var client = Client(transport);
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.CallToolAsync("echo", cancellationToken: ct));
        Assert.Equal(2, count);
        Assert.Equal(CodexMcpState.Ready, client.State);
    }

    [Fact]
    public void StdioRequiresExplicitModernMarkerAndOptIn()
    {
        Assert.Equal(CodexMcpProtocolMode.Legacy, CodexMcpClient.ResolveStdioMode(CodexMcpProtocolMode.V20260728, null));
        Assert.Equal(CodexMcpProtocolMode.Legacy, CodexMcpClient.ResolveStdioMode(CodexMcpProtocolMode.Legacy, Modern));
        Assert.Equal(CodexMcpProtocolMode.V20260728, CodexMcpClient.ResolveStdioMode(CodexMcpProtocolMode.V20260728, Modern));
        Assert.Throws<ArgumentException>(() => CodexMcpClient.ResolveStdioMode(CodexMcpProtocolMode.V20260728, "1999-01-01"));
    }
}
