using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexMcpHttpTransportTests
{
    private static readonly Uri Endpoint = new("https://mcp.example.test/mcp");
    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage Response(string body, string media = "application/json",
        HttpStatusCode status = HttpStatusCode.OK, string? session = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, media) };
        if (session is not null) response.Headers.Add("Mcp-Session-Id", session);
        return response;
    }

    private static HttpResponseMessage Result(JsonElement request, string result, string? session = null) =>
        Response(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = request.GetProperty("id").GetInt64(),
            ["result"] = JsonNode.Parse(result),
        }.ToJsonString(), session: session);

    private const string ServerInfo = """
        {"protocolVersion":"2025-06-18","capabilities":{"tools":{},"resources":{}},"serverInfo":{"name":"fake","version":"1"}}
        """;

    private static ValueTask<bool> Allow(CodexMcpRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    [Fact]
    public async Task RoundTripsSessionHeadersNotificationsAndJsonResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var methods = new List<string>();
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            Assert.Equal(Endpoint, http.RequestUri);
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Contains(http.Headers.Accept, x => x.MediaType == "application/json");
            Assert.Contains(http.Headers.Accept, x => x.MediaType == "text/event-stream");
            Assert.Equal("application/json", http.Content!.Headers.ContentType!.MediaType);
            var body = Json(await http.Content.ReadAsStringAsync(token));
            var method = body.GetProperty("method").GetString()!;
            methods.Add(method);
            if (method == "initialize")
            {
                Assert.False(http.Headers.Contains("Mcp-Session-Id"));
                return Result(body, ServerInfo, "session-1");
            }
            Assert.Equal("session-1", Assert.Single(http.Headers.GetValues("Mcp-Session-Id")));
            Assert.Equal("2025-06-18", Assert.Single(http.Headers.GetValues("MCP-Protocol-Version")));
            return method == "notifications/initialized"
                ? Response("", status: HttpStatusCode.Accepted)
                : Result(body, """{"tools":[{"name":"echo","inputSchema":{"type":"object"}}]}""");
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await client.InitializeAsync("test", "1", ct);
        var result = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Equal("echo", result.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list" }, methods);
    }

    [Fact]
    public async Task ParsesMultilineSseAndCorrelatesResponseAfterNotification()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var request = Json(await http.Content!.ReadAsStringAsync(token));
            return request.GetProperty("method").GetString() switch
            {
                "initialize" => Result(request, ServerInfo),
                "notifications/initialized" => Response("", status: HttpStatusCode.Accepted),
                _ => Response(": keepalive\r\nevent: message\r\ndata: {\"jsonrpc\":\"2.0\",\r\ndata: \"method\":\"notifications/tools/list_changed\"}\r\n\r\n" +
                    "id: e1\ndata: {\"jsonrpc\":\"2.0\",\"id\":" + request.GetProperty("id").GetInt64() +
                    ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"Привет 🌍\"}]}}\n\n", "text/event-stream"),
            };
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await client.InitializeAsync("test", "1", ct);
        var result = await client.CallToolAsync("echo", cancellationToken: ct);
        Assert.Equal("Привет 🌍", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, client.ToolListRevision);
    }

    [Fact]
    public async Task RejectsRedirectWithoutAnotherNetworkRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var sends = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, (_, _) =>
        {
            sends++;
            var response = Response("", status: HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://untrusted.example.test/");
            return Task.FromResult(response);
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.InitializeAsync("test", "1", ct));
        Assert.Equal(1, sends);
        Assert.Equal(CodexMcpState.Faulted, client.State);
    }

    [Fact]
    public async Task ExpiredSessionFailsClosedWithoutReinitialization()
    {
        var ct = TestContext.Current.CancellationToken;
        var sends = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            sends++;
            var request = Json(await http.Content!.ReadAsStringAsync(token));
            return request.GetProperty("method").GetString() switch
            {
                "initialize" => Result(request, ServerInfo, "session-1"),
                "notifications/initialized" => Response("", status: HttpStatusCode.Accepted),
                _ => Response("", status: HttpStatusCode.NotFound),
            };
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await client.InitializeAsync("test", "1", ct);
        var error = await Assert.ThrowsAsync<CodexMcpHttpException>(() => client.CallToolAsync("echo", cancellationToken: ct));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(3, sends);
        Assert.Equal(CodexMcpState.Faulted, client.State);
    }

    [Fact]
    public async Task OptionalGetAndExplicitDeleteUseExactSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var operations = new List<HttpMethod>();
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            operations.Add(http.Method);
            if (http.Method == HttpMethod.Post)
            {
                var request = Json(await http.Content!.ReadAsStringAsync(token));
                return request.GetProperty("method").GetString() == "initialize"
                    ? Result(request, ServerInfo, "session-1")
                    : Response("", status: HttpStatusCode.Accepted);
            }
            Assert.Equal("session-1", Assert.Single(http.Headers.GetValues("Mcp-Session-Id")));
            return Response("", status: http.Method == HttpMethod.Get ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.NoContent);
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await client.InitializeAsync("test", "1", ct);
        Assert.False(await transport.OpenEventStreamAsync(ct));
        Assert.True(await transport.TerminateSessionAsync(ct));
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Post, HttpMethod.Get, HttpMethod.Delete }, operations);
    }

    [Fact]
    public async Task TruncatedSseCannotCompleteCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var request = Json(await http.Content!.ReadAsStringAsync(token));
            return request.GetProperty("method").GetString() switch
            {
                "initialize" => Result(request, ServerInfo, "session-1"),
                "notifications/initialized" => Response("", status: HttpStatusCode.Accepted),
                _ => Response("data: {\"jsonrpc\":\"2.0\"}\n", "text/event-stream"),
            };
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAnyAsync<IOException>(() => client.CallToolAsync("echo", cancellationToken: ct));
        Assert.Equal(CodexMcpState.Faulted, client.State);
    }

    [Fact]
    public async Task SessionChangeFailsClosedBeforeDeliveringResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var request = Json(await http.Content!.ReadAsStringAsync(token));
            return request.GetProperty("method").GetString() switch
            {
                "initialize" => Result(request, ServerInfo, "session-1"),
                "notifications/initialized" => Response("", status: HttpStatusCode.Accepted),
                _ => Result(request, """{"tools":[]}""", "session-2"),
            };
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.ListToolsAsync(cancellationToken: ct));
        Assert.Equal(CodexMcpState.Faulted, client.State);
    }

    [Fact]
    public async Task MismatchedSseResponseCannotBeDeliveredToAnotherCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var request = Json(await http.Content!.ReadAsStringAsync(token));
            return request.GetProperty("method").GetString() switch
            {
                "initialize" => Result(request, ServerInfo),
                "notifications/initialized" => Response("", status: HttpStatusCode.Accepted),
                _ => Response("data: {\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{}}\n\n", "text/event-stream"),
            };
        });
        await using var client = new CodexMcpClient(transport, Allow);
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAnyAsync<IOException>(() => client.CallToolAsync("echo", cancellationToken: ct));
        Assert.Equal(CodexMcpState.Faulted, client.State);
    }

    [Theory]
    [InlineData("http://example.test/mcp")]
    [InlineData("https://user:secret@example.test/mcp")]
    [InlineData("https://example.test/mcp#fragment")]
    public void RejectsUnsafeEndpointBeforeCallingSender(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new CodexMcpHttpTransport(new Uri(endpoint), (_, _) =>
            throw new InvalidOperationException("Sender must not be called.")));
    }
}
