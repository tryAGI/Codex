using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>A conversation item seeded into a new realtime session.</summary>
/// <param name="Role"><c>user</c> or <c>assistant</c>.</param>
/// <param name="Text">Item text.</param>
public sealed record CodexRealtimeInitialItem(string Role, string Text);

/// <summary>Realtime V2 session mode. V1 and V3 are conversational.</summary>
public enum CodexRealtimeSessionMode
{
    /// <summary>Bidirectional conversation.</summary>
    Conversational,
    /// <summary>Input audio transcription without generated responses.</summary>
    Transcription,
}

/// <summary>Requested realtime output modality. Text output is supported by V2.</summary>
public enum CodexRealtimeOutputModality
{
    /// <summary>Audio output.</summary>
    Audio,
    /// <summary>Text output.</summary>
    Text,
}

/// <summary>Settings for a Codex realtime session.</summary>
/// <param name="Instructions">System instructions for the voice model.</param>
public sealed record CodexRealtimeSessionOptions(string Instructions)
{
    /// <summary><c>v3</c> (Codex frameless bidi, default), <c>v2</c> (GA realtime) or <c>v1</c> (existing-call sideband).</summary>
    public string Protocol { get; init; } = "v3";

    private string? model;
    private string? voice;

    /// <summary>Realtime model. Unset or null selects the Rust default for <see cref="Protocol"/>.</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public string Model { get => model ?? CodexProtocolCatalog.GetDefaultRealtimeModel(Protocol); init => model = value; }

    /// <summary>Built-in voice. Unset or null selects the destination default; the getter preserves the ChatGPT V3 call default.</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public string Voice { get => ResolveVoice(chatGptCall: Protocol != "v2"); init => voice = value; }

    /// <summary>Resolves the explicit voice or the default for the actual transport destination.</summary>
    public string ResolveVoice(bool chatGptCall) => voice ?? CodexProtocolCatalog.GetDefaultVoice(Protocol, chatGptCall);

    private bool? delegationAckFiller;

    /// <summary>Let the voice model speak a short acknowledgement while a client delegation runs. Unset reads as false.</summary>
    public bool DelegationAckFiller { get => delegationAckFiller ?? false; init => delegationAckFiller = value; }

    /// <summary>Optional V3 acknowledgement override. Null omits the field, false disables it explicitly.</summary>
    public bool? DelegationAckFillerSetting { get => delegationAckFiller; init => delegationAckFiller = value; }

    /// <summary>Conversational (default) or V2 transcription-only session.</summary>
    public CodexRealtimeSessionMode Mode { get; init; }

    /// <summary>Audio (default) or V2 text output.</summary>
    public CodexRealtimeOutputModality OutputModality { get; init; }

    /// <summary>Optional client-generated session identity, independent of provider session IDs.</summary>
    public string? SessionId { get; init; }

    /// <summary>Optional voice session identity for the transport header.</summary>
    public string? VoiceSessionId { get; init; }

    /// <summary>Validates the Rust 8192 estimated-token limit for a separate start or end mode instruction.</summary>
    public static void ValidateModeInstructions(string? instructions)
    {
        if (instructions is not null && CodexRealtimeStartupContext.ApproximateTokens(instructions) > 8192)
            throw new ArgumentException("Realtime mode instructions exceed 8192 estimated tokens.", nameof(instructions));
    }

    /// <summary>V3 only: choice questions the provider answers when the named session events occur.</summary>
    public IReadOnlyList<CodexLiveDecisionTrigger> LiveDecisionTriggers { get; init; } = [];

    /// <summary>Explicit native delegation diagnostic; empty preserves the subscribed client-delegation route.</summary>
    public IReadOnlyList<CodexLiveChoiceQuestion> LiveDecisionDelegationQuestions { get; init; } = [];

    /// <summary>V3 only: a custom voice ID (<c>voice_…</c>) created with <see cref="CodexLiveFeatureClient.CreateVoiceAsync"/>.</summary>
    public string? LiveCustomVoiceId { get; init; }

