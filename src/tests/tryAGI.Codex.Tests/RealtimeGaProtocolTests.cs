using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeGaProtocolTests
{
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    [Theory]
    [InlineData("response.output_audio_transcript.done", "transcript", "assistant")]
    [InlineData("response.output_text.done", "text", "assistant")]
    [InlineData("conversation.item.input_audio_transcription.completed", "transcript", "user")]
    public void FinalWithoutTextPreservesDeltaFallbackButExplicitEmptyIsCanonical(string type, string field, string role)
    {
        var payload = new Dictionary<string, string> { ["type"] = type };
        var missing = (CodexRealtimeGaProtocol.ReadTranscript(JsonSerializer.SerializeToElement(payload)) ?? throw new InvalidDataException("Required data was null."));
        Assert.Equal(role, missing.Role); Assert.True(missing.Final); Assert.False(missing.ReplaceFinalText);
        payload[field] = "";
        var empty = (CodexRealtimeGaProtocol.ReadTranscript(JsonSerializer.SerializeToElement(payload)) ?? throw new InvalidDataException("Required data was null."));
        Assert.True(empty.Final); Assert.True(empty.ReplaceFinalText); Assert.Equal("", empty.Text);
    }

    [Fact]
    public void ModelSpecificFieldsSurviveWithoutReducingLiveCapabilities()
    {
        var fields = Json("""{"reasoning":{"effort":"high"},"parallel_tool_calls":true,"truncation":"disabled","audio":{"input":{"turn_detection":null,"transcription":{"model":"gpt-4o-transcribe"}}},"future_capability":{"enabled":true}}""");
        var session = CodexRealtimeGaProtocol.Session(new("keep listening") { Protocol = "v2", Model = "gpt-realtime-2.1", Voice = "cedar", ProviderSession = fields });
        var wire = JsonSerializer.SerializeToElement(session);
        Assert.Equal("high", wire.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(wire.GetProperty("future_capability").GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").ValueKind);
        Assert.Equal("cedar", wire.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        Assert.True(CodexRealtimeProtocol.Session(new("live"))["delegation"] is JsonObject);
        Assert.False(session.ContainsKey("delegation"));
        Assert.Throws<ArgumentException>(() => CodexRealtimeGaProtocol.Session(new("x") { Protocol = "v2", ProviderSession = Json("{\"model\":\"hidden\"}") }));
        var message = JsonSerializer.SerializeToElement(CodexRealtimeGaProtocol.Message("developer", "context"));
        Assert.Equal("system", message.GetProperty("item").GetProperty("role").GetString());
    }
    [Fact]
    public void AudioTranscriptsAndExplicitDelegationPreserveRawReceipts()
    {
        var payload = Json("""{"type":"response.output_audio.delta","delta":"AQACAAMA","item_id":"item_a","future_field":42}""");
        var item = new CodexRealtimeEvent("response.output_audio.delta", payload, 1);
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0 }, (item.Audio ?? throw new InvalidDataException("Required data was null.")).Pcm16.ToArray());
        Assert.Equal("item_a", item.Audio.ItemId); Assert.Equal(42, item.Payload.GetProperty("future_field").GetInt32());
        var transcript = CodexRealtimeGaProtocol.ReadTranscript(Json("""{"type":"response.output_audio_transcript.done","transcript":"canonical"}"""));
        Assert.True((transcript ?? throw new InvalidDataException("Required data was null.")).ReplaceFinalText); Assert.True(transcript.Final);
        var call = Json("""{"type":"response.function_call_arguments.done","name":"native_delegate","call_id":"call_real","arguments":"{\"transcript\":\"Inspect device\"}"}""");
        Assert.Equal(new CodexDelegation("call_real", "Inspect device"), CodexRealtimeGaProtocol.ReadDelegation(call));
        Assert.Null(CodexRealtimeGaProtocol.ReadDelegation(Json("""{"type":"response.function_call_arguments.done","name":"other","call_id":"call_real","arguments":"{}"}""")));
    }
    private sealed class Socket(params string[] incoming) : WebSocket
    {
        private readonly Queue<string> messages = new(incoming);
        internal List<JsonElement> Sent { get; } = [];
        internal Func<CancellationToken, Task>? BeforeSend { get; init; }
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => WebSocketState.Open;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (!messages.TryDequeue(out var value)) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var bytes = Encoding.UTF8.GetBytes(value); bytes.CopyTo(buffer.AsSpan()); return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            if (BeforeSend is { } before) await before(ct);
            Sent.Add(Json(Encoding.UTF8.GetString(buffer)));
        }
    }
    [Fact]
    public async Task ConfigurationNeedsExactModelAcknowledgementAndRetainsStartupEvents()
    {
        var socket = new Socket("""{"type":"session.created","session":{"model":"gpt-realtime-2.1"}}""", """{"type":"session.updated","session":{"model":"gpt-realtime-2.1"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await connection.WaitForSessionAsync("session.created", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken, "gpt-realtime-2.1");
        await connection.WaitForSessionAsync("session.updated", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken, "gpt-realtime-2.1");
        Assert.Equal("gpt-realtime-2.1", connection.ModelIdentity.Read("gpt-realtime-2.1").ConfirmedModel);
        Assert.Equal("session.updated", connection.ModelIdentity.Read("gpt-realtime-2.1").SourceEvent);
        var types = new List<string>(); await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) types.Add(item.Type);
        Assert.Equal(new[] { "session.created", "session.updated" }, types);
        await using var wrong = new CodexRealtimeConnection(new Socket("""{"type":"session.created","session":{"model":"different"}}"""), 8192, "v2");
        await Assert.ThrowsAsync<CodexProtocolException>(() => wrong.WaitForSessionAsync("session.created", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken, "gpt-realtime-2.1"));
    }
    [Fact]
    public async Task ParallelFunctionResultsMustAllArriveBeforeAQueuedResponseStarts()
    {
        var socket = new Socket(
            """{"type":"response.created"}""",
            """{"type":"response.function_call_arguments.done","call_id":"one"}""",
            """{"type":"response.function_call_arguments.done","call_id":"two"}""",
            """{"type":"response.done","response":{"status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken))
        {
            if (item.Type != "response.done") continue;
            await connection.SendEventAsync(CodexRealtimeGaProtocol.FunctionOutput("one", "result one"), TestContext.Current.CancellationToken);
            await connection.SendEventAsync(new JsonObject { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(socket.Sent, x => x.GetProperty("type").GetString() == "response.create");
            await connection.SendEventAsync(CodexRealtimeGaProtocol.FunctionOutput("two", "result two"), TestContext.Current.CancellationToken);
            Assert.Equal("response.create", socket.Sent[^1].GetProperty("type").GetString());
        }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SleepingCommentaryReceiptCannotReleasePreviouslyQueuedResponse(bool wakeBeforeLastReceipt, bool cancelReceipt)
    {
        var socket = new Socket(
            """{"type":"response.created"}""",
            """{"type":"response.function_call_arguments.done","call_id":"one"}""",
            """{"type":"response.function_call_arguments.done","call_id":"two"}""",
            """{"type":"response.done","response":{"status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await foreach (var item in connection.ReadEventsAsync(TestContext.Current.CancellationToken))
        {
            if (item.Type == "response.created")
                await connection.AppendContextAsync("Synthetic result queued while awake", channel: "speakable", cancellationToken: TestContext.Current.CancellationToken);
            if (item.Type != "response.done") continue;
            connection.SetResponseRequestsAllowed(false);
            await connection.AppendContextAsync("sleeping receipt", "one", "commentary", TestContext.Current.CancellationToken);
            if (wakeBeforeLastReceipt) connection.SetResponseRequestsAllowed(true);
            if (cancelReceipt)
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    connection.AppendContextAsync("cancelled receipt", "two", "commentary", cancellation.Token));
            }
            await connection.AppendContextAsync("parallel receipt", "two", "commentary", TestContext.Current.CancellationToken);
            Assert.Equal(2, socket.Sent.Count(x => x.TryGetProperty("item", out var receipt) && receipt.GetProperty("type").GetString() == "function_call_output"));
            Assert.DoesNotContain(socket.Sent, x => x.GetProperty("type").GetString() == "response.create");
            connection.SetResponseRequestsAllowed(true);
            Assert.DoesNotContain(socket.Sent, x => x.GetProperty("type").GetString() == "response.create");
            await connection.AppendUserTextAsync("Synthetic fresh owner request", TestContext.Current.CancellationToken);
            Assert.Single(socket.Sent, x => x.GetProperty("type").GetString() == "response.create");
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SleepSuppressionDoesNotWaitForBlockedSocketWrite(bool wakeBeforeWriteCompletes)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new Socket { BeforeSend = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        } };
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var write = connection.AppendUserTextAsync("Synthetic request before sleep", TestContext.Current.CancellationToken);
        await entered.Task;
        connection.SetResponseRequestsAllowed(false);
        if (wakeBeforeWriteCompletes) connection.SetResponseRequestsAllowed(true);
        release.SetResult();
        await write;
        Assert.Single(socket.Sent);
        Assert.DoesNotContain(socket.Sent, x => x.GetProperty("type").GetString() == "response.create");
        connection.SetResponseRequestsAllowed(true);
        Assert.Single(socket.Sent);
    }
    [Fact]
    public async Task ContextIsSilentToolOutputIsNotDuplicatedAndResponseRequestsCoalesce()
    {
        var socket = new Socket("""{"type":"response.done","response":{"status":"completed"}}""");
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        await connection.AppendAudioAsync(new byte[] { 1, 0 }, TestContext.Current.CancellationToken);
        Assert.Equal("input_audio_buffer.append", socket.Sent[0].GetProperty("type").GetString());
        await connection.AppendContextAsync("pending, not success", "call_real", "commentary", TestContext.Current.CancellationToken);
        await connection.AppendContextAsync("later result", "call_real", "commentary", TestContext.Current.CancellationToken);
        Assert.Single(socket.Sent, x => x.TryGetProperty("item", out var i) && i.GetProperty("type").GetString() == "function_call_output");
        Assert.DoesNotContain(socket.Sent, x => x.GetProperty("type").GetString() == "response.create");
        await connection.AppendUserTextAsync("first", TestContext.Current.CancellationToken); await connection.AppendUserTextAsync("second", TestContext.Current.CancellationToken);
        Assert.Single(socket.Sent, x => x.GetProperty("type").GetString() == "response.create");
        await foreach (var _ in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) { }
        Assert.Equal(2, socket.Sent.Count(x => x.GetProperty("type").GetString() == "response.create"));
        await connection.SendEventAsync(new JsonObject { ["type"] = "conversation.item.truncate", ["item_id"] = "real", ["audio_end_ms"] = 25 }, TestContext.Current.CancellationToken);
        Assert.Equal("conversation.item.truncate", socket.Sent[^1].GetProperty("type").GetString());
    }
}
