using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

/// <summary>Offline comparisons against reference inputs and assertions in pinned Rust tests.</summary>
public sealed class RustParityFixtureTests
{
    private const string FixtureResource = "RustRealtimeParity.V1";

    private static JsonElement Fixture()
    {
        using var stream = typeof(RustParityFixtureTests).Assembly.GetManifestResourceStream(FixtureResource)
            ?? throw new InvalidDataException("Rust fixture resource is missing.");
        using var document = JsonDocument.Parse(stream);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    // Preserve array order and JSON value types. Ignore only object property order.
    private static bool Equal(JsonElement expected, JsonElement actual) =>
        JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.GetRawText()));

    private static bool TryPath(JsonElement node, string path, out JsonElement result)
    {
        result = node;
        foreach (var part in path.Split('/').Skip(1))
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(part, out result))
                return false;
        }
        return true;
    }

    private static IReadOnlyList<string> Differences(JsonElement expected, JsonElement actual)
    {
        var errors = new List<string>();
        foreach (var property in expected.EnumerateObject())
        {
            if (!TryPath(actual, property.Name, out var value) || !Equal(property.Value, value))
                errors.Add(property.Name);
        }
        return errors;
    }

    private sealed class ReplaySocket(IEnumerable<JsonElement> incoming) : WebSocket
    {
        private readonly Queue<string> messages = new(incoming.Select(x => x.GetRawText()));
        public List<JsonElement> Sent { get; } = [];
        public WebSocketCloseStatus? ClosedOutput { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => WebSocketState.Open;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => CloseOutputAsync(status, description, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ClosedOutput = status;
            return Task.CompletedTask;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!messages.TryDequeue(out var message))
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var bytes = Encoding.UTF8.GetBytes(message);
            bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(WebSocketMessageType.Text, type);
            Assert.True(end);
            Sent.Add(Json(Encoding.UTF8.GetString(buffer)));
            return Task.CompletedTask;
        }
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("synthetic-fixture-token", "fixture-account"));
    }

    private sealed class ReplayHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Theory]
    [InlineData("v1-direct-raw")]
    [InlineData("v2-direct-transcript")]
    [InlineData("v3-direct-transcript-audio")]
    [InlineData("v3-sideband-delegation")]
    public async Task MockWebSocketReplaysPinnedRustEvents(string id)
    {
        var fixture = Fixture().GetProperty("websocket").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var incoming = fixture.GetProperty("incoming").EnumerateArray().ToArray();
        var socket = new ReplaySocket(incoming);
        var protocol = fixture.GetProperty("protocol").GetString() ?? throw new InvalidDataException("Missing protocol.");
        await using var connection = new CodexRealtimeConnection(socket, 8192, protocol);
        var observed = new List<CodexRealtimeEvent>();
        var ct = TestContext.Current.CancellationToken;
        await foreach (var item in connection.ReadEventsAsync(ct))
            observed.Add(item);

        Assert.Equal(incoming.Length, observed.Count);
        for (var index = 0; index < incoming.Length; index++)
        {
            Assert.Equal(incoming[index].GetProperty("type").GetString(), observed[index].Type);
            Assert.True(Equal(incoming[index], observed[index].Payload), $"Raw event {index} differs.");
            if (!fixture.GetProperty("projections").TryGetProperty(index.ToString(System.Globalization.CultureInfo.InvariantCulture), out var expected))
                continue;
            var item = observed[index];
            if (expected.TryGetProperty("role", out var role))
            {
                var transcript = Assert.IsType<CodexRealtimeTranscript>(item.Transcript);
                Assert.Equal(role.GetString(), transcript.Role);
                Assert.Equal(expected.GetProperty("text").GetString(), transcript.Text);
                Assert.Equal(expected.GetProperty("final").GetBoolean(), transcript.Final);
                if (expected.TryGetProperty("replaceFinalText", out var replace))
                    Assert.Equal(replace.GetBoolean(), transcript.ReplaceFinalText);
            }
            else if (expected.TryGetProperty("pcm", out var pcm))
            {
                var audio = Assert.IsType<CodexRealtimeAudio>(item.Audio);
                Assert.Equal(pcm.GetString(), Convert.ToBase64String(audio.Pcm16.Span));
                Assert.Equal(expected.GetProperty("sampleRate").GetInt32(), audio.SampleRate);
                Assert.Equal(expected.GetProperty("channels").GetInt32(), audio.Channels);
            }
            else
            {
                var delegation = Assert.IsType<CodexDelegation>(item.Delegation);
                Assert.Equal(expected.GetProperty("id").GetString(), delegation.Id);
                Assert.Equal(expected.GetProperty("transcript").GetString(), delegation.Transcript);
            }
        }
        if (fixture.TryGetProperty("detach", out var detach) && detach.GetBoolean())
        {
            await connection.CloseTransportAsync(ct);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedOutput);
        }
        Assert.Equal(fixture.GetProperty("outgoing").GetArrayLength(), socket.Sent.Count);
    }

    [Fact]
    public async Task MockChatGptCallMatchesRustRequestAssertions()
    {
        var fixture = Fixture().GetProperty("http").GetProperty("call");
        var requestCount = 0;
        using var client = new CodexClient(new Credentials(), handler: new ReplayHttp(async (request, ct) =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(fixture.GetProperty("url").GetString(), request.RequestUri?.AbsoluteUri);
            var body = Json(await (request.Content ?? throw new InvalidDataException("Missing JSON body.")).ReadAsStringAsync(ct));
            Assert.Empty(Differences(fixture.GetProperty("expected"), body));
            foreach (var path in fixture.GetProperty("absent").EnumerateArray())
                Assert.False(TryPath(body, path.GetString() ?? "", out _));
            Assert.Equal(fixture.GetProperty("offer").GetString(), body.GetProperty("sdp").GetString());
            Assert.DoesNotContain("synthetic-fixture-token", body.GetRawText());
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(fixture.GetProperty("answer").GetString() ?? "") };
            response.Headers.TryAddWithoutValidation("Location", fixture.GetProperty("location").GetString());
            return response;
        }));
        var call = await client.CreateRealtimeCallAsync(fixture.GetProperty("offer").GetString() ?? "",
            new("instructions")
            {
                Protocol = "v3", Voice = "cove",
                InitialItems = [new("developer", "Remember this."), new("assistant", "Understood.")],
            }, TestContext.Current.CancellationToken);
        Assert.Equal(1, requestCount);
        Assert.Equal(fixture.GetProperty("callId").GetString(), call.CallId);
        Assert.Equal(fixture.GetProperty("answer").GetString(), call.AnswerSdp);
    }

    [Fact]
    public async Task MockResponsesSseMatchesRustRequestAssertionsAndOpaqueOutput()
    {
        var fixture = Fixture().GetProperty("http").GetProperty("responses");
        var incoming = fixture.GetProperty("incoming").EnumerateArray().ToArray();
        var requests = 0;
        using var client = new CodexClient(new Credentials(), handler: new ReplayHttp(async (request, ct) =>
        {
            requests++;
            var body = Json(await (request.Content ?? throw new InvalidDataException("Missing Responses body.")).ReadAsStringAsync(ct));
            Assert.Empty(Differences(fixture.GetProperty("expected"), body));
            Assert.DoesNotContain("synthetic-fixture-token", body.GetRawText());
            var sse = string.Concat(incoming.Select(x => $"data: {JsonSerializer.Serialize(x)}\n\n"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }));
        var events = new List<CodexResponseEvent>();
        await foreach (var item in client.StreamResponseAsync(new("fixture-model", "fixture instructions"), [],
            TestContext.Current.CancellationToken))
            events.Add(item);
        Assert.Equal(1, requests);
        Assert.Equal(incoming.Length, events.Count);
        for (var i = 0; i < incoming.Length; i++)
        {
            Assert.Equal(incoming[i].GetProperty("type").GetString(), events[i].Type);
            Assert.True(Equal(incoming[i], events[i].Payload), $"Responses event {i} differs.");
        }
    }

    [Fact]
    public void DifferentialComparatorRejectsMissingFieldsWrongTypesAndArrayReordering()
    {
        var expected = Json("""{"/present":null,"/sequence":[1,2],"/flag":false}""");
        var correct = Json("""{"flag":false,"sequence":[1,2],"present":null}""");
        var wrong = Json("""{"sequence":[2,1],"flag":"false"}""");
        Assert.Empty(Differences(expected, correct));
        Assert.Equal(new[] { "/present", "/sequence", "/flag" }, Differences(expected, wrong));
    }
}
