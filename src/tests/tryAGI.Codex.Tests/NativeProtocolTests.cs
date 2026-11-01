using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using tryAGI.Codex;

namespace tryAGI.Codex.Tests;

public sealed class NativeProtocolTests
{
    private sealed class Credentials : ICodexCredentialProvider
    { public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new CodexCredentials("secret-token", "account")); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private static HttpResponseMessage Sse(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    public void FramelessTurnDoneSuppliesCanonicalReplacementText(string role)
    {
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        { ["type"] = "turn.done", ["turn"] = new Dictionary<string, string> { ["role"] = role, ["transcript"] = "corrected final" } }));
        var transcript = (CodexRealtimeProtocol.ReadTranscript(json.RootElement) ?? throw new InvalidDataException("Required data was null."));
        Assert.Equal(role, transcript.Role); Assert.True(transcript.Final);
        Assert.True(transcript.ReplaceFinalText); Assert.Equal("corrected final", transcript.Text);
    }

    [Fact]
    public async Task ChatGptCallUsesV3JsonAndPreservesCredentialsOnlyInHeaders()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(async (r, ct) =>
        {
            Assert.Equal("https://chatgpt.com/backend-api/codex/realtime/calls?intent=quicksilver&architecture=avas", (r.RequestUri ?? throw new InvalidDataException("Required data was null.")).AbsoluteUri);
            Assert.Equal("secret-token", (r.Headers.Authorization ?? throw new InvalidDataException("Required data was null.")).Parameter);
            Assert.Equal("quicksilver=v2", r.Headers.GetValues("openai-alpha").Single());
            var body = Json(await (r.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct));
            Assert.Equal("client", body.GetProperty("session").GetProperty("delegation").GetProperty("type").GetString());
            Assert.Equal("cove", body.GetProperty("session").GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
            Assert.DoesNotContain("secret-token", body.GetRawText());
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("v=0\r\nanswer") };
            response.Headers.TryAddWithoutValidation("Location", "/v1/live/rtc_verified?ignored=1"); return response;
        }));
        var call = await client.CreateRealtimeCallAsync("v=0\r\noffer", new("voice instructions"), TestContext.Current.CancellationToken);
        Assert.Equal("rtc_verified", call.CallId);
    }

    [Theory]
    [InlineData("marin")]
    [InlineData("cedar")]
    [InlineData("verse")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task V2OrUnknownVoiceIsRejectedBeforeCreatingACall(string voice)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
            throw new InvalidOperationException("Invalid voice reached provider.")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateRealtimeCallAsync("v=0", new("instructions") { Voice = voice }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void V3AcceptsThePinnedUpstreamChatGptVoiceCatalog()
    {
        Assert.Equal(25, CodexProtocolCatalog.Voices.Count);
        Assert.Equal(9, CodexProtocolCatalog.V1Voices.Count);
        foreach (var voice in CodexProtocolCatalog.Voices)
        {
            var session = JsonSerializer.SerializeToElement(CodexRealtimeProtocol.Session(new("instructions") { Voice = voice }));
            Assert.Equal(voice, session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        }
    }

    [Theory]
    [InlineData("aube")]
    [InlineData("viola")]
    public void NewV3VoicesCannotLeakIntoV1(string voice)
    {
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("instructions") { Protocol = "v1", Voice = voice }));
        Assert.NotNull(CodexRealtimeProtocol.Session(new("instructions") { Protocol = "v3", Voice = voice }));
    }

    [Fact]
    public async Task AmbiguousCreateFailureIsNotRetriedOrLeaked()
    {
        var calls = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("sensitive-response-body") }); }));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => client.CreateRealtimeCallAsync("v=0", new("instructions"), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.DoesNotContain("sensitive", error.Message); Assert.Equal(503, error.StatusCode);
    }

    [Theory]
    [InlineData("../../other")]
    [InlineData("https://other.example/secret")]
    [InlineData("rtc_")]
    public void InvalidCallIdsAreRejected(string location) => Assert.Throws<CodexProtocolException>(() => CodexRealtimeProtocol.CallId(location));

    [Fact]
    public void CredentialsDoNotPrintOrSerializeSecrets()
    {
        var credential = new CodexCredentials("sensitive", "account");
        Assert.DoesNotContain("sensitive", credential.ToString());
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(credential));
    }

    [Fact]
    public void RussianAndEmojiContextChunksRetainTextWithinUpstreamByteLimit()
    {
        var text = string.Concat(Enumerable.Repeat("Переключи HDMI 👋🏽! ", 100));
        var messages = CodexRealtimeProtocol.Context(text, "delegation-1").Select(x => JsonSerializer.SerializeToElement(x)).ToArray();
        var chunks = messages.Select(x => x.GetProperty("content").EnumerateArray().Single().GetProperty("text").GetString() ?? "").ToArray();
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, x => Assert.InRange(Encoding.UTF8.GetByteCount(x), 1, 500));
        Assert.All(messages, x => Assert.Equal("delegation-1", x.GetProperty("delegation_item_id").GetString()));
    }

    [Fact]
    public void DelegationRequiresExplicitClientTargetAndPreservesFragments()
    {
        var data = Json("""{"type":"delegation.created","item":{"id":"d1","type":"delegation","target":"client","content":[{"type":"input_text","text":"HDMI "},{"type":"input_text","text":"2"}]}}""");
        Assert.Equal(new CodexDelegation("d1", "HDMI 2"), CodexRealtimeProtocol.ReadDelegation(data));
        Assert.Null(CodexRealtimeProtocol.ReadDelegation(Json(data.GetRawText().Replace("client", "other", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task NativeAgentExecutesOnlyAdvertisedFunctionsAndRetainsReasoning()
    {
        var requests = 0; var executed = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler(async (r, ct) =>
        {
            var body = Json(await (r.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct));
            Assert.False(body.GetProperty("store").GetBoolean()); Assert.True(body.GetProperty("stream").GetBoolean());
            requests++;
            if (requests == 1) return Sse("""data: {"type":"response.completed","response":{"output":[{"type":"reasoning","encrypted_content":"opaque"},{"type":"function_call","call_id":"c1","name":"read_state","arguments":"{}"}]}}""" + "\n\n");
            Assert.Contains("opaque", body.GetRawText()); Assert.Contains("function_call_output", body.GetRawText());
            return Sse("""data: {"type":"response.completed","response":{"output":[{"type":"message","content":[{"type":"output_text","text":"Готово"}]}]}}""" + "\n\n");
        }));
        var result = await new CodexAgent(client).RunAsync(new("test-model", "instructions")
        { Tools = [Json("""{"type":"function","name":"read_state","parameters":{"type":"object"}}""")] }, [Json("""{"role":"user","content":"read"}""")], (call, _) =>
        { Assert.Equal("read_state", call.Name); executed++; return Task.FromResult("confirmed"); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Готово", result.Text); Assert.Equal(2, result.Requests); Assert.Equal(1, executed);
    }

    [Fact]
    public async Task RepeatedToolIdNeverReexecutesAction()
    {
        var executed = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Sse(
            """data: {"type":"response.completed","response":{"output":[{"type":"function_call","call_id":"same","name":"act","arguments":"{}"}]}}""" + "\n\n"))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(new("model", "instructions")
        { Tools = [Json("""{"type":"function","name":"act","parameters":{"type":"object"}}""")] }, [], (_, _) => { executed++; return Task.FromResult("ok"); }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task UnadvertisedFunctionNeverReachesHostExecutor()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Sse(
            """data: {"type":"response.completed","response":{"output":[{"type":"function_call","call_id":"c","name":"hidden","arguments":"{}"}]}}""" + "\n\n"))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(new("model", "instructions"), [], (_, _) => throw new InvalidOperationException("Unadvertised action executed"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HistoricalFunctionIdIsNotReplayedOnAnotherTurn()
    {
        const string function = """{"type":"function_call","call_id":"previous","name":"act","arguments":"{}"}""";
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Sse(
            "data: {\"type\":\"response.completed\",\"response\":{\"output\":[" + function + "]}}\n\n"))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(new("model", "instructions")
        { Tools = [Json("""{"type":"function","name":"act","parameters":{"type":"object"}}""")] }, [Json(function)], (_, _) => throw new InvalidOperationException("Historical action replayed"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TruncatedSseCannotBecomeSuccessfulResponse()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Sse("data: {\"type\":\"response.created\"}\n\n"))));
        await Assert.ThrowsAsync<CodexProtocolException>(async () => { await foreach (var _ in client.StreamResponseAsync(new("model", "instructions"), [], TestContext.Current.CancellationToken)) { } });
    }

    [Fact]
    public async Task SseDecoderHandlesUtf8AcrossEveryByteBoundary()
    {
        await using var stream = new ByteStream(Encoding.UTF8.GetBytes("data: {\"text\":\"Привет 👋\"}\r\n\r\n"));
        var result = new List<string>(); await foreach (var item in CodexClient.ReadSseAsync(stream, 1024, TestContext.Current.CancellationToken)) result.Add(item);
        Assert.Equal("Привет 👋", Json(Assert.Single(result)).GetProperty("text").GetString());
    }

    [Fact]
    public async Task SseBoundIncludesUnterminatedLines()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data:" + new string('x', 2000)));
        await Assert.ThrowsAsync<CodexProtocolException>(async () => { await foreach (var _ in CodexClient.ReadSseAsync(stream, 1024, TestContext.Current.CancellationToken)) { } });
    }

    private sealed class ByteStream(byte[] bytes) : MemoryStream(bytes)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct); }
}
