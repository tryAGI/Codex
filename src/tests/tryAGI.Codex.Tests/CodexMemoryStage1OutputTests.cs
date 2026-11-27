using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexMemoryStage1OutputTests
{
    [Fact]
    public void V1AndV2SchemaAndOutputAreVersionSpecific()
    {
        var v1 = CodexMemoryStage1Output.Schema(CodexMemoryVersion.V1);
        var v2 = CodexMemoryStage1Output.Schema(CodexMemoryVersion.V2);
        Assert.True(v1.GetProperty("properties").TryGetProperty("raw_memory", out _));
        Assert.False(v2.GetProperty("properties").TryGetProperty("raw_memory", out _));
        Assert.False(v2.GetProperty("additionalProperties").GetBoolean());

        var legacy = CodexMemoryStage1Output.Parse(CodexMemoryVersion.V1, 5,
            """{"raw_memory":"secret","rollout_summary":"legacy","rollout_slug":null}""",
            value => value.Replace("secret", "[redacted]", StringComparison.Ordinal));
        Assert.Equal(new CodexMemoryExtractionOutput(5, "[redacted]", "legacy"), legacy);
        var current = CodexMemoryStage1Output.Parse(CodexMemoryVersion.V2, 6,
            """{"rollout_summary":"current","rollout_slug":"slug"}""", value => value);
        Assert.Equal(new CodexMemoryExtractionOutput(6, "", "current", "slug"), current);
    }

    [Theory]
    [InlineData(CodexMemoryVersion.V2, """{"rollout_summary":"ok","rollout_slug":"a","raw_memory":"forbidden"}""")]
    [InlineData(CodexMemoryVersion.V2, """{"rollout_summary":"ok"}""")]
    [InlineData(CodexMemoryVersion.V2, """{"rollout_summary":"ok","rollout_slug":null}""")]
    [InlineData(CodexMemoryVersion.V2, """{"rollout_summary":"ok","rollout_slug":"a","rollout_slug":"b"}""")]
    [InlineData(CodexMemoryVersion.V1, """{"rollout_summary":"ok","rollout_slug":"a"}""")]
    [InlineData(CodexMemoryVersion.V1, """{"raw_memory":"ok","rollout_summary":9}""")]
    public void MalformedOrCrossVersionOutputIsRejected(CodexMemoryVersion version, string json) =>
        Assert.Throws<JsonException>(() => CodexMemoryStage1Output.Parse(version, 1, json, value => value));

    [Fact]
    public void EmptyRequiredOutputsAndRedactionAreHandledBeforePersistence()
    {
        Assert.Null(CodexMemoryStage1Output.Parse(CodexMemoryVersion.V1, 1,
            """{"raw_memory":"","rollout_summary":"summary"}""", value => value));
        Assert.Null(CodexMemoryStage1Output.Parse(CodexMemoryVersion.V2, 1,
            """{"rollout_summary":"","rollout_slug":"slug"}""", value => value));
        Assert.Null(CodexMemoryStage1Output.Parse(CodexMemoryVersion.V2, 1,
            """{"rollout_summary":"secret","rollout_slug":"slug"}""", value => value == "secret" ? "" : value));
    }

    [Fact]
    public void V2TruncationPreservesUtf8AndBothBoundaries()
    {
        var large = "start" + string.Concat(Enumerable.Repeat("🙂", 4000)) + "end";
        var json = "{\"rollout_summary\":" + JsonSerializer.Serialize(large) + ",\"rollout_slug\":\"slug\"}";
        var output = CodexMemoryStage1Output.Parse(CodexMemoryVersion.V2, 8, json, value => value)!;
        Assert.StartsWith("start", output.RolloutSummary);
        Assert.EndsWith("end", output.RolloutSummary);
        Assert.Contains("[truncated]", output.RolloutSummary);
        Assert.True(new UTF8Encoding(false, true).GetByteCount(output.RolloutSummary) <= 9000);
    }
}
