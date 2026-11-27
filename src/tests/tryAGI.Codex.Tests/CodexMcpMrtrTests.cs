using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexMcpMrtrTests
{
    private static readonly Uri Endpoint = new("https://mcp.example.test/mcp");
    private const string Opaque = " opaque/☃/=?base64?literal?=\n";
    private const string Discovery = """
        {"resultType":"complete","supportedVersions":["2026-07-28"],"capabilities":{"tools":{},"resources":{}}}
        """;
    private const string Form = """
        {"method":"elicitation/create","params":{"mode":"form","message":"Confirm the MCP request.",
        "requestedSchema":{"type":"object","properties":{"confirmed":{"type":"boolean"}},"required":["confirmed"]},
        "_meta":{"inputContext":"server-context"}}}
        """;

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage Reply(JsonElement request, string result)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = request.GetProperty("id").GetInt64(), ["result"] = JsonNode.Parse(result),
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static string InputRequired(string state, string? input = Form) => new JsonObject
    {
        ["resultType"] = "input_required", ["requestState"] = state,
        ["inputRequests"] = input is null ? null : new JsonObject { ["confirmation"] = JsonNode.Parse(input) },
        ["_meta"] = new JsonObject { ["responseContext"] = "preserved" },
    }.ToJsonString();

    private static CodexMcpClient Client(CodexMcpHttpTransport transport,
        Func<CodexMcpRequest, CancellationToken, ValueTask<bool>>? authorize = null, bool enableForm = true,
        bool enableUrl = false) => new(transport, authorize ?? ((_, _) => ValueTask.FromResult(true)),
            null, new CodexMcpProtocolOptions(CodexMcpProtocolMode.V20260728, enableForm, enableUrl));

    [Fact]
    public async Task ConcurrentCallsPreserveOriginalMetadataAndOpaqueStatePerCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var approvals = new List<CodexMcpRequest>();
        var seen = new List<JsonElement>();
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            var method = body.GetProperty("method").GetString();
            var parameters = body.GetProperty("params");
            if (method == "server/discover")
            {
                Assert.True(parameters.GetProperty("_meta").GetProperty("io.modelcontextprotocol/clientCapabilities")
                    .GetProperty("elicitation").TryGetProperty("form", out _));
                return Reply(body, Discovery);
            }
            seen.Add(parameters.Clone());
            var caller = parameters.GetProperty("arguments").GetProperty("caller").GetString();
            Assert.Equal(caller, parameters.GetProperty("_meta").GetProperty("requestContext").GetString());
            if (!parameters.TryGetProperty("requestState", out var state))
                return Reply(body, InputRequired(Opaque));
            Assert.Equal(Opaque, state.GetString());
            Assert.Equal(caller, parameters.GetProperty("inputResponses").GetProperty("confirmation")
                .GetProperty("content").GetProperty("caller").GetString());
            return Reply(body, new JsonObject
            {
                ["resultType"] = "complete", ["content"] = new JsonArray((JsonNode)new JsonObject
                {
                    ["type"] = "text", ["text"] = caller,
                }),
            }.ToJsonString());
        });
        await using var client = Client(transport, (request, _) =>
        {
            lock (approvals) approvals.Add(request);
            return ValueTask.FromResult(true);
        });
        await client.InitializeAsync("test", "1", ct);

        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<JsonElement> Run(string caller)
        {
            return await client.CallToolWithInputAsync("echo", Json($$"""{"caller":"{{caller}}"}"""), async (round, token) =>
            {
                Assert.Equal(Opaque, round.RequestState);
                Assert.Equal(1, round.Round);
                Assert.Equal(caller, round.OriginalParameters.GetProperty("arguments").GetProperty("caller").GetString());
                Assert.Equal("preserved", round.RawResult.GetProperty("_meta").GetProperty("responseContext").GetString());
                Assert.Equal("server-context", round.InputRequests.GetProperty("confirmation").GetProperty("params")
                    .GetProperty("_meta").GetProperty("inputContext").GetString());
                if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult();
                await both.Task.WaitAsync(token);
                return Json(new JsonObject
                {
                    ["confirmation"] = new JsonObject
                    {
                        ["action"] = "accept", ["content"] = new JsonObject { ["confirmed"] = true, ["caller"] = caller },
                        ["_meta"] = new JsonObject { ["clientContext"] = "preserved" },
                    },
                }.ToJsonString());
            }, Json($$"""{"requestContext":"{{caller}}"}"""), cancellationToken: ct);
        }

        var a = Run("a");
        var b = Run("b");
        var results = await Task.WhenAll(a, b);
        Assert.Equal(new[] { "a", "b" }, results.Select(result => result.GetProperty("content")[0].GetProperty("text").GetString()));
        Assert.Equal(4, seen.Count);
        Assert.Equal(4, approvals.Count(request => request.Method == "tools/call"));
        Assert.Equal(2, approvals.Count(request => request.Parameters.TryGetProperty("inputResponses", out _)));
    }

    [Fact]
    public async Task ResourceStateOnlyRoundsUseExactStateAndDoNotInventInputResponses()
    {
        var ct = TestContext.Current.CancellationToken;
        var states = new List<string?>();
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            if (body.GetProperty("method").GetString() == "server/discover") return Reply(body, Discovery);
            Assert.Equal("resources/read", body.GetProperty("method").GetString());
            var parameters = body.GetProperty("params");
            Assert.Equal("memo://resource", parameters.GetProperty("uri").GetString());
            Assert.False(parameters.TryGetProperty("inputResponses", out _));
            var state = parameters.TryGetProperty("requestState", out var value) ? value.GetString() : null;
            states.Add(state);
            return Reply(body, state switch
            {
                null => InputRequired("state-1", null),
                "state-1" => InputRequired("state-2", null),
                "state-2" => """{"resultType":"complete","contents":[{"uri":"memo://resource","text":"ok"}]}""",
                _ => throw new InvalidOperationException("Unexpected state."),
            });
        });
        await using var client = Client(transport, enableForm: false);
        await client.InitializeAsync("test", "1", ct);
        var result = await client.ReadResourceWithInputAsync("memo://resource", null, cancellationToken: ct);
        Assert.Equal("ok", result.GetProperty("contents")[0].GetProperty("text").GetString());
        Assert.Equal(new string?[] { null, "state-1", "state-2" }, states);
    }

    [Fact]
    public async Task ExplicitDeclineOrCancelIsForwardedWithoutFabricatedContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var observed = false;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            if (body.GetProperty("method").GetString() == "server/discover") return Reply(body, Discovery);
            if (!body.GetProperty("params").TryGetProperty("inputResponses", out var response))
                return Reply(body, InputRequired(Opaque));
            Assert.Equal("decline", response.GetProperty("confirmation").GetProperty("action").GetString());
            Assert.False(response.GetProperty("confirmation").TryGetProperty("content", out _));
            observed = true;
            return Reply(body, """{"resultType":"complete","content":[],"isError":true}""");
        });
        await using var client = Client(transport);
        await client.InitializeAsync("test", "1", ct);
        var result = await client.CallToolWithInputAsync("echo", null, (_, _) =>
            Task.FromResult<JsonElement?>(Json("""{"confirmation":{"action":"decline"}}""")), cancellationToken: ct);
        Assert.True(observed);
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"other":{"action":"accept","content":{}}}""")]
    [InlineData("""{"confirmation":{"action":"accept"}}""")]
    [InlineData("""{"confirmation":{"action":"cancel","content":{"forged":true}}}""")]
    [InlineData("""{"confirmation":{"action":"approve"}}""")]
    public async Task InvalidOrUncorrelatedHostReplyCannotSendContinuation(string reply)
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            if (body.GetProperty("method").GetString() == "server/discover") return Reply(body, Discovery);
            calls++;
            return Reply(body, InputRequired(Opaque));
        });
        await using var client = Client(transport);
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.CallToolWithInputAsync("echo", null,
            (_, _) => Task.FromResult<JsonElement?>(Json(reply)), cancellationToken: ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisabledInputCapabilityAndHostDenialDoNotResume()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            if (body.GetProperty("method").GetString() == "server/discover") return Reply(body, Discovery);
            calls++;
            return Reply(body, InputRequired(Opaque));
        });
        await using var client = Client(transport, enableForm: false);
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.CallToolWithInputAsync("echo", null,
            (_, _) => throw new InvalidOperationException("Host must not receive disabled input."), cancellationToken: ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HostMustReauthorizeContinuationEvenAfterValidInputResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            if (body.GetProperty("method").GetString() == "server/discover") return Reply(body, Discovery);
            calls++;
            return Reply(body, InputRequired(Opaque));
        });
        await using var client = Client(transport, (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(!request.Parameters.TryGetProperty("requestState", out _));
        });
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.CallToolWithInputAsync("echo", null,
            (_, _) => Task.FromResult<JsonElement?>(Json("""{"confirmation":{"action":"accept","content":{"confirmed":true}}}""")), cancellationToken: ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RepeatedStateFailsClosedInsteadOfReplayingIndefinitely()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        await using var transport = new CodexMcpHttpTransport(Endpoint, async (http, token) =>
        {
            var body = Json(await http.Content!.ReadAsStringAsync(token));
            if (body.GetProperty("method").GetString() == "server/discover") return Reply(body, Discovery);
            calls++;
            return Reply(body, InputRequired(Opaque, null));
        });
        await using var client = Client(transport);
        await client.InitializeAsync("test", "1", ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.CallToolWithInputAsync("echo", null, null, cancellationToken: ct));
        Assert.Equal(2, calls);
    }
}
