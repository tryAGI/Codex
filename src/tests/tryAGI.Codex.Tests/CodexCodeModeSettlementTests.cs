namespace tryAGI.Codex.Tests;

public sealed class CodexCodeModeSettlementTests
{
    private static CodexCodeModeSession Session(int slots = 4) =>
        new(new("account", "connection", "thread"), maximumCallbacks: slots);

    private static CodexCodeModeAdmission Prepare(CodexCodeModeSession session, string id,
        Func<CodexCodeModeNotificationInvocation, CancellationToken, Task>? notify = null,
        Func<CodexCodeModeInvocation, CancellationToken, Task<CodexToolOutput>>? execute = null)
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"type":"function","name":"echo"}""");
        var registry = new CodexToolRegistry([new(new(null, "echo"), document.RootElement, CodexToolExposure.Direct)]);
        return session.Prepare(id, "turn-" + id, "provider-" + id, registry, DateTimeOffset.UtcNow.AddMinutes(5),
            (_, _) => ValueTask.FromResult(true),
            execute ?? ((_, _) => Task.FromResult(new CodexToolOutput("ok"))), notify);
    }

    private static CodexCodeModeNotification Notice(string id, string cell = "cell", long generation = 1) =>
        new(generation, id, cell, "provider-" + id, Guid.NewGuid(), "update");

    private static CodexCodeModeCallback Call(string id, string cell = "cell") =>
        new(1, id, cell, "provider-" + id, Guid.NewGuid(), new("nested", "echo", "{}", CodexToolCallKind.Function));

    [Fact]
    public async Task NormalEarlyClosureDrainsNotificationsAndRevokesToolsBeforeClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolReleased = new TaskCompletionSource<CodexToolOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = Prepare(session, "a", async (notice, token) =>
        {
            Assert.Equal("turn-a", notice.TurnId);
            Assert.Equal("thread", notice.Scope.ThreadId);
            entered.TrySetResult();
            await released.Task.WaitAsync(token);
        }, async (_, token) =>
        {
            toolEntered.TrySetResult();
            return await toolReleased.Task.WaitAsync(token);
        });
        var delivery = session.DeliverNotificationAsync(Notice("a"), ct);
        await entered.Task.WaitAsync(ct);
        var tool = session.InvokeToolAsync(Call("a"), ct);
        await toolEntered.Task.WaitAsync(ct);
        var settlement = session.WaitForSettlementAsync(1, "a", ct);
        Assert.True(session.ReportCellClosed(1, "a", "cell"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool);
        Assert.False(delivery.IsCompleted);
        Assert.False(settlement.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeliverNotificationAsync(Notice("a"), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeToolAsync(Call("a"), ct));
        Assert.Equal("cell", session.Acknowledge(1, "a", "cell", admission.CatalogRevision));
        session.ClaimStartedCell(1, "a", "cell");
        Assert.False(settlement.IsCompleted);
        released.TrySetResult();
        await delivery;
        var receipt = await settlement;
        Assert.Equal(CodexCodeModeSettlementKind.Completed, receipt.Kind);
        Assert.Equal(1, receipt.NotificationsCompleted);
        Assert.Equal(0, receipt.NotificationsFailed);
        Assert.Equal(0, receipt.NotificationsRevoked);
        Assert.Equal(receipt, await session.WaitForSettlementAsync(1, "a", ct));
        Assert.False(session.ReportCellClosed(1, "a", "cell"));
    }

    [Fact]
    public async Task EarlyClosureWithoutNotificationsWaitsForStartedClaimAndKeepsAcknowledgedCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session();
        var first = Prepare(session, "a");
        Assert.True(session.ReportCellClosed(1, "a", "cell"));
        var wait = session.WaitForSettlementAsync(1, "a", ct);
        Assert.False(wait.IsCompleted);
        session.Acknowledge(1, "a", "cell", first.CatalogRevision);
        Assert.False(wait.IsCompleted);
        session.ClaimStartedCell(1, "a", "cell");
        Assert.Equal(CodexCodeModeSettlementKind.Completed, (await wait).Kind);
        Assert.True(Prepare(session, "b").ReusesCatalog);
    }

    [Fact]
    public async Task TerminationRevokesDeliveryEvenWhenDelegateReturnsLate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = Prepare(session, "a", async (_, _) => { started.TrySetResult(); await late.Task.WaitAsync(ct); });
        session.Acknowledge(1, "a", "cell", admission.CatalogRevision);
        session.ClaimStartedCell(1, "a", "cell");
        var delivery = session.DeliverNotificationAsync(Notice("a"), ct);
        await started.Task.WaitAsync(ct);
        Assert.True(session.ReportCellClosed(1, "a", "cell", terminated: true));
        var receipt = await session.WaitForSettlementAsync(1, "a", ct);
        Assert.Equal(CodexCodeModeSettlementKind.Terminated, receipt.Kind);
        Assert.Equal(0, receipt.NotificationsCompleted);
        Assert.Equal(1, receipt.NotificationsRevoked);
        late.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
    }

    [Fact]
    public async Task SharedCapacityDuplicateAndIdentityFences()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session(slots: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Prepare(session, "a", async (_, token) => { entered.TrySetResult(); await released.Task.WaitAsync(token); });
        Prepare(session, "b", (_, _) => Task.CompletedTask);
        var notice = Notice("a");
        var delivery = session.DeliverNotificationAsync(notice, ct);
        await entered.Task.WaitAsync(ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.DeliverNotificationAsync(notice, ct));
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.DeliverNotificationAsync(Notice("b"), ct));
        await Assert.ThrowsAsync<CodexProtocolException>(() => session.DeliverNotificationAsync(Notice("b", "other") with { ToolCallId = "wrong" }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => session.DeliverNotificationAsync(Notice("b", "other") with { NotificationId = Guid.Empty }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeToolAsync(Call("b", "other"), ct));
        released.TrySetResult();
        await delivery;
        Assert.Equal("ok", (await session.InvokeToolAsync(Call("b", "other"), ct)).Text);
    }

    [Fact]
    public async Task FailedNotificationIsCountedAndDoesNotPreventTerminalReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session();
        var admission = Prepare(session, "a", (_, _) => Task.FromException(new IOException("delivery failed")));
        session.Acknowledge(1, "a", "cell", admission.CatalogRevision);
        session.ClaimStartedCell(1, "a", "cell");
        await Assert.ThrowsAsync<IOException>(() => session.DeliverNotificationAsync(Notice("a"), ct));
        session.ReportCellClosed(1, "a", "cell");
        var receipt = await session.WaitForSettlementAsync(1, "a", ct);
        Assert.Equal(1, receipt.NotificationsFailed);
        Assert.Equal(0, receipt.NotificationsCompleted);
    }

    [Fact]
    public async Task DisconnectRevokesNotificationsAndOldWaiterReceivesOnlyDisconnectedReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Prepare(session, "a", async (_, token) => { entered.TrySetResult(); await released.Task.WaitAsync(token); });
        var delivery = session.DeliverNotificationAsync(Notice("a"), ct);
        await entered.Task.WaitAsync(ct);
        var pending = session.WaitForSettlementAsync(1, "a", ct);
        Assert.Equal("cell", Assert.Single(session.Reconnect()));
        Assert.Equal(CodexCodeModeSettlementKind.Disconnected, (await pending).Kind);
        Assert.Equal(1, (await pending).NotificationsRevoked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.False(session.ReportCellClosed(1, "a", "cell"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WaitForSettlementAsync(1, "a", ct));
        var next = Prepare(session, "a", (_, _) => Task.CompletedTask);
        Assert.Equal(2, next.Generation);
        await session.DeliverNotificationAsync(Notice("a", generation: 2), ct);
    }
}
