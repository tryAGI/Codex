using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexMcpStdioTransportTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static MemoryStream StreamOf(string value) => new(Encoding.UTF8.GetBytes(value));

    [Fact]
    public async Task ReceivesMultipleFramesAndRetainsUnicodeAndExtensions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var input = StreamOf("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"text\":\"Привет 🌍\"}}\r\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\",\"future\":true}\n");
        await using var output = new MemoryStream();
        await using var transport = new CodexMcpStdioTransport(input, output);
        var first = await transport.ReceiveAsync(ct);
        var second = await transport.ReceiveAsync(ct);
        Assert.Equal("Привет 🌍", first!.Value.GetProperty("result").GetProperty("text").GetString());
        Assert.True(second!.Value.GetProperty("future").GetBoolean());
        Assert.Null(await transport.ReceiveAsync(ct));
    }

    [Fact]
    public async Task WritesSingleCompactLineAndDoesNotWriteOversizeFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var transport = new CodexMcpStdioTransport(input, output, 60);
        await transport.SendAsync(Json("{\n \"jsonrpc\": \"2.0\",\n \"method\": \"ping\"\n}"), ct);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}\n", text);
        await Assert.ThrowsAsync<CodexProtocolException>(() => transport.SendAsync(
            Json("{\"value\":\"" + new string('x', 80) + "\"}"), ct).AsTask());
        Assert.Equal(text, Encoding.UTF8.GetString(output.ToArray()));
        await transport.SendAsync(Json("{}"), ct);
        Assert.EndsWith("{}\n", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"id\":")]
    [InlineData("not json\n{}\n")]
    [InlineData("[1,2]\n")]
    [InlineData("\n")]
    public async Task RejectsMalformedOrTruncatedFramesAndPoisonsConnection(string wire)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var input = StreamOf(wire);
        await using var output = new MemoryStream();
        await using var transport = new CodexMcpStdioTransport(input, output);
        await Assert.ThrowsAsync<CodexProtocolException>(() => transport.ReceiveAsync(ct).AsTask());
        await Assert.ThrowsAsync<IOException>(() => transport.ReceiveAsync(ct).AsTask());
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(Json("{}"), ct).AsTask());
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task RejectsUnterminatedOversizeBeforeEofAndCountsUtf8Bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var input = StreamOf(new string('x', 16000));
        await using var output = new MemoryStream();
        await using var transport = new CodexMcpStdioTransport(input, output, 64);
        await Assert.ThrowsAsync<CodexProtocolException>(() => transport.ReceiveAsync(ct).AsTask());
        Assert.True(input.Position < input.Length);
        await using var shortInput = StreamOf("{\"value\":\"ééé\"}\n");
        await using var shortOutput = new MemoryStream();
        await using var limited = new CodexMcpStdioTransport(shortInput, shortOutput, 15);
        await Assert.ThrowsAsync<CodexProtocolException>(() => limited.ReceiveAsync(ct).AsTask());
    }

    [Fact]
    public async Task PreCancelledWriteDoesNotPoisonAndDisposeIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var transport = new CodexMcpStdioTransport(input, output);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(Json("{}"), cancelled.Token).AsTask());
        await transport.SendAsync(Json("{}"), ct);
        Assert.Equal("{}\n", Encoding.UTF8.GetString(output.ToArray()));
        await transport.DisposeAsync();
        await transport.DisposeAsync();
        Assert.False(input.CanRead);
        Assert.False(output.CanWrite);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.ReceiveAsync(ct).AsTask());
    }
}
