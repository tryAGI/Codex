using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeEventParityTests
{
    private static CodexRealtimeEvent Event(string protocol, string json)
    {
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.Clone();
        return new(payload.GetProperty("type").GetString()!, payload, 0) { Protocol = protocol };
    }

    // Each event name is taken from the three upstream Rust parser match arms.
    [Theory]
    [InlineData("v1", "session.updated")]
    [InlineData("v1", "conversation.output_audio.delta")]
    [InlineData("v1", "conversation.input_transcript.delta")]
    [InlineData("v1", "conversation.item.input_audio_transcription.delta")]
    [InlineData("v1", "conversation.input_transcript.turn_marked")]
    [InlineData("v1", "conversation.item.input_audio_transcription.completed")]
    [InlineData("v1", "conversation.output_transcript.delta")]
    [InlineData("v1", "response.output_text.delta")]
    [InlineData("v1", "response.output_audio_transcript.delta")]
    [InlineData("v1", "response.output_audio_transcript.done")]
    [InlineData("v1", "conversation.item.added")]
    [InlineData("v1", "conversation.item.done")]
    [InlineData("v1", "conversation.handoff.requested")]
    [InlineData("v1", "error")]
    [InlineData("v2", "session.updated")]
    [InlineData("v2", "response.output_audio.delta")]
    [InlineData("v2", "response.audio.delta")]
    [InlineData("v2", "conversation.item.input_audio_transcription.delta")]
    [InlineData("v2", "conversation.item.input_audio_transcription.completed")]
    [InlineData("v2", "response.output_text.delta")]
    [InlineData("v2", "response.output_audio_transcript.delta")]
    [InlineData("v2", "response.output_text.done")]
    [InlineData("v2", "response.output_audio_transcript.done")]
    [InlineData("v2", "input_audio_buffer.speech_started")]
    [InlineData("v2", "conversation.item.added")]
    [InlineData("v2", "conversation.item.created")]
    [InlineData("v2", "conversation.item.done")]
    [InlineData("v2", "response.created")]
    [InlineData("v2", "response.cancelled")]
    [InlineData("v2", "response.done")]
    [InlineData("v2", "error")]
    [InlineData("v3", "session.started")]
    [InlineData("v3", "session.updated")]
    [InlineData("v3", "output_audio.delta")]
    [InlineData("v3", "input_transcript.added")]
    [InlineData("v3", "output_transcript.added")]
    [InlineData("v3", "turn.done")]
    [InlineData("v3", "delegation.created")]
    [InlineData("v3", "error")]
    public void EveryPinnedParserVariantIsRecognizedAndRawJsonPreserved(string protocol, string type)
    {
        var json = JsonSerializer.Serialize(new { type, unknown_future_field = 42 });
        var receipt = Event(protocol, json);
        Assert.True(receipt.RecognizedByRust);
        Assert.Equal(json, receipt.Payload.GetRawText());
        Assert.False(Event(protocol, """{"type":"future.event"}""").RecognizedByRust);
    }

    [Theory]
    [InlineData("v1", """{"type":"session.updated","session":{"id":"s"}}""")]
    [InlineData("v2", """{"type":"session.updated","session":{"id":"s"}}""")]
    [InlineData("v3", """{"type":"session.started","session":{"id":"s"}}""")]
    public void SessionIdentityIsRequiredAndOptionalInstructionsAreNotInvented(string protocol, string json)
    {
        var receipt = Event(protocol, json);
        Assert.Equal(new CodexRealtimeSessionReceipt("s", null, null), receipt.Session);
        Assert.Null(Event(protocol, """{"type":"session.updated","session":{"id":42}}""").Session);
        Assert.Equal("prompt", Event(protocol, """{"type":"session.updated","session":{"id":"s","instructions":"prompt","model":"m"}}""").Session?.Instructions);
    }

    [Theory]
    [InlineData("v1", """{"type":"conversation.output_audio.delta","data":"AQACAA==","sample_rate":16000,"num_channels":2,"samples_per_channel":1,"item_id":"ignored"}""", 16000, 2, null, 1)]
    [InlineData("v2", """{"type":"response.audio.delta","delta":"AQACAA==","sample_rate":48000,"channels":2,"samples_per_channel":1,"item_id":"i"}""", 48000, 2, "i", 1)]
    [InlineData("v2", """{"type":"response.output_audio.delta","delta":"AQACAA=="}""", 24000, 1, null, null)]
    [InlineData("v3", """{"type":"output_audio.delta","audio":"AQACAA==","sample_rate":48000,"channels":2,"samples_per_channel":1,"item_id":"ignored"}""", 24000, 1, null, null)]
    public void AudioMetadataMatchesAdapter(string protocol, string json, int rate, int channels, string? itemId, int? samples)
    {
        var audio = Assert.IsType<CodexRealtimeAudio>(Event(protocol, json).Audio);
        Assert.Equal(rate, audio.SampleRate);
        Assert.Equal(channels, audio.Channels);
        Assert.Equal(itemId, audio.ItemId);
        Assert.Equal(samples is null ? (uint?)null : (uint)samples.Value, audio.SamplesPerChannel);
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, audio.Pcm16.ToArray());
    }

    [Fact]
    public void V1RequiresMetadataAndAllAdaptersRetainBoundedAudioValidation()
    {
        Assert.Null(Event("v1", """{"type":"conversation.output_audio.delta","delta":"AQACAA=="}""").Audio);
        Assert.Throws<CodexProtocolException>(() => Event("v1", """{"type":"conversation.output_audio.delta","delta":"not-base64","sample_rate":24000,"channels":1}""").Audio);
        Assert.Throws<CodexProtocolException>(() => Event("v2", """{"type":"response.output_audio.delta","delta":"not-base64"}""").Audio);
        Assert.Throws<CodexProtocolException>(() => Event("v3", """{"type":"output_audio.delta","audio":"not-base64"}""").Audio);
    }

    [Theory]
    [InlineData("v1", """{"type":"conversation.input_transcript.delta","delta":"hello"}""", "user", false)]
    [InlineData("v1", """{"type":"conversation.item.input_audio_transcription.delta","delta":"hello"}""", "user", false)]
    [InlineData("v1", """{"type":"conversation.input_transcript.turn_marked","transcript":"hello"}""", "user", true)]
    [InlineData("v1", """{"type":"conversation.output_transcript.delta","delta":"hello"}""", "assistant", false)]
    [InlineData("v1", """{"type":"response.output_text.delta","delta":"hello"}""", "assistant", false)]
    [InlineData("v1", """{"type":"response.output_audio_transcript.delta","delta":"hello"}""", "assistant", false)]
    [InlineData("v1", """{"type":"response.output_audio_transcript.done","transcript":"hello"}""", "assistant", true)]
    [InlineData("v2", """{"type":"conversation.item.input_audio_transcription.delta","delta":"hello"}""", "user", false)]
    [InlineData("v2", """{"type":"conversation.item.input_audio_transcription.completed","transcript":"hello"}""", "user", true)]
    [InlineData("v2", """{"type":"response.output_text.delta","delta":"hello"}""", "assistant", false)]
    [InlineData("v2", """{"type":"response.output_audio_transcript.delta","delta":"hello"}""", "assistant", false)]
    [InlineData("v2", """{"type":"response.output_text.done","text":"hello"}""", "assistant", true)]
    [InlineData("v2", """{"type":"response.output_audio_transcript.done","transcript":"hello"}""", "assistant", true)]
    [InlineData("v3", """{"type":"input_transcript.added","item":{"text":"hello"}}""", "user", false)]
    [InlineData("v3", """{"type":"output_transcript.added","item":{"text":"hello"}}""", "assistant", false)]
    [InlineData("v3", """{"type":"turn.done","turn":{"role":"assistant","transcript":"hello"}}""", "assistant", true)]
    public void TranscriptAliasesAreProjected(string protocol, string json, string role, bool final)
    {
        var value = Assert.IsType<CodexRealtimeTranscript>(Event(protocol, json).Transcript);
        Assert.Equal(role, value.Role);
        Assert.Equal("hello", value.Text);
        Assert.Equal(final, value.Final);
    }

    [Fact]
    public void HandoffNoopAndUnknownFunctionAreDistinct()
    {
        var legacy = Event("v1", """{"type":"conversation.handoff.requested","handoff_id":"h","item_id":"i","input_transcript":"query"}""");
        Assert.Equal(new CodexRealtimeHandoff("h", "i", "query"), legacy.Handoff);
        var ga = Event("v2", """{"type":"conversation.item.done","item":{"type":"function_call","id":"i","call_id":"h","name":"background_agent","arguments":"{\"prompt\":\" query \"}"}}""");
        Assert.Equal(new CodexRealtimeHandoff("h", "i", "query"), ga.Handoff);
        Assert.Equal(new CodexDelegation("h", "query"), ga.Delegation);
        var noop = Event("v2", """{"type":"conversation.item.done","item":{"type":"function_call","id":"i","name":"remain_silent"}}""");
        Assert.Equal(new CodexRealtimeNoop("i", "i"), noop.Noop);
        Assert.Null(noop.Delegation);
        var other = Event("v2", """{"type":"conversation.item.done","item":{"type":"function_call","id":"i","name":"anything_else"}}""");
        Assert.Null(other.Delegation);
        Assert.NotNull(other.ConversationItem);
        var frameless = Event("v3", """{"type":"delegation.created","item":{"type":"delegation","target":"client","id":"h","content":[{"type":"input_text","text":"query"},{"type":"output_text","text":"ignored"}]}}""");
        Assert.Equal(new CodexRealtimeHandoff("h", "h", "query"), frameless.Handoff);
        Assert.Null(Event("v3", """{"type":"delegation.created","item":{"type":"delegation","target":"server","id":"h","content":[]}}""").Handoff);
        Assert.Null(Event("v1", """{"type":"conversation.handoff.requested","handoff_id":"h"}""").Handoff);
    }

    [Theory]
    [InlineData("response.created")]
    [InlineData("response.cancelled")]
    [InlineData("response.done")]
    public void ResponseLifecycleIdentityIsOptionalAndNestedIdWins(string type)
    {
        var value = Event("v2", JsonSerializer.Serialize(new { type, response_id = "top", response = new { id = "nested", status = "cancelled" } }));
        Assert.Equal(new CodexRealtimeResponseReceipt(type, "nested", "cancelled"), value.Response);
        Assert.Null(Event("v2", JsonSerializer.Serialize(new { type })).Response?.ResponseId);
    }

    [Fact]
    public void ConversationSpeechTurnAndErrorReceiptsPreserveUnknownFields()
    {
        var added = Event("v1", """{"type":"conversation.item.added","item":{"id":"i","future":7}}""").ConversationItem!;
        Assert.False(added.Done);
        Assert.Equal(7, added.Item.GetProperty("future").GetInt32());
        Assert.True(Event("v2", """{"type":"conversation.item.done","item":{"id":"i"}}""").ConversationItem!.Done);
        Assert.Null(Event("v2", """{"type":"conversation.item.done","item":{}}""").ConversationItem);
        Assert.Equal(new CodexRealtimeSpeechStarted(null, null), Event("v2", """{"type":"input_audio_buffer.speech_started"}""").SpeechStarted);
        Assert.Equal(new CodexRealtimeTurn("user", "hello", "t"), Event("v3", """{"type":"turn.done","turn":{"id":"t","role":"user","transcript":"hello"}}""").Turn);
        Assert.Null(Event("v3", """{"type":"turn.done","turn":{"role":"invalid","transcript":"hello"}}""").Turn);
        var error = Event("v2", """{"type":"error","error":{"event_id":"request","code":"error_code","message":"private"}}""");
        Assert.Equal("error_code", error.Error?.Code);
        Assert.Equal("private", error.Payload.GetProperty("error").GetProperty("message").GetString());
    }
}
