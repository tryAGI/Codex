namespace tryAGI.Codex;

/// <summary>Exact notification identity from the bound Code Mode host stream.</summary>
public sealed record CodexCodeModeNotification(long Generation, string ExecutionId, string CellId,
    string ToolCallId, Guid NotificationId, string Text);

/// <summary>Notification delivered to its own host-selected execution delegate, not a tool grant.</summary>
public sealed record CodexCodeModeNotificationInvocation(CodexCodeModeScope Scope, string TurnId,
    CodexCodeModeNotification Notification);

/// <summary>Terminal reason for a Code Mode cell or admission.</summary>
public enum CodexCodeModeSettlementKind
{
    /// <summary>The host reported normal closure and admitted notifications settled.</summary>
    Completed,
    /// <summary>The host reported termination; outstanding callbacks were revoked.</summary>
    Terminated,
    /// <summary>The local owner abandoned the execution without a confirmed host result.</summary>
    Abandoned,
    /// <summary>The host binding was lost. No result is implied.</summary>
    Disconnected,
}

/// <summary>Local terminal settlement. Notification success counts only acknowledged delegate
/// completion, not user-visible rendering. Failed or revoked notifications are reported separately.</summary>
public sealed record CodexCodeModeSettlement(long Generation, string ExecutionId, string? CellId,
    CodexCodeModeSettlementKind Kind, int NotificationsCompleted, int NotificationsFailed,
    int NotificationsRevoked);

public sealed partial class CodexCodeModeSession
{
    private readonly Dictionary<Guid, ActiveCallback> notifications = [];
    private readonly HashSet<Guid> seenNotifications = [];
    private readonly Dictionary<string, CodexCodeModeSettlement> settlements = new(StringComparer.Ordinal);

