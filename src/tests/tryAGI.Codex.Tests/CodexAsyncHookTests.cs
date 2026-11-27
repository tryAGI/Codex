using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexAsyncHookTests
{
    private static readonly CodexAsyncHookScope Scope = new("account", "connection", "thread", "authority-1");
    private static CodexHookContext Context(string name = "PreToolUse", string eventId = "event") =>
        new("account", "connection", "thread", "turn", eventId, name, "/workspace", "env", "Bash", "tool");
    private static CodexHookDefinition Hook(string id, string mode = "async", string trust = "trusted", string name = "PreToolUse") =>
        new(id, name, "command", "/hooks/config.json", "project", trust, 0, ExecutionMode: mode);
    private static JsonElement Json(string value)
    {
        using var doc = JsonDocument.Parse(value);
        return doc.RootElement.Clone();
    }
    private static JsonElement Input => Json("""{"tool_input":{"command":"before"}}""");
    private static CodexHookRuntime Runtime(CodexAsyncHookQueue queue,
        Func<CodexHookInvocation, CancellationToken, Task<CodexHookExecutionResult>> execute,
        Func<string, JsonElement, CancellationToken, Task>? notify = null,
        Func<CodexHookInvocation, CancellationToken, ValueTask<bool>>? authorize = null) =>
        new(authorize ?? ((_, _) => ValueTask.FromResult(true)), execute,
            notify ?? ((_, _, _) => Task.CompletedTask), null, queue);

    [Fact]
    public async Task AsyncHookDoesNotBlockOrRewriteSynchronousGateAndDrainsOnlySafeOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var queue = new CodexAsyncHookQueue(Scope);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        var runtime = Runtime(queue, async (call, token) =>
        {
            if (call.Definition.ExecutionMode == "sync") return new(0);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(0, Json("""
                {"continue":false,"systemMessage":"warning","hookSpecificOutput":{"hookEventName":"PreToolUse",
                "permissionDecision":"deny","updatedInput":{"command":"unsafe"},"additionalContext":"context"}}
                """));
        }, (_, _, _) => { notifications++; return Task.CompletedTask; });
        var outcome = await runtime.RunAsync(Context(), [Hook("async"), Hook("sync", "sync")], Input,
            (_, _, _) => throw new InvalidOperationException("Async rewrite cannot be admitted."), ct);
        await started.Task.WaitAsync(ct);
        Assert.True(outcome.CanProceed);
        Assert.Null(outcome.UpdatedInput);
        Assert.Null(outcome.PermissionAdvice);
        Assert.Equal(2, notifications);
        Assert.Empty(queue.Drain(Scope));
        release.TrySetResult();
        await queue.WaitForPendingAsync(ct);
        var result = Assert.Single(queue.Drain(Scope));
        Assert.Equal("turn", result.Context.TurnId);
        Assert.Equal("async", result.Run.GetProperty("executionMode").GetString());
        Assert.Equal("completed", result.Run.GetProperty("status").GetString());
        Assert.Equal("context", Assert.Single(result.AdditionalContexts));
        Assert.Contains("warning", result.Warnings);
        Assert.Contains("Asynchronous hook control effects were ignored.", result.Warnings);
        Assert.Equal(2, notifications);
        Assert.Empty(queue.Drain(Scope));
    }

    [Fact]
    public async Task DeniedHookNeverExecutesAndCapacityDoesNotChangeProtectedDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var queue = new CodexAsyncHookQueue(Scope, maximumOutstanding: 1);
        var runtime = Runtime(queue, (_, _) => throw new InvalidOperationException("Must not dispatch."),
            authorize: (_, _) => ValueTask.FromResult(false));
        var outcome = await runtime.RunAsync(Context(), [Hook("denied"), Hook("overflow")], Input, cancellationToken: ct);
        Assert.True(outcome.CanProceed);
        Assert.Equal("failed", Assert.Single(outcome.Runs).GetProperty("status").GetString());
        await queue.WaitForPendingAsync(ct);
        var failed = Assert.Single(queue.Drain(Scope));
        Assert.Equal("failed", failed.Run.GetProperty("status").GetString());
        Assert.Empty(failed.AdditionalContexts);
    }

    [Fact]
    public async Task DuplicateRunIsNotReplayedAndConflictCannotAlias()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var queue = new CodexAsyncHookQueue(Scope);
        var calls = 0;
        var runtime = Runtime(queue, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new CodexHookExecutionResult(0));
        });
        await runtime.RunAsync(Context(), [Hook("once")], Input, cancellationToken: ct);
        await queue.WaitForPendingAsync(ct);
        Assert.Single(queue.Drain(Scope));
        await runtime.RunAsync(Context(), [Hook("once")], Input, cancellationToken: ct);
        await queue.WaitForPendingAsync(ct);
        Assert.Empty(queue.Drain(Scope));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<CodexProtocolException>(() => runtime.RunAsync(Context(), [Hook("once")],
            Json("""{"tool_input":{"command":"changed"}}"""), cancellationToken: ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task QueueRejectsCrossScopeAndRevocationDiscardsLateCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var queue = new CodexAsyncHookQueue(Scope);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<CodexHookExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = Runtime(queue, async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            return await late.Task.WaitAsync(ct);
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.RunAsync(
            Context() with { AccountId = "other" }, [Hook("once")], Input, cancellationToken: ct));
        await runtime.RunAsync(Context(), [Hook("once")], Input, cancellationToken: ct);
        await started.Task.WaitAsync(ct);
        Assert.Throws<UnauthorizedAccessException>(() => queue.Drain(Scope with { AuthorityRevision = "revoked" }));
        await queue.DisposeAsync();
        await cancelled.Task.WaitAsync(ct);
        late.TrySetResult(new(0, Json("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"stale"}}""")));
        Assert.Throws<ObjectDisposedException>(() => queue.Drain(Scope));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.RunAsync(Context(eventId: "next"),
            [Hook("once")], Input, cancellationToken: ct));
    }

    [Fact]
    public async Task AsyncExitCodeAndStopDecisionNeverControlTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var queue = new CodexAsyncHookQueue(Scope);
        var runtime = Runtime(queue, (call, _) => Task.FromResult(call.Definition.Id == "exit"
            ? new CodexHookExecutionResult(2)
            : new CodexHookExecutionResult(0, Json("""{"decision":"block","reason":"More work"}"""))));
        var outcome = await runtime.RunAsync(Context("Stop"), [Hook("exit", name: "Stop"), Hook("block", name: "Stop")],
            Json("{}"), cancellationToken: ct);
        Assert.True(outcome.CanProceed);
        Assert.False(outcome.RequireMoreWork);
        await queue.WaitForPendingAsync(ct);
        var results = queue.Drain(Scope);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.Run.GetProperty("status").GetString() == "failed");
        Assert.Contains(results, result => result.Warnings.Contains("Asynchronous hook control effects were ignored."));
    }
}
