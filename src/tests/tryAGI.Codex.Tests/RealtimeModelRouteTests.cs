using System.Net;
using System.Text.Json;
namespace tryAGI.Codex.Tests;

public sealed class RealtimeModelRouteTests
{
    private sealed class Credentials : ICodexCredentialProvider
    { public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new CodexCredentials("secret", "account")); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    [Fact]
    public async Task LegacySubscriptionProbeUsesReviewedHeaderAndSession()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler(async (request, ct) =>
        {
            Assert.Contains("chatgpt.com/backend-api/codex/realtime/calls", (request.RequestUri ?? throw new InvalidDataException("Required data was null.")).AbsoluteUri);
            Assert.Equal("quicksilver=v1", request.Headers.GetValues("openai-alpha").Single());
            using var json = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct));
            var session = json.RootElement.GetProperty("session");
            Assert.Equal("quicksilver", session.GetProperty("type").GetString());
            Assert.Equal("gpt-realtime-2.1", session.GetProperty("model").GetString());
            Assert.False(session.TryGetProperty("delegation", out _));
            Assert.Equal(24000, session.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("rate").GetInt32());
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("v=0\r\nanswer") };
            response.Headers.TryAddWithoutValidation("Location", "/v1/realtime/rtc_test"); return response;
        }));
        await client.CreateRealtimeCallAsync("v=0", new("silent") { Protocol = "v1", Model = "gpt-realtime-2.1" }, TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task RouteVersionsAreNotInterchangeable()
    {
        var uri = new Uri("wss://api.openai.com/v1/live");
        Assert.Equal("wss://api.openai.com/v1/realtime?intent=quicksilver&call_id=rtc_test", CodexRealtimeProtocol.SidebandUri(uri, "rtc_test", "v1").AbsoluteUri);
        Assert.Equal("wss://api.openai.com/v1/live/rtc_test", CodexRealtimeProtocol.SidebandUri(uri, "rtc_test", "v3").AbsoluteUri);
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("silent") { Protocol = "v2" }));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("silent") { Protocol = "v1", InitialItems = [new("user", "x")] }));
        Assert.Null((CodexRealtimeProtocol.SessionUpdate(new("silent") { Protocol = "v1" })["session"] ?? throw new InvalidDataException("Required data was null."))["model"]);
        using var client = new CodexClient(new Credentials());
        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectRealtimeWebSocketAsync(new("silent") { Protocol = "v1" }, TestContext.Current.CancellationToken));
    }
    // Rust v0.172.0: core/realtime_conversation.rs model selection, RealtimeSessionConfig::voice_or_default,
    // and realtime_call.rs ChatGPT call override.
    [Theory]
    [InlineData("v1", true, "gpt-realtime-1.5", "cove")]
    [InlineData("v1", false, "gpt-realtime-1.5", "cove")]
    [InlineData("v2", false, "gpt-realtime-1.5", "marin")]
    [InlineData("v3", true, "gpt-live-1.1-codex", "cove")]
    [InlineData("v3", false, "gpt-live-1.1-codex", "marin")]
    public void RustDefaultsFollowVersionAndDestination(string protocol, bool chatGptCall, string model, string voice)
    {
        var options = new CodexRealtimeSessionOptions("instructions") { Protocol = protocol };
        var wire = protocol == "v2" ? CodexRealtimeGaProtocol.Session(options) : CodexRealtimeProtocol.Session(options, chatGptCall);
        Assert.Equal(model, wire["model"]?.GetValue<string>());
        Assert.Equal(voice, wire["audio"]?["output"]?["voice"]?.GetValue<string>());
        Assert.Equal(model, options.Model);
        Assert.Equal(voice, options.ResolveVoice(chatGptCall));
        var explicitVoice = protocol == "v2" ? "cedar" : "juniper";
        var explicitOptions = options with { Model = "explicit-model", Voice = explicitVoice };
        var explicitWire = protocol == "v2" ? CodexRealtimeGaProtocol.Session(explicitOptions) : CodexRealtimeProtocol.Session(explicitOptions, chatGptCall);
        Assert.Equal("explicit-model", explicitWire["model"]?.GetValue<string>());
        Assert.Equal(explicitVoice, explicitWire["audio"]?["output"]?["voice"]?.GetValue<string>());
        var reset = explicitOptions with { Model = null, Voice = null };
        Assert.Equal(model, reset.Model);
        Assert.Equal(voice, reset.ResolveVoice(chatGptCall));
    }

    [Fact]
    public void VoiceCatalogsMatchRust()
    {
        Assert.Equal(new[] { "juniper", "maple", "spruce", "ember", "vale", "breeze", "arbor", "sol", "cove" }, CodexProtocolCatalog.V1Voices);
        Assert.Equal(new[] { "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar" }, CodexProtocolCatalog.V2Voices);
        Assert.Equal(new[] { "juniper", "maple", "spruce", "ember", "vale", "breeze", "arbor", "sol", "cove", "marin", "aube", "bubbie", "haetsal", "hanul", "himari", "leher", "miko", "neer", "porto", "rindo", "rio", "rivage", "selva", "sonna", "tinta", "viola" }, CodexProtocolCatalog.Voices);
        Assert.Throws<ArgumentException>(() => CodexRealtimeGaProtocol.Session(new("instructions") { Protocol = "v2", Voice = "cove" }));
    }

    [Fact]
    public async Task RejectionRetainsMachineCodeWithoutProviderTextOrRetry()
    {
        var calls = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"code":"model_not_found","type":"invalid_request_error","param":"model","message":"secret owner text"}}""") }); }));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => client.CreateRealtimeCallAsync("v=0", new("silent"), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.Equal("model_not_found", error.RealtimeError?.Code);
        Assert.DoesNotContain("secret owner text", JsonSerializer.Serialize(error.RealtimeError));
    }
}
