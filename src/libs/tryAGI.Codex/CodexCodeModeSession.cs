using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Authenticated host binding for a Code Mode session. This identity is resolved by
/// the host, never by JavaScript or provider output.</summary>
public sealed record CodexCodeModeScope(string AccountId, string ConnectionId, string ThreadId,
    string? EnvironmentId = null);

/// <summary>Exact cell admission request. A null catalog revision means a self-contained catalog;
/// a reused revision omits definitions on the wire only after the host has acknowledged it.</summary>
public sealed record CodexCodeModeAdmission(long Generation, string ExecutionId, string TurnId,
    string ToolCallId, long? CatalogRevision, bool ReusesCatalog,
    IReadOnlyList<CodexRegisteredTool> ToolsToSend, DateTimeOffset ExpiresAt);

/// <summary>Exact nested callback identity; tool availability is not permission to execute.</summary>
public sealed record CodexCodeModeCallback(long Generation, string ExecutionId, string CellId,
    string ToolCallId, Guid InvocationId, CodexToolCall Tool);

/// <summary>Trusted per-execution callback with the actual host scope and validated tool input.</summary>
public sealed record CodexCodeModeInvocation(CodexCodeModeScope Scope, string TurnId,
    CodexCodeModeCallback Callback);

/// <summary>Generation-fenced Code Mode catalog admission and nested tool dispatch.
/// This class is not a JavaScript runtime or gRPC client. The embedding host owns all
/// remote cell operations and must explicitly authorize and deduplicate every tool effect.
/// An ambiguous or cancelled execution is never automatically replayed.</summary>
public sealed partial class CodexCodeModeSession : IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly int maxExecutions;
    private readonly int maxCallbacks;
    private readonly int maxRememberedIds;
    private readonly Dictionary<string, Execution> executions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> cells = new(StringComparer.Ordinal);
    private readonly HashSet<string> usedExecutions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> cancelledBeforeArrival = [];
    private readonly HashSet<Guid> seenInvocations = [];
    private readonly Dictionary<Guid, ActiveCallback> callbacks = [];
    private long generation = 1;
    private long revision;
    private string? catalogOwner;
    private CodexRegisteredTool[]? acknowledgedCatalog;
    private bool disposed;

    /// <summary>Creates a bounded session. Retired execution/invocation IDs are retained until
    /// reconnect; exhaustion fails closed rather than evicting replay protection.</summary>
    public CodexCodeModeSession(CodexCodeModeScope scope, TimeProvider? clock = null,
        int maximumExecutions = 32, int maximumCallbacks = 64, int maximumRememberedIds = 4096)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateId(scope.AccountId);
        ValidateId(scope.ConnectionId);
        ValidateId(scope.ThreadId);
        if (scope.EnvironmentId is not null) ValidateId(scope.EnvironmentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExecutions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCallbacks);
        if (maximumRememberedIds < maximumExecutions || maximumRememberedIds < maximumCallbacks)
            throw new ArgumentOutOfRangeException(nameof(maximumRememberedIds));
        Scope = scope;
        this.clock = clock ?? TimeProvider.System;
        maxExecutions = maximumExecutions;
        maxCallbacks = maximumCallbacks;
        maxRememberedIds = maximumRememberedIds;
    }

    /// <summary>Authenticated session scope.</summary>
    public CodexCodeModeScope Scope { get; }

    /// <summary>Current remote host generation; stale generation events never target new cells.</summary>
    public long Generation { get { lock (gate) return generation; } }

    /// <summary>Prepares one execution with its own immutable eligible catalog and delegate.
    /// Concurrent admissions never depend on a pending catalog replacement. The host must
    /// send the returned request exactly once and call Acknowledge or CloseExecution.</summary>
    public CodexCodeModeAdmission Prepare(string executionId, string turnId, string toolCallId,
        CodexToolRegistry registry, DateTimeOffset expiresAt,
        Func<CodexCodeModeInvocation, CancellationToken, ValueTask<bool>> authorize,
        Func<CodexCodeModeInvocation, CancellationToken, Task<CodexToolOutput>> execute,
        Func<CodexCodeModeNotificationInvocation, CancellationToken, Task>? notify = null)
    {
        ValidateId(executionId);
        ValidateId(turnId);
        ValidateId(toolCallId);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(execute);
        var tools = registry.CodeModeTools().Select(tool => tool with { Definition = tool.Definition.Clone() }).ToArray();
        var eligible = new Dictionary<(string Namespace, string Name), CodexToolCallKind>();
        foreach (var tool in tools)
        {
            var kind = CodexRealtimeProtocol.Text(tool.Definition, "type") == "custom" ? CodexToolCallKind.Custom : CodexToolCallKind.Function;
            eligible.Add((tool.Identity.Namespace, tool.Identity.Name), kind);
        }

        lock (gate)
        {
            EnsureOpen();
            var remaining = expiresAt - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromDays(30))
                throw new ArgumentOutOfRangeException(nameof(expiresAt), "Execution deadline must be within 30 days.");
            if (usedExecutions.Contains(executionId)) throw new InvalidOperationException("Code Mode execution ID was reused.");
            if (executions.Count >= maxExecutions || usedExecutions.Count >= maxRememberedIds)
                throw new InvalidOperationException("Code Mode execution capacity exhausted.");
            long? selectedRevision = null;
            var reuses = false;
            if (tools.Length != 0 && catalogOwner is null)
            {
                catalogOwner = executionId;
                reuses = acknowledgedCatalog is { } previous && EqualCatalog(previous, tools);
                if (!reuses)
                {
                    acknowledgedCatalog = null;
                    revision = checked(revision + 1);
                }
                selectedRevision = revision;
            }
            var admission = new CodexCodeModeAdmission(generation, executionId, turnId, toolCallId,
                selectedRevision, reuses, Array.AsReadOnly(reuses ? [] : tools), expiresAt);
            usedExecutions.Add(executionId);
            executions.Add(executionId, new Execution(admission, tools, eligible, authorize, execute, notify));
            return admission;
        }
    }

    /// <summary>Commits the host's exact admission acknowledgment and binds the returned cell.
    /// A callback may have already established the cell identity; both must agree.</summary>
    public string Acknowledge(long hostGeneration, string executionId, string cellId, long? catalogRevision)
    {
        ValidateId(cellId);
        lock (gate)
        {
            var execution = Find(hostGeneration, executionId);
            if (execution.Acknowledged || execution.Admission.CatalogRevision != catalogRevision)
                throw new CodexProtocolException("Code Mode admission acknowledgment does not match the execution.");
            if (clock.GetUtcNow() >= execution.Admission.ExpiresAt)
                throw new InvalidOperationException("Code Mode admission expired.");
            BindCell(execution, cellId);
            execution.Acknowledged = true;
            if (catalogOwner == executionId)
            {
                acknowledgedCatalog = execution.Tools;
                catalogOwner = null;
            }
            return PublicCellId(hostGeneration, cellId);
        }
    }

    /// <summary>Executes one nested callback through existing tool identity validation and
    /// an explicit host authorization callback. A late result after cancellation, closure,
    /// expiry or reconnect is discarded. A callback is never retried automatically.</summary>
    public async Task<CodexToolOutput> InvokeToolAsync(CodexCodeModeCallback request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Tool);
        ValidateId(request.CellId);
        ValidateId(request.ToolCallId);
        if (request.InvocationId == Guid.Empty) throw new ArgumentException("Missing Code Mode invocation ID.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        Execution execution;
        ActiveCallback active;
        CodexCodeModeInvocation invocation;
        lock (gate)
        {
            execution = Find(request.Generation, request.ExecutionId);
            if (execution.TerminalKind is not null)
                throw new InvalidOperationException("Code Mode cell is closed.");
            if (execution.Admission.ToolCallId != request.ToolCallId)
                throw new CodexProtocolException("Code Mode callback belongs to a different provider call.");
            if (clock.GetUtcNow() >= execution.Admission.ExpiresAt)
                throw new TimeoutException("Code Mode execution expired.");
            if (seenInvocations.Contains(request.InvocationId))
                throw new CodexProtocolException("Code Mode invocation ID was reused.");
            if (seenInvocations.Count + cancelledBeforeArrival.Count >= maxRememberedIds &&
                !cancelledBeforeArrival.ContainsKey(request.InvocationId))
                throw new InvalidOperationException("Code Mode callback replay protection capacity exhausted.");
            if (cancelledBeforeArrival.TryGetValue(request.InvocationId, out var cancelledExecution) &&
                cancelledExecution != request.ExecutionId)
                throw new CodexProtocolException("Code Mode invocation cancellation has different execution scope.");
            BindCell(execution, request.CellId);
            seenInvocations.Add(request.InvocationId);
            if (cancelledBeforeArrival.Remove(request.InvocationId))
                throw new OperationCanceledException("Code Mode callback was cancelled before arrival.");
            if (callbacks.Count + notifications.Count >= maxCallbacks)
                throw new InvalidOperationException("Code Mode pending callback limit exceeded.");
            var validated = ValidateTool(request.Tool, execution.Eligible);
            invocation = new(Scope, execution.Admission.TurnId, request with { Tool = validated });
            active = new(request.ExecutionId, new CancellationTokenSource());
            callbacks.Add(request.InvocationId, active);
        }

        try
        {
            var remaining = execution.Admission.ExpiresAt - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Code Mode execution expired.");
            // The host deadline remains authoritative across authorization and execution.
            using var deadline = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, active.Token, deadline.Token);
            var ct = linked.Token;
            ct.ThrowIfCancellationRequested();
            if (!await execution.Authorize(invocation, ct).AsTask().WaitAsync(ct).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Code Mode nested tool was not admitted by host policy.");
            lock (gate)
            {
                EnsureCallbackCurrent(request, active);
                ct.ThrowIfCancellationRequested();
            }
            // The delegate must enforce its own atomic dispatch grant and honor cancellation.
            var result = await execution.Execute(invocation, ct).WaitAsync(ct).ConfigureAwait(false);
            lock (gate)
            {
                EnsureCallbackCurrent(request, active);
                ct.ThrowIfCancellationRequested();
                if (clock.GetUtcNow() >= execution.Admission.ExpiresAt)
                    throw new TimeoutException("Code Mode execution expired before completion.");
                callbacks.Remove(request.InvocationId);
                return result;
            }
        }
        finally
        {
            lock (gate)
                if (callbacks.TryGetValue(request.InvocationId, out var current) && ReferenceEquals(current, active))
                    callbacks.Remove(request.InvocationId);
            active.Dispose();
        }
    }

    /// <summary>Cancels an exact active callback or records cancellation before arrival.
    /// The callback must belong to the specified generation and execution.</summary>
    public bool CancelInvocation(long hostGeneration, string executionId, Guid invocationId)
    {
        if (invocationId == Guid.Empty) throw new ArgumentException("Missing invocation ID.", nameof(invocationId));
        ActiveCallback? cancelled = null;
        lock (gate)
        {
            if (Find(hostGeneration, executionId).TerminalKind is not null) return false;
            if (callbacks.TryGetValue(invocationId, out var active))
            {
                if (active.ExecutionId != executionId) throw new CodexProtocolException("Callback belongs to another execution.");
                callbacks.Remove(invocationId);
                cancelled = active;
            }
            else if (seenInvocations.Contains(invocationId)) return false;
            else
            {
                if (cancelledBeforeArrival.TryGetValue(invocationId, out var previous))
                {
                    if (previous != executionId) throw new CodexProtocolException("Callback belongs to another execution.");
                    return false;
                }
                if (seenInvocations.Count + cancelledBeforeArrival.Count >= maxRememberedIds)
                    throw new InvalidOperationException("Code Mode callback replay protection capacity exhausted.");
                cancelledBeforeArrival.Add(invocationId, executionId);
                return true;
            }
        }
        cancelled.Cancel();
        return true;
    }

    /// <summary>Closes or abandons one execution, revokes its callbacks, and returns whether
    /// it was live. An unacknowledged replacement is not retained as a reusable revision.</summary>
    public bool CloseExecution(long hostGeneration, string executionId, string? cellId = null)
    {
        ActiveCallback[] cancelled;
        lock (gate)
        {
            EnsureOpen();
            if (hostGeneration != generation) return false;
            if (!executions.TryGetValue(executionId, out var execution)) return false;
            if (cellId is not null)
            {
                ValidateId(cellId);
                BindCell(execution, cellId);
            }
            cancelled = [.. RemoveActive(callbacks, executionId), .. RevokeNotifications(execution)];
            Settle(execution, CodexCodeModeSettlementKind.Abandoned);
        }
        foreach (var callback in cancelled) callback.Cancel();
        return true;
    }

    /// <summary>Fences the disconnected host generation, revokes all callbacks and returns the
    /// previously observed public cell IDs. The host opens a new connection explicitly.
    /// No execution is replayed, and no old catalog can be referenced by the new host.</summary>
    public IReadOnlyList<string> Reconnect()
    {
        ActiveCallback[] cancelled;
        string[] closed;
        lock (gate)
        {
            EnsureOpen();
            closed = executions.Values.Where(entry => entry.CellId is not null)
                .Select(entry => PublicCellId(generation, entry.CellId!)).ToArray();
            cancelled = Clear();
            generation = checked(generation + 1);
        }
        foreach (var callback in cancelled) callback.Cancel();
        return Array.AsReadOnly(closed);
    }

    /// <summary>Maps a public cell ID to the exact live generation's remote ID. Generation one
    /// retains the Rust wire spelling; later generations use gN: prefixes.</summary>
    public string ResolveRemoteCell(long hostGeneration, string publicCellId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicCellId);
        if (publicCellId.Length > 1050 || publicCellId.Any(char.IsControl))
            throw new ArgumentException("Invalid public Code Mode cell ID.", nameof(publicCellId));
        lock (gate)
        {
            EnsureOpen();
            if (hostGeneration != generation) throw new InvalidOperationException("Stale Code Mode host generation.");
            var prefix = generation == 1 ? "" : $"g{generation}:";
            if (!publicCellId.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Cell belongs to a stale Code Mode generation.");
            var cellId = publicCellId[prefix.Length..];
            if (!cells.TryGetValue(cellId, out var executionId) ||
                !executions.TryGetValue(executionId, out var execution) || execution.TerminalKind is not null ||
                clock.GetUtcNow() >= execution.Admission.ExpiresAt)
                throw new InvalidOperationException("Unknown or closed Code Mode cell.");
            return cellId;
        }
    }

    /// <summary>Closes this session without replaying any outstanding execution.</summary>
    public void Dispose()
    {
        ActiveCallback[] cancelled;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancelled = Clear();
        }
        foreach (var callback in cancelled) callback.Cancel();
    }

    private ActiveCallback[] Clear()
    {
        var active = callbacks.Values.Concat(notifications.Values).ToArray();
        foreach (var execution in executions.Values.ToArray())
        {
            _ = RevokeNotifications(execution);
            Settle(execution, CodexCodeModeSettlementKind.Disconnected);
        }
        settlements.Clear();
        callbacks.Clear();
        notifications.Clear();
        seenNotifications.Clear();
        cells.Clear();
        usedExecutions.Clear();
        seenInvocations.Clear();
        cancelledBeforeArrival.Clear();
        catalogOwner = null;
        acknowledgedCatalog = null;
        revision = 0;
        return active;
    }

    private Execution Find(long hostGeneration, string executionId)
    {
        EnsureOpen();
        if (hostGeneration != generation || !executions.TryGetValue(executionId, out var execution))
            throw new InvalidOperationException("Unknown or stale Code Mode execution.");
        return execution;
    }

    private void BindCell(Execution execution, string cellId)
    {
        if (execution.CellId is { } current && current != cellId ||
            cells.TryGetValue(cellId, out var owner) && owner != execution.Admission.ExecutionId)
            throw new CodexProtocolException("Code Mode cell ownership mismatch.");
        cells[cellId] = execution.Admission.ExecutionId;
        execution.CellId = cellId;
    }

    private void EnsureCallbackCurrent(CodexCodeModeCallback request, ActiveCallback expected)
    {
        if (disposed || request.Generation != generation || !executions.ContainsKey(request.ExecutionId) ||
            !callbacks.TryGetValue(request.InvocationId, out var active) || !ReferenceEquals(active, expected))
            throw new OperationCanceledException("Code Mode callback no longer belongs to an active cell.");
    }

    private static CodexToolCall ValidateTool(CodexToolCall tool,
        Dictionary<(string Namespace, string Name), CodexToolCallKind> eligible)
    {
        if (!Enum.IsDefined(tool.Kind)) throw new CodexProtocolException("Unknown Code Mode callback tool kind.");
        ValidateId(tool.Id);
        var item = new JsonObject
        {
            ["type"] = tool.Kind == CodexToolCallKind.Custom ? "custom_tool_call" : "function_call",
            ["call_id"] = tool.Id, ["name"] = tool.Name, ["namespace"] = tool.Namespace,
            [tool.Kind == CodexToolCallKind.Custom ? "input" : "arguments"] = tool.Input,
        };
        using var parsed = JsonDocument.Parse(item.ToJsonString());
        return CodexToolCalls.Validate(parsed.RootElement, eligible, allowCustom: true);
    }

    private static bool EqualCatalog(CodexRegisteredTool[] left, CodexRegisteredTool[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Identity == pair.Second.Identity && pair.First.Exposure == pair.Second.Exposure &&
            pair.First.NamespaceDescription == pair.Second.NamespaceDescription &&
            pair.First.SearchText == pair.Second.SearchText &&
            JsonElement.DeepEquals(pair.First.Definition, pair.Second.Definition));

    private static string PublicCellId(long generation, string cellId) => generation == 1 ? cellId : $"g{generation}:{cellId}";

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 1024 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid Code Mode identity.");
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class Execution(CodexCodeModeAdmission admission, CodexRegisteredTool[] tools,
        Dictionary<(string Namespace, string Name), CodexToolCallKind> eligible,
        Func<CodexCodeModeInvocation, CancellationToken, ValueTask<bool>> authorize,
        Func<CodexCodeModeInvocation, CancellationToken, Task<CodexToolOutput>> execute,
        Func<CodexCodeModeNotificationInvocation, CancellationToken, Task>? notify)
    {
        internal CodexCodeModeAdmission Admission { get; } = admission;
        internal CodexRegisteredTool[] Tools { get; } = tools;
        internal Dictionary<(string Namespace, string Name), CodexToolCallKind> Eligible { get; } = eligible;
        internal Func<CodexCodeModeInvocation, CancellationToken, ValueTask<bool>> Authorize { get; } = authorize;
        internal Func<CodexCodeModeInvocation, CancellationToken, Task<CodexToolOutput>> Execute { get; } = execute;
        internal string? CellId { get; set; }
        internal bool Acknowledged { get; set; }
        internal bool Claimed { get; set; }
        internal CodexCodeModeSettlementKind? TerminalKind { get; set; }
        internal int PendingNotifications { get; set; }
        internal int NotificationsCompleted { get; set; }
        internal int NotificationsFailed { get; set; }
        internal int NotificationsRevoked { get; set; }
        internal Func<CodexCodeModeNotificationInvocation, CancellationToken, Task>? Notify { get; } = notify;
        internal TaskCompletionSource<CodexCodeModeSettlement> Settlement { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ActiveCallback(string executionId, CancellationTokenSource source) : IDisposable
    {
        private readonly object callbackGate = new();
        private bool disposed;
        internal string ExecutionId { get; } = executionId;
        internal CancellationToken Token { get; } = source.Token;
        internal void Cancel()
        {
            lock (callbackGate)
                if (!disposed)
                {
                    // A throwing host cancellation observer cannot prevent revocation of other callbacks.
                    try { source.Cancel(); }
                    catch (AggregateException) { }
                }
        }
        public void Dispose()
        {
            lock (callbackGate)
            {
                if (disposed) return;
                disposed = true;
                source.Dispose();
            }
        }
    }
}