    /// <summary>V3 only: description sent with <see cref="LiveCustomVoiceId"/>, at most 4000 characters.</summary>
    public string? LiveVoiceDescription { get; init; }

    /// <summary>Additional GA session fields, including tools, reasoning, VAD, transcription and truncation. Identity fields use dedicated properties.</summary>
    public JsonElement? ProviderSession { get; init; }

    /// <summary>Conversation items seeded when the session starts.</summary>
    public IReadOnlyList<CodexRealtimeInitialItem> InitialItems { get; init; } = [];
}

/// <summary>A realtime call created over WebRTC.</summary>
/// <param name="CallId">Call ID used to connect the sideband WebSocket.</param>
/// <param name="AnswerSdp">SDP answer to apply to the caller's peer connection.</param>
public sealed record CodexRealtimeCall(string CallId, string AnswerSdp);

/// <summary>A task the voice model handed to the client.</summary>
/// <param name="Id">Delegation ID. Deduplicate durably before acting on it.</param>
/// <param name="Transcript">What the user asked for, as the model summarized it.</param>
public sealed record CodexDelegation(string Id, string Transcript);

/// <summary>One server event from a realtime connection, with typed views of the common payloads.</summary>
/// <param name="Type">Event type.</param>
/// <param name="Payload">Raw event JSON.</param>
/// <param name="ReceivedAfterConnectMs">Milliseconds between connecting and receiving this event.</param>
public sealed record CodexRealtimeEvent(string Type, JsonElement Payload, double ReceivedAfterConnectMs)
{
    /// <summary>The wire adapter that produced this event, or null for a manually constructed event.</summary>
    public string? Protocol { get; init; }

    /// <summary>Generation captured at receipt for asynchronous feedback. Never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CodexRealtimeConnection.FeedbackAdmission? Admission { get; init; }

    /// <summary>A superseded response receipt remains raw, but must not release feedback or playback.</summary>
    public bool StaleForFeedback { get; init; }

    /// <summary>Whether the pinned Rust adapter recognizes this event type (independent of payload validity).</summary>
    public bool RecognizedByRust => Protocol is { } protocol
        ? CodexRealtimeEventProjections.Recognized(protocol, Type)
        : new[] { "v1", "v2", "v3" }.Any(p => CodexRealtimeEventProjections.Recognized(p, Type));

    /// <summary>Session identity and optional configuration receipt.</summary>
    public CodexRealtimeSessionReceipt? Session => CodexRealtimeEventProjections.Session(Payload);
    /// <summary>Explicit background-agent handoff, including its conversation item ID.</summary>
    public CodexRealtimeHandoff? Handoff => CodexRealtimeEventProjections.Handoff(Payload, Protocol);
    /// <summary>Explicit remain-silent function, separate from delegation.</summary>
    public CodexRealtimeNoop? Noop => CodexRealtimeEventProjections.Noop(Payload, Protocol);
    /// <summary>Conversation item added or done, with original item JSON.</summary>
    public CodexRealtimeConversationItem? ConversationItem => CodexRealtimeEventProjections.ConversationItem(Payload);
    /// <summary>Speech-started event, if reported.</summary>
    public CodexRealtimeSpeechStarted? SpeechStarted => CodexRealtimeEventProjections.SpeechStarted(Payload);
    /// <summary>Response creation or terminal receipt, if reported.</summary>
    public CodexRealtimeResponseReceipt? Response => CodexRealtimeEventProjections.Response(Payload);
    /// <summary>Completed frameless turn, if reported.</summary>
    public CodexRealtimeTurn? Turn => CodexRealtimeEventProjections.Turn(Payload);

