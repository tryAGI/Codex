using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class LiveFeatureTests
{
    private static readonly CodexLiveChoiceQuestion Question = new("route", "Select a route.", ["coding", "none"]);
    private sealed class Credentials : ICodexCredentialProvider
    { public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new CodexCredentials("private-token", "private-account")); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void FeaturesAreOptInAndSleepUpdatesDoNotRecreateStartupConfiguration()
    {
        var basis = new CodexRealtimeSessionOptions("normal instructions");
        var ordinary = CodexRealtimeProtocol.Session(basis);
        Assert.Null(ordinary["triggers"]);
        Assert.Equal("client", ordinary["delegation"]?["type"]?.GetValue<string>());
        var settings = basis with { LiveDecisionTriggers = [new("session.input_transcript.delta", [Question])],
            LiveCustomVoiceId = "voice_saved", LiveVoiceDescription = "A warm adult voice." };
        var session = CodexRealtimeProtocol.Session(settings);
        Assert.Equal("voice_saved", session["audio"]?["output"]?["voice"]?["id"]?.GetValue<string>());
        Assert.Contains("A warm adult voice.", session["instructions"]?.GetValue<string>() ?? "");
        Assert.Equal("decisions", session["triggers"]?[0]?["type"]?.GetValue<string>());
        Assert.Equal("client", session["delegation"]?["type"]?.GetValue<string>());
        var update = CodexRealtimeProtocol.SessionUpdate(settings)["session"];
        Assert.Null(update?["model"]); Assert.Null(update?["triggers"]); Assert.Null(update?["audio"]);
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(settings with { Protocol = "v1" }));
    }

    [Theory]
    [InlineData("session.input_transcript.delta")]
    [InlineData("session.output_transcript.delta")]
    public void TriggerShapeUsesDocumentedChoiceQuestions(string direction)
    {
        var json = CodexLiveFeatures.Triggers([new(direction, [Question])]);
        Assert.Equal(direction, json[0]?["event_type"]?.GetValue<string>());
        Assert.Equal("coding", json[0]?["decisions"]?["questions"]?[0]?["choices"]?[0]?["value"]?.GetValue<string>());
    }

    [Fact]
    public void InvalidOrUnboundedConfigurationIsRejectedBeforeDispatch()
    {
        Assert.Throws<ArgumentException>(() => CodexLiveFeatures.Questions([Question, Question]));
        Assert.Throws<ArgumentException>(() => CodexLiveFeatures.Questions([Question with { Choices = ["same", "same"] }]));
        Assert.Throws<ArgumentException>(() => CodexLiveFeatures.Triggers([new("input_transcript.added", [Question])]));
        Assert.Throws<ArgumentException>(() => CodexLiveFeatures.PromptVoice("voice", "", null));
        Assert.Throws<ArgumentException>(() => CodexLiveFeatures.PromptVoice("voice", "Adult voice", " "));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("x") { LiveCustomVoiceId = "bad/id" }));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("x") { LiveVoiceDescription = "orphan description" }));
    }

    [Fact]
    public async Task OAuthDecisionRequestUsesFixedOriginAndNeverExposesCredentialsInBody()
    {
        var calls = 0;
        using var client = new CodexLiveFeatureClient(new Credentials(), handler: new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal("https://api.openai.com/v1/decisions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("private-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("private-account", request.Headers.GetValues("ChatGPT-Account-Id").Single());
            var body = await (request.Content ?? throw new InvalidDataException()).ReadAsStringAsync(ct);
            Assert.DoesNotContain("private-token", body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("gpt-6-luna", json.RootElement.GetProperty("model").GetString());
            return Reply("""{"id":"dec_test","answers":[{"type":"choice","name":"route","choice":"coding","confidence":0.95}]}""");
        }));
        var result = await client.DecideAsync("Synthetic coding request", [Question], TestContext.Current.CancellationToken);
        Assert.Equal("coding", CodexLiveFeatures.ReadAnswers(result)?[0].Choice); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RejectionsAreSingleAttemptAndKeepOnlyBoundedMachineCodes(int status)
    {
        var calls = 0;
        using var client = new CodexLiveFeatureClient(new Credentials(), handler: new Handler((_, _) =>
        { calls++; return Task.FromResult(Reply("""{"error":{"code":"insufficient_scope","message":"private-token user speech"}}""", (HttpStatusCode)status)); }));
        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => client.DecideAsync("text", [Question], TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal("insufficient_scope", error.RealtimeError?.Code);
        Assert.DoesNotContain("private-token", error.ToString()); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task VoiceCreationIsExplicitApiKeyRouteAndReturnsReusableIdentityWithoutPreview()
    {
        var calls = 0;
        using var client = CodexLiveFeatureClient.ForApiKey(_ => ValueTask.FromResult("private-api-key"), new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal("https://api.openai.com/v1/audio/voices", request.RequestUri?.AbsoluteUri);
            Assert.False(request.Headers.Contains("ChatGPT-Account-Id"));
            using var doc = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException()).ReadAsStringAsync(ct));
            Assert.Equal("prompt", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("auto", doc.RootElement.GetProperty("model").GetString());
            return Reply("""{"id":"voice_saved","name":"Example","type":"prompt"}""");
        }));
        var voice = await client.CreateVoiceAsync("Example", "A warm adult voice.", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("voice_saved", voice.Id); Assert.Equal("A warm adult voice.", voice.Prompt); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("refusal", "none")]
    [InlineData("choice", "unadvertised")]
    public async Task UnsupportedAnswersCannotBecomeRoutingHints(string type, string choice)
    {
        var body = new JsonObject { ["answers"] = new JsonArray(new JsonObject { ["type"] = type, ["name"] = "route", ["choice"] = choice }) };
        using var client = new CodexLiveFeatureClient(new Credentials(), handler: new Handler((_, _) => Task.FromResult(Reply(body.ToJsonString()))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => client.DecideAsync("text", [Question], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DecisionReceiptsRetainSourceIdentityAndRejectDuplicateOrUnsafeAnswers()
    {
        using var valid = JsonDocument.Parse("""{"type":"decision.event","source_event_id":"source_1","source_event_type":"session.input_transcript.delta","trigger_id":"trigger_1","event":{"type":"decision.completed","decision":{"answers":[{"type":"choice","name":"route","choice":"coding"}]}}}""");
        Assert.Equal("source_1", CodexLiveFeatures.ReadDecision(valid.RootElement)?.SourceEventId);
        using var duplicate = JsonDocument.Parse("""{"answers":[{"type":"choice","name":"route","choice":"coding"},{"type":"choice","name":"route","choice":"none"}]}""");
        Assert.Null(CodexLiveFeatures.ReadAnswers(duplicate.RootElement));
        using var unsafeAnswer = JsonDocument.Parse("""{"answers":[{"type":"choice","name":"route","choice":"private arbitrary text"}]}""");
        Assert.Null(CodexLiveFeatures.ReadAnswers(unsafeAnswer.RootElement));
    }
}