    /// <summary>Delivers one correlated notification through the execution's explicit host delegate.
    /// The delegate must enforce current delivery policy. Normal closure drains admitted deliveries;
    /// termination and disconnect revoke them. No late result is treated as delivered.</summary>
    public async Task DeliverNotificationAsync(CodexCodeModeNotification request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.CellId);
        ValidateId(request.ToolCallId);
        if (request.NotificationId == Guid.Empty) throw new ArgumentException("Missing notification ID.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Text);
        if (request.Text.Length > 65536) throw new ArgumentException("Code Mode notification exceeds the size limit.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        Execution execution;
        ActiveCallback active;
        Func<CodexCodeModeNotificationInvocation, CancellationToken, Task> deliver;
        lock (gate)
        {
            execution = Find(request.Generation, request.ExecutionId);
            if (execution.Admission.ToolCallId != request.ToolCallId)
                throw new CodexProtocolException("Code Mode notification belongs to a different provider call.");
            if (execution.TerminalKind is not null)
                throw new InvalidOperationException("Code Mode cell is closed.");
            if (clock.GetUtcNow() >= execution.Admission.ExpiresAt)
                throw new TimeoutException("Code Mode execution expired.");
            if (seenNotifications.Contains(request.NotificationId))
                throw new CodexProtocolException("Code Mode notification ID was reused.");
            if (seenNotifications.Count >= maxRememberedIds)
                throw new InvalidOperationException("Code Mode notification replay protection capacity exhausted.");
            BindCell(execution, request.CellId);
            seenNotifications.Add(request.NotificationId);
            if (callbacks.Count + notifications.Count >= maxCallbacks)
                throw new InvalidOperationException("Code Mode pending callback limit exceeded.");
            deliver = execution.Notify ?? throw new UnauthorizedAccessException("No authorized notification delegate.");
            active = new(request.ExecutionId, new CancellationTokenSource());
            notifications.Add(request.NotificationId, active);
            execution.PendingNotifications++;
        }
        var acknowledged = false;
        try
        {
            var remaining = execution.Admission.ExpiresAt - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Code Mode execution expired.");
            using var deadline = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Token, deadline.Token);
            var ct = linked.Token;
            ct.ThrowIfCancellationRequested();
            await deliver(new(Scope, execution.Admission.TurnId, request), ct).WaitAsync(ct).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed || request.Generation != generation ||
                    !notifications.TryGetValue(request.NotificationId, out var current) || !ReferenceEquals(current, active))
                    throw new OperationCanceledException("Code Mode notification is no longer admitted.");
                ct.ThrowIfCancellationRequested();
                if (clock.GetUtcNow() >= execution.Admission.ExpiresAt)
                    throw new TimeoutException("Code Mode notification expired.");
                // Acknowledge and settle atomically against termination.
                acknowledged = true;
                FinishNotification(execution, request.NotificationId, active, acknowledged);
            }
        }
        finally
        {
            lock (gate)
                FinishNotification(execution, request.NotificationId, active, acknowledged);
            active.Dispose();
        }
    }

    /// <summary>Claims the started cell after the host's exact admission acknowledgment.
    /// Closure observed before acknowledgment is retained until this claim and notification drain.</summary>
    public void ClaimStartedCell(long hostGeneration, string executionId, string cellId)
    {
        ValidateId(cellId);
        lock (gate)
        {
            var execution = Find(hostGeneration, executionId);
            if (!execution.Acknowledged || execution.Claimed || execution.CellId != cellId)
                throw new CodexProtocolException("Code Mode started cell cannot be claimed.");
            execution.Claimed = true;
            TrySettle(execution);
        }
    }

    /// <summary>Records the terminal cell event from the bound host stream. Normal closure
    /// revokes tool callbacks but retains admitted notifications until settlement. Termination
    /// revokes both. This is distinct from local abandonment via CloseExecution.</summary>
    public bool ReportCellClosed(long hostGeneration, string executionId, string cellId, bool terminated = false)
    {
        ValidateId(cellId);
        ActiveCallback[] revoked;
        lock (gate)
        {
            EnsureOpen();
            if (hostGeneration != generation) return false;
            if (!executions.TryGetValue(executionId, out var execution))
            {
                if (settlements.TryGetValue(executionId, out var previous) &&
                    previous.CellId is { } known && known != PublicCellId(hostGeneration, cellId))
                    throw new CodexProtocolException("Code Mode closure has different cell identity.");
                return false;
            }
            BindCell(execution, cellId);
            var terminal = terminated ? CodexCodeModeSettlementKind.Terminated : CodexCodeModeSettlementKind.Completed;
            if (execution.TerminalKind is { } earlier &&
                (earlier == terminal || earlier == CodexCodeModeSettlementKind.Terminated)) return false;
            execution.TerminalKind = terminal;
            var tools = RemoveActive(callbacks, executionId);
            var deliveries = terminated ? RevokeNotifications(execution) : [];
            revoked = [.. tools, .. deliveries];
            TrySettle(execution);
        }
        foreach (var active in revoked) active.Cancel();
        return true;
    }

    /// <summary>Waits for a correlated terminal receipt without cancelling the execution on
    /// waiter cancellation. The execution deadline bounds the wait; expiry is not success.</summary>
    public async Task<CodexCodeModeSettlement> WaitForSettlementAsync(long hostGeneration, string executionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<CodexCodeModeSettlement> pending;
        TimeSpan remaining;
        lock (gate)
        {
            EnsureOpen();
            if (hostGeneration != generation) throw new InvalidOperationException("Stale Code Mode host generation.");
            if (settlements.TryGetValue(executionId, out var settled)) return settled;
            var execution = Find(hostGeneration, executionId);
            pending = execution.Settlement.Task;
            remaining = execution.Admission.ExpiresAt - clock.GetUtcNow();
        }
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Code Mode execution expired before settlement.");
        return await pending.WaitAsync(remaining, clock, cancellationToken).ConfigureAwait(false);
    }

    private void FinishNotification(Execution execution, Guid notificationId, ActiveCallback expected, bool acknowledged)
    {
        if (!notifications.TryGetValue(notificationId, out var active) || !ReferenceEquals(active, expected))
            return;
        notifications.Remove(notificationId);
        execution.PendingNotifications--;
        if (acknowledged) execution.NotificationsCompleted++;
        else execution.NotificationsFailed++;
        TrySettle(execution);
    }

    private void TrySettle(Execution execution)
    {
        if (execution.TerminalKind is { } kind && execution.Acknowledged && execution.Claimed &&
            execution.PendingNotifications == 0)
            Settle(execution, kind);
    }

    private void Settle(Execution execution, CodexCodeModeSettlementKind kind)
    {
        var admission = execution.Admission;
        var receipt = new CodexCodeModeSettlement(admission.Generation, admission.ExecutionId,
            execution.CellId is { } cell ? PublicCellId(admission.Generation, cell) : null,
            kind, execution.NotificationsCompleted, execution.NotificationsFailed, execution.NotificationsRevoked);
        settlements.Add(admission.ExecutionId, receipt);
        executions.Remove(admission.ExecutionId);
        if (catalogOwner == admission.ExecutionId) catalogOwner = null;
        execution.Settlement.TrySetResult(receipt);
    }

    private ActiveCallback[] RevokeNotifications(Execution execution)
    {
        var removed = RemoveActive(notifications, execution.Admission.ExecutionId);
        execution.NotificationsRevoked += removed.Length;
        execution.PendingNotifications -= removed.Length;
        return removed;
    }

    private static ActiveCallback[] RemoveActive(Dictionary<Guid, ActiveCallback> source, string executionId)
    {
        var matches = source.Where(pair => pair.Value.ExecutionId == executionId).ToArray();
        foreach (var pair in matches) source.Remove(pair.Key);
        return matches.Select(pair => pair.Value).ToArray();
    }
}
