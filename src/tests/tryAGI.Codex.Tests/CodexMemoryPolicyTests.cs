using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexMemoryPolicyTests
{
    // Pinned Rust: ext/memories/src/extension.rs and tests.rs.
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, true, true)]
    public void ContextAndDedicatedToolsRequireEffectiveOptIn(
        bool feature, bool use, bool dedicated, bool readEnabled, bool toolsEnabled)
    {
        var policy = new CodexMemoryPolicy(feature, use, dedicated);
        Assert.Equal(readEnabled, policy.IsReadPathEnabled);
        Assert.Equal(toolsEnabled, policy.AreDedicatedToolsEnabled);
    }

    [Theory]
    [InlineData(CodexMemoryVersion.V1, "memories")]
    [InlineData(CodexMemoryVersion.V2, "memories_v2")]
    public void ThreadNamespaceRemainsPinnedWhenConfigurationChanges(CodexMemoryVersion version, string directory)
    {
        var original = new CodexMemoryPolicy(true, true, true, version);
        var disabled = original.WithConfiguration(true, false, true);
        Assert.False(disabled.IsReadPathEnabled);
        Assert.False(disabled.AreDedicatedToolsEnabled);
        Assert.Equal(version, disabled.Version);
        Assert.Equal(directory, disabled.DirectoryName);

        var enabled = disabled.WithConfiguration(true, true, true);
        Assert.True(enabled.AreDedicatedToolsEnabled);
        Assert.Equal(directory, enabled.DirectoryName);
    }

    [Fact]
    public void UnknownVersionIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CodexMemoryPolicy(true, true, true, (CodexMemoryVersion)42));

    // Pinned Rust: rollout/src/policy.rs, should_persist_response_item_for_memories.
    [Theory]
    [InlineData("agent_message", true)]
    [InlineData("local_shell_call", true)]
    [InlineData("function_call", true)]
    [InlineData("tool_search_call", true)]
    [InlineData("function_call_output", true)]
    [InlineData("tool_search_output", true)]
    [InlineData("custom_tool_call", true)]
    [InlineData("custom_tool_call_output", true)]
    [InlineData("web_search_call", true)]
    [InlineData("reasoning", false)]
    [InlineData("additional_tools", false)]
    [InlineData("configuration_update", false)]
    [InlineData("image_generation_call", false)]
    [InlineData("compaction", false)]
    [InlineData("compaction_trigger", false)]
    [InlineData("context_compaction", false)]
    [InlineData("other", false)]
    [InlineData("future_item", false)]
    public void ExtractionUsesPinnedResponseItemDiscriminators(string type, bool expected)
    {
        using var document = JsonDocument.Parse("{\"type\":\"" + type + "\"}");
        Assert.Equal(expected, CodexMemoryPolicy.IsExtractionCandidate(document.RootElement));
    }

    [Theory]
    [InlineData("{\"type\":\"message\",\"role\":\"user\"}", true)]
    [InlineData("{\"type\":\"message\",\"role\":\"assistant\"}", true)]
    [InlineData("{\"type\":\"message\",\"role\":\"developer\"}", false)]
    [InlineData("{\"type\":\"message\"}", false)]
    [InlineData("{\"type\":\"message\",\"role\":null}", false)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    public void DeveloperAndMalformedMessagesDoNotEnterExtraction(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, CodexMemoryPolicy.IsExtractionCandidate(document.RootElement));
        Assert.Equal(json, document.RootElement.GetRawText());
    }
}
