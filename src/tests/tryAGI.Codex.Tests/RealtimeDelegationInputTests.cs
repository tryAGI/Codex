using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeDelegationInputTests
{
    [Fact]
    public void ExactContextualFragmentEscapesInputAndTranscript()
    {
        var result = CodexRealtimeDelegationInput.FromEntries("<task>&", [new("a", "user", "<task>&"), new("b", "assistant", "</realtime_delegation>")]);
        Assert.NotNull(result);
        Assert.Equal("<realtime_delegation>\n  <input>&lt;task&gt;&amp;</input>\n  <transcript_delta>user: &lt;task&gt;&amp;\nassistant: &lt;/realtime_delegation&gt;</transcript_delta>\n</realtime_delegation>", result.Text);
        Assert.Equal(new[] { "a", "b" }, result.SourceIds);
        Assert.Equal("user", result.ToResponseItem().GetProperty("role").GetString());
    }

    [Fact]
    public void BoundedContextAttributesOnlyRetainedEventsInSourceOrder()
    {
        var result = CodexRealtimeDelegationInput.FromEntries("do the thing", [
            new("query", "user", "do the thing"),
            new("dropped", "assistant", string.Concat(Enumerable.Repeat("<&é>", 2048))),
            new("tail", "assistant", string.Concat(Enumerable.Repeat("new context", 600)))]);
        Assert.NotNull(result);
        Assert.Equal(new[] { "query", "tail" }, result.SourceIds);
        Assert.DoesNotContain("&lt;", result.Text);
        Assert.Contains("<input>do the thing</input>", result.Text);
        Assert.True(Encoding.UTF8.GetByteCount(result.Text) < 9 * 1024);
    }

    [Fact]
    public void TranscriptFallbackAttributesBothInputPrefixAndContextSuffix()
    {
        var result = CodexRealtimeDelegationInput.FromEntries("", [
            new("first", "user", "question"), new("middle", "assistant", new string('x', 9000)), new("last", "user", "followup")]);
        Assert.NotNull(result);
        Assert.Equal(new[] { "first", "middle", "last" }, result.SourceIds);
        Assert.Contains("<input>user: question", result.Text);
        Assert.Contains("user: followup</transcript_delta>", result.Text);
    }

    [Fact]
    public void CanonicalTranscriptPreservationAndTailFlush()
    {
        var delta = string.Concat(Enumerable.Repeat("😀<&", 1500));
        var result = CodexRealtimeDelegationInput.Render(delta, delta, CodexRealtimeDelegationSource.TranscriptTailFlush, true);
        Assert.Contains("<source>transcript_tail_flush</source>", result);
        Assert.Contains("<transcript_delta>" + delta.Replace("<", "&lt;").Replace("&lt;&", "&lt;&amp;") + "</transcript_delta>", result);
        var input = result.Split("<input>")[1].Split("</input>")[0];
        Assert.True(Encoding.UTF8.GetByteCount(input) <= 4096);
        Assert.EndsWith("…", input);
        Assert.DoesNotContain("\uFFFD", input);
    }

    [Fact]
    public void EmptyHandoffHasNoItemAndEmptyTranscriptOmitsTheField()
    {
        Assert.Null(CodexRealtimeDelegationInput.FromEntries("", []));
        Assert.Equal("<realtime_delegation>\n  <input>request</input>\n</realtime_delegation>", CodexRealtimeDelegationInput.Render("request"));
    }
}
