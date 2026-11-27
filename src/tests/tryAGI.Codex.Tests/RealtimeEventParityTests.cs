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
        var unknown = Event(protocol, """{"type":"future.event","future":42}""");
        Assert.False(unknown.RecognizedByRust);
        Assert.Null(unknown.Audio);
        Assert.Null(unknown.Transcript);
        Assert.Null(unknown.Delegation);
        Assert.Null(unknown.Response);
        Assert.Null(unknown.GetProviderErrorText());
        VerifyProjectionVariants(protocol, type);
    }

    // Every parser match arm is exercised with valid, omitted and wrong-typed fields.
    // The SDK deliberately keeps raw events and stricter PCM validation even when Rust
    // would discard an event or accept undecoded audio. Legacy textless finals retain
    // the existing delta-fallback projection without pretending to supply final text.
    private static void VerifyProjectionVariants(string protocol, string type)
    {
        CodexRealtimeEvent Frame(string fields = "{}")
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(fields)!.AsObject();
            body["type"] = type;
            body["future"] = 42;
            var json = body.ToJsonString();
            var frame = Event(protocol, json);
            Assert.Equal(json, frame.Payload.GetRawText());
            return frame;
        }

        var absent = Frame();
        switch (type)
        {
            case "session.updated":
            case "session.started":
                Assert.Equal(new CodexRealtimeSessionReceipt("s", "instructions", "model"),
                    Frame("""{"session":{"id":"s","instructions":"instructions","model":"model"}}""").Session);
                Assert.Equal(new CodexRealtimeSessionReceipt("s", null, null), Frame("""{"session":{"id":"s"}}""").Session);
                Assert.Equal(new CodexRealtimeSessionReceipt("s", null, null), Frame("""{"session":{"id":"s","instructions":5,"model":false}}""").Session);
                Assert.Null(absent.Session);
                Assert.Null(Frame("""{"session":{"id":1}}""").Session);
                Assert.Null(Frame("""{"session":[]}""").Session);
                break;
            case "conversation.output_audio.delta":
            case "response.output_audio.delta":
            case "response.audio.delta":
            case "output_audio.delta":
            {
                var field = protocol == "v3" ? "audio" : "delta";
                var valid = Frame("{\"" + field + "\":\"AQACAA==\",\"sample_rate\":16000,\"channels\":2,\"samples_per_channel\":1,\"item_id\":\"i\"}");
                var audio = Assert.IsType<CodexRealtimeAudio>(valid.Audio);
                Assert.Equal(new byte[] { 1, 0, 2, 0 }, audio.Pcm16.ToArray());
                Assert.Equal(protocol == "v3" ? 24000 : 16000, audio.SampleRate);
                Assert.Equal(protocol == "v3" ? 1 : 2, audio.Channels);
                Assert.Equal(protocol == "v2" ? "i" : null, audio.ItemId);
                Assert.Equal(protocol == "v3" ? (uint?)null : 1u, audio.SamplesPerChannel);
                var noMetadata = Frame("{\"" + field + "\":\"AQACAA==\"}");
                var badMetadata = Frame("{\"" + field + "\":\"AQACAA==\",\"sample_rate\":\"invalid\",\"channels\":null,\"num_channels\":2,\"samples_per_channel\":-1,\"item_id\":false}");
                if (protocol == "v1")
                {
                    Assert.Null(absent.Audio);
                    Assert.Null(noMetadata.Audio);
                    Assert.Null(badMetadata.Audio);
                }
                else
                {
                    Assert.Equal(24000, noMetadata.Audio!.SampleRate);
                    Assert.Equal(1, noMetadata.Audio.Channels);
                    Assert.Equal(1, badMetadata.Audio!.Channels);
                    Assert.Equal(24000, badMetadata.Audio.SampleRate);
                    Assert.Null(badMetadata.Audio.SamplesPerChannel);
                    Assert.Null(badMetadata.Audio.ItemId);
                    Assert.Throws<CodexProtocolException>(() => absent.Audio);
                }
                var invalid = Frame("{\"" + field + "\":false,\"sample_rate\":24000,\"channels\":1}");
                Assert.Throws<CodexProtocolException>(() => invalid.Audio);
                break;
            }
            case "conversation.input_transcript.delta":
            case "conversation.item.input_audio_transcription.delta":
            case "conversation.output_transcript.delta":
            case "response.output_text.delta":
            case "response.output_audio_transcript.delta":
                Assert.Equal(type.Contains("input", StringComparison.Ordinal) ? "user" : "assistant",
                    Frame("""{"delta":"words","item_id":"i"}""").Transcript!.Role);
                Assert.Equal("words", Frame("""{"delta":"words"}""").Transcript!.Text);
                Assert.False(Frame("""{"delta":"words"}""").Transcript!.Final);
                Assert.Null(absent.Transcript);
                Assert.Null(Frame("""{"delta":4}""").Transcript);
                break;
            case "conversation.input_transcript.turn_marked":
            case "conversation.item.input_audio_transcription.completed":
            case "response.output_audio_transcript.done":
            case "response.output_text.done":
            {
                var field = type == "response.output_text.done" ? "text" : "transcript";
                var final = Assert.IsType<CodexRealtimeTranscript>(Frame("{\"" + field + "\":\"final\"}").Transcript);
                Assert.Equal(type.Contains("input", StringComparison.Ordinal) ? "user" : "assistant", final.Role);
                Assert.Equal("final", final.Text);
                Assert.True(final.Final);
                Assert.True(final.ReplaceFinalText);
                Assert.False(absent.Transcript!.ReplaceFinalText);
                Assert.False(Frame("{\"" + field + "\":false}").Transcript!.ReplaceFinalText);
                Assert.True(Frame("{\"" + field + "\":\"\"}").Transcript!.ReplaceFinalText);
                // A wrong field must not masquerade as canonical transcript text.
                Assert.False(Frame(field == "text" ? """{"transcript":"wrong"}""" : """{"text":"wrong"}""").Transcript!.ReplaceFinalText);
                break;
            }
            case "input_transcript.added":
            case "output_transcript.added":
            {
                var value = Assert.IsType<CodexRealtimeTranscript>(Frame("""{"item":{"text":"words","id":"i"},"start_ms":1,"end_ms":2}""").Transcript);
                Assert.Equal(type == "input_transcript.added" ? "user" : "assistant", value.Role);
                Assert.Equal("words", value.Text);
                Assert.False(value.Final);
                Assert.Null(absent.Transcript);
                Assert.Null(Frame("""{"item":{"text":false}}""").Transcript);
                Assert.Null(Frame("""{"item":"invalid"}""").Transcript);
                Assert.Null(Frame("""{"item":{"text":"words","id":false},"start_ms":"bad"}""").Transcript!.InputFragmentId);
                break;
            }
            case "turn.done":
                Assert.Equal(new CodexRealtimeTurn("user", "words", "t"),
                    Frame("""{"turn":{"role":"user","transcript":"words","id":"t"}}""").Turn);
                Assert.Equal(new CodexRealtimeTurn("assistant", "words", null),
                    Frame("""{"turn":{"role":"assistant","transcript":"words"}}""").Turn);
                Assert.True(Frame("""{"turn":{"role":"user","transcript":"words"}}""").Transcript!.ReplaceFinalText);
                Assert.Null(absent.Turn);
                Assert.Null(absent.Transcript);
                Assert.Null(Frame("""{"turn":{"role":"developer","transcript":"words"}}""").Turn);
                Assert.Null(Frame("""{"turn":{"role":"user","transcript":false}}""").Transcript);
                break;
            case "conversation.item.added":
            case "conversation.item.created":
                Assert.Equal("i", Frame("""{"item":{"id":"i"}}""").ConversationItem!.Id);
                Assert.False(Frame("""{"item":{"id":"i"}}""").ConversationItem!.Done);
                Assert.Null(Frame("""{"item":{}}""").ConversationItem!.Id);
                Assert.Null(absent.ConversationItem);
                // Rust clones the item as Value, even if it is not an object.
                Assert.Equal(JsonValueKind.String, Frame("""{"item":"opaque"}""").ConversationItem!.Item.ValueKind);
                break;
            case "conversation.item.done":
                Assert.Equal("i", Frame("""{"item":{"id":"i"}}""").ConversationItem!.Id);
                Assert.True(Frame("""{"item":{"id":"i"}}""").ConversationItem!.Done);
                Assert.Null(absent.ConversationItem);
                Assert.Null(Frame("""{"item":{"id":false}}""").ConversationItem);
                Assert.Null(Frame("""{"item":"invalid"}""").ConversationItem);
                break;
            case "conversation.handoff.requested":
                Assert.Equal(new CodexRealtimeHandoff("h", "i", "words"),
                    Frame("""{"handoff_id":"h","item_id":"i","input_transcript":"words"}""").Handoff);
                Assert.Null(absent.Handoff);
                Assert.Null(Frame("""{"handoff_id":"h","item_id":"i"}""").Handoff);
                Assert.Null(Frame("""{"handoff_id":3,"item_id":"i","input_transcript":"words"}""").Handoff);
                break;
            case "delegation.created":
                Assert.Equal(new CodexRealtimeHandoff("h", "h", "words"),
                    Frame("""{"item":{"type":"delegation","target":"client","id":"h","content":[{"type":"input_text","text":"words"},{"type":"input_text","text":3},{"type":"other","text":"ignored"}]}}""").Handoff);
                Assert.Equal(new CodexRealtimeHandoff("h", "h", ""),
                    Frame("""{"item":{"type":"delegation","target":"client","id":"h","content":[]}}""").Handoff);
                Assert.Null(absent.Handoff);
                Assert.Null(Frame("""{"item":{"type":"delegation","target":"client","id":"h","content":false}}""").Handoff);
                Assert.Null(Frame("""{"item":{"type":"delegation","target":"other","id":"h","content":[]}}""").Handoff);
                break;
            case "input_audio_buffer.speech_started":
                Assert.Equal(new CodexRealtimeSpeechStarted("i", 12), Frame("""{"item_id":"i","audio_start_ms":12}""").SpeechStarted);
                Assert.Equal(new CodexRealtimeSpeechStarted(null, null), absent.SpeechStarted);
                Assert.Equal(new CodexRealtimeSpeechStarted(null, null), Frame("""{"item_id":false,"audio_start_ms":-1}""").SpeechStarted);
                break;
            case "response.created":
            case "response.cancelled":
            case "response.done":
                Assert.Equal(new CodexRealtimeResponseReceipt(type, "r", "completed"), Frame("""{"response":{"id":"r","status":"completed"},"response_id":"ignored"}""").Response);
                Assert.Equal(new CodexRealtimeResponseReceipt(type, "r", null), Frame("""{"response_id":"r","response":{"id":false}}""").Response);
                Assert.Equal(new CodexRealtimeResponseReceipt(type, null, null), absent.Response);
                Assert.Equal(new CodexRealtimeResponseReceipt(type, null, null), Frame("""{"response":false,"response_id":42}""").Response);
                break;
            case "error":
                Assert.Equal("top", Frame("""{"message":"top","error":{"message":"nested"}}""").GetProviderErrorText());
                Assert.Equal("nested", Frame("""{"error":{"message":"nested","code":"invalid"}}""").GetProviderErrorText());
                Assert.Equal("invalid", Frame("""{"error":{"code":"invalid"}}""").Error!.Code);
                Assert.Equal("false", Frame("""{"error":false}""").GetProviderErrorText());
                Assert.Equal("null", Frame("""{"error":null}""").GetProviderErrorText());
                Assert.Null(absent.GetProviderErrorText());
                Assert.Null(absent.Error!.Kind);
                Assert.Null(Frame("""{"message":4}""").GetProviderErrorText());
                break;
            default:
                throw new InvalidOperationException("Missing fixture for " + protocol + "/" + type);
        }
    }

    [Theory]
    [InlineData("v1", """{"type":"response.output_audio.delta","delta":"AQACAA=="}""")]
    [InlineData("v2", """{"type":"conversation.output_audio.delta","delta":"AQACAA==","sample_rate":24000,"channels":1}""")]
    [InlineData("v1", """{"type":"response.output_text.done","text":"unexpected"}""")]
    [InlineData("v2", """{"type":"conversation.input_transcript.delta","delta":"unexpected"}""")]
    [InlineData("v3", """{"type":"response.output_text.delta","delta":"unexpected"}""")]
    public void OtherAdapterAudioAndTranscriptEventsRemainRaw(string protocol, string json)
    {
        var value = Event(protocol, json);
        Assert.False(value.RecognizedByRust);
        Assert.Null(value.Audio);
        Assert.Null(value.Transcript);
        Assert.Equal(json, value.Payload.GetRawText());
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
