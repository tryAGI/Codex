using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>A session receipt; the raw provider session remains available in <see cref="CodexRealtimeEvent.Payload"/>.</summary>
public sealed record CodexRealtimeSessionReceipt(string Id, string? Instructions, string? Model);

/// <summary>A provider request to hand work to the background agent.</summary>
public sealed record CodexRealtimeHandoff(string HandoffId, string ItemId, string InputTranscript);

/// <summary>A provider request to remain silent, not to run a reasoning task.</summary>
public sealed record CodexRealtimeNoop(string CallId, string ItemId);

/// <summary>Added or finished conversation item. The raw item is preserved without a schema restriction.</summary>
public sealed record CodexRealtimeConversationItem(string? Id, bool Done, JsonElement Item);

/// <summary>Input speech start receipt. Item ID may be omitted by the provider.</summary>
public sealed record CodexRealtimeSpeechStarted(string? ItemId, double? AudioStartMs);

/// <summary>Response lifecycle receipt, including an optional provider identity and terminal status.</summary>
public sealed record CodexRealtimeResponseReceipt(string EventType, string? ResponseId, string? Status);

/// <summary>Frameless completed turn, with canonical transcript and optional identity.</summary>
public sealed record CodexRealtimeTurn(string Role, string Transcript, string? Id);

internal static class CodexRealtimeEventProjections
{
    private static string? Text(JsonElement e, string key) => CodexRealtimeProtocol.Text(e, key);
    private static JsonElement Child(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var child) ? child : default;
    private static double? Number(JsonElement e, string key) =>
        Child(e, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 ? n : null;

    internal static bool Recognized(string protocol, string type)
    {
        if (type is "session.updated" or "error") return protocol is "v1" or "v2" or "v3";
        return protocol switch
        {
            "v1" => type is "conversation.output_audio.delta" or "conversation.input_transcript.delta" or
                "conversation.item.input_audio_transcription.delta" or "conversation.input_transcript.turn_marked" or
                "conversation.item.input_audio_transcription.completed" or "conversation.output_transcript.delta" or
                "response.output_text.delta" or "response.output_audio_transcript.delta" or "response.output_audio_transcript.done" or
                "conversation.item.added" or "conversation.item.done" or "conversation.handoff.requested",
            "v2" => type is "response.output_audio.delta" or "response.audio.delta" or
                "conversation.item.input_audio_transcription.delta" or "conversation.item.input_audio_transcription.completed" or
                "response.output_text.delta" or "response.output_audio_transcript.delta" or "response.output_text.done" or
                "response.output_audio_transcript.done" or "input_audio_buffer.speech_started" or
                "conversation.item.added" or "conversation.item.created" or "conversation.item.done" or
                "response.created" or "response.cancelled" or "response.done",
            "v3" => type is "session.started" or "output_audio.delta" or "input_transcript.added" or
                "output_transcript.added" or "turn.done" or "delegation.created",
            _ => false,
        };
    }

    internal static CodexRealtimeSessionReceipt? Session(JsonElement e)
    {
        if (Text(e, "type") is not ("session.updated" or "session.started" or "session.created")) return null;
        var session = Child(e, "session");
        return Text(session, "id") is { } id ? new(id, Text(session, "instructions"), Text(session, "model")) : null;
    }

    internal static CodexRealtimeHandoff? Handoff(JsonElement e, string? protocol)
    {
        var type = Text(e, "type");
        if (type == "delegation.created" && protocol is null or "v3" && CodexRealtimeProtocol.ReadDelegation(e) is { } delegation)
            return new(delegation.Id, delegation.Id, delegation.Transcript);
        if (type == "conversation.handoff.requested" && protocol is null or "v1" &&
            Text(e, "handoff_id") is { } handoff && Text(e, "item_id") is { } itemId && Text(e, "input_transcript") is { } transcript)
            return new(handoff, itemId, transcript);
        if (type != "conversation.item.done" || protocol is not (null or "v2")) return null;
        var item = Child(e, "item");
        if (Text(item, "type") != "function_call" || Text(item, "name") != "background_agent") return null;
        var callId = Text(item, "call_id") ?? Text(item, "id");
        if (callId is null) return null;
        var arguments = Text(item, "arguments") ?? "";
        if (arguments.Length > 65536) return null;
        return new(callId, Text(item, "id") ?? callId, ExtractTranscript(arguments));
    }

    private static string ExtractTranscript(string arguments)
    {
        // Rust checks these aliases in this exact order and retains unknown argument text.
        try
        {
            using var document = JsonDocument.Parse(arguments);
            foreach (var key in new[] { "input_transcript", "input", "text", "prompt", "query" })
                if (Text(document.RootElement, key)?.Trim() is { Length: > 0 } value) return value;
        }
        catch (JsonException) { }
        return arguments;
    }

    internal static CodexRealtimeNoop? Noop(JsonElement e, string? protocol)
    {
        if (Text(e, "type") != "conversation.item.done" || protocol is not (null or "v2")) return null;
        var item = Child(e, "item");
        if (Text(item, "type") != "function_call" || Text(item, "name") != "remain_silent") return null;
        var callId = Text(item, "call_id") ?? Text(item, "id");
        return callId is not null ? new(callId, Text(item, "id") ?? callId) : null;
    }

    internal static CodexRealtimeConversationItem? ConversationItem(JsonElement e)
    {
        var type = Text(e, "type");
        if (type is not ("conversation.item.added" or "conversation.item.created" or "conversation.item.done")) return null;
        var item = Child(e, "item");
        var id = Text(item, "id");
        if (item.ValueKind == JsonValueKind.Undefined || type == "conversation.item.done" && id is null) return null;
        return new(id, type == "conversation.item.done", item);
    }

    internal static CodexRealtimeSpeechStarted? SpeechStarted(JsonElement e) =>
        Text(e, "type") == "input_audio_buffer.speech_started" ? new(Text(e, "item_id"), Number(e, "audio_start_ms")) : null;

    internal static CodexRealtimeResponseReceipt? Response(JsonElement e)
    {
        var type = Text(e, "type");
        if (type is not ("response.created" or "response.cancelled" or "response.done")) return null;
        var response = Child(e, "response");
        return new(type, Text(response, "id") ?? Text(e, "response_id"), Text(response, "status") ?? Text(e, "status"));
    }

    internal static CodexRealtimeTurn? Turn(JsonElement e)
    {
        if (Text(e, "type") != "turn.done") return null;
        var turn = Child(e, "turn");
        return Text(turn, "role") is "user" or "assistant" && Text(turn, "transcript") is { } transcript
            ? new(Text(turn, "role")!, transcript, Text(turn, "id")) : null;
    }

    internal static string? ProviderErrorText(JsonElement e)
    {
        if (Text(e, "type") != "error") return null;
        // Rust first reads a top-level message, then a nested message, then the raw error value.
        var error = Child(e, "error");
        return Text(e, "message") ?? Text(error, "message") ??
            (error.ValueKind == JsonValueKind.Undefined ? null : error.GetRawText());
    }

    internal static uint? Unsigned(JsonElement e, string key) =>
        Child(e, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetUInt32(out var n) ? n : null;
}
