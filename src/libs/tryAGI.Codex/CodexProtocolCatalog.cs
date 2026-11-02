using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Reviewed protocol capabilities. A source change requires review, never an automatic runtime upgrade.</summary>
public static class CodexProtocolCatalog
{
    private static readonly JsonElement Baseline = Read();
    /// <summary>Default voice of Codex realtime V3.</summary>
    public static string DefaultVoice => (Baseline.GetProperty("defaultVoice").GetString() ?? throw new InvalidDataException("Required data was null."));
    /// <summary>Default realtime model.</summary>
    public static string DefaultRealtimeModel => (Baseline.GetProperty("defaultRealtimeModel").GetString() ?? throw new InvalidDataException("Required data was null."));
    /// <summary>Built-in voices of Codex realtime V3.</summary>
    public static IReadOnlyList<string> Voices { get; } = Array.AsReadOnly(Baseline.GetProperty("voices")
        .EnumerateArray().Select(x => (x.GetString() ?? throw new InvalidDataException("Required data was null."))).ToArray());
    /// <summary>Built-in voices of the V1 realtime protocol.</summary>
    public static IReadOnlyList<string> V1Voices { get; } = Array.AsReadOnly(Baseline.GetProperty("otherProtocolVoices").GetProperty("realtime-v1")
        .EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("Required voice was null.")).ToArray());
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
