using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed record CodexRealtimeInitialItem(string Role, string Text);
public sealed record CodexRealtimeSessionOptions(string Instructions)
{
    public string Protocol { get; init; } = "v3";
    public string Model { get; init; } = CodexProtocolCatalog.DefaultRealtimeModel;
    public string Voice { get; init; } = CodexProtocolCatalog.DefaultVoice;
    public bool DelegationAckFiller { get; init; }
    public IReadOnlyList<CodexLiveDecisionTrigger> LiveDecisionTriggers { get; init; } = [];
    /// <summary>Explicit native delegation diagnostic; empty preserves the subscribed client-delegation route.</summary>
    public IReadOnlyList<CodexLiveChoiceQuestion> LiveDecisionDelegationQuestions { get; init; } = [];
    public string? LiveCustomVoiceId { get; init; }
    public string? LiveVoiceDescription { get; init; }
    /// <summary>Additional GA session fields, including tools, reasoning, VAD, transcription and truncation. Identity fields use dedicated properties.</summary>
    public JsonElement? ProviderSession { get; init; }
    public IReadOnlyList<CodexRealtimeInitialItem> InitialItems { get; init; } = [];
}

public sealed record CodexRealtimeCall(string CallId, string AnswerSdp);
public sealed record CodexDelegation(string Id, string Transcript);
public sealed record CodexRealtimeEvent(string Type, JsonElement Payload, double ReceivedAfterConnectMs)
{
    public CodexDelegation? Delegation => CodexRealtimeProtocol.ReadDelegation(Payload) ?? CodexRealtimeGaProtocol.ReadDelegation(Payload);
    public CodexRealtimeTranscript? Transcript => CodexRealtimeProtocol.ReadTranscript(Payload) ?? CodexRealtimeGaProtocol.ReadTranscript(Payload);
    public CodexRealtimeError? Error => CodexRealtimeProtocol.ReadError(Payload);
    public CodexRealtimeAudio? Audio => CodexRealtimeProtocol.ReadAudio(Payload) ?? CodexRealtimeGaProtocol.ReadAudio(Payload);
}
public sealed record CodexRealtimeTranscript(string Role, string Text, bool Final)
{
    public bool ReplaceFinalText { get; init; }
    public string? InputFragmentId { get; init; }
    public double? InputStartMs { get; init; }
    public double? InputEndMs { get; init; }
}
public sealed record CodexRealtimeAudio(ReadOnlyMemory<byte> Pcm16, int SampleRate = 24000, int Channels = 1, string? ItemId = null);
public sealed record CodexRealtimeError(string? EventId, string? Code, string? Kind, string? Parameter);

/// <summary>Frameless bidi (Codex realtime V3) wire shapes, pinned in UPSTREAM.md.</summary>
public static class CodexRealtimeProtocol
{
    /// <summary>V3 update intentionally omits model, matching upstream methods_frameless_bidi.
    /// Sending this command does not prove acceptance: retain session.updated/error receipts.</summary>
    public static JsonObject SessionUpdate(CodexRealtimeSessionOptions options)
    {
        var session = Session(options); session.Remove("model");
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
    public static JsonObject Session(CodexRealtimeSessionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Instructions);
        CodexRealtimeStartupContext.ValidateInitialItems(options.InitialItems);
        // Upstream 0.163 separates V1 and V3; a newly added V3 voice is not valid on V1.
        var voices = options.Protocol == "v1" ? CodexProtocolCatalog.V1Voices : CodexProtocolCatalog.Voices;
        if (!voices.Contains(options.Voice, StringComparer.Ordinal))
            throw new ArgumentException("Voice is not supported by the selected Codex realtime protocol.", nameof(options));
        if (options.Protocol != "v3" && (options.LiveDecisionTriggers.Count != 0 || options.LiveDecisionDelegationQuestions.Count != 0 || options.LiveCustomVoiceId is not null || options.LiveVoiceDescription is not null))
            throw new ArgumentException("Live features require explicit V3 capability verification.");
        if (options.LiveCustomVoiceId is { } voiceId && (!voiceId.StartsWith("voice_", StringComparison.Ordinal) || !CodexLiveFeatures.Identifier(voiceId)) ||
            options.LiveVoiceDescription is { } description && (string.IsNullOrWhiteSpace(description) || description.Length > 4000 || options.LiveCustomVoiceId is null))
            throw new ArgumentException("Use a saved custom voice ID and bounded original description.");
        if (options.Protocol == "v1")
        {
            if (options.InitialItems.Count != 0) throw new ArgumentException("Initial items require V3.", nameof(options));
            return new JsonObject { ["type"] = "quicksilver", ["model"] = options.Model, ["instructions"] = options.Instructions,
                ["audio"] = new JsonObject { ["input"] = new JsonObject { ["format"] = new JsonObject
                { ["type"] = "audio/pcm", ["rate"] = 24000 } }, ["output"] = new JsonObject { ["voice"] = options.Voice } } };
        }
        if (options.Protocol != "v3") throw new ArgumentException("AVAS calls support V1/V3; V2 requires a different route.", nameof(options));
        var session = new JsonObject
        {
            ["model"] = options.Model, ["instructions"] = options.Instructions,
            ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = options.Voice } },
            ["delegation"] = new JsonObject { ["type"] = "client", ["ack_filler"] = options.DelegationAckFiller },
        };
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
        if (protocol == "v3") return new Uri(configured.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(callId));
        if (protocol != "v1") throw new ArgumentException("Unsupported AVAS protocol.", nameof(protocol));
        var path = configured.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/live", StringComparison.Ordinal)) path = path[..^5] + "/realtime";
        return new UriBuilder(configured) { Path = path, Query = "call_id=" + Uri.EscapeDataString(callId) }.Uri;
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
