using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Outcome of <see cref="CodexThreadAgent.RunAsync"/>.</summary>
/// <param name="Thread">The thread after the turn was recorded.</param>
/// <param name="Result">The agent run.</param>
public sealed record CodexThreadAgentResult(CodexThreadDocument Thread, CodexAgentResult Result)
{
    /// <summary>Projected context at the terminal boundary, separating provider observations from estimates.</summary>
    public CodexContextWindowStatus? ContextWindow { get; init; }
}

/// <summary>Executes a new turn against a durable native journal. The host still resolves
/// settings, owns execution leases, supplies financial admission and authorizes every tool.
/// Reads never resume work; an ambiguous initial StartTurn requires host reconciliation.</summary>
/// <param name="store">Thread journal.</param>
/// <param name="credentials">Supplies ChatGPT OAuth credentials.</param>
/// <param name="budget">Admission and accounting hooks for every model request.</param>
/// <param name="endpoints">Endpoints, timeouts and size bounds.</param>
/// <param name="clientFactory">Optional factory for the underlying <see cref="CodexClient"/>.</param>
public sealed class CodexThreadAgent(CodexThreadStore store, ICodexCredentialProvider credentials,
    ICodexUsageObserver budget, CodexEndpointOptions? endpoints = null,
    Func<ICodexCredentialProvider, ICodexUsageObserver, CodexClient>? clientFactory = null)
{
    /// <summary>Optional host-owned scheduler reused across turns. The host disposes it and
    /// supplies a budget observer and client factory that support concurrent requests.</summary>
    public CodexThreadCompactionScheduler? BackgroundCompactions { get; init; }

    /// <summary>Starts a turn on a stored thread, runs it, and records every dispatch and output in the journal.</summary>
    /// <param name="threadId">Thread ID.</param>
    /// <param name="expectedRevision">Revision the caller last read.</param>
    /// <param name="turnId">New turn ID, chosen by the caller before the call.</param>
    /// <param name="ownerId">Host process or worker running the turn.</param>
    /// <param name="options">Model, instructions and tools.</param>
    /// <param name="input">New input items.</param>
    /// <param name="execute">Executes one authorized tool call.</param>
    /// <param name="maximumRequests">Upper bound on model requests.</param>
    /// <param name="observe">Receives every raw Responses event.</param>
    /// <param name="observeDiagnostics">Receives timing and size metadata.</param>
    /// <param name="canExecuteConcurrently">Returns <see langword="true"/> for calls that may run alongside others.</param>
    /// <param name="turnStarted">Called once the turn is durably recorded.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <param name="control">Steers or interrupts the running turn.</param>
    /// <param name="validatedItem">Receives each validated output item.</param>
    /// <param name="observeAsync">Receives every raw Responses event and is awaited before the run continues.</param>
    /// <param name="journalProjection">Rewrites input items before they are stored in the journal, for example to drop image bytes.</param>
    /// <param name="historyTurnLimit">Send only the last N stored turns as history, 1–4096.</param>
    /// <exception cref="CodexThreadConflictException">The thread changed since <paramref name="expectedRevision"/>.</exception>
    public async Task<CodexThreadAgentResult> RunAsync(Guid threadId, long expectedRevision, Guid turnId,
        string ownerId, CodexResponseOptions options, IReadOnlyList<JsonElement> input,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute,
        int maximumRequests = 8, Action<CodexResponseEvent>? observe = null,
        Action<CodexResponseDiagnostic>? observeDiagnostics = null,
        Func<CodexToolCall, bool>? canExecuteConcurrently = null,
        Func<CodexThreadDocument, CancellationToken, Task>? turnStarted = null,
        CancellationToken cancellationToken = default, CodexAgentControl? control = null,
        Func<JsonElement, CancellationToken, Task>? validatedItem = null,
        Func<CodexResponseEvent, CancellationToken, Task>? observeAsync = null,
        Func<JsonElement, JsonElement>? journalProjection = null, int? historyTurnLimit = null)
        => await RunCoreAsync(threadId, expectedRevision, turnId, ownerId, options, input, execute,
            maximumRequests, observe, observeDiagnostics, canExecuteConcurrently, turnStarted,
            cancellationToken, control, validatedItem, observeAsync, journalProjection, historyTurnLimit, null).ConfigureAwait(false);

    internal bool IsBoundTo(CodexThreadStore journal) => ReferenceEquals(store, journal);

    internal Task<CodexThreadAgentResult> RunRealtimeHandoffAsync(Guid threadId, long expectedRevision, string turnOwnerId,
        CodexRealtimeHandoffRequest handoff, CodexResponseOptions options, CodexAgentControl control,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute, int maximumRequests,
        Func<CodexThreadDocument, CancellationToken, Task>? turnStarted,
        Func<JsonElement, CancellationToken, Task>? validatedItem,
        Action<CodexResponseEvent>? observe, CancellationToken ct)
        => RunCoreAsync(threadId, expectedRevision, handoff.SubmissionId, turnOwnerId, options,
            [new CodexRealtimeDelegationInput(handoff.ContextualInput, []).ToResponseItem()], execute,
            maximumRequests, observe, null, null, turnStarted, ct, control, validatedItem, null, null, null, handoff);

    private async Task<CodexThreadAgentResult> RunCoreAsync(Guid threadId, long expectedRevision, Guid turnId,
        string ownerId, CodexResponseOptions options, IReadOnlyList<JsonElement> input,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute,
        int maximumRequests, Action<CodexResponseEvent>? observe, Action<CodexResponseDiagnostic>? observeDiagnostics,
        Func<CodexToolCall, bool>? canExecuteConcurrently,
        Func<CodexThreadDocument, CancellationToken, Task>? turnStarted, CancellationToken cancellationToken,
        CodexAgentControl? control, Func<JsonElement, CancellationToken, Task>? validatedItem,
        Func<CodexResponseEvent, CancellationToken, Task>? observeAsync,
        Func<JsonElement, JsonElement>? journalProjection, int? historyTurnLimit, CodexRealtimeHandoffRequest? handoff)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(execute);
        if (maximumRequests is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        if (historyTurnLimit is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(historyTurnLimit));
        if (options.AutomaticCompaction is { } configured)
        {
            CodexContextWindow.ValidatePolicy(configured);
            if (configured.MaximumInlineCompactions is < 0 or > 32)
                throw new ArgumentOutOfRangeException(nameof(configured.MaximumInlineCompactions));
            if (configured.BackgroundTokenThreshold is { } threshold &&
                (threshold <= 0 || threshold >= configured.TokenThreshold || BackgroundCompactions is null ||
                    historyTurnLimit is not null || journalProjection is not null))
                throw new ArgumentException("Background compaction requires a lower positive threshold, a scheduler and unmodified native history.", nameof(options));
        }
        if (options.Identity is not { } identity || !Guid.TryParse(identity.ThreadId, out var wireThread) ||
            wireThread != threadId || !Guid.TryParse(identity.TurnId, out var wireTurn) || wireTurn != turnId)
            throw new ArgumentException("Thread execution requires exact host-owned wire thread and turn identities.", nameof(options));
        identity.Validate();
        var before = await store.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (before.Revision != expectedRevision) throw new CodexThreadConflictException();
        if (control?.GoalTools is not null)
        {
            if (journalProjection is not null || !CodexThreadStore.ReadStateEligible(before))
                throw new InvalidOperationException("Native goal tools require an unredacted persistent user thread.");
            options = options with { Tools = CodexNativeGoalTools.Attach(options.Tools) };
        }
        if (control?.GoalSteeringEnabled == true && (journalProjection is not null || historyTurnLimit is not null || !CodexThreadStore.ReadStateEligible(before)))
            throw new InvalidOperationException("Goal steering requires complete, unredacted persistent user history.");
        var descendantGoal = control?.CapturedDescendantGoal;
        if (control?.DescendantGoalRootThreadId is { } goalRoot)
        {
            if (goalRoot == threadId) throw new ArgumentException("Descendant accounting cannot target the same thread.");
            var directory = await store.ReadAgentIdentitiesAsync(goalRoot, cancellationToken).ConfigureAwait(false);
            descendantGoal = await store.CaptureDescendantGoalAttributionAsync(directory, directory.GetActive(threadId), cancellationToken).ConfigureAwait(false);
        }
        if (descendantGoal is { } attribution && (journalProjection is not null || handoff is not null ||
            attribution.OriginThreadId != threadId || !Guid.TryParse(identity.ParentThreadId, out var parent) || parent != attribution.ParentThreadId))
            throw new ArgumentException("Descendant goal accounting requires the original unprojected child and exact parent identity.");
        var guard = new AccountCredentials(credentials, before.AccountId);
        var authOwner = CodexThreadCompactionScheduler.AuthOwner.From(
            await guard.GetCredentialsAsync(cancellationToken).ConfigureAwait(false));
        // Observed context is funding-owner scoped, including during ordinary foreground turns.
        guard = new AccountCredentials(credentials, before.AccountId, authOwner);
        if (historyTurnLimit is null && options.AutomaticCompaction is { } compaction && before.Turns.Length != 0 &&
            before.Turns.All(turn => turn.Status != "inProgress"))
        {
            var candidate = CodexThreadStore.CaptureCompaction(before, options);
            var upcoming = CodexThreadStore.RealtimeCarryoverBeforeTurn(before, before.Turns.Length).Concat(input).ToArray();
            var hardThresholdReached = candidate.History.Count != 0 && CodexContextWindow.Evaluate(options,
                [.. candidate.History, .. upcoming], CodexContextWindow.ReadObservation(before.Turns[^1], before),
                before.CompactionCheckpoint, authOwner).TokenLimitReached;
            var adopted = false;
            if (compaction.BackgroundTokenThreshold is not null && BackgroundCompactions is { } scheduler)
            {
                var ready = await scheduler.TakeAsync(store, budget, authOwner, before, options,
                    hardThresholdReached, cancellationToken).ConfigureAwait(false);
                if (ready is not null)
                {
                    if (CodexThreadCompactionScheduler.AuthOwner.From(await guard.GetCredentialsAsync(cancellationToken)
                        .ConfigureAwait(false)) != authOwner)
                        throw new CodexProtocolException("Background compaction auth owner changed; adoption refused.");
                    var latest = await store.ReadAsync(before.Id, cancellationToken).ConfigureAwait(false);
                    if (latest.Revision != before.Revision)
                    {
                        if (!CodexDetachedCompactionAccounting.OnlyReceiptsAppended(before, latest))
                            throw new CodexThreadConflictException();
                        before = latest;
                    }
                    before = await store.InstallBackgroundCompactionAsync(ready.Candidate, ready.Result, options,
                        before.Revision, compaction.Retention, cancellationToken).ConfigureAwait(false);
                    expectedRevision = before.Revision;
                    adopted = true;
                }
            }
            if (!adopted && hardThresholdReached)
            {
                // Account and budget admission remain mandatory even for discarded compaction attempts.
                var accounting = new CodexDetachedCompactionObserver(store, budget, candidate, authOwner);
                using var compactor = clientFactory is null ? new CodexClient(guard, endpoints, usageObserver: accounting)
                    : clientFactory(guard, accounting);
                if (!compactor.IsBoundTo(guard, accounting)) throw new InvalidOperationException("Compaction client must retain account and budget guards.");
                var result = await compactor.CompactAsync(options with { RequiredFirstFunction = null }, candidate.History,
                    cancellationToken, observeDiagnostics, candidate.ExecutedCallIds).ConfigureAwait(false);
                await guard.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
                var latest = await store.ReadAsync(before.Id, cancellationToken).ConfigureAwait(false);
                if (latest.Revision != before.Revision &&
                    !CodexDetachedCompactionAccounting.OnlyReceiptsAppended(before, latest))
                    throw new CodexThreadConflictException();
                before = await store.InstallBackgroundCompactionAsync(candidate, result, options,
                    latest.Revision, compaction.Retention, cancellationToken).ConfigureAwait(false);
                expectedRevision = before.Revision;
            }
        }
        using var ownedControl = control is null && (before.RealtimeSessions is { Length: > 0 } ||
            before.Turns.Any(t => t.RealtimeModeTransitions is { Length: > 0 })) ? new CodexAgentControl() : null;
        control ??= ownedControl;
        var persistedInput = journalProjection is null ? input : input.Select(journalProjection).ToArray();
        control?.InitializeInput(input, handoff?.SubmissionId.ToString(), handoff is null ? null : "realtime");
        CodexThreadDocument started;
        if (handoff is null)
            started = await store.StartTurnWithGoalAttributionAsync(threadId, expectedRevision, turnId, ownerId, persistedInput,
                identity.RootTurnId is { } root ? Guid.Parse(root) : turnId, cancellationToken,
                control?.ClientIds, control?.TurnTrigger, control?.GoalContinuationId, descendantGoal).ConfigureAwait(false);
        else
        {
            var accepted = await store.AdmitRealtimeHandoffAsync(threadId, expectedRevision, handoff.SessionId,
                handoff.OwnerId, handoff.AttachmentId, handoff.HandoffId, handoff.SubmissionId, null,
                ownerId, handoff.ContextualInput, cancellationToken).ConfigureAwait(false);
            if (!accepted.Admitted) throw new InvalidOperationException("Realtime handoff already committed; runner replay refused.");
            started = accepted.Thread;
        }
        using var execution = new Execution(store, started, turnId, ownerId, budget, input, journalProjection, historyTurnLimit, control, options, authOwner);
        control?.BindRealtimeAdmission(execution.AdmitRealtimeSteerAsync);
        control?.BindRealtimeMode(execution.CaptureRealtimeModeAsync);
        try
        {
            // The host may publish pending after the durable turn exists, before any
            // provider admission. Give it an owned snapshot, never the live runner state.
            if (turnStarted is not null)
                await turnStarted(store.ValidateSnapshot(CodexThreadStore.Encode(started)), cancellationToken).ConfigureAwait(false);
            using var client = clientFactory is null ? new CodexClient(guard, endpoints, usageObserver: execution)
                : clientFactory(guard, execution);
            if (!client.IsBoundTo(guard, execution))
                throw new InvalidOperationException("Thread client factory must retain account and journal admission guards.");
            async Task Validated(JsonElement item, CancellationToken ct)
            {
                await execution.ValidatedAsync(item, ct).ConfigureAwait(false);
                control?.ObserveRealtimeValidated(item);
                if (validatedItem is not null) await validatedItem(item, ct).ConfigureAwait(false);
            }
            var runner = new CodexAgent(client)
            {
                PrepareNativeHistory = historyTurnLimit is null && journalProjection is null && (options.AutomaticCompaction is not null || control?.GoalSteeringEnabled == true)
                    ? (settings, history, ct) => execution.PrepareWireHistoryAsync(client, settings, history, observeDiagnostics, ct,
                        BackgroundCompactions, authOwner, guard,
                        (candidate, effective, context) =>
                        {
                            if (BackgroundCompactions is { } scheduler)
                                ScheduleBackground(scheduler, candidate, effective, authOwner, context);
                        })
                    : null,
                PrepareNativeRequest = control?.GoalSteeringEnabled == true ? execution.PrepareGoalRequestAsync : null,
                BeginNativeTerminalToolBatch = control?.GoalSteeringEnabled == true ? execution.BeginTerminalGoalToolBatch : null,
                NativeTerminalSummary = control?.GoalSteeringEnabled == true ? execution.GoalTerminalSummary : null,
                ObserveNativeCompletion = historyTurnLimit is null && journalProjection is null
                    ? execution.ObserveNativeCompletion : null,
            };
            var result = await runner.RunWithToolsAsync(options, execution.Seed,
                (call, ct) => execution.ExecuteAsync(call, execute, ct, control), Validated,
                maximumRequests, item =>
                {
                    control?.ObserveRealtimeProvider(item);
                    observe?.Invoke(item);
                }, cancellationToken, observeDiagnostics,
                completeAfterSingleTool: control?.GoalTools is null ? null : execution.CompleteAfterGoalTool,
                canExecuteConcurrently: call => !(control?.GoalTools is not null && CodexNativeGoalTools.IsGoalCall(call))
                    && canExecuteConcurrently?.Invoke(call) == true,
                previouslyExecutedCallIds: CodexThreadStore.DispatchedToolCallIds(started), control: control,
                allowSilentCompletion: control?.GoalSteeringEnabled == true && control.GoalContinuationId is not null,
                observeAsync: async (item, ct) =>
                {
                    await execution.ObserveArtifactPreviewAsync(item, ct).ConfigureAwait(false);
                    if (observeAsync is not null) await observeAsync(item, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
            await execution.FinishAsync(result.History, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && historyTurnLimit is null && journalProjection is null &&
                BackgroundCompactions is { } background &&
                (execution.LastEffectiveSettings ?? options).AutomaticCompaction?.BackgroundTokenThreshold is not null)
            {
                try { ScheduleBackground(background, execution.Current, execution.LastEffectiveSettings ?? options, authOwner, execution.ContextWindow); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // The turn is already terminal. A failed speculative preparation is not
                    // a failed foreground turn and never authorizes another attempt.
                    background.RecordPreparationFailure();
                }
            }
            return new(execution.Current, result) { ContextWindow = execution.ContextWindow };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            try { await execution.FailAsync(error is OperationCanceledException).ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            { throw new AggregateException("Native turn failed and its terminal journal state is unconfirmed; inspect the same turn, never replay.", error, cleanup); }
            throw;
        }
    }

    private void ScheduleBackground(CodexThreadCompactionScheduler scheduler, CodexThreadDocument current,
        CodexResponseOptions settings, CodexThreadCompactionScheduler.AuthOwner owner, CodexContextWindowStatus? context)
        => ScheduleBackground(scheduler, CodexThreadStore.CaptureCompaction(current, settings), settings, owner, context);

    private void ScheduleBackground(CodexThreadCompactionScheduler scheduler, CodexThreadCompactionCandidate candidate,
        CodexResponseOptions settings, CodexThreadCompactionScheduler.AuthOwner owner, CodexContextWindowStatus? context)
    {
        if (settings.AutomaticCompaction is not { BackgroundTokenThreshold: { } lower } policy ||
            lower <= 0 || lower >= policy.TokenThreshold) return;
        // Copy caller-owned collections and use a separate transport. A speculative request
        // must not monopolize the foreground WebSocket lease or inherit its drain signal.
        var snapshot = settings with
        {
            Tools = Array.AsReadOnly(settings.Tools.Select(item => item.Clone()).ToArray()),
            OutputSchema = settings.OutputSchema?.Clone(), RequiredFirstFunction = null,
            WebSocketSession = null, PrewarmWebSocket = false, WebSocketDrain = default,
        };
        var estimate = context?.ProjectedActiveTokens ?? CodexThreadCompaction.EstimateTokens(snapshot, candidate.History);
        if (context is not null ? !context.BackgroundThresholdReached :
            policy.TokenLimitScope == CodexAutoCompactTokenLimitScope.BodyAfterPrefix || estimate < lower || estimate >= policy.TokenThreshold) return;
        scheduler.TrySchedule(store, budget, owner, candidate, async ct =>
        {
            var guard = new AccountCredentials(credentials, candidate.AccountId, owner);
            await guard.GetCredentialsAsync(ct).ConfigureAwait(false);
            var accounting = new CodexDetachedCompactionObserver(store, budget, candidate, owner);
            using var compactor = clientFactory is null ? new CodexClient(guard, endpoints, usageObserver: accounting)
                : clientFactory(guard, accounting);
            if (!compactor.IsBoundTo(guard, accounting))
                throw new InvalidOperationException("Background compaction must retain account and budget guards.");
            var result = await compactor.CompactAsync(snapshot, candidate.History, ct,
                previouslyExecutedCallIds: candidate.ExecutedCallIds).ConfigureAwait(false);
            await guard.GetCredentialsAsync(ct).ConfigureAwait(false);
            return result;
        });
    }

    private sealed class AccountCredentials(ICodexCredentialProvider source, string account,
        CodexThreadCompactionScheduler.AuthOwner? owner = null) : ICodexCredentialProvider
    {
        public async ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            var value = await source.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(value.AccountId, account, StringComparison.Ordinal) ||
                owner is not null && CodexThreadCompactionScheduler.AuthOwner.From(value) != owner)
                throw new CodexProtocolException("Native thread account or background auth owner changed; dispatch refused.");
            return value;
        }
    }

    private sealed class Execution : ICodexUsageObserver, ICodexValidatedResponseObserver, ICodexAbnormalResponseObserver, IDisposable
    {
        private readonly CodexThreadStore store;
        private readonly Guid turnId;
        private readonly string owner;
        private readonly ICodexUsageObserver budget;
        private readonly Func<JsonElement, JsonElement>? projection;
        private readonly CodexAgentControl? control;
        private CodexRealtimeModeCapture? pendingMode;
        private CodexGoalSteeringSnapshot? capturedGoal;
        private CodexGoalSteeringCapture? pendingGoalContext;
        private CodexGoalSteeringKey? preparedGoalKey;
        private long capturedGoalRevision;
        private bool capturedBudgetLimited;
        private bool capturedWrapUpAdmitted;
        private bool capturedAutomaticGoalStopped;
        private CodexStoredGoalWrapUp? pendingGoalWrapUp;
        private bool terminalGoalBatch;
        private readonly bool deferModeUntilProjection;
        private CodexStoredRealtimeSession? capturedModeSession;
        private bool modeCapturePending;
        private readonly CodexRealtimeArtifactStream artifacts = new();
        private IReadOnlyList<JsonElement>? preparedWireHistory;
        private CodexThreadCompactionCandidate? inlineCandidate;
        private string? inlineCallId;
        private int inlineAttempts;
        private bool changedWireProjection;
        private readonly CodexThreadCompactionScheduler.AuthOwner authOwner;
        private CodexStoredContextWindowObservation? contextObservation;
        private bool observedForegroundCompletion;
        private readonly bool allowMailSampling;
        private string? preparedInferenceRequestId;
        private int preparedInferenceOutputCount;
        private IReadOnlyList<JsonElement>? preparedMailInput;
        private readonly Dictionary<string, CodexStoredMailSampling> observedMailSampling = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CodexStoredInferenceCompletion> observedInferenceCompletions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CodexStoredInferenceAccounting> observedInlineCompaction = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (CodexStoredInferenceAccounting Binding, JsonElement Evidence)> observedAbnormal = new(StringComparer.Ordinal);
        internal CodexContextWindowStatus? ContextWindow { get; private set; }
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly ConcurrentDictionary<string, JsonElement> observed = new(StringComparer.Ordinal);
        internal CodexThreadDocument Current { get; private set; }
        internal IReadOnlyList<JsonElement> Seed { get; }
        internal CodexResponseOptions? LastEffectiveSettings { get; private set; }
        private CodexStoredTurn Turn => Current.Turns.Single(t => t.Id == turnId);
        public bool RequiresPreparedRequest => true;

        internal Execution(CodexThreadStore store, CodexThreadDocument current, Guid turnId, string owner, ICodexUsageObserver budget,
            IReadOnlyList<JsonElement> currentInput, Func<JsonElement, JsonElement>? projection, int? historyTurnLimit, CodexAgentControl? control,
            CodexResponseOptions settings, CodexThreadCompactionScheduler.AuthOwner authOwner)
        {
            this.store = store; Current = current; this.turnId = turnId; this.owner = owner; this.budget = budget; this.projection = projection; this.control = control;
            this.authOwner = authOwner;
            allowMailSampling = projection is null && historyTurnLimit is null;
            deferModeUntilProjection = projection is null && historyTurnLimit is null && settings.AutomaticCompaction is not null;
            if (projection is null && historyTurnLimit is null && current.Turns.Length > 1)
                contextObservation = CodexContextWindow.ReadObservation(current.Turns[^2], current);
            var firstTurnIndex = Math.Max(0, current.Turns.Length - (historyTurnLimit ?? current.Turns.Length));
            Seed = CodexThreadCompaction.Project(current, historyTurnLimit is null ? current.CompactionCheckpoint : null,
                settings.Model, CodexThreadCompaction.ScopeHash(settings), current.Turns.Length, firstTurnIndex, turnId, currentInput);
        }

        // Independent queued input, goal bookkeeping and realtime/public native-item observations
        // may advance the revision. Model history, settings, owners and dispatch receipts remain fenced.
        private static JsonElement ExecutionSnapshot(CodexThreadDocument document) =>
            CodexThreadStore.Encode(document with
            {
                RealtimeSessions = null, QueuedSubmissions = null, DetachedCompactionReceipts = null, Goal = null,
                GoalExecutionClock = null, DeferredGoalId = null,
                Revision = 1, UpdatedAt = default,
                Turns = document.Turns.Select(t => t with { NativeBackingArtifacts = null }).ToArray(),
            });

        private async Task<T> JournalAsync<T>(Func<CodexThreadDocument, Task<T>> mutate,
            Func<T, CodexThreadDocument> snapshot, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var result = await mutate(Current).ConfigureAwait(false);
                    Current = snapshot(result);
                    return result;
                }
                catch (CodexThreadConflictException) when (attempt < 3)
                {
                    var actual = await store.ReadAsync(Current.Id, ct).ConfigureAwait(false);
                    if (actual.RealtimeSessions is null && actual.Turns.All(t => t.NativeBackingArtifacts is null)
                        && CodexThreadStore.SameQueuedSubmissions(Current, actual)
                        && CodexThreadStore.SameGoalSnapshot(Current.Goal, actual.Goal)
                        && Current.GoalExecutionClock == actual.GoalExecutionClock && Current.DeferredGoalId == actual.DeferredGoalId &&
                        !CodexDetachedCompactionAccounting.Appended(Current, actual) ||
                        !JsonElement.DeepEquals(ExecutionSnapshot(Current), ExecutionSnapshot(actual)))
                        throw;
                    Current = actual;
                }
            }
        }

        private Task<CodexThreadDocument> JournalAsync(Func<CodexThreadDocument, Task<CodexThreadDocument>> mutate, CancellationToken ct)
            => JournalAsync(mutate, snapshot => snapshot, ct);

        private async Task RecordCapturedClientsAsync(CancellationToken ct)
        {
            if (control?.ClientIds is not { } clients) return;
            var persisted = Turn.ClientUserMessageIds ?? [];
            // A durable steering admission can be waiting for the next provider capture.
            if (persisted.Take(clients.Length).SequenceEqual(clients)) return;
            if (!clients.Take(persisted.Length).SequenceEqual(persisted))
                throw new InvalidOperationException("Realtime steering and captured user identities disagree.");
            await JournalAsync(doc => store.RecordInputClientsAsync(doc.Id, doc.Revision, turnId, owner, clients, ct), ct).ConfigureAwait(false);
        }

        internal async Task<CodexRealtimeHandoffAdmission> AdmitRealtimeSteerAsync(CodexRealtimeHandoffRequest handoff, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RecordCapturedClientsAsync(ct).ConfigureAwait(false);
                return await JournalAsync(doc => store.AdmitRealtimeHandoffAsync(doc.Id, doc.Revision,
                    handoff.SessionId, handoff.OwnerId, handoff.AttachmentId, handoff.HandoffId,
                    handoff.SubmissionId, turnId, owner, handoff.ContextualInput, ct), result => result.Thread, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        internal async Task ObserveArtifactPreviewAsync(CodexResponseEvent item, CancellationToken ct)
        {
            // A host projection may intentionally redact assistant content. Never retain
            // raw preview bytes behind that projection's back.
            if (projection is not null || artifacts.Observe(item) is not { } candidate) return;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await JournalAsync(doc => doc.RealtimeSessions?.Any(s => s.PresentationTurnIds?.Contains(turnId) == true) == true
                    ? store.RecordRealtimeArtifactPreviewFromSnapshotAsync(doc, turnId, owner, candidate, ct)
                    : store.RecordRealtimeArtifactPreviewAsync(doc.Id, doc.Revision, turnId, owner, candidate, ct), ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        internal async Task CaptureRealtimeModeAsync(List<JsonElement> history, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var actual = await store.ReadAsync(Current.Id, ct).ConfigureAwait(false);
                var turn = actual.Turns.Single(t => t.Id == turnId);
                if (turn.OwnerId != owner || turn.Status != "inProgress")
                    throw new InvalidOperationException("Realtime mode capture lost its native turn owner.");
                if (control?.GoalSteeringEnabled == true)
                {
                    capturedGoal = CodexGoalSteeringSnapshot.From(actual.Goal);
                    capturedGoalRevision = actual.Revision;
                    capturedBudgetLimited = CodexThreadStore.GoalBudgetLimitedForTurn(actual, turn);
                    capturedWrapUpAdmitted = turn.Dispatches.Any(receipt => receipt.GoalWrapUp is not null);
                    capturedAutomaticGoalStopped = turn.GoalContinuation is { } continuation &&
                        (continuation.OriginThreadId != actual.Id || actual.Goal?.Id != continuation.GoalId || actual.Goal.Status != "active");
                }
                if (deferModeUntilProjection)
                {
                    // Freeze the logical state with the captured steering/input generation.
                    // A slow compaction must not sample a different realtime lifecycle.
                    capturedModeSession = CodexRealtimeModeContext.CaptureSession(actual);
                    modeCapturePending = true;
                }
                else
                {
                    pendingMode = CodexRealtimeModeContext.Capture(actual, history, Seed.Count);
                    if (pendingMode is { } capture) history.Add(capture.Message);
                }
            }
            finally { gate.Release(); }
        }

        internal async Task<CodexNativeRequestPlan> PrepareGoalRequestAsync(CodexResponseOptions settings, CancellationToken ct)
        {
            pendingGoalWrapUp = null;
            if (capturedWrapUpAdmitted)
                return new(settings, "The authorized budget wrap-up has ended. Any remaining work requires a new authorized turn.");
            if (capturedAutomaticGoalStopped && !capturedBudgetLimited)
                return new(settings, "The original automatic goal is no longer active. No additional substantive work was started.");
            if (!capturedBudgetLimited || capturedGoal is not { } goal || goal.Key.Id is not { } id || goal.Key.TokenBudget is not { } limit)
                return new(settings);
            // The extra request is never implicit. It uses the ordinary provider reservation,
            // exact generation/usage fence, and no advertised tools or speculative compaction.
            var summary = GoalSummary(goal);
            if (control?.GoalWrapUpAuthority is not { } authorize)
                return new(settings, summary);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            bool allowed;
            try
            {
                allowed = await authorize(new(Current.AccountId, Current.Id, turnId, id, capturedGoalRevision,
                    goal.Key.Objective!, goal.TokensUsed, limit), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                return new(settings, summary);
            }
            if (!allowed) return new(settings, summary);
            pendingGoalWrapUp = new(id, capturedGoalRevision, goal.TokensUsed, goal.Key.Fingerprint);
            return new(settings with
            {
                Tools = [], RequiredFirstFunction = null, ParallelToolCalls = false,
                AutomaticCompaction = null, PrewarmWebSocket = false
            }, TerminalRequest: true);
        }

        internal bool BeginTerminalGoalToolBatch()
        {
            terminalGoalBatch = preparedGoalKey is not null && CodexThreadStore.GoalBudgetLimitedForTurn(Current, Turn);
            return terminalGoalBatch;
        }

        internal string GoalTerminalSummary() => GoalSummary(CodexGoalSteeringSnapshot.From(Current.Goal));

        private static string GoalSummary(CodexGoalSteeringSnapshot goal)
        {
            var tokens = goal.TokensUsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var budget = goal.Key.TokenBudget is { } limit
                ? " / " + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            return "Goal status: " + (goal.Key.Status ?? "cleared") + ". Tokens used: " + tokens + budget +
                ". No further substantive goal work was started; any remaining work requires a new authorized turn.";
        }

        internal void ObserveNativeCompletion(CodexResponseOptions settings, IReadOnlyList<JsonElement> input,
            CodexResponseEvent terminal)
        {
            LastEffectiveSettings = settings;
            observedForegroundCompletion = true;
            contextObservation = CodexContextWindow.Capture(settings, Current.CompactionCheckpoint, authOwner, input, terminal, contextObservation);
        }

        private async Task CaptureInferenceCompletionAsync(string requestId, CodexResponseOptions settings,
            string responseId, JsonElement response, IReadOnlyList<JsonElement> output, CancellationToken ct)
        {
            if (projection is not null || preparedInferenceRequestId != requestId ||
                settings.RequestKind != "turn" || CodexRealtimeProtocol.Text(response, "id") != responseId) return;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var receipt = Turn.Dispatches.SingleOrDefault(item => item.CallId == requestId);
                if (receipt is not { Kind: "inference", State: "dispatching", MailSampling: null, InferenceCompletion: null } ||
                    Turn.Output.Length != preparedInferenceOutputCount) return;
                observedInferenceCompletions.Add(requestId, CodexInferenceRecovery.Capture(Turn, responseId, response, output) with
                {
                    Accounting = CodexInferenceAccounting.Capture(Current, turnId, authOwner, settings.Model, response),
                });
                if (!allowMailSampling || preparedMailInput is not { } input ||
                    CodexRealtimeProtocol.Text(response, "model") is { } model && model != settings.Model) return;
                var metadata = new List<JsonElement?>();
                var projected = CodexThreadCompaction.Project(Current, Current.CompactionCheckpoint, settings.Model,
                    CodexThreadCompaction.ScopeHash(settings), Current.Turns.Length,
                    currentTurnId: turnId, includeActiveOutput: true, metadata: metadata);
                // Match the immutable pre-dispatch snapshot, not mutable caller history.
                // Unmatchable projections remain pending, never inferred by repeated text.
                if (!CodexThreadStore.SameHistory(projected, input)) return;
                var occurrences = CodexNativeMailSampling.Capture(Current, projected, metadata);
                if (occurrences.Length != 0)
                    observedMailSampling.Add(requestId, new(responseId, occurrences));
            }
            finally { gate.Release(); }
        }

        internal async Task<IReadOnlyList<JsonElement>> PrepareWireHistoryAsync(CodexClient client,
            CodexResponseOptions settings, List<JsonElement> originalHistory,
            Action<CodexResponseDiagnostic>? observeDiagnostics, CancellationToken ct,
            CodexThreadCompactionScheduler? scheduler, CodexThreadCompactionScheduler.AuthOwner authOwner,
            ICodexCredentialProvider credentialGuard,
            Action<CodexThreadCompactionCandidate, CodexResponseOptions, CodexContextWindowStatus> schedule)
        {
            var wire = await PrepareCompactedWireHistoryAsync(client, settings, originalHistory,
                observeDiagnostics, ct, scheduler, authOwner, credentialGuard, schedule).ConfigureAwait(false);
            if (!modeCapturePending && capturedGoal is null) return wire;

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Reconstruct world-state context against the actual retained prompt.
                // Both fragments are appended to the original durable history, never
                // substituted for an observed prefix.
                if (modeCapturePending)
                {
                    pendingMode = CodexRealtimeModeContext.CaptureProjected(Current, capturedModeSession, wire,
                        originalHistory.Count - Seed.Count);
                    modeCapturePending = false;
                    capturedModeSession = null;
                    if (pendingMode is { } capture)
                    {
                        originalHistory.Add(capture.Message);
                        wire = Array.AsReadOnly(wire.Append(capture.Message).ToArray());
                    }
                }
                if (capturedGoal is { } goal)
                {
                    preparedGoalKey = goal.Key;
                    capturedGoal = null;
                    pendingGoalContext = CodexGoalSteeringContext.Capture(Current, goal, wire,
                        originalHistory.Count - Seed.Count);
                    if (pendingGoalContext is { } context)
                    {
                        originalHistory.Add(context.Message);
                        wire = Array.AsReadOnly(wire.Append(context.Message).ToArray());
                    }
                }
                await SynchronizeAsync(originalHistory.Skip(Seed.Count).ToArray(), ct).ConfigureAwait(false);
                preparedWireHistory = wire;
                return preparedWireHistory;
            }
            finally { gate.Release(); }
        }

        private async Task<IReadOnlyList<JsonElement>> PrepareCompactedWireHistoryAsync(CodexClient client,
            CodexResponseOptions settings, IReadOnlyList<JsonElement> originalHistory,
            Action<CodexResponseDiagnostic>? observeDiagnostics, CancellationToken ct,
            CodexThreadCompactionScheduler? scheduler, CodexThreadCompactionScheduler.AuthOwner authOwner,
            ICodexCredentialProvider credentialGuard,
            Action<CodexThreadCompactionCandidate, CodexResponseOptions, CodexContextWindowStatus> schedule)
        {
            CodexThreadCompactionCandidate? candidate = null;
            CodexThreadDocument? captured = null;
            var hardThresholdReached = false;
            LastEffectiveSettings = settings;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (originalHistory.Count < Seed.Count ||
                    Seed.Where((item, index) => !JsonElement.DeepEquals(item, originalHistory[index])).Any())
                    throw new CodexProtocolException("Native runner rewrote its original history.");
                // Persist tool outputs and any captured steering before compaction can remove
                // them from the model-visible context. The agent's original history is never changed.
                await SynchronizeAsync(originalHistory.Skip(Seed.Count).ToArray(), ct).ConfigureAwait(false);
                await RecordCapturedClientsAsync(ct).ConfigureAwait(false);
                var scope = CodexThreadCompaction.ScopeHash(settings);
                var checkpoint = Current.CompactionCheckpoint;
                var wireHistory = changedWireProjection || checkpoint?.ActiveBoundary?.TurnId == turnId
                    ? CodexThreadCompaction.Project(Current, checkpoint, settings.Model, scope, Current.Turns.Length,
                        currentTurnId: turnId, includeActiveOutput: true)
                    : originalHistory;
                preparedWireHistory = Array.AsReadOnly(wireHistory.Select(item => item.Clone()).ToArray());
                if (settings.AutomaticCompaction is { } policy)
                {
                    if (policy.MaximumInlineCompactions is < 0 or > 32)
                        throw new ArgumentOutOfRangeException(nameof(policy.MaximumInlineCompactions));
                    ContextWindow = CodexContextWindow.Evaluate(settings, preparedWireHistory, contextObservation, checkpoint, authOwner);
                    hardThresholdReached = preparedWireHistory.Count != 0 && ContextWindow.TokenLimitReached;
                    var speculative = scheduler is not null && ContextWindow.BackgroundThresholdReached;
                    if (Turn.Output.Length > 0 && (hardThresholdReached || speculative))
                    {
                        captured = Current;
                        if (checkpoint?.ActiveBoundary?.TurnId != turnId ||
                            Turn.Output.Length > checkpoint.ActiveBoundary.OutputCount)
                        {
                            candidate = CodexThreadStore.CaptureActiveCompaction(Current, turnId, owner, settings);
                            if (candidate is not null && !CodexThreadStore.SameHistory(candidate.History, preparedWireHistory))
                                candidate = null; // Raw prompt and durable history must agree before an attempt.
                        }
                    }
                }
            }
            finally { gate.Release(); }

            // Do not wait on a speculative request while holding the journal gate:
            // realtime steering must be able to append input during the wait.
            if (captured is not null && candidate is not null && scheduler is not null &&
                settings.AutomaticCompaction is { BackgroundTokenThreshold: not null } backgroundPolicy)
            {
                var ready = await scheduler.TakeAsync(store, budget, authOwner, captured, settings,
                    hardThresholdReached, ct, turnId, owner).ConfigureAwait(false);
                if (ready is not null)
                {
                    if (CodexThreadCompactionScheduler.AuthOwner.From(await credentialGuard.GetCredentialsAsync(ct)
                        .ConfigureAwait(false)) != authOwner)
                        throw new CodexProtocolException("Background compaction auth owner changed; adoption refused.");
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        try
                        {
                            Current = await JournalAsync(doc => store.InstallActiveBackgroundCompactionAsync(ready.Candidate, ready.Result,
                                settings, doc.Revision, turnId, owner, backgroundPolicy.Retention, ct), ct).ConfigureAwait(false);
                            changedWireProjection = true;
                            contextObservation = null;
                            preparedWireHistory = Array.AsReadOnly(CodexThreadCompaction.Project(Current,
                                Current.CompactionCheckpoint, settings.Model, CodexThreadCompaction.ScopeHash(settings),
                                Current.Turns.Length, currentTurnId: turnId, includeActiveOutput: true).ToArray());
                            return preparedWireHistory;
                        }
                        catch (CodexThreadConflictException)
                        {
                            // A speculative result is never installed over concurrent journal changes.
                            // Its provider accounting is already settled by the background client.
                            scheduler.RecordDiscard();
                        }
                    }
                    finally { gate.Release(); }
                }
                if (!hardThresholdReached && !ct.IsCancellationRequested)
                {
                    try { schedule(candidate, settings, ContextWindow!); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { scheduler.RecordPreparationFailure(); }
                }
            }

            if (!hardThresholdReached || candidate is null || settings.AutomaticCompaction is not { } inlinePolicy ||
                inlineAttempts >= inlinePolicy.MaximumInlineCompactions)
                return preparedWireHistory!;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Current.Revision != candidate.Revision) return preparedWireHistory!;
                inlineAttempts++;
                inlineCandidate = candidate;
                inlineCallId = null;
            }
            finally { gate.Release(); }
            try
            {
                var result = await client.CompactAsync(settings with
                {
                    RequiredFirstFunction = null,
                    WebSocketDrain = default,
                }, candidate.History, ct, observeDiagnostics, candidate.ExecutedCallIds).ConfigureAwait(false);
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    try
                    {
                        Current = await store.InstallActiveCompactionAsync(candidate, result,
                            inlineCallId ?? throw new CodexProtocolException("Missing compaction inference receipt."),
                            owner, settings.AutomaticCompaction!.Retention, ct).ConfigureAwait(false);
                        changedWireProjection = true;
                        contextObservation = null;
                        preparedWireHistory = Array.AsReadOnly(Current.CompactionCheckpoint!.Projection
                            .Select(item => item.Clone()).ToArray());
                    }
                    catch (CodexThreadConflictException)
                    {
                        // The provider result has already been settled. A newer journal revision
                        // invalidates the candidate; never install it or repeat the paid request.
                    }
                }
                finally { gate.Release(); }
            }
            finally
            {
                inlineCandidate = null;
                inlineCallId = null;
            }
            return preparedWireHistory!;
        }

        public Task ReserveAsync(string requestId, string model, CancellationToken ct) => budget.ReserveAsync(requestId, model, ct);
        public Task<CodexResponsesIdentity?> ReserveWithIdentityAsync(string requestId, string model, CancellationToken ct) =>
            budget.ReserveWithIdentityAsync(requestId, model, ct);

        public async Task PreparedAsync(string id, string model, JsonElement request, CancellationToken ct)
        {
            var input = request.GetProperty("input").EnumerateArray().Select(x => x.Clone()).ToArray();
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (inlineCandidate is { } candidate)
                {
                    if (Current.Revision != candidate.Revision || input.Length != candidate.History.Count + 1 ||
                        !CodexThreadStore.SameHistory(input.Take(input.Length - 1).ToArray(), candidate.History) ||
                        CodexRealtimeProtocol.Text(input[^1], "type") != "compaction_trigger")
                        throw new CodexThreadConflictException();
                    // No CAS retry: compaction must be admitted against its exact captured prefix.
                    var compactAdmission = capturedGoal is { } captured
                        ? await store.AdmitSteeredInferenceAsync(Current.Id, candidate.Revision, turnId, owner, id, request, captured.Key, ct).ConfigureAwait(false)
                        : await store.AdmitAsync(Current.Id, candidate.Revision, turnId, owner, id, "inference", request, ct).ConfigureAwait(false);
                    if (!compactAdmission.Admitted) throw new CodexProtocolException("Compaction identity already dispatched; replay refused.");
                    Current = compactAdmission.Thread;
                    inlineCallId = id;
                }
                else
                {
                    if (preparedWireHistory is { } wire)
                    {
                        if (!CodexThreadStore.SameHistory(input, wire))
                            throw new CodexProtocolException("Native request rewrote the prepared wire history.");
                    }
                    else
                    {
                        if (input.Length < Seed.Count || Seed.Where((item, index) => !JsonElement.DeepEquals(item, input[index])).Any())
                            throw new CodexProtocolException("Native request rewrote the captured thread history.");
                        await SynchronizeAsync(input.Skip(Seed.Count).ToArray(), ct).ConfigureAwait(false);
                        await RecordCapturedClientsAsync(ct).ConfigureAwait(false);
                    }
                    var admission = await JournalAsync(doc => preparedGoalKey is { } key
                        ? store.AdmitSteeredInferenceAsync(doc.Id, doc.Revision, turnId, owner, id, request, key, ct, pendingGoalWrapUp)
                        : store.AdmitAsync(doc.Id, doc.Revision, turnId, owner, id, "inference", request, ct),
                        result => result.Thread, ct).ConfigureAwait(false);
                    if (!admission.Admitted) throw new CodexProtocolException("Inference identity already dispatched; replay refused.");
                    pendingGoalWrapUp = null;
                    preparedMailInput = allowMailSampling && input.Any(CodexNativeMailSampling.IsMail) ? input : null;
                    preparedInferenceRequestId = projection is null ? id : null;
                    preparedInferenceOutputCount = Turn.Output.Length;
                }
            }
            finally { gate.Release(); }
            await budget.PreparedAsync(id, model, request, ct).ConfigureAwait(false);
        }

        public async Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        {
            Remember(id, response);
            await budget.CompleteAsync(id, model, response, ct).ConfigureAwait(false);
            await SettleAsync(id, false, ct).ConfigureAwait(false);
        }

        public async Task CompleteValidatedAsync(string id, CodexResponseOptions settings, string responseId,
            JsonElement response, IReadOnlyList<JsonElement> output, CancellationToken ct)
        {
            Remember(id, response);
            // Commit the validated terminal and any sampling proof before external
            // accounting. A host can reconcile the same reservation after a crash;
            // neither a failed observer nor a missing output append permits replay.
            try
            {
                if (settings.RequestKind == "compaction" && inlineCandidate is not null &&
                    inlineCallId == id && projection is null && CodexRealtimeProtocol.Text(response, "id") == responseId)
                    observedInlineCompaction.Add(id, CodexInferenceAccounting.Capture(Current, turnId, authOwner, settings.Model, response)
                        with { RequestKind = "compaction" });
                else
                    await CaptureInferenceCompletionAsync(id, settings, responseId, response, output, ct).ConfigureAwait(false);
                await SettleAsync(id, false, ct).ConfigureAwait(false);
            }
            finally { await budget.CompleteAsync(id, settings.Model, response, ct).ConfigureAwait(false); }
        }
        private bool CaptureAbnormalAccounting(string id, CodexResponseOptions settings, string eventType,
            string responseId, JsonElement evidence, CodexObservedUsage? usage = null)
        {
            // The caller's private redaction projection must never be bypassed by a recovery sidecar.
            if (projection is not null || (settings.RequestKind == "compaction"
                    ? inlineCandidate is null || inlineCallId != id
                    : settings.RequestKind != "turn" || preparedInferenceRequestId != id))
                return false;
            var binding = CodexAbnormalInferenceAccounting.Classify(
                CodexInferenceAccounting.Capture(Current, turnId, authOwner, settings.Model, evidence) with
                { RequestKind = settings.RequestKind == "compaction" ? "compaction" : null }, eventType, usage);
            if (!CodexInferenceAccounting.Valid(binding, turnId) ||
                !CodexAbnormalInferenceAccounting.ValidId(responseId) ||
                CodexAbnormalInferenceAccounting.ResponseId(binding, evidence) != responseId) return false;
            observedAbnormal.Add(id, (binding, evidence.Clone()));
            return true;
        }

        public async Task CompleteUnvalidatedAsync(string id, CodexResponseOptions settings, string eventType,
            string responseId, JsonElement response, CancellationToken ct)
        {
            if (!CaptureAbnormalAccounting(id, settings, eventType, responseId, response))
            {
                await CompleteAsync(id, settings.Model, response, ct).ConfigureAwait(false);
                return;
            }
            try { await SettleAsync(id, false, ct).ConfigureAwait(false); }
            finally { await budget.CompleteAsync(id, settings.Model, response, ct).ConfigureAwait(false); }
        }

        public async Task ObserveIncompleteAsync(string id, CodexResponseOptions settings, JsonElement payload,
            CodexObservedUsage usage, CancellationToken ct)
        {
            if (usage.ResponseId is not { } responseId ||
                !CaptureAbnormalAccounting(id, settings, usage.EventType, responseId, payload, usage))
            {
                await IncompleteWithUsageAsync(id, settings.Model, usage, ct).ConfigureAwait(false);
                return;
            }
            try { await SettleAsync(id, false, ct, reconcile: true).ConfigureAwait(false); }
            finally { await budget.IncompleteWithUsageAsync(id, settings.Model, usage, ct).ConfigureAwait(false); }
        }

        public async Task IncompleteAsync(string id, string model, CancellationToken ct)
        {
            try { await budget.IncompleteAsync(id, model, ct).ConfigureAwait(false); }
            finally { await SettleAsync(id, false, ct, reconcile: true).ConfigureAwait(false); }
        }
        public async Task IncompleteWithUsageAsync(string id, string model, CodexObservedUsage? usage, CancellationToken ct)
        {
            try { await budget.IncompleteWithUsageAsync(id, model, usage, ct).ConfigureAwait(false); }
            finally { await SettleAsync(id, false, ct, reconcile: true).ConfigureAwait(false); }
        }
        public async Task NotDispatchedAsync(string id, string model, CancellationToken ct)
        {
            try { await budget.NotDispatchedAsync(id, model, ct).ConfigureAwait(false); }
            finally { await SettleAsync(id, true, ct, reconcile: true).ConfigureAwait(false); }
        }

        private void Remember(string id, JsonElement result)
        {
            result = projection is null ? result : projection(result);
            // Once a result has returned, caller cancellation must not erase that fact.
            if (!observed.TryAdd(id, result.Clone()) && !JsonElement.DeepEquals(observed[id], result))
                throw new CodexProtocolException("Conflicting native dispatch results.");
        }
        private async Task SettleAsync(string id, bool notDispatched, CancellationToken ct, bool reconcile = false)
        {
            CodexThreadDocument? descendantReceipt = null;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Normal execution keeps its expected revision. Only terminal/non-dispatch
                // reconciliation may reload; it never enables another provider/tool dispatch.
                if (reconcile) Current = await store.ReadAsync(Current.Id, ct).ConfigureAwait(false);
                if (Turn.OwnerId != owner) throw new InvalidOperationException("Native turn owner changed.");
                var prior = Turn.Dispatches.SingleOrDefault(d => d.CallId == id);
                if (prior is null || prior.State is "succeeded" or "failed" || prior.AbnormalAccounting is not null) return;
                var result = observed.GetValueOrDefault(id);
                var known = result.ValueKind != JsonValueKind.Undefined;
                if (observedAbnormal.TryGetValue(id, out var abnormal))
                    await JournalAsync(doc => store.SettleAbnormalInferenceAccountingAsync(doc.Id, doc.Revision, turnId,
                        owner, id, abnormal.Evidence, abnormal.Binding, ct), ct).ConfigureAwait(false);
                else if (known && observedInferenceCompletions.TryGetValue(id, out var completion))
                    await JournalAsync(doc => store.SettleValidatedInferenceAsync(doc.Id, doc.Revision, turnId,
                        owner, id, result, completion, observedMailSampling.GetValueOrDefault(id), ct), ct).ConfigureAwait(false);
                else if (known && observedInlineCompaction.TryGetValue(id, out var compaction))
                    await JournalAsync(doc => store.SettleInlineCompactionAccountingAsync(doc.Id, doc.Revision,
                        turnId, owner, id, result, compaction, ct), ct).ConfigureAwait(false);
                else
                    await JournalAsync(doc => store.SettleAsync(doc.Id, doc.Revision, turnId, owner, id,
                        known ? "succeeded" : notDispatched ? "failed" : "unknown",
                        known ? result : notDispatched ? Json(new JsonObject { ["notDispatched"] = true }) : null, ct), ct).ConfigureAwait(false);
                if (prior.GoalInference?.GoalThreadId is not null) descendantReceipt = Current;
            }
            finally { gate.Release(); }
            // The child receipt is already durable. Root accounting uses its stable identity
            // and never holds the child gate. If the root CAS is ambiguous, stop further child
            // dispatch and let the host reconcile this exact receipt instead of replaying it.
            if (descendantReceipt is not null)
                _ = await store.AccountDescendantGoalAsync(descendantReceipt, turnId, id, ct).ConfigureAwait(false);
        }

        internal async Task<CodexToolOutput> ExecuteAsync(CodexToolCall call,
            Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute, CancellationToken ct,
            CodexAgentControl? control = null)
        {
            var request = Json(new JsonObject { ["kind"] = call.Kind.ToString(), ["name"] = call.Name,
                ["namespace"] = call.Namespace, ["input"] = call.Input });
            var goalPolicy = control?.GoalTools;
            var nativeGoal = goalPolicy is not null && CodexNativeGoalTools.IsGoalCall(call);
            CodexNativeToolArtifactContext? artifacts = null;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var admitted = await JournalAsync(doc => preparedGoalKey is { } key
                    ? store.AdmitSteeredToolAsync(doc.Id, doc.Revision, turnId, owner, call, key, nativeGoal, terminalGoalBatch, ct)
                    : nativeGoal ? store.AdmitGoalToolAsync(doc.Id, doc.Revision, turnId, owner, call, ct)
                    : store.AdmitAsync(doc.Id, doc.Revision, turnId, owner, call.Id, "tool", request, ct),
                    result => result.Thread, ct).ConfigureAwait(false);
                if (!admitted.Admitted) throw new CodexProtocolException("Tool identity already dispatched; replay refused.");
                if (Turn.Dispatches.Single(receipt => receipt.CallId == call.Id).GoalToolSuppression is { } suppression)
                    return CodexNativeGoalTools.Error(suppression);
                // Do not retain raw public artifact content behind an explicit host redaction
                // projection. The existing model-output callback remains available unchanged.
                if (projection is null && !nativeGoal)
                {
                    var itemId = CodexThreadStore.RealtimeTimeline(Turn, true).Select((item, index) =>
                        (Item: item, Id: CodexRealtimeProtocol.Text(item, "id") ?? Turn.Id + ":" + index))
                        .SingleOrDefault(x => CodexRealtimeProtocol.Text(x.Item, "type") is "function_call" or "custom_tool_call" &&
                            CodexRealtimeProtocol.Text(x.Item, "call_id") == call.Id).Id;
                    if (itemId is not null)
                        artifacts = new(Current.Id, turnId, Current.AccountId, call.Id, itemId,
                            async (stage, item, token) =>
                            {
                                await gate.WaitAsync(token).ConfigureAwait(false);
                                try
                                {
                                    await JournalAsync(doc => store.ObserveNativeBackingArtifactAsync(doc.Id, doc.Revision,
                                        turnId, owner, call.Id, stage, item, token), result => result.Thread, token).ConfigureAwait(false);
                                }
                                finally { gate.Release(); }
                            });
                }
            }
            finally { gate.Release(); }
            var entered = false;
            async Task<CodexToolOutput> Invoke(CancellationToken token)
            {
                entered = true;
                CodexToolOutput output;
                if (nativeGoal)
                {
                    var local = await InvokeGoalToolAsync(call, goalPolicy!, token).ConfigureAwait(false);
                    output = local.Output;
                    if (local.Committed) return output;
                }
                else
                {
                    try { output = await execute(artifacts is null ? call : call with { NativeArtifacts = artifacts }, token).ConfigureAwait(false); }
                    finally { if (artifacts is not null) await artifacts.CloseAsync().ConfigureAwait(false); }
                }
                var receipt = Json(new JsonObject { ["type"] = call.Kind == CodexToolCallKind.Custom ? "custom_tool_call_output" : "function_call_output",
                    ["call_id"] = call.Id, ["output"] = output.Content() });
                Remember(call.Id, receipt);
                await SettleAsync(call.Id, false, token).ConfigureAwait(false);
                return output;
            }
            try { return await (control is null ? Invoke(ct) : control.ToolAsync(Invoke, ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (control is not null && !entered)
            {
                // Durable journal admission is not physical callback dispatch. An interruption
                // winning this boundary must not start the callback after its cancellation.
                if (artifacts is not null) await artifacts.CloseAsync().ConfigureAwait(false);
                await SettleAsync(call.Id, true, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<(CodexToolOutput Output, bool Committed)> InvokeGoalToolAsync(
            CodexToolCall call, CodexGoalToolPolicy policy, CancellationToken ct)
        {
            CodexNativeGoalTools.Command command;
            try { command = CodexNativeGoalTools.Prepare(call, policy.MaximumTokenBudget); }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                return (CodexNativeGoalTools.Error("Invalid goal tool arguments."), false);
            }

            CodexThreadDocument snapshot;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { snapshot = Current; }
            finally { gate.Release(); }
            if (command.Name == CodexNativeGoalTools.Update && preparedGoalKey is { } sampled && !sampled.SameObjective(snapshot.Goal))
                return (CodexNativeGoalTools.Error("The goal objective or generation changed after sampling; inspect the new goal first."), false);
            if (CodexNativeGoalTools.Rejection(command, snapshot.Goal) is { } rejection)
                return (CodexNativeGoalTools.Error(rejection), false);

            var authorized = command.Name == CodexNativeGoalTools.Get;
            if (!authorized)
            {
                // Authorization must not hold the journal gate, and cannot be replayed
                // against a different goal or a later document revision.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    authorized = await policy.Authorize(new(snapshot.AccountId, snapshot.Id, turnId,
                        call.Id, snapshot.Revision, snapshot.Goal?.Id, snapshot.Goal?.Status,
                        command.Name, command.Arguments.Clone()), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException && !ct.IsCancellationRequested)
                {
                    return (CodexNativeGoalTools.Error("Goal mutation authorization could not be confirmed."), false);
                }
                if (!authorized) return (CodexNativeGoalTools.Error("Goal mutation was not authorized."), false);
            }

            CodexThreadStore.GoalToolCommit committed;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Current.Revision != snapshot.Revision)
                    return (CodexNativeGoalTools.Error("Goal snapshot changed; inspect it before another request."), false);
                try
                {
                    committed = await store.SettleGoalToolAsync(snapshot.Id, snapshot.Revision, turnId, owner,
                        call, command, authorized, ct).ConfigureAwait(false);
                    Current = committed.Thread;
                }
                catch (CodexThreadConflictException)
                {
                    return (CodexNativeGoalTools.Error("Goal snapshot changed; inspect it before another request."), false);
                }
            }
            finally { gate.Release(); }
            if (committed.Mutated) await policy.Notify(committed.Thread, turnId).ConfigureAwait(false);
            return (committed.Output, true);
        }

        internal string? CompleteAfterGoalTool(CodexToolCall call, string output)
        {
            // A terminal goal-tool mutation can finish an automatic goal turn locally.
            // Do not grant another provider or tool dispatch after the goal stopped.
            if (!CodexNativeGoalTools.IsGoalCall(call) || call.Name != CodexNativeGoalTools.Update ||
                Turn.GoalContinuation is not { } continuation || Current.Goal is not { } goal ||
                goal.Id != continuation.GoalId || goal.Status == "active" ||
                Turn.Dispatches.SingleOrDefault(item => item.CallId == call.Id)?.GoalToolResult is not { } receipt ||
                receipt.OriginThreadId != Current.Id || receipt.GoalId != goal.Id || receipt.Status != goal.Status)
                return null;
            var tokens = goal.TokensUsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var budget = goal.TokenBudget is { } limit
                ? " / " + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            return "Goal status: " + goal.Status + ". Tokens used: " + tokens + budget + ".";
        }

        internal async Task ValidatedAsync(JsonElement item, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await SynchronizeAsync([.. Turn.Output, item], ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        private async Task SynchronizeAsync(IReadOnlyList<JsonElement> output, CancellationToken ct)
        {
            if (projection is not null) output = output.Select(projection).ToArray();
            if (output.Count == Turn.Output.Length && !output.Where((item, i) => !JsonElement.DeepEquals(item, Turn.Output[i])).Any()) return;
            var mode = pendingMode;
            var goalContext = pendingGoalContext;
            await JournalAsync(doc => store.SynchronizeOutputCoreAsync(doc.Id, doc.Revision, turnId, owner, output, mode, ct, goalContext), ct).ConfigureAwait(false);
            pendingMode = null;
            pendingGoalContext = null;
        }
        internal async Task FinishAsync(IReadOnlyList<JsonElement> history, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var output = history.Skip(Seed.Count).ToArray();
                await SynchronizeAsync(output, ct).ConfigureAwait(false);
                await RecordCapturedClientsAsync(ct).ConfigureAwait(false);
                CodexStoredContextWindowObservation? snapshot = null;
                if (observedForegroundCompletion && LastEffectiveSettings is { } settings)
                {
                    var projected = CodexThreadCompaction.Project(Current, Current.CompactionCheckpoint, settings.Model,
                        CodexThreadCompaction.ScopeHash(settings), Current.Turns.Length,
                        currentTurnId: turnId, includeActiveOutput: true);
                    ContextWindow = CodexContextWindow.Evaluate(settings, projected, contextObservation,
                        Current.CompactionCheckpoint, authOwner);
                    snapshot = ContextWindow.Observation;
                }
                await JournalAsync(doc => store.CompleteWithContextAsync(doc.Id, doc.Revision, turnId, owner,
                    "completed", Turn.Output, snapshot, ct), ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        internal async Task FailAsync(bool interrupted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Current = await store.ReadAsync(Current.Id, timeout.Token).ConfigureAwait(false);
            if (Turn.OwnerId != owner) throw new InvalidOperationException("Native turn owner changed.");
            if (Turn.Status != "inProgress") return;
            foreach (var id in Turn.Dispatches.Where(d => d.State is "dispatching" or "unknown").Select(d => d.CallId).ToArray())
                await SettleAsync(id, false, timeout.Token).ConfigureAwait(false);
            await JournalAsync(doc => store.CompleteAsync(doc.Id, doc.Revision, turnId, owner,
                interrupted ? "interrupted" : "failed", CodexThreadStore.RecoverGoalToolOutputs(Turn, CodexInferenceRecovery.RecoverOutput(Turn)), timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        public void Dispose() => gate.Dispose();
    }

    private static JsonElement Json(JsonNode node)
    { using var body = JsonDocument.Parse(node.ToJsonString()); return body.RootElement.Clone(); }
}
