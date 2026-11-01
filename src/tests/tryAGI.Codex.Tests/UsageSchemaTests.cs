using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class UsageSchemaTests
{
    [Fact]
    public void PreservesTypesBooleansAndUnknownNumbersWithoutPrivateValues()
    {
        using var payload = JsonDocument.Parse("""
            {"type":"session.usage.updated","usage":{"audio_duration_ms":12.5,"settled":false,
            "status":"private billing value","future":1e100,"signed":-2,"unknown":null,
            "details":[{"secret":"private array value"}]},"usage_limit":{"allowed":true},
            "context_window":{"usage_ratio":0.25},"transcript":"private transcript",
            "session":{"token":"private token"}}
            """);
        var at = DateTimeOffset.UtcNow;
        var snapshot = Assert.IsType<CodexUsageSchemaSnapshot>(CodexUsageSchema.ObserveSession(payload.RootElement, at));
        Assert.Equal(at, snapshot.ObservedAt);
        Assert.False(snapshot.Truncated);
        var fields = snapshot.Fields.ToDictionary(f => f.Path);
        Assert.Equal(12.5m, fields["usage.audio_duration_ms"].Number);
        Assert.False(fields["usage.settled"].Boolean);
        Assert.True(fields["usage_limit.allowed"].Boolean);
        Assert.Null(fields["usage.future"].Number);
        Assert.Equal("number", fields["usage.future"].Kind);
        Assert.Equal(-2m, fields["usage.signed"].Number);
        Assert.Equal("null", fields["usage.unknown"].Kind);
        Assert.Equal(1, fields["usage.details"].ArrayLength);
        Assert.Equal(21, fields["usage.status"].StringLength);
        var encoded = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("private", encoded);
        Assert.DoesNotContain("transcript", encoded);
        Assert.DoesNotContain("token", encoded);
        Assert.Equal(snapshot.Fields, JsonSerializer.Deserialize<CodexUsageSchemaSnapshot>(encoded)?.Fields);
    }

    [Fact]
    public void BareCloseIsAnEmptyObservationRatherThanFinalBilling()
    {
        using var payload = JsonDocument.Parse("""{"type":"session.closed"}""");
        var snapshot = Assert.IsType<CodexUsageSchemaSnapshot>(CodexUsageSchema.ObserveSession(payload.RootElement, DateTimeOffset.UtcNow));
        Assert.Empty(snapshot.Fields);
        Assert.Equal("session.closed", snapshot.EventType);
        Assert.False(snapshot.Truncated);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"type\":false}")]
    [InlineData("{\"type\":\"response.completed\",\"usage\":{\"settled\":true}}")]
    public void DoesNotInterpretUnrelatedEvents(string json)
    {
        using var payload = JsonDocument.Parse(json);
        Assert.Null(CodexUsageSchema.ObserveSession(payload.RootElement, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GlobalFieldAndPathBoundsAreVisible()
    {
        var counters = string.Join(',', Enumerable.Range(0, 1000).Select(i => $"\"metric_{i}\":true"));
        using var payload = JsonDocument.Parse("{\"type\":\"session.usage.updated\",\"usage\":{" + counters + "}}");
        var snapshot = Assert.IsType<CodexUsageSchemaSnapshot>(CodexUsageSchema.ObserveSession(payload.RootElement, DateTimeOffset.UtcNow));
        Assert.True(snapshot.Truncated);
        Assert.Equal(CodexUsageSchema.MaximumFields, snapshot.Fields.Length);
        Assert.True(snapshot.Fields.Sum(f => f.Path.Length) <= CodexUsageSchema.MaximumPathCharacters);
    }

    [Fact]
    public void RejectsAmbiguousPathsAndReportsDepthOrNameLoss()
    {
        using var payload = JsonDocument.Parse("""
            {"type":"session.closed","usage":{"a":true,"a":false,"a.b":42,
            "deep":{"a":{"b":{"c":{"d":{"e":true}}}}}}}
            """);
        var snapshot = Assert.IsType<CodexUsageSchemaSnapshot>(CodexUsageSchema.ObserveSession(payload.RootElement, DateTimeOffset.UtcNow));
        Assert.True(snapshot.Truncated);
        Assert.True(Assert.Single(snapshot.Fields, f => f.Path == "usage.a").Boolean);
        Assert.DoesNotContain(snapshot.Fields, f => f.Path == "usage.a.b" || f.Path.EndsWith(".e", StringComparison.Ordinal));
    }
}
