using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Public GA Realtime wire protocol. Raw events and session fields remain available alongside typed helpers.</summary>
internal static class CodexRealtimeGaProtocol
{
    public static IReadOnlyList<string> Voices => CodexProtocolCatalog.V2Voices;
    public static JsonObject Session(CodexRealtimeSessionOptions options)
    {
        if (options.Protocol != "v2") throw new ArgumentException("GA Realtime requires protocol v2.");
        CodexRealtimeProtocol.ValidateSessionOptions(options);
        var session = options.ProviderSession is { } fields ? JsonNode.Parse(fields.GetRawText()) as JsonObject
            ?? throw new ArgumentException("providerSession must be an object.") : new JsonObject();
        // Identity is selected explicitly by the host, never hidden in additional provider fields.
        foreach (var key in new[] { "type", "model", "instructions", "id" })
            if (session.ContainsKey(key)) throw new ArgumentException("Use the dedicated setting for " + key + ".");
        session["model"] = options.Model;
        if (options.SessionId is { } id) session["id"] = id;
        var audio = Object(session, "audio"); var input = Object(audio, "input");
        if (!input.ContainsKey("format")) input["format"] = Pcm();
        if (!input.ContainsKey("transcription")) input["transcription"] = new JsonObject { ["model"] = "gpt-4o-mini-transcribe" };
        if (options.Mode == CodexRealtimeSessionMode.Transcription)
        {
            if (options.OutputModality != CodexRealtimeOutputModality.Audio || audio.ContainsKey("output") ||
                session.ContainsKey("output_modalities") || session.ContainsKey("tools") || session.ContainsKey("tool_choice"))
                throw new ArgumentException("Transcription mode does not accept output configuration.", nameof(options));
            session["type"] = "transcription";
            return session;
        }
        session["type"] = "realtime"; session["instructions"] = options.Instructions;
        if (!session.ContainsKey("output_modalities")) session["output_modalities"] = new JsonArray(options.OutputModality == CodexRealtimeOutputModality.Text ? "text" : "audio");
        var output = Object(audio, "output");
        if (!input.ContainsKey("noise_reduction")) input["noise_reduction"] = new JsonObject { ["type"] = "near_field" };
        if (!input.ContainsKey("turn_detection")) input["turn_detection"] = new JsonObject
        {
            ["type"] = "server_vad", ["interrupt_response"] = true, ["create_response"] = true, ["silence_duration_ms"] = 500,
        };
        if (!session.ContainsKey("tools")) session["tools"] = RustTools();
        if (!session.ContainsKey("tool_choice")) session["tool_choice"] = "auto";
        if (!output.ContainsKey("format")) output["format"] = Pcm();
        if (output.ContainsKey("voice")) throw new ArgumentException("Use the dedicated voice setting.");
        var voice = options.ResolveVoice(chatGptCall: false);
        if (!Voices.Contains(voice, StringComparer.Ordinal)) throw new ArgumentException("Voice is not supported by V2.", nameof(options));
        output["voice"] = voice;
        return session;
    }
    // The schemas belong to the V2 wire adapter; tool execution remains the host's responsibility.
    private static JsonArray RustTools() => new(
        new JsonObject
        {
            ["type"] = "function", ["name"] = "background_agent",
            ["description"] = "Send a user request to the background agent. Use this as the default action. Do not rephrase the user's ask or rewrite it in your own words; pass along the user's own words. If the background agent is idle, this starts a new task and returns the final result to the user. If the background agent is already working on a task, this sends the request as guidance to steer that previous task. If the user asks to do something next, later, after this, or once current work finishes, call this tool so the work is actually queued instead of merely promising to do it later.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject
                {
                    ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "The user request to delegate to the background agent." },
                },
                ["required"] = new JsonArray("prompt"), ["additionalProperties"] = false,
            },
        },
        new JsonObject
        {
            ["type"] = "function", ["name"] = "remain_silent",
            ["description"] = "Call this when the best response is to say nothing. Use it instead of speaking after hidden system/control messages, after background agent updates in silent modes, or whenever acknowledging aloud would be distracting. This tool has no user-visible effect.",
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
        });

    private static JsonObject Pcm() => new() { ["type"] = "audio/pcm", ["rate"] = 24000 };
    private static JsonObject Object(JsonObject parent, string key)
    {
        if (!parent.ContainsKey(key)) parent[key] = new JsonObject();
        return parent[key] as JsonObject ?? throw new ArgumentException(key + " must be an object.");
    }
    public static JsonObject SessionUpdate(CodexRealtimeSessionOptions options)
    {
        var session = Session(options);
        session.Remove("id"); session.Remove("model");
        return new() { ["type"] = "session.update", ["session"] = session };
    }
    public static JsonObject AudioAppend(ReadOnlySpan<byte> pcm)
    {
        var result = CodexRealtimeProtocol.AudioAppend(pcm); result["type"] = "input_audio_buffer.append"; return result;
    }
    public static JsonObject Message(string role, string text)
    {
        if (role is not ("user" or "assistant" or "developer" or "system")) throw new ArgumentException("Unsupported message role.");
        return new() { ["type"] = "conversation.item.create", ["item"] = new JsonObject { ["type"] = "message", ["role"] = role == "developer" ? "system" : role,
            ["content"] = new JsonArray(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = text }) } };
    }
    public static JsonObject FunctionOutput(string callId, string output) => new() { ["type"] = "conversation.item.create", ["item"] = new JsonObject
    { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output } };
    public static CodexRealtimeAudio? ReadAudio(JsonElement payload)
    {
        if (CodexRealtimeProtocol.Text(payload, "type") != "response.output_audio.delta") return null;
        if (CodexRealtimeProtocol.Text(payload, "delta") is not { Length: > 0 and <= 349528 } encoded)
            throw new CodexProtocolException("GA output audio missing or exceeded the bounded frame size.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new CodexProtocolException("GA output audio is invalid base64."); }
        if (bytes.Length is <= 0 or > 262144 || bytes.Length % 2 != 0)
            throw new CodexProtocolException("GA output audio is invalid PCM16.");
        return new(bytes, ItemId: CodexRealtimeProtocol.Text(payload, "item_id"));
    }
    public static CodexRealtimeTranscript? ReadTranscript(JsonElement payload)
    {
        var type = CodexRealtimeProtocol.Text(payload, "type");
        if (type is "response.output_audio_transcript.delta" or "response.output_text.delta")
            return new("assistant", CodexRealtimeProtocol.Text(payload, "delta") ?? "", false);
        if (type is "response.output_audio_transcript.done" or "response.output_text.done")
        {
            var final = CodexRealtimeProtocol.Text(payload, "transcript") ?? CodexRealtimeProtocol.Text(payload, "text");
            return new("assistant", final ?? "", true) { ReplaceFinalText = final is not null };
        }
        if (type == "conversation.item.input_audio_transcription.completed")
        {
            var final = CodexRealtimeProtocol.Text(payload, "transcript");
            return new("user", final ?? "", true) { ReplaceFinalText = final is not null };
        }
        return null;
    }
    /// <summary>Only this explicitly declared tool maps to the backing reasoning lane; arbitrary functions stay raw.</summary>
    public static CodexDelegation? ReadDelegation(JsonElement payload)
    {
        if (CodexRealtimeProtocol.Text(payload, "type") != "response.function_call_arguments.done" ||
            CodexRealtimeProtocol.Text(payload, "name") != "native_delegate" || CodexRealtimeProtocol.Text(payload, "call_id") is not { } id ||
            CodexRealtimeProtocol.Text(payload, "arguments") is not { Length: <= 65536 } arguments) return null;
        try
        {
            using var json = JsonDocument.Parse(arguments);
            return CodexRealtimeProtocol.Text(json.RootElement, "transcript") is { Length: > 0 and <= 16000 } text ? new(id, text) : null;
        }
        catch (JsonException) { return null; }
    }
}
