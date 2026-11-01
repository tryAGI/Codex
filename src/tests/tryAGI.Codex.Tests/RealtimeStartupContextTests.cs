using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeStartupContextTests
{
    [Fact]
    public void NewestConversationAndEveryBackgroundSectionAreBounded()
    {
        var history = Enumerable.Range(0, 40).SelectMany(i => new[] {
            new CodexRealtimeInitialItem("user", $"question-{i}: " + new string('я', 800)),
            new CodexRealtimeInitialItem("assistant", "answer-" + i) }).ToArray();
        var context = CodexRealtimeStartupContext.Build(history, new string('р', 20000), "worker workspace");
        Assert.Contains("question-39", context);
        Assert.DoesNotContain("question-0:", context);
        Assert.Contains("## Recent work", context);
        Assert.Contains("## Machine and workspace", context);
        Assert.True(CodexRealtimeStartupContext.ApproximateTokens((context ?? throw new InvalidDataException("Required data was null."))) <= 5400);
    }
    [Theory]
    [InlineData("я😀終", 80)]
    [InlineData("abc", 16)]
    public void TruncationPreservesUnicodeAndCountsItsMarker(string seed, int tokens)
    {
        var source = string.Concat(Enumerable.Repeat(seed, 1000));
        var result = CodexRealtimeStartupContext.Truncate(source, tokens);
        Assert.True(CodexRealtimeStartupContext.ApproximateTokens(result) <= tokens);
        Assert.DoesNotContain('\uFFFD', result);
        Assert.Contains("truncated", result);
        Assert.EndsWith(seed[^1].ToString(), result, StringComparison.Ordinal);
    }
    [Fact]
    public void InitialHistoryIsRejectedBeforeWireSerialization()
    {
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("instructions") {
            InitialItems = Enumerable.Repeat(new CodexRealtimeInitialItem("user", "x"), 129).ToArray() }));
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.Session(new("instructions") {
            InitialItems = [new("user", new string('я', 16385))] }));
        var session = JsonSerializer.SerializeToElement(CodexRealtimeProtocol.Session(new("instructions") { InitialItems = [new("assistant", "spoken"), new("user", "heard")] }));
        Assert.Collection(session.GetProperty("initial_items").EnumerateArray(),
            item => Assert.Equal("output_text", item.GetProperty("content").EnumerateArray().Single().GetProperty("type").GetString()),
            item => Assert.Equal("input_text", item.GetProperty("content").EnumerateArray().Single().GetProperty("type").GetString()));
    }
    [Fact]
    public void HistoryImportIsImmutableAndValueComparable()
    {
        var source = new[] { new CodexRealtimeInitialItem("user", "hello") };
        var before = new CodexRealtimeInitialItems(source); source[0] = new("user", "changed");
        var after = (JsonSerializer.Deserialize<CodexRealtimeInitialItems>(JsonSerializer.Serialize(before)) ?? throw new InvalidDataException("Required data was null."));
        Assert.Equal(before, after); Assert.Equal("hello", before[0].Text);
    }
    [Fact]
    public void ReasoningFallbackExtractsConversationWithoutToolsOrEncryptedReasoning()
    {
        using var document = JsonDocument.Parse("""[{"type":"message","role":"user","content":[{"type":"input_text","text":"question"}]},{"type":"function_call","name":"action","arguments":"secret"},{"type":"reasoning","encrypted_content":"private"},{"type":"message","role":"developer","content":[{"type":"input_text","text":"authority"}]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]""");
        var messages = CodexRealtimeStartupContext.ConversationMessages(document.RootElement.EnumerateArray());
        Assert.Equal(2, messages.Count);
        var context = (CodexRealtimeStartupContext.Build(messages) ?? throw new InvalidDataException("Required data was null."));
        Assert.Contains("question", context); Assert.Contains("answer", context);
        Assert.DoesNotContain("secret", context); Assert.DoesNotContain("private", context); Assert.DoesNotContain("authority", context);
    }
    [Fact]
    public void EmptyContextDoesNotAddInstructionsAndDeveloperMessagesAreExcluded()
    {
        Assert.Null(CodexRealtimeStartupContext.Build([]));
        Assert.Null(CodexRealtimeStartupContext.Build([new("developer", "do not restore as conversation")]));
    }
}
