using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexCodeModeSessionTests
{
    private static readonly CodexCodeModeScope Scope = new("account", "connection", "thread", "environment");
    private static JsonElement Json(string value)
    {
        using var doc = JsonDocument.Parse(value);
        return doc.RootElement.Clone();
    }
    private static CodexRegisteredTool Tool(string name, CodexToolExposure exposure = CodexToolExposure.Direct,
        string description = "original", string ns = "functions", string type = "function") =>
        new(new(ns, name), Json($$"""{"type":"{{type}}","name":"{{name}}","description":"{{description}}"}"""), exposure);
    private static CodexToolRegistry Registry(params CodexRegisteredTool[] tools) => new(tools);
    private static ValueTask<bool> Allow(CodexCodeModeInvocation request, CancellationToken ct) => ValueTask.FromResult(true);
    private static Task<CodexToolOutput> Echo(CodexCodeModeInvocation request, CancellationToken ct) => Task.FromResult(new CodexToolOutput(request.Callback.Tool.Input));
    private static CodexCodeModeAdmission Prepare(CodexCodeModeSession session, string id, CodexToolRegistry registry,
        Func<CodexCodeModeInvocation, CancellationToken, ValueTask<bool>>? authorize = null,
        Func<CodexCodeModeInvocation, CancellationToken, Task<CodexToolOutput>>? execute = null) =>
        session.Prepare(id, "turn-" + id, "provider-" + id, registry, DateTimeOffset.UtcNow.AddMinutes(5),
            authorize ?? Allow, execute ?? Echo);
    private static CodexCodeModeCallback Callback(string id, string cell, string tool = "echo", string ns = "functions",
        long generation = 1, Guid? invocation = null, CodexToolCallKind kind = CodexToolCallKind.Function) =>
        new(generation, id, cell, "provider-" + id, invocation ?? Guid.NewGuid(),
            new CodexToolCall("nested", tool, "{}", kind) { Namespace = ns });

    [Fact]
    public void CatalogReferenceRequiresAcknowledgmentAndConcurrentAdmissionIsSelfContained()
    {
        using var session = new CodexCodeModeSession(Scope);
        var registry = Registry(Tool("echo"));
        var first = Prepare(session, "first", registry);
        Assert.False(first.ReusesCatalog);
        Assert.Equal(1, first.CatalogRevision);
        Assert.Single(first.ToolsToSend);
        var concurrent = Prepare(session, "concurrent", registry);
        Assert.Null(concurrent.CatalogRevision);
        Assert.Single(concurrent.ToolsToSend);
        Assert.True(session.CloseExecution(1, "first"));
        var second = Prepare(session, "second", registry);
        Assert.Equal(2, second.CatalogRevision);
        Assert.Single(second.ToolsToSend);
        Assert.Equal("cell-second", session.Acknowledge(1, "second", "cell-second", 2));
        var hit = Prepare(session, "hit", registry);
        Assert.True(hit.ReusesCatalog);
        Assert.Equal(2, hit.CatalogRevision);
        Assert.Empty(hit.ToolsToSend);
        session.Acknowledge(1, "hit", "cell-hit", 2);
        var changed = Prepare(session, "changed", Registry(Tool("echo", description: "changed")));
        Assert.False(changed.ReusesCatalog);
        Assert.Equal(3, changed.CatalogRevision);
    }

    [Fact]
    public void ReplacementCancellationInvalidatesPreviouslyAcknowledgedCatalog()
    {
        using var session = new CodexCodeModeSession(Scope);
        var original = Registry(Tool("echo"));
        Prepare(session, "original", original);
        session.Acknowledge(1, "original", "original-cell", 1);
        var replacement = Prepare(session, "replacement", Registry(Tool("other")));
        Assert.Equal(2, replacement.CatalogRevision);
        session.CloseExecution(1, "replacement");
        var next = Prepare(session, "next", original);
        Assert.Equal(3, next.CatalogRevision);
        Assert.False(next.ReusesCatalog);
    }

    [Fact]
    public async Task EarlyCallbackUsesOwnDelegateAndAcknowledgmentMustMatchCellAndRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = new CodexCodeModeSession(Scope);
        var registry = Registry(Tool("echo"));
        Prepare(session, "a", registry, execute: (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(Scope, request.Scope);
            Assert.Equal("turn-a", request.TurnId);
            return Task.FromResult(new CodexToolOutput("a"));
        });
        Prepare(session, "b", registry, execute: (_, _) => Task.FromResult(new CodexToolOutput("b")));
        Assert.Equal("a", (await session.InvokeToolAsync(Callback("a", "cell-a"), ct)).Text);
        Assert.Equal("b", (await session.InvokeToolAsync(Callback("b", "cell-b"), ct)).Text);
        Assert.Throws<CodexProtocolException>(() => session.Acknowledge(1, "a", "cell-b", 1));
        Assert.Throws<CodexProtocolException>(() => session.Acknowledge(1, "a", "cell-a", 99));
        Assert.Equal("cell-a", session.Acknowledge(1, "a", "cell-a", 1));
        Assert.Equal("cell-b", session.Acknowledge(1, "b", "cell-b", null));
    }

    [Fact]
    public async Task RegistryVisibilityAndToolKindAreNotImplicitExecutionPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = new CodexCodeModeSession(Scope);
        var authorizationCalls = 0;
        Prepare(session, "execution", Registry(Tool("echo"), Tool("model", CodexToolExposure.DirectModelOnly),
            Tool("secret", CodexToolExposure.Hidden), Tool("custom", CodexToolExposure.CodeModeOnly, type: "custom")),
            authorize: (_, _) => { authorizationCalls++; return ValueTask.FromResult(false); },
            execute: (_, _) => throw new InvalidOperationException("Denied call must not execute."));
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.InvokeToolAsync(Callback("execution", "cell", "model"), ct));
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.InvokeToolAsync(Callback("execution", "cell", "secret"), ct));
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.InvokeToolAsync(Callback("execution", "cell", ns: "other"), ct));
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.InvokeToolAsync(Callback("execution", "cell", "custom"), ct));
        Assert.Equal(0, authorizationCalls);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.InvokeToolAsync(Callback("execution", "cell"), ct));
        Assert.Equal(1, authorizationCalls);
    }

    [Fact]
    public async Task DuplicateInvocationAndCancellationBeforeArrivalNeverDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = new CodexCodeModeSession(Scope);
        var dispatched = 0;
        Prepare(session, "execution", Registry(Tool("echo")), execute: (_, _) =>
        {
            dispatched++;
            return Task.FromResult(new CodexToolOutput("ok"));
        });
        var first = Callback("execution", "cell");
        Assert.Equal("ok", (await session.InvokeToolAsync(first, ct)).Text);
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.InvokeToolAsync(first, ct));
        var next = Callback("execution", "cell");
        Assert.True(session.CancelInvocation(1, "execution", next.InvocationId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InvokeToolAsync(next, ct));
        Assert.Equal(1, dispatched);
    }

    [Fact]
    public async Task ClosingCellRevokesPendingCallbacksAndDropsLateResults()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = new CodexCodeModeSession(Scope);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<CodexToolOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var testCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Prepare(session, "execution", Registry(Tool("echo")), execute: async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.SetResult();
            return await result.Task.WaitAsync(testCancellation.Token);
        });
        var pending = session.InvokeToolAsync(Callback("execution", "cell"), ct);
        await started.Task.WaitAsync(ct);
        session.Acknowledge(1, "execution", "cell", 1);
        Assert.True(session.CloseExecution(1, "execution", "cell"));
        await cancelled.Task.WaitAsync(ct);
        result.TrySetResult(new("late"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(session.CloseExecution(1, "execution", "cell"));
        Assert.Throws<InvalidOperationException>(() => Prepare(session, "execution", Registry(Tool("echo"))));
    }

    [Fact]
    public async Task ReconnectFencesOldCellsAndCallbacksWithoutReusingCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = new CodexCodeModeSession(Scope);
        var registry = Registry(Tool("echo"));
        Prepare(session, "execution", registry);
        session.Acknowledge(1, "execution", "same", 1);
        Assert.Equal("same", Assert.Single(session.Reconnect()));
        var next = Prepare(session, "execution", registry);
        Assert.Equal(2, next.Generation);
        Assert.False(next.ReusesCatalog);
        Assert.Equal("g2:same", session.Acknowledge(2, "execution", "same", 1));
        Assert.Throws<InvalidOperationException>(() => session.ResolveRemoteCell(2, "same"));
        Assert.Equal("same", session.ResolveRemoteCell(2, "g2:same"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeToolAsync(Callback("execution", "same"), ct));
        Assert.False(session.CloseExecution(1, "execution", "same"));
        Assert.Equal("{}", (await session.InvokeToolAsync(Callback("execution", "same", generation: 2), ct)).Text);
    }

    [Fact]
    public void EarlyCellClosureInvalidatesUnacknowledgedReplacement()
    {
        using var session = new CodexCodeModeSession(Scope);
        var registry = Registry(Tool("echo"));
        Prepare(session, "first", registry);
        Assert.True(session.CloseExecution(1, "first", "early-cell"));
        Assert.Throws<InvalidOperationException>(() => session.Acknowledge(1, "first", "early-cell", 1));
        var next = Prepare(session, "next", registry);
        Assert.Equal(2, next.CatalogRevision);
        Assert.False(next.ReusesCatalog);
        Assert.Throws<CodexProtocolException>(() => session.Acknowledge(1, "next", "early-cell", 2));
    }

    [Fact]
    public async Task PendingCallbackLimitAndExactCancellationCannotCrossExecutions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = new CodexCodeModeSession(Scope, maximumCallbacks: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<CodexToolOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = Registry(Tool("echo"));
        Prepare(session, "a", registry, execute: async (_, token) =>
        {
            entered.TrySetResult();
            return await result.Task.WaitAsync(token);
        });
        Prepare(session, "b", registry);
        var first = Callback("a", "cell-a");
        var pending = session.InvokeToolAsync(first, ct);
        await entered.Task.WaitAsync(ct);
        Assert.Throws<CodexProtocolException>(() => session.CancelInvocation(1, "b", first.InvocationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeToolAsync(Callback("b", "cell-b"), ct));
        Assert.True(session.CancelInvocation(1, "a", first.InvocationId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("{}", (await session.InvokeToolAsync(Callback("b", "cell-b"), ct)).Text);
    }
}
