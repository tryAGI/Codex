using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexHookRuntimeTests
{
    private static JsonElement Json(string value)
    {
        using var doc = JsonDocument.Parse(value);
        return doc.RootElement.Clone();
    }

    private static CodexHookContext Context(string name = "PreToolUse") =>
        new("account", "connection", "thread", "turn", "event", name, "/workspace", "env", "Bash", "tool");
    private static CodexHookDefinition Hook(string id, long order = 0, string name = "PreToolUse",
        string mode = "sync", string trust = "trusted", bool builtin = false, string? environment = null) =>
        new(id, name, "command", "/hooks/config.json", "project", trust, order,
            ExecutionMode: mode, Builtin: builtin, EnvironmentId: environment);
    private static JsonElement Input => Json("""{"tool_input":{"command":"before"}}""");

    [Fact]
    public async Task OrderedHooksPublishCorrelatedLifecycleAndReadmitRewrittenInput()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new List<(string Method, JsonElement Body)>();
        var calls = new List<CodexHookInvocation>();
        var admitted = new List<JsonElement>();
        var runtime = new CodexHookRuntime((_, _) => ValueTask.FromResult(true), (call, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add(call);
            return Task.FromResult(new CodexHookExecutionResult(0, call.Definition.Id == "first"
                ? Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":{"command":"after"},"additionalContext":"context"}}""")
                : Json("{}")));
        }, (method, body, _) => { events.Add((method, body)); return Task.CompletedTask; });
        var outcome = await runtime.RunAsync(Context(), [Hook("second", 2), Hook("first", 1)], Input,
            (_, input, token) => { token.ThrowIfCancellationRequested(); admitted.Add(input); return ValueTask.FromResult(true); }, ct);
        Assert.True(outcome.CanProceed);
        Assert.Equal("allow", outcome.PermissionAdvice);
        Assert.Equal("after", outcome.UpdatedInput!.Value.GetProperty("command").GetString());
        Assert.Equal("after", calls[1].Payload.GetProperty("tool_input").GetProperty("command").GetString());
        Assert.Single(admitted);
        Assert.Equal("context", Assert.Single(outcome.AdditionalContexts));
        Assert.Equal(new[] {"hook/started","hook/completed","hook/started","hook/completed"}, events.Select(e => e.Method));
        Assert.Equal("thread", events[0].Body.GetProperty("threadId").GetString());
        Assert.Equal("event:first", events[0].Body.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal("preToolUse", events[0].Body.GetProperty("run").GetProperty("eventName").GetString());
        Assert.Equal("completed", events[1].Body.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RewriteWithoutAdmissionFailsClosedAndStopsFurtherHooks()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var runtime = new CodexHookRuntime((_, _) => ValueTask.FromResult(true), (_, _) =>
        {
            calls++;
            return Task.FromResult(new CodexHookExecutionResult(0,
                Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":{"command":"changed"}}}""")));
        }, (_, _, _) => Task.CompletedTask);
        var outcome = await runtime.RunAsync(Context(), [Hook("first"), Hook("second", 1)], Input,
            cancellationToken: ct);
        Assert.False(outcome.CanProceed);
        Assert.Null(outcome.UpdatedInput);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("untrusted", "sync")]
    [InlineData("modified", "sync")]
    [InlineData("trusted", "async")]
    public async Task UntrustedOrUnsupportedHooksCannotExecute(string trust, string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new CodexHookRuntime((_, _) => ValueTask.FromResult(true),
            (_, _) => throw new InvalidOperationException("Must not dispatch."), (_, _, _) => Task.CompletedTask);
        var outcome = await runtime.RunAsync(Context(), [Hook("blocked", trust: trust, mode: mode)], Input,
            cancellationToken: ct);
        Assert.True(outcome.Blocked);
        Assert.Equal("failed", Assert.Single(outcome.Runs).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedPermissions":{"unsafe":true}}}}""")]
    [InlineData("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedInput":{}}}}""")]
    [InlineData("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow"}}""")]
    [InlineData("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","interrupt":true}}}""")]
    public async Task PermissionHookRejectsUnsupportedRewritesAndWrongEvents(string output)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new CodexHookRuntime((_, _) => ValueTask.FromResult(true),
            (_, _) => Task.FromResult(new CodexHookExecutionResult(0, Json(output))), (_, _, _) => Task.CompletedTask);
        var outcome = await runtime.RunAsync(Context("PermissionRequest"), [Hook("permission", name: "PermissionRequest")],
            Input, cancellationToken: ct);
        Assert.True(outcome.Blocked);
        Assert.Null(outcome.PermissionAdvice);
    }

    [Fact]
    public async Task StopBlockRequestsMoreWorkRatherThanAuthorizingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new CodexHookRuntime((_, _) => ValueTask.FromResult(true),
            (_, _) => Task.FromResult(new CodexHookExecutionResult(0, Json("""{"decision":"block","reason":"More work"}"""))),
            (_, _, _) => Task.CompletedTask);
        var outcome = await runtime.RunAsync(Context("Stop"), [Hook("stop", name: "Stop")], Json("{}"), cancellationToken: ct);
        Assert.True(outcome.RequireMoreWork);
        Assert.False(outcome.Blocked);
        Assert.Equal("More work", outcome.Reason);
    }

    [Fact]
    public async Task EnvironmentAndInternalSubagentAreFencedAndBuiltinNotificationsHidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var notifications = 0;
        var runtime = new CodexHookRuntime((_, _) => ValueTask.FromResult(true),
            (_, _) => { calls++; return Task.FromResult(new CodexHookExecutionResult(0)); },
            (_, _, _) => { notifications++; return Task.CompletedTask; });
        var hooks = new[] { Hook("wrong", environment: "other"), Hook("builtin", builtin: true, environment: "env") };
        var outcome = await runtime.RunAsync(Context(), hooks, Input, cancellationToken: ct);
        Assert.Single(outcome.Runs);
        Assert.Equal(1, calls);
        Assert.Equal(0, notifications);
        Assert.Empty((await runtime.RunAsync(Context() with { IsInternalSubagent = true }, hooks, Input,
            cancellationToken: ct)).Runs);
    }

    [Fact]
    public async Task CancelledHookPublishesStoppedAndNeverReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var events = new List<JsonElement>();
        var runtime = new CodexHookRuntime((_, _) => ValueTask.FromResult(true), (_, token) =>
        {
            cancelled.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable");
        }, (_, body, _) => { events.Add(body); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RunAsync(Context(), [Hook("hook")], Input, cancellationToken: cancelled.Token));
        Assert.Equal(2, events.Count);
        Assert.Equal("stopped", events[1].GetProperty("run").GetProperty("status").GetString());
    }
}
