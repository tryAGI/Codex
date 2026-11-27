using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Reviewed protocol capabilities. A source change requires review, never an automatic runtime upgrade.</summary>
public static class CodexProtocolCatalog
{
    private static readonly JsonElement Baseline = Read();
    /// <summary>Default voice of a ChatGPT Codex realtime V3 WebRTC call.</summary>
    public static string DefaultVoice => (Baseline.GetProperty("defaultVoice").GetString() ?? throw new InvalidDataException("Required data was null."));
    /// <summary>Default V3 realtime model.</summary>
    public static string DefaultRealtimeModel => (Baseline.GetProperty("defaultRealtimeModel").GetString() ?? throw new InvalidDataException("Required data was null."));
    /// <summary>Built-in voices of Codex realtime V3.</summary>
    public static IReadOnlyList<string> Voices { get; } = Array.AsReadOnly(Baseline.GetProperty("voices")
        .EnumerateArray().Select(x => (x.GetString() ?? throw new InvalidDataException("Required data was null."))).ToArray());
    /// <summary>Built-in voices of the V1 realtime protocol.</summary>
    public static IReadOnlyList<string> V1Voices { get; } = Array.AsReadOnly(Baseline.GetProperty("otherProtocolVoices").GetProperty("realtime-v1")
        .EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Required voice was null.")).ToArray());
    /// <summary>Built-in voices of the V2 realtime protocol.</summary>
    public static IReadOnlyList<string> V2Voices { get; } = Array.AsReadOnly(Baseline.GetProperty("otherProtocolVoices").GetProperty("realtime-v2")
        .EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Required voice was null.")).ToArray());

    /// <summary>Default model for the requested Rust wire adapter.</summary>
    public static string GetDefaultRealtimeModel(string protocol) => protocol switch
    {
        "v1" or "v2" => Baseline.GetProperty("defaultLegacyRealtimeModel").GetString() ?? throw new InvalidDataException("Required model was null."),
        "v3" => DefaultRealtimeModel,
        _ => throw new ArgumentException("Unsupported realtime protocol.", nameof(protocol)),
    };

    /// <summary>Default voice for the wire adapter and destination. ChatGPT call creation overrides the adapter default.</summary>
    public static string GetDefaultVoice(string protocol, bool chatGptCall = false) => protocol switch
    {
        "v1" => DefaultVoice,
        "v2" or "v3" => chatGptCall ? DefaultVoice : Baseline.GetProperty("defaultAdapterVoice").GetString() ?? throw new InvalidDataException("Required voice was null."),
        _ => throw new ArgumentException("Unsupported realtime protocol.", nameof(protocol)),
    };

    /// <summary>The whole reviewed baseline, including upstream source hashes.</summary>
    public static JsonElement Snapshot => Baseline.Clone();

    private static JsonElement Read()
    {
        using var stream = typeof(CodexProtocolCatalog).Assembly.GetManifestResourceStream("tryAGI.Codex.ProtocolBaseline.json")
            ?? throw new InvalidOperationException("Codex protocol baseline is unavailable.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }
}