    /// <summary>The delegation carried by this event, if any. Arbitrary function calls are not delegations.</summary>
    public CodexDelegation? Delegation => Handoff is { } handoff
        ? new(handoff.HandoffId, handoff.InputTranscript)
        : Protocol is null or "v2" ? CodexRealtimeGaProtocol.ReadDelegation(Payload) : null;

    /// <summary>The transcript fragment carried by this event, if any.</summary>
    public CodexRealtimeTranscript? Transcript => Protocol == "v3" ? CodexRealtimeProtocol.ReadTranscript(Payload)
        : Protocol is "v1" or "v2" ? CodexRealtimeGaProtocol.ReadTranscript(Payload)
        : CodexRealtimeProtocol.ReadTranscript(Payload) ?? CodexRealtimeGaProtocol.ReadTranscript(Payload);

    /// <summary>The provider error carried by this event, if any.</summary>
    public CodexRealtimeError? Error => CodexRealtimeProtocol.ReadError(Payload);

    /// <summary>The output audio carried by this event, if any.</summary>
    public CodexRealtimeAudio? Audio => Protocol == "v3" ? CodexRealtimeProtocol.ReadAudio(Payload)
        : Protocol is "v1" or "v2" ? CodexRealtimeGaProtocol.ReadAudio(Payload)
        : CodexRealtimeProtocol.ReadAudio(Payload) ?? CodexRealtimeGaProtocol.ReadAudio(Payload);
}

/// <summary>A user or assistant transcript fragment.</summary>
/// <param name="Role"><c>user</c> or <c>assistant</c>.</param>
/// <param name="Text">Transcript text: a delta, or the whole utterance when <paramref name="Final"/> is set.</param>
/// <param name="Final">The utterance is complete.</param>
public sealed record CodexRealtimeTranscript(string Role, string Text, bool Final)
{
    /// <summary>The final text replaces the accumulated deltas instead of being appended to them.</summary>
    public bool ReplaceFinalText { get; init; }

    /// <summary>Provider ID of the user input fragment this transcript belongs to.</summary>
    public string? InputFragmentId { get; init; }

    /// <summary>Start of the transcribed input, in milliseconds of session audio.</summary>
    public double? InputStartMs { get; init; }

    /// <summary>End of the transcribed input, in milliseconds of session audio.</summary>
    public double? InputEndMs { get; init; }
}

/// <summary>A chunk of model output audio.</summary>
/// <param name="Pcm16">Little-endian 16-bit PCM samples.</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="ItemId">Conversation item the audio belongs to, if known.</param>
public sealed record CodexRealtimeAudio(ReadOnlyMemory<byte> Pcm16, int SampleRate = 24000, int Channels = 1, string? ItemId = null)
{
    /// <summary>Provider-reported sample count per channel, when supplied by the legacy adapter.</summary>
    public uint? SamplesPerChannel { get; init; }
}

/// <summary>An error event from the realtime provider. Carries identifiers only, never request content.</summary>
/// <param name="EventId">The client event that caused the error, if reported.</param>
/// <param name="Code">Provider error code.</param>
/// <param name="Kind">Provider error type.</param>
/// <param name="Parameter">The offending parameter, if reported.</param>
public sealed record CodexRealtimeError(string? EventId, string? Code, string? Kind, string? Parameter);

