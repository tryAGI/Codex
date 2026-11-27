using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexCodeModeGrpcTransportTests
{
    private static readonly Uri Endpoint = new("https://code-mode.example.test/");
    private static readonly Guid Invocation = Guid.Parse("12345678-1234-4321-9876-123456789abc");

    private static byte[] Wire(Action<CodexCodeModeProtoWriter> build)
    {
        var writer = new CodexCodeModeProtoWriter();
        build(writer);
        return writer.ToArray();
    }

    private static byte[] Frames(params byte[][] messages)
    {
        using var stream = new MemoryStream();
        foreach (var message in messages)
        {
            var header = new byte[5];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);
            stream.Write(header);
            stream.Write(message);
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage Response(byte[] bytes, int? status = 0)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        if (status is { } code) response.TrailingHeaders.Add("grpc-status", code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private static async Task<byte[]> RequestBytes(HttpRequestMessage request, string method, CancellationToken ct)
    {
        Assert.Equal(new Uri(Endpoint, "/codex.code_mode.v1.CodeModeHost/" + method), request.RequestUri);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/grpc", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains(request.Headers.TE, value => value.Value == "trailers");
        var bytes = await request.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(0, bytes[0]);
        Assert.Equal((uint)(bytes.Length - 5), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4)));
        return bytes[5..];
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task OpenSessionUsesPinnedFieldNumbersAndParsesIndependentLeaseEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new CodexCodeModeGrpcTransport(Endpoint, async (request, token) =>
        {
            var bytes = await RequestBytes(request, "OpenSession", token);
            Assert.Equal(Convert.FromHexString("0A050896011002100218202805"), bytes);
            return Response(Frames(
                Convert.FromHexString("0A070A017310021801"),
                Wire(writer => writer.Message(2, value => value.Text(1, Invocation.ToString("D")))),
                Wire(writer => writer.Message(3, value =>
                {
                    value.Text(1, Invocation.ToString("D")); value.Text(2, "e"); value.Text(3, "c");
                    value.Text(4, "call"); value.Text(5, "progress");
                })),
                Wire(writer => writer.Message(5, value =>
                {
                    value.Text(1, "e"); value.Text(2, "c"); value.UInt(3, 9);
                })),
                Convert.FromHexString("3200")));
        });
        var client = new CodexCodeModeHostClient(transport);
        var events = new List<CodexCodeModeHostEvent>();
        await foreach (var item in client.OpenSessionAsync(32, 5, 2, 150, 2, ct))
            events.Add(item);
        var opened = Assert.IsType<CodexCodeModeHostEvent.Opened>(events[0]);
        Assert.Equal("s", opened.Host.SessionId);
        Assert.True(opened.Host.SupportsCatalogRevision);
        Assert.Equal(2, opened.Host.OutputTruncation);
        Assert.Equal(Invocation, Assert.IsType<CodexCodeModeHostEvent.ToolCancelled>(events[1]).InvocationId);
        Assert.Equal("progress", Assert.IsType<CodexCodeModeHostEvent.Notification>(events[2]).Text);
        Assert.Equal(9UL, Assert.IsType<CodexCodeModeHostEvent.CellClosed>(events[3]).FinalToolCallSequence);
        Assert.IsType<CodexCodeModeHostEvent.Heartbeat>(events[4]);
    }

    [Fact]
    public async Task ExecuteCarriesExactCatalogAndReturnsStartedThenOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new CodexCodeModeHostClient(new(Endpoint, async (http, token) =>
        {
            var bytes = await RequestBytes(http, "Execute", token);
            var request = new CodexCodeModeProto(bytes);
            Assert.Equal("session", request.Text(1));
            Assert.Equal("execution", request.Text(2));
            Assert.Equal("provider", request.Text(3));
            Assert.Equal("text(await tools.echo({}));", request.Text(4));
            Assert.Equal(11UL, request.UInt(6));
            Assert.Equal(100UL, request.UInt(7));
            Assert.Equal(3UL, request.UInt(8));
            var tool = Assert.Single(request.Repeated(5));
            Assert.Equal("echo", tool.Text(1));
            Assert.Equal("echo", tool.Message(2).Text(1));
            Assert.Equal("functions", tool.Message(2).Text(2));
            Assert.Equal(1UL, tool.UInt(4));
            Assert.Equal("""{"type":"object"}""", Encoding.UTF8.GetString(tool.Bytes(5)!.Value.Span));
            return Response(Frames(
                Wire(writer => writer.Message(1, started =>
                {
                    started.Text(1, "execution"); started.Text(2, "cell");
                })),
                Wire(writer => writer.Message(2, outcome =>
                {
                    outcome.Text(1, "cell");
                    outcome.Message(2, content => content.Message(1, text => text.Text(1, "hello")));
                    outcome.Message(2, content => content.Message(2, image =>
                    {
                        image.Text(1, "data:image/png;base64,AA=="); image.UInt(2, 3);
                    }));
                    outcome.Message(5, _ => { });
                    outcome.UInt(6, 123456);
                }))));
        }));
        var events = new List<CodexCodeModeHostExecuteEvent>();
        await foreach (var item in client.ExecuteAsync("session", "execution", "provider", "text(await tools.echo({}));",
            [new("echo", new(null, "echo"), CodexToolCallKind.Function, "Echo", Json("""{"type":"object"}"""))],
            11, 100, 3, ct)) events.Add(item);
        Assert.Equal("cell", Assert.IsType<CodexCodeModeHostExecuteEvent.Started>(events[0]).CellId);
        var result = Assert.IsType<CodexCodeModeHostExecuteEvent.Outcome>(events[1]).Result;
        Assert.Equal(CodexCodeModeHostOutcomeKind.Completed, result.Kind);
        Assert.Equal(123456UL, result.HostDurationNanoseconds);
        Assert.Equal("hello", result.Content[0].Value);
        Assert.Equal(3, result.Content[1].ImageDetail);
    }

    [Fact]
    public async Task ExecuteDoesNotExposeOutcomeWhenFinalGrpcStatusFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new CodexCodeModeHostClient(new(Endpoint, (_, _) => Task.FromResult(Response(Frames(
            Wire(writer => writer.Message(1, started => { started.Text(1, "e"); started.Text(2, "c"); })),
            Wire(writer => writer.Message(2, result =>
            {
                result.Text(1, "c");
                result.Message(5, _ => { });
            }))), 14))));
        var observed = new List<CodexCodeModeHostExecuteEvent>();
        var error = await Assert.ThrowsAsync<CodexCodeModeGrpcException>(async () =>
        {
            await foreach (var item in client.ExecuteAsync("s", "e", "p", "text(1)", [], cancellationToken: ct))
                observed.Add(item);
        });
        Assert.Equal(14, error.StatusCode);
        Assert.IsType<CodexCodeModeHostExecuteEvent.Started>(Assert.Single(observed));
    }

    [Fact]
    public async Task SubscriptionCorrelatesSessionAndPreservesJsonAndHeartbeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new CodexCodeModeHostClient(new(Endpoint, async (http, token) =>
        {
            var request = new CodexCodeModeProto(await RequestBytes(http, "SubscribeToToolCalls", token));
            Assert.Equal("session", request.Text(1));
            Assert.Empty(request.Repeated(2));
            return Response(Frames(
                Wire(writer =>
                {
                    writer.Text(1, "session"); writer.Text(2, "execution"); writer.Text(3, "cell");
                    writer.Text(4, Invocation.ToString("D")); writer.Text(5, "runtime-call");
                    writer.Message(6, name => { name.Text(1, "echo"); name.Text(2, "mcp"); });
                    writer.UInt(7, 1); writer.Bytes(8, """{"x":1}"""u8); writer.UInt(9, 1);
                    writer.Text(10, "trace");
                }),
                Wire(writer => { writer.Text(1, "session"); writer.Message(11, _ => { }); })));
        }));
        var calls = new List<CodexCodeModeHostToolCall>();
        await foreach (var call in client.SubscribeToToolCallsAsync("session", cancellationToken: ct))
            calls.Add(call);
        Assert.Equal(Invocation, calls[0].InvocationId);
        Assert.Equal(new CodexToolIdentity("mcp", "echo"), calls[0].Identity);
        Assert.Equal("runtime-call", calls[0].RuntimeToolCallId);
        Assert.Equal(1, calls[0].Input!.Value.GetProperty("x").GetInt32());
        Assert.Equal(1UL, calls[0].Sequence);
        Assert.True(calls[1].Heartbeat);
        Assert.Null(calls[1].InvocationId);
    }

    [Fact]
    public async Task WaitTerminateYieldCancelAndCompletionUseIndependentUnaryCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        var methods = new List<string>();
        var client = new CodexCodeModeHostClient(new(Endpoint, async (http, token) =>
        {
            var method = http.RequestUri!.Segments[^1];
            methods.Add(method);
            var payload = new CodexCodeModeProto(await RequestBytes(http, method, token));
            Assert.Equal("s", payload.Text(1));
            if (method is "Wait" or "Terminate")
            {
                Assert.Equal("c", payload.Text(2));
                if (method == "Wait") { Assert.Equal("w", payload.Text(3)); Assert.Equal(100UL, payload.UInt(4)); }
                return Response(Frames(Wire(writer => writer.Message(method == "Wait" ? 1 : 2, outcome =>
                {
                    outcome.Text(1, "c");
                    outcome.Message(method == "Wait" ? 3 : 4, _ => { });
                }))));
            }
            if (method == "YieldObservation") { Assert.Equal("w", payload.Text(3)); Assert.False(payload.Has(2)); }
            if (method == "CancelWait") Assert.Equal("w", payload.Text(2));
            if (method == "CompleteToolCall")
            {
                Assert.Equal(Invocation.ToString("D"), payload.Text(2));
                if (payload.Has(3)) Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(payload.Message(3).Bytes(1)!.Value.Span));
                else Assert.Equal("denied", payload.Message(4).Text(1));
            }
            if (method == "AcknowledgeNotification") Assert.Equal(Invocation.ToString("D"), payload.Text(2));
            return Response(Frames(Array.Empty<byte>()));
        }));
        var wait = await client.WaitAsync("s", "c", "w", 100, cancellationToken: ct);
        Assert.False(wait.MissingCell);
        Assert.Equal(CodexCodeModeHostOutcomeKind.Yielded, wait.Outcome.Kind);
        var termination = await client.TerminateAsync("s", "c", cancellationToken: ct);
        Assert.True(termination.MissingCell);
        Assert.Equal(CodexCodeModeHostOutcomeKind.Terminated, termination.Outcome.Kind);
        await client.YieldObservationAsync("s", "w", true, ct);
        await client.CancelWaitAsync("s", "w", ct);
        await client.CompleteToolCallAsync("s", Invocation, Json("""{"ok":true}"""), ct);
        await client.FailToolCallAsync("s", Invocation, "denied", ct);
        await client.AcknowledgeNotificationAsync("s", Invocation, ct);
        await client.CloseSessionAsync("s", ct);
        Assert.Equal(new[] { "Wait", "Terminate", "YieldObservation", "CancelWait", "CompleteToolCall",
            "CompleteToolCall", "AcknowledgeNotification", "CloseSession" }, methods);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public async Task UnaryRejectsMissingMultipleOrTruncatedFrames(int mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = mode switch { 1 => Frames([], []), 2 => new byte[] { 0, 0, 0, 0, 1 }, _ => [] };
        var transport = new CodexCodeModeGrpcTransport(Endpoint, (_, _) => Task.FromResult(Response(bytes)));
        await Assert.ThrowsAnyAsync<IOException>(() => transport.UnaryAsync("CloseSession", ReadOnlyMemory<byte>.Empty, ct));
    }

    [Fact]
    public async Task NonzeroOrMissingFinalStatusNeverCountsAsSuccessfulUnaryAndNoReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var transport = new CodexCodeModeGrpcTransport(Endpoint, (_, _) =>
        {
            calls++;
            return Task.FromResult(Response(Frames(Array.Empty<byte>()), calls == 1 ? 14 : null));
        });
        var error = await Assert.ThrowsAsync<CodexCodeModeGrpcException>(() => transport.UnaryAsync("CloseSession", ReadOnlyMemory<byte>.Empty, ct));
        Assert.Equal(14, error.StatusCode);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<CodexProtocolException>(() => transport.UnaryAsync("CloseSession", ReadOnlyMemory<byte>.Empty, ct));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvalidFrameAndDestinationFailClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Throws<ArgumentException>(() => new CodexCodeModeGrpcTransport(new("http://example.test/"),
            (_, _) => throw new InvalidOperationException()));
        var transport = new CodexCodeModeGrpcTransport(Endpoint,
            (_, _) => Task.FromResult(Response([0, 0, 0, 1, 0])), maximumMessageBytes: 16);
        await Assert.ThrowsAsync<CodexProtocolException>(() => transport.UnaryAsync("CloseSession", ReadOnlyMemory<byte>.Empty, ct));
        var compressed = new CodexCodeModeGrpcTransport(Endpoint,
            (_, _) => Task.FromResult(Response([1, 0, 0, 0, 0])));
        await Assert.ThrowsAsync<CodexProtocolException>(() => compressed.UnaryAsync("CloseSession", ReadOnlyMemory<byte>.Empty, ct));
        var wrong = new CodexCodeModeGrpcTransport(Endpoint, (_, _) =>
        {
            var response = Response(Frames(Array.Empty<byte>()));
            response.RequestMessage = new(HttpMethod.Post, "https://other.example.test/");
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<CodexProtocolException>(() => wrong.UnaryAsync("CloseSession", ReadOnlyMemory<byte>.Empty, ct));
    }

    [Fact]
    public async Task UncorrelatedSubscriptionAndAmbiguousProtobufOneofAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new CodexCodeModeGrpcTransport(Endpoint, (_, _) => Task.FromResult(Response(Frames(
            Wire(writer => { writer.Text(1, "other"); writer.Message(11, _ => { }); })))));
        var client = new CodexCodeModeHostClient(transport);
        await Assert.ThrowsAsync<CodexProtocolException>(async () =>
        {
            await foreach (var item in client.SubscribeToToolCallsAsync("session", cancellationToken: ct)) _ = item;
        });
        var ambiguous = new CodexCodeModeHostClient(new(Endpoint, (_, _) =>
            Task.FromResult(Response(Frames(Convert.FromHexString("0A030A01733200"))))));
        await Assert.ThrowsAsync<CodexProtocolException>(async () =>
        {
            await foreach (var item in ambiguous.OpenSessionAsync(1, cancellationToken: ct)) _ = item;
        });
    }
}
