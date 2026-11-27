using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexRolloutRecordTests
{
    // Rust baseline: history/src/rollout_payload.rs and rollout/src/lib.rs.
    [Fact]
    public void FlattenedEnvelopePreservesDecimalAndOpaqueFields()
    {
        const string json = """
            {"timestamp":"2026-01-01T00:00:00Z","ordinal":7,"type":"response_item","payload":{"type":"function_call_output","output":{"cost":0.12345678901234567890123456789,"exp":1.20e-12}},"metadata":{"future":true}}
            """;
        var record = CodexRolloutRecord.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("response_item", record.Type);
        Assert.Equal(7UL, record.Ordinal);
        Assert.True(record.IsKnownType);
        Assert.Equal("0.12345678901234567890123456789", record.Payload.GetProperty("output").GetProperty("cost").GetRawText());
        Assert.Equal("1.20e-12", record.Payload.GetProperty("output").GetProperty("exp").GetRawText());

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            record.WriteTo(writer);
        Assert.Equal(json, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"event_msg\",\"payload\":{}}")]
    [InlineData("{\"timestamp\":3,\"type\":\"event_msg\",\"payload\":{}}")]
    [InlineData("{\"timestamp\":\"t\",\"type\":\"event_msg\"}")]
    [InlineData("{\"timestamp\":\"t\",\"type\":\"event_msg\",\"payload\":{},\"ordinal\":-1}")]
    [InlineData("{\"timestamp\":\"t\",\"type\":\"event_msg\",\"payload\":{},\"ordinal\":1.5}")]
    [InlineData("{\"timestamp\":\"t\",\"type\":\"event_msg\",\"payload\":{},\"ordinal\":18446744073709551616}")]
    public void InvalidEnvelopeIsRejected(string json)
        => Assert.Throws<JsonException>(() => CodexRolloutRecord.Parse(json));

    [Theory]
    [InlineData("{\"timestamp\":\"t\",\"type\":\"session_meta\",\"payload\":{}}")]
    [InlineData("{\"timestamp\":\"t\",\"ordinal\":null,\"type\":\"session_meta\",\"payload\":{}}")]
    public void LegacyOrdinalIsAbsent(string json)
    {
        var record = CodexRolloutRecord.Parse(json);
        Assert.Null(record.Ordinal);
        Assert.Throws<InvalidDataException>(() => record.NextPaginatedOrdinal());
    }

    [Fact]
    public void FutureRecordIsPreservedButCannotAuthorizeContinuation()
    {
        const string json = """
            {"timestamp":"t","ordinal":9,"type":"future_type","payload":{"data":"retain"},"new_field":42}
            """;
        var record = CodexRolloutRecord.Parse(json);
        Assert.False(record.IsKnownType);
        Assert.Equal(json, record.Envelope.GetRawText());
        Assert.Throws<InvalidDataException>(() => record.NextPaginatedOrdinal());
    }

    // Rust baseline: rollout/src/ordinal.rs, incomplete inherited prefix and checked overflow.
    [Fact]
    public void PaginatedContinuationFencesIncompletePrefixAndOverflow()
    {
        var final = CodexRolloutRecord.Parse("""
            {"timestamp":"t","type":"event_msg","ordinal":8,"payload":{}}
            """);
        Assert.Equal(9UL, final.NextPaginatedOrdinal());
        Assert.Equal(9UL, final.NextPaginatedOrdinal(9));
        Assert.Throws<InvalidDataException>(() => final.NextPaginatedOrdinal(10));
        var overflow = CodexRolloutRecord.Parse("""
            {"timestamp":"t","type":"event_msg","ordinal":18446744073709551615,"payload":{}}
            """);
        Assert.Equal(ulong.MaxValue, overflow.Ordinal);
        Assert.Throws<InvalidDataException>(() => overflow.NextPaginatedOrdinal());
    }
}