/// <summary>Frameless bidi (Codex realtime V3) wire shapes, pinned in UPSTREAM.md.</summary>
internal static class CodexRealtimeProtocol
{
    /// <summary>V3 update intentionally omits model, matching upstream methods_frameless_bidi.
    /// Sending this command does not prove acceptance: retain session.updated/error receipts.</summary>
    public static JsonObject SessionUpdate(CodexRealtimeSessionOptions options, bool chatGptCall = true)
    {
        var session = Session(options, chatGptCall); session.Remove("model"); session.Remove("id");
        session.Remove("triggers"); // Startup configuration; sleep/wake updates must not recreate triggers.
        if (options.LiveDecisionDelegationQuestions.Count > 0) session.Remove("delegation"); // Delegation type is fixed at creation.
        if (options.LiveCustomVoiceId is not null) session.Remove("audio"); // Never reset a saved custom voice on sleep/wake.
        return new JsonObject { ["type"] = "session.update", ["session"] = session };
    }
    public static JsonObject AudioAppend(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length is <= 0 or > 262144 || pcm16.Length % 2 != 0)
            throw new ArgumentException("Audio must contain bounded PCM16 samples at 24kHz mono.", nameof(pcm16));
        return new JsonObject { ["type"] = "input_audio.append", ["audio"] = Convert.ToBase64String(pcm16) };
    }
    public static CodexRealtimeAudio? ReadAudio(JsonElement payload)
    {
        if (Text(payload, "type") != "output_audio.delta") return null;
        if (Text(payload, "audio") is not { Length: > 0 and <= 349528 } encoded)
            throw new CodexProtocolException("V3 output audio missing or exceeded bounded PCM frame size.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new CodexProtocolException("V3 output audio is invalid base64."); }
        if (bytes.Length is <= 0 or > 262144 || bytes.Length % 2 != 0)
            throw new CodexProtocolException("V3 output audio is invalid PCM16.");
        // Pinned frameless parser supplies 24kHz mono and no item/sample-boundary ID.
        return new(bytes);
    }
    public static CodexRealtimeTranscript? ReadTranscript(JsonElement payload)
    {
        var type = Text(payload, "type");
        if (type is "input_transcript.added" or "output_transcript.added" &&
            payload.TryGetProperty("item", out var item) && Text(item, "text") is { } delta)
            return new(type == "input_transcript.added" ? "user" : "assistant", delta, false)
            {
                InputFragmentId = type == "input_transcript.added" ? Text(item, "id") : null,
                InputStartMs = type == "input_transcript.added" ? Milliseconds(payload, "start_ms") : null,
                InputEndMs = type == "input_transcript.added" ? Milliseconds(payload, "end_ms") : null,
            };
        if (type == "turn.done" && payload.TryGetProperty("turn", out var turn) &&
            Text(turn, "role") is "user" or "assistant" && Text(turn, "transcript") is { } text)
            return new((Text(turn, "role") ?? throw new InvalidDataException("Required data was null.")), text, true) { ReplaceFinalText = true };
        return null;
    }
    public static CodexRealtimeError? ReadError(JsonElement payload)
    {
        if (Text(payload, "type") != "error") return null;
        var error = payload.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : payload;
        static string? Safe(JsonElement item, string key) => Text(item, key) is { Length: > 0 and <= 128 } text &&
            text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? text : null;
        return new(Safe(error, "event_id") ?? Safe(payload, "event_id"), Safe(error, "code"), Safe(error, "type"), Safe(error, "param"));
    }
    internal static void ValidateSessionOptions(CodexRealtimeSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Instructions);
        if (options.Protocol is not ("v1" or "v2" or "v3")) throw new ArgumentException("Unsupported realtime protocol.", nameof(options));
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.OutputModality)) throw new ArgumentException("Unsupported session mode or modality.", nameof(options));
        if (options.Protocol != "v2" && options.Mode != CodexRealtimeSessionMode.Conversational)
            throw new ArgumentException("Transcription sessions require V2.", nameof(options));
        if (options.Protocol != "v2" && options.OutputModality != CodexRealtimeOutputModality.Audio)
            throw new ArgumentException("Text output requires V2.", nameof(options));
        CodexRealtimeStartupContext.ValidateInitialItems(options.InitialItems);
        if (options.Protocol != "v3" && options.InitialItems.Count != 0)
            throw new ArgumentException("Initial items require V3.", nameof(options));
        foreach (var id in new[] { options.SessionId, options.VoiceSessionId })
            if (id is not null && (id.Length is < 1 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))))
                throw new ArgumentException("Invalid realtime session identity.", nameof(options));
        if (options.Protocol != "v3" && (options.LiveDecisionTriggers.Count != 0 || options.LiveDecisionDelegationQuestions.Count != 0 || options.LiveCustomVoiceId is not null || options.LiveVoiceDescription is not null))
            throw new ArgumentException("Live features require explicit V3 capability verification.", nameof(options));
    }

    public static JsonObject Session(CodexRealtimeSessionOptions options, bool chatGptCall = true)
    {
        ValidateSessionOptions(options);
        // Rust v0.172 keeps separate voice catalogs; select the destination before validating.
        var voice = options.ResolveVoice(chatGptCall);
        var voices = options.Protocol == "v1" ? CodexProtocolCatalog.V1Voices : CodexProtocolCatalog.Voices;
        if (!voices.Contains(voice, StringComparer.Ordinal))
            throw new ArgumentException("Voice is not supported by the selected Codex realtime protocol.", nameof(options));
        if (options.LiveCustomVoiceId is { } voiceId && (!voiceId.StartsWith("voice_", StringComparison.Ordinal) || !CodexLiveFeatures.Identifier(voiceId)) ||
            options.LiveVoiceDescription is { } description && (string.IsNullOrWhiteSpace(description) || description.Length > 4000 || options.LiveCustomVoiceId is null))
            throw new ArgumentException("Use a saved custom voice ID and bounded original description.");
        if (options.Protocol == "v1")
        {
            var legacy = new JsonObject { ["type"] = "quicksilver", ["model"] = options.Model, ["instructions"] = options.Instructions,
                ["audio"] = new JsonObject { ["input"] = new JsonObject { ["format"] = new JsonObject
                { ["type"] = "audio/pcm", ["rate"] = 24000 } }, ["output"] = new JsonObject { ["voice"] = voice } } };
            if (options.SessionId is { } id) legacy["id"] = id;
            return legacy;
        }
        if (options.Protocol != "v3") throw new ArgumentException("AVAS calls support V1/V3; V2 requires a different route.", nameof(options));
        var session = new JsonObject
        {
            ["model"] = options.Model, ["instructions"] = options.Instructions,
            ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = voice } },
            ["delegation"] = new JsonObject { ["type"] = "client" },
        };
        if (options.DelegationAckFillerSetting is { } ackFiller)
            ((JsonObject)session["delegation"]!)["ack_filler"] = ackFiller;
        if (options.LiveDecisionTriggers.Count > 0) session["triggers"] = CodexLiveFeatures.Triggers(options.LiveDecisionTriggers);
        if (options.LiveDecisionDelegationQuestions.Count > 0)
            session["delegation"] = new JsonObject { ["type"] = "decisions", ["decisions"] = new JsonObject
            { ["model"] = "gpt-6-luna", ["questions"] = CodexLiveFeatures.Questions(options.LiveDecisionDelegationQuestions) } };
        if (options.LiveCustomVoiceId is { } customVoice)
        {
            var audio = session["audio"] as JsonObject ?? throw new InvalidDataException("Missing Live audio configuration.");
            var output = audio["output"] as JsonObject ?? throw new InvalidDataException("Missing Live output configuration.");
            output["voice"] = new JsonObject { ["id"] = customVoice };
            if (options.LiveVoiceDescription is { } voiceDescription)
                session["instructions"] = options.Instructions + "\nVoice description: " + voiceDescription;
        }
        if (options.InitialItems.Count > 0)
        {
            var items = new JsonArray();
            foreach (var item in options.InitialItems)
            {
                if (item.Role is not ("user" or "assistant" or "developer")) throw new ArgumentException("Unsupported realtime message role.");
                items.Add((JsonNode)new JsonObject { ["type"] = "message", ["role"] = item.Role,
                    ["content"] = new JsonArray(new JsonObject { ["type"] = item.Role == "assistant" ? "output_text" : "input_text", ["text"] = item.Text }) });
            }
            session["initial_items"] = items;
        }
        return session;
    }

    internal static Uri SidebandUri(Uri configured, string callId, string protocol)
    {
        if (CallId(callId) != callId) throw new ArgumentException("Invalid call ID.", nameof(callId));
        return RealtimeSocketUri(configured, protocol, null, callId);
    }

    internal static Uri RealtimeSocketUri(Uri backend, string protocol, string? model, string? callId = null)
    {
        if (protocol is not ("v1" or "v2" or "v3")) throw new ArgumentException("Unsupported realtime protocol.", nameof(protocol));
        if (backend.Scheme is not ("https" or "wss")) throw new ArgumentException("Realtime requires a secure transport.", nameof(backend));
        var path = backend.AbsolutePath.TrimEnd('/');
        var suffix = protocol == "v3" ? "/live" : "/realtime";
        if (path.Length == 0) path = "/v1" + suffix;
        else if (path.EndsWith("/v1", StringComparison.Ordinal)) path += suffix;
        else if (protocol == "v3" && path.EndsWith("/realtime", StringComparison.Ordinal)) path = path[..^9] + suffix;
        // The existing SDK sideband base was a V3 /live URL, not a provider API base.
        else if (callId is not null && protocol != "v3" && path.EndsWith("/live", StringComparison.Ordinal)) path = path[..^5] + suffix;
        var query = new List<string>();
        if (backend.Query.Length > 1) query.Add(backend.Query[1..]);
        if (protocol == "v1") query.Add("intent=quicksilver");
        if (model is not null) query.Add("model=" + Uri.EscapeDataString(model));
        if (callId is not null)
        {
            if (protocol == "v3") path += "/" + Uri.EscapeDataString(callId);
            else query.Add("call_id=" + Uri.EscapeDataString(callId));
        }
        return new UriBuilder(backend) { Scheme = "wss", Port = backend.IsDefaultPort ? -1 : backend.Port, Path = path, Query = string.Join("&", query) }.Uri;
    }

    public static CodexDelegation? ReadDelegation(JsonElement payload)
    {
        if (Text(payload, "type") != "delegation.created" || !payload.TryGetProperty("item", out var item) ||
            Text(item, "type") != "delegation" || Text(item, "target") != "client" ||
            Text(item, "id") is not { Length: > 0 } id || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        return new(id, string.Concat(content.EnumerateArray().Where(x => Text(x, "type") == "input_text").Select(x => Text(x, "text") ?? "")));
    }

    public static IEnumerable<JsonObject> Context(string text, string? delegationId = null, string? channel = "speakable")
    {
        if (channel is not (null or "speakable" or "commentary")) throw new ArgumentException("Unknown context channel.", nameof(channel));
        if (delegationId is { Length: 0 }) throw new ArgumentException("A delegation ID cannot be empty.", nameof(delegationId));
        foreach (var chunk in Utf8Chunks(text))
        {
            var message = new JsonObject { ["type"] = delegationId is null ? "session.context.append" : "delegation.context.append",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = chunk }) };
            if (delegationId is not null) message["delegation_item_id"] = delegationId;
            if (channel is not null) message["channel"] = channel;
            yield return message;
        }
    }

    internal static IEnumerable<string> Utf8Chunks(string text)
    {
        var chunk = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 500) { yield return chunk.ToString(); chunk.Clear(); bytes = 0; }
            chunk.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        if (chunk.Length > 0 || text.Length == 0) yield return chunk.ToString();
    }

    public static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static double? Milliseconds(JsonElement value, string key) => value.TryGetProperty(key, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0
            ? number : null;

    internal static string CallId(string location)
    {
        var id = location.Split('?')[0].Split('/').LastOrDefault(s => s.StartsWith("rtc_", StringComparison.Ordinal) && s.Length > 4 || Guid.TryParseExact(s, "D", out _));
        return id is not null && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? id : throw new CodexProtocolException("Call response has no valid call ID.");
    }
}
