using System.Net;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexAgentItemObserverTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("test", "test"));
    }
    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") });
    }
    private const string Message = """{"id":"m1","type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}""";
    private static string Event(string type, string fields) => $"data: {{\"type\":\"{type}\",{fields}}}\n\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionObserverRunsOnlyAfterWholeOutputReconciliation(bool conflict)
    {
        var stream = Event("response.output_item.added", "\"item\":" + Message) +
            Event("response.output_text.delta", "\"item_id\":\"m1\",\"delta\":\"done\"") +
            Event("response.output_item.done", "\"output_index\":0,\"item\":" + Message) +
            Event("response.completed", "\"response\":{\"output\":[" + (conflict ? Message.Replace("done", "changed", StringComparison.Ordinal) : Message) + "]}");
        using var client = new CodexClient(new Credentials(), handler: new Handler(stream));
        var preview = new List<string>(); var completed = new List<string>();
        var run = new CodexAgent(client).RunWithRichResultsAsync(new("test", "test"), [], (_, _) => throw new InvalidOperationException(), observeAsync: (item, token) => { token.ThrowIfCancellationRequested(); preview.Add(item.Type); return Task.CompletedTask; }, observeValidatedItem: (item, _) => { completed.Add((item.GetProperty("id").GetString() ?? throw new InvalidDataException("Required data was null."))); return Task.CompletedTask; }, cancellationToken: TestContext.Current.CancellationToken);
        if (conflict) { await Assert.ThrowsAsync<CodexProtocolException>(() => run); Assert.Empty(completed); }
        else { Assert.Equal("done", (await run).Text); Assert.Equal(new[] { "m1" }, completed); }
        Assert.Contains("response.output_text.delta", preview);
    }
}
