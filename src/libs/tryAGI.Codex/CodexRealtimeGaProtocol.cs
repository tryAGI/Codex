using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Public GA Realtime wire protocol. Raw events and session fields remain available alongside typed helpers.</summary>
internal static class CodexRealtimeGaProtocol
{
    public static IReadOnlyList<string> Voices { get; } = ["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"];
    public static JsonObject Session(CodexRealtimeSessionOptions options)
    {
        if (options.Protocol != "v2") throw new ArgumentException("GA Realtime requires protocol v2.");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Instructions);
        CodexRealtimeStartupContext.ValidateInitialItems(options.InitialItems);
        var session = options.ProviderSession is { } fields ? JsonNode.Parse(fields.GetRawText()) as JsonObject
            ?? throw new ArgumentException("providerSession must be an object.") : new JsonObject();
        // Identity is selected explicitly by the host, never hidden in additional provider fields.
        foreach (var key in new[] { "type", "model", "instructions" })
            if (session.ContainsKey(key)) throw new ArgumentException("Use the dedicated setting for " + key + ".");
        session["type"] = "realtime"; session["model"] = options.Model; session["instructions"] = options.Instructions;
        if (!session.ContainsKey("output_modalities")) session["output_modalities"] = new JsonArray("audio");
        var audio = Object(session, "audio"); var input = Object(audio, "input"); var output = Object(audio, "output");
        if (!input.ContainsKey("format")) input["format"] = Pcm();
        if (!input.ContainsKey("transcription")) input["transcription"] = new JsonObject { ["model"] = "gpt-4o-mini-transcribe" };
        if (!input.ContainsKey("turn_detection")) input["turn_detection"] = new JsonObject { ["type"] = "semantic_vad" };
        if (!output.ContainsKey("format")) output["format"] = Pcm();
        if (output.ContainsKey("voice")) throw new ArgumentException("Use the dedicated voice setting.");
        output["voice"] = options.Voice;
        return session;
    }
    private static JsonObject Pcm() => new() { ["type"] = "audio/pcm", ["rate"] = 24000 };
    private static JsonObject Object(JsonObject parent, string key)
    {
        if (!parent.ContainsKey(key)) parent[key] = new JsonObject();
        return parent[key] as JsonObject ?? throw new ArgumentException(key + " must be an object.");
    }
    public static JsonObject SessionUpdate(CodexRealtimeSessionOptions options) => new() { ["type"] = "session.update", ["session"] = Session(options) };
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
