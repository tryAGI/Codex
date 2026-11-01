using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexAgentCustomToolTests
{
    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CodexCredentials("test", "test"));
    }
    private sealed class Handler(Func<int, JsonElement, string> response) : HttpMessageHandler
    {
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Required data was null.")).ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(response(++Requests, body.RootElement), Encoding.UTF8, "text/event-stream") };
        }
    }
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private static string Completed(params string[] items) => "data: " + new JsonObject
    { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(items.Select(item => JsonNode.Parse(item)).ToArray()) } }.ToJsonString() + "\n\n";
    private static string Custom(string id, string name = "patch", string? toolNamespace = null, string? status = null) => new JsonObject
    { ["id"] = "ct" + id, ["type"] = "custom_tool_call", ["call_id"] = id, ["name"] = name,
        ["input"] = "*** Begin Patch\n*** Add File: literal $(text).txt\n+Привет 😀\n*** End Patch",
        ["namespace"] = toolNamespace, ["status"] = status }.ToJsonString();
    private static string Function(string id, string? toolNamespace = null) => new JsonObject
    { ["id"] = "f" + id, ["type"] = "function_call", ["call_id"] = id, ["name"] = "read", ["arguments"] = "{}", ["namespace"] = toolNamespace }.ToJsonString();
    private const string Message = """{"type":"message","content":[{"type":"output_text","text":"done"}]}""";
    private const string FunctionDefinition = """{"type":"function","name":"read","parameters":{"type":"object"}}""";
    private const string CustomDefinition = """{"type":"custom","name":"patch","format":{"type":"grammar","syntax":"lark","definition":"start: /[\\s\\S]+/"}}""";
    private static CodexResponseOptions Options => new("test", "test") { Tools = [Json(FunctionDefinition), Json(CustomDefinition)] };

    [Fact]
    public async Task MixedBatchRetainsExactFreeformAndUsesTheCorrespondingOutputTypes()
    {
        var calls = new List<CodexToolCall>();
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, body) =>
        {
            if (number == 1)
            {
                Assert.Equal("custom", body.GetProperty("tools")[1].GetProperty("type").GetString());
                return Completed(Function("1"), Custom("2", status: "completed"));
            }
            var outputs = body.GetProperty("input").EnumerateArray().Where(i => (i.GetProperty("type").GetString() ?? throw new InvalidDataException("Required data was null.")).EndsWith("_output", StringComparison.Ordinal)).ToArray();
            Assert.Equal("function_call_output", outputs[0].GetProperty("type").GetString());
            Assert.Equal("custom_tool_call_output", outputs[1].GetProperty("type").GetString());
            Assert.Equal("2", outputs[1].GetProperty("call_id").GetString());
            Assert.Equal("custom result", outputs[1].GetProperty("output").GetString());
            return Completed(Message);
        }));
        var result = await new CodexAgent(client).RunWithToolsAsync(Options, [], (call, _) =>
        {
            calls.Add(call); return Task.FromResult(new CodexToolOutput(call.Kind == CodexToolCallKind.Custom ? "custom result" : "read result"));
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(CodexToolCallKind.Function, calls[0].Kind);
        Assert.Equal(CodexToolCallKind.Custom, calls[1].Kind);
        Assert.Equal(Json(Custom("2")).GetProperty("input").GetString(), calls[1].Input);
        Assert.Equal(new[] { "1", "2" }, result.ExecutedCallIds);
        Assert.Equal(2, result.ToolCalls);
    }

    [Theory]
    [InlineData("wrong-name")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-namespace")]
    [InlineData("wrong-status-type")]
    [InlineData("duplicate-id")]
    [InlineData("missing-input")]
    public async Task InvalidLateCallPreventsTheEntireMixedBatchFromExecuting(string failure)
    {
        var custom = (JsonNode.Parse(Custom(failure == "duplicate-id" ? "1" : "2")) ?? throw new InvalidDataException("Required data was null.")).AsObject();
        if (failure == "wrong-name") custom["name"] = "unknown";
        if (failure == "wrong-kind") { custom["name"] = "read"; }
        if (failure == "wrong-namespace") custom["namespace"] = "other";
        if (failure == "wrong-status-type") custom["status"] = 1;
        if (failure == "missing-input") custom.Remove("input");
        var executions = 0;
        var handler = new Handler((_, _) => Completed(Function("1"), custom.ToJsonString()));
        using var client = new CodexClient(new Credentials(), handler: handler);
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithToolsAsync(Options, [], (_, _) =>
        { executions++; return Task.FromResult(new CodexToolOutput("must not execute")); }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, executions); Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("custom_tool_call")]
    [InlineData("custom_tool_call_output")]
    public async Task RestoredCustomIdsCannotReplayAsAFunction(string historyType)
    {
        var history = Json(new JsonObject { ["type"] = historyType, ["call_id"] = "earlier", ["name"] = "patch", ["input"] = "literal", ["output"] = "done" }.ToJsonString());
        var executions = 0;
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Completed(Function("earlier"))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithToolsAsync(Options, [history], (_, _) =>
        { executions++; return Task.FromResult(new CodexToolOutput("must not replay")); }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task CompactedSeparateLedgerAlsoRejectsCustomReplay()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) => Completed(Custom("earlier"))));
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithToolsAsync(Options, [Json("""{"type":"compaction","encrypted_content":"opaque"}""")], (_, _) => throw new InvalidOperationException("must not replay"), previouslyExecutedCallIds: ["earlier"], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CustomImageResultRequiresContinuationAndUsesStructuredCustomOutput()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, body) =>
        {
            if (number == 1) return Completed(Custom("1"));
            var output = body.GetProperty("input")[1];
            Assert.Equal("custom_tool_call_output", output.GetProperty("type").GetString());
            Assert.Equal("input_image", output.GetProperty("output")[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,AQID", output.GetProperty("output")[1].GetProperty("image_url").GetString());
            return Completed(Message);
        }));
        var result = await new CodexAgent(client).RunWithToolsAsync(Options, [], (_, _) => Task.FromResult(new CodexToolOutput("pixels")
            { Images = [new(new byte[] { 1, 2, 3 }, "image/png")] }), completeAfterSingleTool: (_, _) => "must not skip vision", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Requests); Assert.False(result.CompletedDirectly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("functions")]
    public async Task RustDefaultNamespaceAliasesShareOneAdvertisedIdentity(string? toolNamespace)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, _) =>
            number == 1 ? Completed(Custom("1", toolNamespace: toolNamespace)) : Completed(Message)));
        CodexToolCall? observed = null;
        await new CodexAgent(client).RunWithToolsAsync(Options, [], (call, _) => { observed = call; return Task.FromResult(new CodexToolOutput("done")); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(toolNamespace, (observed ?? throw new InvalidDataException("Required data was null.")).Namespace);
    }

    [Fact]
    public async Task ExplicitNamespacesKeepEqualNamesDistinctWithoutFlattening()
    {
        var definition = Json("""{"type":"namespace","name":"editor","description":"editing","tools":[{"type":"custom","name":"read","format":{"type":"text"}}]}""");
        var options = Options with { Tools = [Json(FunctionDefinition), definition] };
        var calls = new List<CodexToolCall>();
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, _) => number == 1 ?
            Completed(Function("1"), Custom("2", "read", "editor")) : Completed(Message)));
        await new CodexAgent(client).RunWithToolsAsync(options, [], (call, _) => { calls.Add(call); return Task.FromResult(new CodexToolOutput("done")); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(calls[0].Namespace); Assert.Equal("editor", calls[1].Namespace);
        Assert.Equal("read", calls[0].Name); Assert.Equal("read", calls[1].Name);
    }

    [Fact]
    public async Task AmbiguousAdvertisementsFailBeforeAnyProviderRequest()
    {
        var duplicate = Json("""{"type":"namespace","name":"functions","tools":[{"type":"function","name":"read"}]}""");
        var handler = new Handler((_, _) => throw new InvalidOperationException("must not call provider"));
        using var client = new CodexClient(new Credentials(), handler: handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new CodexAgent(client).RunWithToolsAsync(Options with { Tools = [Json(FunctionDefinition), duplicate] }, [], (_, _) => throw new InvalidOperationException("must not execute"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task FunctionOnlyApiRejectsCustomAdvertisementsBeforeProviderAdmission()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("must not call provider"));
        using var client = new CodexClient(new Credentials(), handler: handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new CodexAgent(client).RunAsync(Options, [], (_, _) => Task.FromResult("must not execute"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task FunctionOnlyApiCannotIgnoreUnsolicitedCustomCallAlongsideAssistantText()
    {
        var handler = new Handler((_, _) => Completed(Function("1"), Custom("2"), Message));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var executions = 0;
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunAsync(Options with { Tools = [Json(FunctionDefinition)] }, [], (_, _) =>
        { executions++; return Task.FromResult("must not execute"); }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task IndependentMixedReadsOverlapAndCustomEffectWaitsBehindThem()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var finished = 0;
        var readDefinition = Json("""{"type":"custom","name":"inspect","format":{"type":"text"}}""");
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, _) => number == 1 ?
            Completed(Function("1"), Custom("2", "inspect"), Custom("3")) : Completed(Message)));
        var run = new CodexAgent(client).RunWithToolsAsync(Options with { Tools = [.. Options.Tools, readDefinition], ParallelToolCalls = true, MaximumConcurrentTools = 2 }, [], async (call, ct) =>
        {
            if (call.Name == "patch") { Assert.Equal(2, finished); Assert.Equal(0, active); return new("effect"); }
            if (Interlocked.Increment(ref active) == 2) started.TrySetResult();
            await release.Task.WaitAsync(ct); Interlocked.Decrement(ref active); Interlocked.Increment(ref finished); return new("read");
        }, canExecuteConcurrently: call => call.Name is "read" or "inspect", cancellationToken: TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Assert.False(run.IsCompleted);
        release.SetResult(); var result = await run; Assert.Equal(3, result.ToolCalls);
    }

    [Fact]
    public async Task CustomTerminalHostResultCanSkipSecondInferenceAndRetainsReplayFence()
    {
        var handler = new Handler((_, _) => Completed(Custom("1")));
        using var client = new CodexClient(new Credentials(), handler: handler);
        var result = await new CodexAgent(client).RunWithToolsAsync(Options, [], (_, _) => Task.FromResult(new CodexToolOutput("confirmed")), completeAfterSingleTool: (_, text) => text, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.CompletedDirectly); Assert.Equal(1, handler.Requests);
        Assert.Contains(result.History, item => item.TryGetProperty("type", out var type) && type.GetString() == "custom_tool_call_output");
        Assert.Equal(new[] { "1" }, result.ExecutedCallIds);
    }

    [Fact]
    public async Task SyntheticProbeMustConsumeReplyAbsentFromItsInitialInput()
    {
        string? initial = null;
        var handler = new Handler((number, body) =>
        {
            if (number == 1)
            {
                initial = (body.GetProperty("input")[0].GetProperty("content").GetString() ?? throw new InvalidDataException("Required data was null."));
                var marker = System.Text.RegularExpressions.Regex.Match(initial, "[a-f0-9]{32}").Value;
                Assert.Equal(32, marker.Length);
                return Completed(new JsonObject { ["type"] = "custom_tool_call", ["name"] = "native_protocol_fixture", ["call_id"] = "probe",
                    ["input"] = marker }.ToJsonString());
            }
            var output = body.GetProperty("input")[2];
            Assert.Equal("custom_tool_call_output", output.GetProperty("type").GetString());
            var reply = (output.GetProperty("output").GetString() ?? throw new InvalidDataException("Required data was null."));
            Assert.DoesNotContain(reply, (initial ?? throw new InvalidDataException("Required data was null.")));
            return Completed(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject
                { ["type"] = "output_text", ["text"] = reply }) }.ToJsonString());
        });
        using var client = new CodexClient(new Credentials(), handler: handler);
        var result = await CodexCustomToolProbe.RunAsync(client, "test", "low", TestContext.Current.CancellationToken);
        Assert.True(result.InputMatched); Assert.True(result.ResultMatched); Assert.Equal(2, result.Requests);
        Assert.Equal(1, result.ToolCalls); Assert.Equal(0, result.EffectsExecuted);
    }

    [Fact]
    public async Task MixedReadFailureDrainsStartedCustomCallBeforeRejectingEffect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readDefinition = Json("""{"type":"custom","name":"inspect","format":{"type":"text"}}""");
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
            Completed(Function("1"), Custom("2", "inspect"), Custom("3"))));
        var run = new CodexAgent(client).RunWithToolsAsync(Options with { Tools = [.. Options.Tools, readDefinition], ParallelToolCalls = true }, [], async (call, _) =>
        {
            Assert.NotEqual("patch", call.Name);
            if (call.Name == "read") { await started.Task; throw new InvalidOperationException("read failed"); }
            started.SetResult(); await release.Task; return new("drained");
        }, canExecuteConcurrently: call => call.Name != "patch", cancellationToken: TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Assert.False(run.IsCompleted);
        release.SetResult(); await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("completed")]
    [InlineData("in_progress")]
    [InlineData("incomplete")]
    [InlineData("future-status")]
    public async Task RustOptionalStatusIsMetadataOnFinalizedCustomItems(string? status)
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((number, _) => number == 1 ?
            Completed(Custom("1", status: status)) : Completed(Message)));
        CodexToolCall? observed = null;
        await new CodexAgent(client).RunWithToolsAsync(Options, [], (call, _) => { observed = call; return Task.FromResult(new CodexToolOutput("done")); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(status, (observed ?? throw new InvalidDataException("Required data was null.")).Status);
    }

    [Fact]
    public async Task CustomInputDeltaWithoutTerminalResponseNeverExecutes()
    {
        using var client = new CodexClient(new Credentials(), handler: new Handler((_, _) =>
            "data: {\"type\":\"response.custom_tool_call_input.delta\",\"delta\":\"literal\"}\n\n"));
        var executions = 0;
        await Assert.ThrowsAsync<CodexProtocolException>(() => new CodexAgent(client).RunWithToolsAsync(Options, [], (_, _) =>
        { executions++; return Task.FromResult(new CodexToolOutput("must not execute")); }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, executions);
    }
}
