using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexAsyncUserMessageTests
{
    private static CodexAsyncUserMessagePolicy Policy(bool restricted = false, bool questions = true,
        bool feature = false, params string[] capabilities) =>
        new(restricted, questions, feature, capabilities);
    private static readonly CodexAsyncUserMessagePolicy Enabled = Policy(capabilities:
        [CodexAsyncUserMessageTools.RequestUserInput, CodexAsyncUserMessageTools.SendMessage]);

    private static CodexToolCall Call(string name, string input) => new("call", name, input, CodexToolCallKind.Function);

    [Theory]
    [InlineData(false, true, false, "request_user_input_async", true, false)]
    [InlineData(false, true, false, "send_user_message_async", true, false)]
    [InlineData(false, false, false, "send_user_message_async", false, false)]
    [InlineData(false, true, false, "send_message_to_user_async", false, true)]
    [InlineData(false, true, true, "", false, true)]
    [InlineData(false, true, false, "", false, false)]
    [InlineData(true, true, true, "request_user_input_async", false, false)]
    [InlineData(true, true, true, "send_message_to_user_async", false, false)]
    public void RegistrationMatchesRootFeatureAndCatalogPolicy(bool restricted, bool questions, bool feature,
        string capability, bool expectQuestion, bool expectMessage)
    {
        var registered = CodexAsyncUserMessageTools.Register(Policy(restricted, questions, feature,
            capability.Length == 0 ? [] : [capability]));
        Assert.Equal(expectQuestion, registered.Any(tool => tool.Identity.Name == CodexAsyncUserMessageTools.RequestUserInput));
        Assert.Equal(expectMessage, registered.Any(tool => tool.Identity.Name == CodexAsyncUserMessageTools.SendMessage));
        Assert.All(registered, tool => Assert.Equal(CodexToolExposure.DirectModelOnly, tool.Exposure));
        var registry = new CodexToolRegistry(registered);
        Assert.Empty(registry.CodeModeTools());
        Assert.Equal(registered.Count, CodexToolCalls.Advertised(registry.CreateModelTools(), allowCustom: true).Count);
    }

    [Fact]
    public async Task QuestionsPublishAsyncAgentMessageAndReturnAcceptedWithoutWaitingForAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = Call(CodexAsyncUserMessageTools.RequestUserInput, """
            {"questions":[{"title":"Which environment?","options":["Staging","Production"]},{"title":"What deadline?"}]}
            """);
        var publications = new List<CodexAsyncUserMessagePublication>();
        var output = await CodexAsyncUserMessageTools.ExecuteAsync(Enabled, "thread", "turn", call, (publication, token) =>
        {
            token.ThrowIfCancellationRequested();
            publications.Add(publication);
            return Task.FromResult(true);
        }, ct);
        var published = Assert.Single(publications);
        Assert.Equal("thread", published.ThreadId);
        Assert.Equal("turn", published.TurnId);
        Assert.Equal("call", published.CallId);
        var item = published.Item;
        Assert.Equal("agentMessage", item.GetProperty("type").GetString());
        Assert.Equal("call", item.GetProperty("id").GetString());
        Assert.Equal("final_answer", item.GetProperty("phase").GetString());
        Assert.Equal("async", item.GetProperty("delivery").GetString());
        Assert.Equal("Which environment?\n- Staging\n- Production\n\nWhat deadline?", item.GetProperty("text").GetString());
        Assert.Equal("Staging", item.GetProperty("questions")[0].GetProperty("options")[0].GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("questions")[1].GetProperty("options").ValueKind);
        Assert.Equal("""{"accepted":true}""", output.Text);
    }

    [Fact]
    public void FreeformMessageTrimsAndPreservesNoQuestionPayload()
    {
        var item = CodexAsyncUserMessageTools.Prepare(Enabled, "thread", "turn",
            Call(CodexAsyncUserMessageTools.SendMessage, """{"message":"  Important update.  "}""")).Item;
        Assert.Equal("Important update.", item.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("questions").ValueKind);
        Assert.Equal("async", item.GetProperty("delivery").GetString());
    }

    [Theory]
    [InlineData("request_user_input_async", """{"questions":[]}""")]
    [InlineData("request_user_input_async", """{"questions":[{"title":" "}]}""")]
    [InlineData("request_user_input_async", """{"questions":[{"title":"Q","options":[]}]}""")]
    [InlineData("request_user_input_async", """{"questions":[{"title":"Q","options":[" "]}]}""")]
    [InlineData("request_user_input_async", """{"questions":[{"title":"Q","unexpected":true}]}""")]
    [InlineData("request_user_input_async", """{"questions":[{"title":"Q"}],"approval":true}""")]
    [InlineData("send_message_to_user_async", """{"message":" "}""")]
    [InlineData("send_message_to_user_async", """{"message":"one","message":"two"}""")]
    public void InvalidArgumentsNeverPrepareAnItem(string name, string arguments) =>
        Assert.Throws<CodexProtocolException>(() =>
            CodexAsyncUserMessageTools.Prepare(Enabled, "thread", "turn", Call(name, arguments)));

    [Fact]
    public async Task RestrictedOrDeniedPublicationCannotReturnAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = Call(CodexAsyncUserMessageTools.SendMessage, """{"message":"update"}""");
        Assert.Throws<UnauthorizedAccessException>(() =>
            CodexAsyncUserMessageTools.Prepare(Enabled with { RootCapabilitiesRestricted = true }, "thread", "turn", call));
        Assert.Throws<UnauthorizedAccessException>(() =>
            CodexAsyncUserMessageTools.Prepare(Enabled, "thread", "turn", call with { Namespace = "other" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CodexAsyncUserMessageTools.ExecuteAsync(Enabled, "thread", "turn", call, (_, _) => Task.FromResult(false), ct));
    }
}
