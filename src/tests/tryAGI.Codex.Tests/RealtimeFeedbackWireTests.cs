using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeFeedbackWireTests
{
    [Theory]
    [InlineData("v1", "conversation.handoff.append", "codex", false)]
    [InlineData("v2", "conversation.item.create", null, true)]
    [InlineData("v3", "session.context.append", null, false)]
    public void StandaloneSpeechUsesPinnedAdapter(string version, string type, string? handoff, bool create)
    {
        var wire = CodexRealtimeFeedbackWire.Build(version, CodexRealtimeFeedbackKind.Speech, "hello", null, "commentary");
        var message = Assert.Single(wire.Messages);
        Assert.Equal(type, message["type"]!.GetValue<string>());
        Assert.Equal(handoff, message["handoff_id"]?.GetValue<string>());
        Assert.Equal(create, wire.CreateResponse);
        Assert.False(wire.FunctionReceipt);
        if (version == "v2")
        {
            Assert.Equal("user", message["item"]!["role"]!.GetValue<string>());
            Assert.Equal("[BACKEND] hello", message["item"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        if (version == "v3") Assert.Equal("speakable", message["channel"]!.GetValue<string>());
    }

    [Fact]
    public void V2ProgressAndCompletionAreSeparateAndItemsHavePrefix()
    {
        var progress = CodexRealtimeFeedbackWire.Build("v2", CodexRealtimeFeedbackKind.Progress, "thinking", "call", "commentary");
        Assert.False(progress.CreateResponse);
        Assert.False(progress.FunctionReceipt);
        Assert.Equal("user", Assert.Single(progress.Messages)["item"]!["role"]!.GetValue<string>());
        var final = CodexRealtimeFeedbackWire.Build("v2", CodexRealtimeFeedbackKind.Final, "answer", "call", "speakable");
        Assert.False(final.CreateResponse);
        Assert.False(final.FunctionReceipt);
        var complete = CodexRealtimeFeedbackWire.Build("v2", CodexRealtimeFeedbackKind.Complete, "complete", "call", null);
        Assert.True(complete.CreateResponse);
        Assert.True(complete.FunctionReceipt);
        Assert.Equal(CodexRealtimeFeedbackWire.CompleteAck, Assert.Single(complete.Messages)["item"]!["output"]!.GetValue<string>());
        var item = CodexRealtimeFeedbackWire.Build("v2", CodexRealtimeFeedbackKind.Item, "answer", "call", null, "prefix");
        Assert.False(item.CreateResponse);
        var itemMessage = Assert.Single(item.Messages)["item"]!;
        Assert.Equal("developer", itemMessage["role"]!.GetValue<string>());
        Assert.Equal("input_text", itemMessage["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("prefix\n\n[BACKEND] answer", itemMessage["content"]![0]!["text"]!.GetValue<string>());
        var ack = CodexRealtimeFeedbackWire.Build("v2", CodexRealtimeFeedbackKind.Complete, "", "call", null);
        Assert.False(ack.CreateResponse);
        Assert.Equal("", Assert.Single(ack.Messages)["item"]!["output"]!.GetValue<string>());
    }

    [Fact]
    public void LegacyFinalAndFramelessStatusFollowDifferentWireContracts()
    {
        var final = CodexRealtimeFeedbackWire.Build("v1", CodexRealtimeFeedbackKind.Final, "answer", "handoff", null);
        Assert.Equal("\"Agent Final Message\":\n\nanswer", Assert.Single(final.Messages)["output_text"]!.GetValue<string>());
        Assert.Empty(CodexRealtimeFeedbackWire.Build("v1", CodexRealtimeFeedbackKind.Complete, "complete", "handoff", null).Messages);
        Assert.Empty(CodexRealtimeFeedbackWire.Build("v3", CodexRealtimeFeedbackKind.Complete, "complete", "handoff", null).Messages);
        var status = CodexRealtimeFeedbackWire.Build("v3", CodexRealtimeFeedbackKind.Status, "[STATUS] working", "handoff", null);
        var message = Assert.Single(status.Messages);
        Assert.Equal("delegation.context.append", message["type"]!.GetValue<string>());
        Assert.Equal("handoff", message["delegation_item_id"]!.GetValue<string>());
        Assert.Equal("commentary", message["channel"]!.GetValue<string>());
        Assert.False(status.CreateResponse);
        var thinking = CodexRealtimeFeedbackWire.Build("v3", CodexRealtimeFeedbackKind.Progress, "thinking", "handoff", null);
        Assert.Null(Assert.Single(thinking.Messages)["channel"]);
    }
}
