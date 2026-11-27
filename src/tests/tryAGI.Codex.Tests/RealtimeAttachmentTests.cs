using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeAttachmentTests
{
    private sealed class OAuth : ICodexCredentialProvider
    {
        public int Requests { get; private set; }
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Requests++;
            return ValueTask.FromResult(new CodexCredentials("test-token", "account-a"));
        }
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [InlineData("v3")]
    public async Task CallerStreamUsesSameParserAndDetachLeavesMediaChannelsOpen(string protocol)
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = new OAuth();
        using var client = new CodexClient(auth);
        var incoming = Channel.CreateUnbounded<string>();
        var outgoing = Channel.CreateUnbounded<string>();
        var stream = new CodexRealtimeAttachmentStream(incoming.Reader, outgoing.Writer);
        var call = await client.AttachExistingRealtimeCallAsync("rtc_test", stream, protocol, cancellationToken: ct);
        await using var attachment = call;
        Assert.Equal("rtc_test", call.CallId);
        Assert.Equal(protocol, call.Connection.Protocol);
        Assert.Equal(0, auth.Requests);
        Assert.False(outgoing.Reader.TryRead(out _));
        await incoming.Writer.WriteAsync("""{"type":"session.started","session":{"id":"session-a"}}""", ct);
        await using (var reader = call.Connection.ReadEventsAsync(ct).GetAsyncEnumerator(ct))
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal("session.started", reader.Current.Type);
        }
        await call.Connection.SendEventAsync(new JsonObject { ["type"] = "test.request" }, ct);
        Assert.Equal("test.request", JsonNode.Parse(await outgoing.Reader.ReadAsync(ct))?["type"]?.GetValue<string>());
        await call.DetachAsync(ct);
        await call.DetachAsync(ct);
        Assert.False(outgoing.Reader.TryRead(out _));
        Assert.True(incoming.Writer.TryWrite("""{"type":"session.updated"}"""));
        Assert.True(outgoing.Writer.TryWrite("""{"type":"caller.still.owns"}"""));
        await using var reattached = await client.AttachExistingRealtimeCallAsync("rtc_test", stream, protocol, cancellationToken: ct);
        await using var nextReader = reattached.Connection.ReadEventsAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await nextReader.MoveNextAsync());
        Assert.Equal("session.updated", nextReader.Current.Type);
    }

    [Theory]
    [InlineData("wss://example.invalid/v1/live")]
    [InlineData("wss://api.openai.com.example.invalid/v1/live")]
    [InlineData("wss://api.openai.com:444/v1/live")]
    [InlineData("wss://api.openai.com/v1/live?redirect=1")]
    public async Task UntrustedDestinationRejectedBeforeCredentials(string uri)
    {
        var auth = new OAuth();
        using var client = new CodexClient(auth);
        await Assert.ThrowsAsync<ArgumentException>(() => client.AttachExistingRealtimeCallAsync("rtc_test",
            new CodexRealtimeAttachmentOptions { SidebandBaseUri = new Uri(uri) }, TestContext.Current.CancellationToken));
        Assert.Equal(0, auth.Requests);
    }

    [Fact]
    public async Task AccountMismatchRejectedBeforeNetwork()
    {
        var auth = new OAuth();
        using var client = new CodexClient(auth);
        await Assert.ThrowsAsync<CodexRealtimeAccountMismatchException>(() => client.AttachExistingRealtimeCallAsync("rtc_test",
            new CodexRealtimeAttachmentOptions { ExpectedAccountId = "account-b" }, TestContext.Current.CancellationToken));
        Assert.Equal(1, auth.Requests);
    }

    [Fact]
    public async Task ExternalHeadersRequireExplicitDestinationAndNeverFetchOAuth()
    {
        var auth = new OAuth();
        using var client = new CodexClient(auth);
        var headersRequested = 0;
        ValueTask<IReadOnlyDictionary<string, string>> Headers(CancellationToken ct)
        {
            headersRequested++;
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["Host"] = "api.openai.com" });
        }
        var options = new CodexRealtimeAttachmentOptions
        {
            Authentication = CodexRealtimeAttachmentAuthentication.ExternalHeaders,
            ExternalHeaderProvider = Headers,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => client.AttachExistingRealtimeCallAsync("rtc_test", options,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, headersRequested);
        await Assert.ThrowsAsync<ArgumentException>(() => client.AttachExistingRealtimeCallAsync("rtc_test",
            new CodexRealtimeAttachmentOptions
            {
                Authentication = options.Authentication, ExternalHeaderProvider = Headers,
                SidebandBaseUri = new Uri("wss://external.invalid/v1/live"),
            }, TestContext.Current.CancellationToken));
        Assert.Equal(1, headersRequested);
        Assert.Equal(0, auth.Requests);
    }

    [Fact]
    public async Task ElapsedDeadlineAndCancellationHaveNoAuthorizationOrTransportEffects()
    {
        var auth = new OAuth();
        using var client = new CodexClient(auth);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AttachExistingRealtimeCallAsync("rtc_test",
            new CodexRealtimeAttachmentOptions { StartBefore = DateTimeOffset.UtcNow.AddSeconds(-1) }, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AttachExistingRealtimeCallAsync("rtc_test",
            cancellationToken: cancelled.Token));
        Assert.Equal(0, auth.Requests);
    }
}
