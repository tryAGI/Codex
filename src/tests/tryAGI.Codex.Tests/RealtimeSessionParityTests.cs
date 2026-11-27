using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeSessionParityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void FramelessAcknowledgementSettingHasThreeWireStates(bool? ack)
    {
        var settings = new CodexRealtimeSessionOptions("test") { DelegationAckFillerSetting = ack };
        var delegation = (JsonObject)CodexRealtimeProtocol.Session(settings)["delegation"]!;
        Assert.Equal(ack.HasValue, delegation.ContainsKey("ack_filler"));
        if (ack.HasValue) Assert.Equal(ack.Value, delegation["ack_filler"]!.GetValue<bool>());
        Assert.Equal(ack ?? false, settings.DelegationAckFiller);
        var update = (JsonObject)CodexRealtimeProtocol.SessionUpdate(settings)["session"]!;
        Assert.False(update.ContainsKey("id"));
        Assert.False(update.ContainsKey("model"));
        Assert.Equal(ack.HasValue, ((JsonObject)update["delegation"]!).ContainsKey("ack_filler"));
    }

    [Fact]
    public void LegacyBooleanInitializerStillWritesExplicitFalse()
    {
        var settings = new CodexRealtimeSessionOptions("test") { DelegationAckFiller = false };
        Assert.False(CodexRealtimeProtocol.Session(settings)["delegation"]!["ack_filler"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public void LegacyFullSessionHasIdentityButUpdateDoesNot(string protocol)
    {
        var settings = new CodexRealtimeSessionOptions("test") { Protocol = protocol, SessionId = "session-test" };
        var full = protocol == "v1" ? CodexRealtimeProtocol.Session(settings) : CodexRealtimeGaProtocol.Session(settings);
        var update = (JsonObject)(protocol == "v1" ? CodexRealtimeProtocol.SessionUpdate(settings) : CodexRealtimeGaProtocol.SessionUpdate(settings))["session"]!;
        Assert.Equal("session-test", full["id"]!.GetValue<string>());
        Assert.True(full.ContainsKey("model"));
        Assert.False(update.ContainsKey("id"));
        Assert.False(update.ContainsKey("model"));
    }

    [Theory]
    [InlineData(CodexRealtimeOutputModality.Audio, "audio")]
    [InlineData(CodexRealtimeOutputModality.Text, "text")]
    public void V2ConversationalWireMatchesRust(CodexRealtimeOutputModality modality, string wire)
    {
        var session = CodexRealtimeGaProtocol.Session(new("test") { Protocol = "v2", OutputModality = modality });
        Assert.Equal("realtime", session["type"]!.GetValue<string>());
        Assert.Equal(wire, session["output_modalities"]![0]!.GetValue<string>());
        var input = session["audio"]!["input"]!;
        Assert.Equal("near_field", input["noise_reduction"]!["type"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"type":"server_vad","interrupt_response":true,"create_response":true,"silence_duration_ms":500}"""), input["turn_detection"]));
        Assert.Equal("background_agent", session["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("prompt", session["tools"]![0]!["parameters"]!["required"]![0]!.GetValue<string>());
        Assert.Equal("remain_silent", session["tools"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("auto", session["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void V2TranscriptionUpdateContainsOnlyInputConfiguration()
    {
        var session = CodexRealtimeGaProtocol.SessionUpdate(new("") { Protocol = "v2", Mode = CodexRealtimeSessionMode.Transcription })["session"];
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"type":"transcription","audio":{"input":{"format":{"type":"audio/pcm","rate":24000},"transcription":{"model":"gpt-4o-mini-transcribe"}}}}"""), session));
    }

    [Fact]
    public void UnsupportedCombinationsAndInstructionLimitsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => CodexRealtimeGaProtocol.Session(new("test") { Protocol = "v2", InitialItems = [new("user", "hello")] }));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("test") { Mode = CodexRealtimeSessionMode.Transcription }));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("test") { OutputModality = CodexRealtimeOutputModality.Text }));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("test") { SessionId = "bad\r\nheader" }));
        CodexRealtimeSessionOptions.ValidateModeInstructions(new string('a', 32768));
        Assert.Throws<ArgumentException>(() => CodexRealtimeSessionOptions.ValidateModeInstructions(new string('a', 32769)));
    }
}
