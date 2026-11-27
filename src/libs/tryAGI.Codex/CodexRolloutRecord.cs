using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>
/// A lossless JSON envelope for a Rust Codex rollout record. This is a migration
/// boundary, not a second native thread journal or a replay authorization.
/// </summary>
public sealed class CodexRolloutRecord
{
    private CodexRolloutRecord(JsonElement envelope, string timestamp, ulong? ordinal, string type)
    {
        Envelope = envelope;
        Timestamp = timestamp;
        Ordinal = ordinal;
        Type = type;
    }

    /// <summary>The exact JSON value, including unknown fields and nested decimal representations.</summary>
    public JsonElement Envelope { get; }

    /// <summary>The original Rust timestamp string.</summary>
    public string Timestamp { get; }

    /// <summary>The optional paginated history ordinal; legacy records omit it.</summary>
    public ulong? Ordinal { get; }

    /// <summary>The Rust rollout item discriminator.</summary>
    public string Type { get; }

    /// <summary>The original item payload. No schema projection is applied.</summary>
    public JsonElement Payload => Envelope.GetProperty("payload");

    /// <summary>
    /// Whether the discriminator is present in rust-v0.172.0. This does not
    /// certify the payload or authorize replay of the record.
    /// </summary>
    public bool IsKnownType => Type is
        "session_meta" or "dynamic_tools_updated" or "response_item" or
        "inter_agent_communication" or "inter_agent_communication_metadata" or
        "compacted" or "turn_context" or "token_usage_record" or "world_state" or
        "retained_context" or "security_risk_score" or "event_msg" or "realtime_item";

    /// <summary>Parses one JSONL record without projecting its payload into CLR numeric types.</summary>
    public static CodexRolloutRecord Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        using var document = JsonDocument.Parse(line);
        return Decode(document.RootElement);
    }

    /// <summary>Parses one UTF-8 JSONL record.</summary>
    public static CodexRolloutRecord Parse(ReadOnlyMemory<byte> line)
    {
        using var document = JsonDocument.Parse(line);
        return Decode(document.RootElement);
    }

    /// <summary>Writes the entire original JSON envelope without dropping opaque fields.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Envelope.WriteTo(writer);
    }

    /// <summary>
    /// Computes the next paginated ordinal after a verified final record.
    /// Rejects missing ordinals, incomplete inherited prefixes and overflow.
    /// This does not read or modify storage.
    /// </summary>
    public ulong NextPaginatedOrdinal(ulong? inheritedHistoryStartOrdinal = null)
    {
        if (Ordinal is not { } ordinal || !IsKnownType)
            throw new InvalidDataException("Final paginated rollout record is missing a supported item or ordinal.");
        if (inheritedHistoryStartOrdinal is > 0 && ordinal < inheritedHistoryStartOrdinal.Value - 1)
            throw new InvalidDataException("Inherited rollout prefix is incomplete.");
        if (ordinal == ulong.MaxValue)
            throw new InvalidDataException("Paginated rollout ordinal overflow.");
        return ordinal + 1;
    }

    private static CodexRolloutRecord Decode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("timestamp", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(type.GetString()) ||
            !root.TryGetProperty("payload", out _))
            throw new JsonException("Invalid rollout record envelope.");

        ulong? ordinal = null;
        if (root.TryGetProperty("ordinal", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var parsed))
                throw new JsonException("Rollout ordinal must be an unsigned 64-bit integer.");
            ordinal = parsed;
        }

        return new CodexRolloutRecord(root.Clone(), timestamp.GetString()!, ordinal, type.GetString()!);
    }
}
