using System.Net;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeApiCallTests
{
    private sealed class ApiKeys : ICodexRealtimeApiKeyProvider
    {
        public int Requests { get; private set; }
        public ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            Requests++;
            return ValueTask.FromResult("api-test-key");
        }
    }

    private sealed class OAuth : ICodexCredentialProvider
    {
        public int Requests { get; private set; }
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Requests++;
            return ValueTask.FromResult(new CodexCredentials("oauth-test-token", "test-account"));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    // Rust realtime_call.rs sends_api_session_call_as_multipart_body and
    // sends_frameless_session_call_to_live_without_legacy_query_params.
    [Theory]
    [InlineData("v1", null, "cove", "https://api.openai.com/v1/realtime/calls?intent=quicksilver&architecture=avas", "quicksilver=v1")]
    [InlineData("v3", null, "marin", "https://api.openai.com/v1/live", "quicksilver=v2")]
    [InlineData("v3", "marin", "marin", "https://api.openai.com/v1/live", "quicksilver=v2")]
    [InlineData("v3", "cove", "cove", "https://api.openai.com/v1/live", "quicksilver=v2")]
    public async Task ApiSessionCallUsesRustMultipart(string protocol, string? voice, string expectedVoice, string uri, string alpha)
    {
        const string boundary = "codex-realtime-call-boundary";
        const string offer = "v=offer\r\n";
        var apiKeys = new ApiKeys();
        using var client = CodexClient.ForRealtimeApi(apiKeys, handler: new Handler(async (request, ct) =>
        {
            Assert.Equal(uri, request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api-test-key", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("ChatGPT-Account-Id"));
            Assert.Equal(alpha, request.Headers.GetValues("openai-alpha").Single());
            var content = request.Content ?? throw new InvalidDataException("Missing call content.");
            Assert.Equal("multipart/form-data; boundary=" + boundary, content.Headers.ContentType?.ToString());
            var raw = await content.ReadAsStringAsync(ct);
            var prefix = $"--{boundary}\r\nContent-Disposition: form-data; name=\"sdp\"\r\nContent-Type: application/sdp\r\n\r\n{offer}\r\n" +
                $"--{boundary}\r\nContent-Disposition: form-data; name=\"session\"\r\nContent-Type: application/json\r\n\r\n";
            var suffix = $"\r\n--{boundary}--\r\n";
            Assert.StartsWith(prefix, raw);
            Assert.EndsWith(suffix, raw);
            using var doc = JsonDocument.Parse(raw[prefix.Length..^suffix.Length]);
            Assert.Equal(expectedVoice, doc.RootElement.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
            Assert.Equal("explicit-model", doc.RootElement.GetProperty("model").GetString());
            Assert.False(doc.RootElement.TryGetProperty("id", out _));
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("v=0\r\n") };
            response.Headers.TryAddWithoutValidation("Location", "/v1/realtime/calls/calls/12345678-abcd-4321-aabc-123456789abc?forwarded=1");
            return response;
        }));
        var call = await client.CreateRealtimeCallAsync(offer, new("hi") { Protocol = protocol, Voice = voice, Model = "explicit-model" }, TestContext.Current.CancellationToken);
        Assert.Equal("12345678-abcd-4321-aabc-123456789abc", call.CallId);
        Assert.Equal(1, apiKeys.Requests);
    }

    [Fact]
    public async Task WrongCredentialDomainAndV2CallsAreRejectedBeforeDispatch()
    {
        var oauth = new OAuth();
        var apiKeys = new ApiKeys();
        using var wrong = new CodexClient(oauth, new() { BackendBaseUri = new("https://api.openai.com/v1/") },
            new Handler((_, _) => throw new InvalidOperationException("Invalid destination dispatched.")));
        await Assert.ThrowsAsync<ArgumentException>(() => wrong.CreateRealtimeCallAsync("v=offer", new("hi"), TestContext.Current.CancellationToken));
        Assert.Equal(0, oauth.Requests);
        using var api = CodexClient.ForRealtimeApi(apiKeys, handler: new Handler((_, _) => throw new InvalidOperationException("Invalid version dispatched.")));
        await Assert.ThrowsAsync<ArgumentException>(() => api.CreateRealtimeCallAsync("v=offer", new("hi") { Protocol = "v2" }, TestContext.Current.CancellationToken));
        Assert.Equal(0, apiKeys.Requests);
    }

    [Fact]
    public async Task AmbiguousApiCallFailureIsNotReplayed()
    {
        var requests = 0;
        using var client = CodexClient.ForRealtimeApi(new ApiKeys(), handler: new Handler((_, _) =>
        {
            requests++;
            throw new HttpRequestException("Ambiguous network failure.");
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateRealtimeCallAsync("v=offer", new("hi"), TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }
}
