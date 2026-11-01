using System.Net;
using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class CodexResponseErrorTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("PRIVATE_TOKEN", "PRIVATE_ACCOUNT"));
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task RejectedResponsesExposeStructuredCodeAndCallIdPathWithoutProviderTextOrCredentials()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler("""
            {"error":{"type":"invalid_request_error","code":"string_above_max_length","param":"input[3].call_id","message":"PRIVATE_ARGUMENT PRIVATE_TOKEN PRIVATE_ACCOUNT"}}
            """));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(async () =>
        {
            await foreach (var item in client.StreamResponseAsync(new("test-model", "instructions"), [], TestContext.Current.CancellationToken)) { _ = item; }
        });
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("input[3].call_id", error.RealtimeError?.Parameter);
        Assert.Contains("string_above_max_length", error.Message);
        Assert.Contains("invalid_request_error", error.Message);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Theory]
    [InlineData("PRIVATE_ARGUMENT invalid-json")]
    [InlineData("{\"error\":{\"code\":\"PRIVATE SECRET\",\"param\":\"input[2].PRIVATE_FIELD\",\"message\":\"PRIVATE_ARGUMENT\"}}")]
    public async Task MalformedAndUnsafeErrorMetadataDoesNotLeak(string body)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(body));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(async () =>
        {
            await foreach (var item in client.StreamResponseAsync(new("test-model", "instructions"), [], TestContext.Current.CancellationToken)) { _ = item; }
        });
        Assert.Equal(400, error.StatusCode);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }
}
