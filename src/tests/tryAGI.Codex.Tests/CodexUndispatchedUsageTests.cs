using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexUndispatchedUsageTests
{
    private sealed class Usage : ICodexUsageObserver
    {
        internal readonly List<string> Events = [];
        public Task ReserveAsync(string id, string model, CancellationToken ct) { Events.Add("reserve"); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) { Events.Add("complete"); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Assert.False(ct.IsCancellationRequested); Events.Add("unknown"); return Task.CompletedTask; }
        public Task NotDispatchedAsync(string id, string model, CancellationToken ct) { Assert.False(ct.IsCancellationRequested); Events.Add("not_dispatched"); return Task.CompletedTask; }
    }
    private sealed class Credentials(Action? before = null, bool fail = false) : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
        {
            before?.Invoke();
            if (fail) throw new InvalidOperationException("Missing local authorization");
            return ValueTask.FromResult(new CodexCredentials("test", "test"));
        }
    }
    private sealed class Handler(Usage usage, bool fail = false, bool incomplete = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            usage.Events.Add("http");
            if (fail) throw new HttpRequestException("Ambiguous transport failure after dispatch entry");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(incomplete ? "" : "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") });
        }
    }
    private static async Task Read(CodexClient client, CodexResponseOptions options, CancellationToken ct = default)
    { await foreach (var _ in client.StreamResponseAsync(options, [], ct)) { } }

    [Fact]
    public async Task LocalValidationFailureAfterReservationProvesNoDispatch()
    {
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(), usageObserver: usage, handler: new Handler(usage));
        await Assert.ThrowsAsync<ArgumentException>(() => Read(client, new("test", "test") { Verbosity = "invalid" }, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "reserve", "not_dispatched" }, usage.Events);
    }
    [Fact]
    public async Task AuthorizationFailureBeforeHttpDoesNotBecomeUnknownExpense()
    {
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(fail: true), usageObserver: usage, handler: new Handler(usage));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(client, new("test", "test"), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "reserve", "not_dispatched" }, usage.Events);
    }
    [Fact]
    public async Task CancellationBeforeSendPersistsNoDispatchWithIndependentToken()
    {
        var usage = new Usage(); using var lifetime = new CancellationTokenSource();
        using var client = new CodexClient(new Credentials(lifetime.Cancel), usageObserver: usage, handler: new Handler(usage));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(client, new("test", "test"), lifetime.Token));
        Assert.Equal(new[] { "reserve", "not_dispatched" }, usage.Events);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnteredHttpOrLostTerminalReceiptMustKeepUnknownReservation(bool transportFailure)
    {
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(), usageObserver: usage,
            handler: new Handler(usage, fail: transportFailure, incomplete: !transportFailure));
        if (transportFailure) await Assert.ThrowsAsync<HttpRequestException>(() => Read(client, new("test", "test"), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<CodexProtocolException>(() => Read(client, new("test", "test"), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "reserve", "http", "unknown" }, usage.Events);
    }
    [Fact]
    public async Task ValidCompletionStillSettlesBeforeReturning()
    {
        var usage = new Usage();
        using var client = new CodexClient(new Credentials(), usageObserver: usage, handler: new Handler(usage));
        await Read(client, new("test", "test"), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "reserve", "http", "complete" }, usage.Events);
    }
    private sealed class LegacyUsage : ICodexUsageObserver
    {
        internal bool Unknown;
        public Task ReserveAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) => Task.CompletedTask;
        public Task IncompleteAsync(string id, string model, CancellationToken ct) { Unknown = true; return Task.CompletedTask; }
    }
    [Fact]
    public async Task OlderObserverRemainsConservativeUntilItImplementsProofOfNoDispatch()
    {
        var usage = new LegacyUsage();
        using var client = new CodexClient(new Credentials(fail: true), usageObserver: usage, handler: new Handler(new Usage()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(client, new("test", "test"), TestContext.Current.CancellationToken));
        Assert.True(usage.Unknown);
    }
}
