using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Native voice/reasoning feedback routing.</summary>
public enum CodexRealtimeReasoningChannelMode
{
    /// <summary>Use the provider's implicit Thinking channel.</summary>
    Thinking,
    /// <summary>Send backend output on Commentary.</summary>
    Commentary,
    /// <summary>Route BEM tagged analysis/commentary quietly and answers speakably.</summary>
    BemTags,
}

/// <summary>Host-selected orchestration policy. Reasoning credentials, tool authorization and
/// model options are separate from the voice transport. No filesystem scan is performed.</summary>
public sealed record CodexRealtimeCoordinatorOptions
{
    /// <summary>Do not automatically run or emit feedback for client-managed delegations.</summary>
    public bool ClientManagedHandoffs { get; init; }
    /// <summary>Allow PCM on the control connection only when the host owns direct audio transport.</summary>
    public bool DirectAudio { get; init; }
    /// <summary>Maximum inference requests in one native reasoning turn.</summary>
    public int MaximumReasoningRequests { get; init; } = 8;
    /// <summary>V3 reasoning feedback channel selection.</summary>
    public CodexRealtimeReasoningChannelMode ChannelMode { get; init; }
    /// <summary>Emit only explicitly authored reasoning status, never hidden reasoning.</summary>
    public bool BackendReasoningStatus { get; init; }
    /// <summary>Emit complete backend messages as conversation items rather than delegation updates.</summary>
    public bool ResponsesAsItems { get; init; }
    /// <summary>Optional prefix for complete backend conversation items.</summary>
    public string? ResponseItemPrefix { get; init; }
    /// <summary>Optional host BEM prefix overrides.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ChannelPrefixes { get; init; }
}

/// <summary>Binds an already registered logical realtime session to the same native thread and
/// RPC catalog. Transport events, reasoning work, feedback, audio and host observers have
/// separate bounded lanes; no tool callback is awaited on the socket reader.</summary>
public sealed class CodexRealtimeCoordinator : IAsyncDisposable
{
    private readonly CodexThreadStore store;
    private readonly CodexThreadAgent agent;
    private readonly CodexThreadRpc rpc;
    private readonly CodexRealtimeSessionController controller;
    private readonly Guid threadId;
    private readonly string sessionId;
    private readonly string ownerId;
    private readonly string accountId;
    private readonly Func<CodexThreadDocument, CodexResponseOptions> reasoningOptions;
    private readonly Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute;
    private readonly CodexRealtimeCoordinatorOptions options;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private readonly CodexRealtimeFeedbackStream stream;
    private RunState? current;
    private Binding? activeFeedback;
    private Binding? responseFeedback;
    private string? feedbackRequestId;
    private Task? ownedReasoning;
    private Exception? reasoningFailure;
    private bool disposed;

    private sealed record Handoff(CodexRealtimeEvent Event, Guid Attachment, CodexRealtimeConnection Connection);
    private sealed record Binding(CodexRealtimeFeedbackHandoff Handoff, Guid TurnId,
        CodexRealtimeConnection Connection, CodexRealtimeConnection.FeedbackAdmission Admission, RunState Run, string WireHandoffId)
    {
        internal bool HasOutput;
        internal bool Completed;
    }
    private sealed record Feedback(Binding Binding, string Text, string? Channel, CodexRealtimeFeedbackKind Kind = CodexRealtimeFeedbackKind.Progress, string? WireId = null);
    private sealed record Control(string Text, bool Speech);

    private sealed class RunState
    {
        internal readonly CodexRealtimeBoundedQueue<Handoff> Handoffs = new(64, 4 * 1024 * 1024, "handoff");
        internal readonly CodexRealtimeBoundedQueue<CodexRealtimeEvent> Events = new(256, 8 * 1024 * 1024, "observer");
        internal readonly CodexRealtimeBoundedQueue<CodexRealtimeEvent> AudioEvents = new(256, 8 * 1024 * 1024, "audio observer");
        internal readonly CodexRealtimeBoundedQueue<byte[]> Audio = new(256, 8 * 1024 * 1024, "audio");
        internal readonly CodexRealtimeBoundedQueue<Control> Control = new(64, 1024 * 1024, "control");
        internal readonly CodexRealtimeBoundedQueue<Feedback> Feedback = new(64, 1024 * 1024, "feedback");
    }

    /// <summary>Construct an account-bound coordinator over the existing thread store, agent,
    /// RPC host and lifecycle controller. Their identities must refer to the same journal.</summary>
    public CodexRealtimeCoordinator(CodexThreadStore store, CodexThreadAgent agent, CodexThreadRpc rpc,
        CodexRealtimeSessionController controller, Guid threadId, string sessionId, string ownerId,
        string expectedAccountId, Func<CodexThreadDocument, CodexResponseOptions> reasoningOptions,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute,
        CodexRealtimeCoordinatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(controller);
        if (!agent.IsBoundTo(store) || !rpc.IsBoundTo(store) || !controller.IsBoundTo(store, threadId, sessionId, ownerId))
            throw new ArgumentException("Realtime coordinator requires the exact existing thread journal and lifecycle binding.");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAccountId);
        this.store = store; this.agent = agent; this.rpc = rpc; this.controller = controller;
        this.threadId = threadId; this.sessionId = sessionId; this.ownerId = ownerId; accountId = expectedAccountId;
        this.reasoningOptions = reasoningOptions ?? throw new ArgumentNullException(nameof(reasoningOptions));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.options = options ?? new();
        if (this.options.MaximumReasoningRequests is < 1 or > 32 || !Enum.IsDefined(this.options.ChannelMode))
            throw new ArgumentOutOfRangeException(nameof(options));
        stream = new((CodexRealtimeFeedbackMode)this.options.ChannelMode,
            clientManagedHandoffs: this.options.ClientManagedHandoffs, responsesAsItems: this.options.ResponsesAsItems,
            backendReasoningStatus: this.options.BackendReasoningStatus, channelPrefixes: this.options.ChannelPrefixes);
    }

    /// <summary>The exact backing reasoning thread.</summary>
    public Guid ThreadId => threadId;
    /// <summary>The persisted logical voice session.</summary>
    public string SessionId => sessionId;
    /// <summary>Last owned reasoning failure, if any. No remote action is automatically replayed.</summary>
    public Exception? ReasoningFailure { get { lock (gate) return reasoningFailure; } }

    /// <summary>Attach to the registered call after validating its thread/account ownership.</summary>
    public async Task<CodexRealtimeLifecycleStatus> AttachAsync(CancellationToken ct = default)
    {
        await ReadBoundAsync(ct).ConfigureAwait(false);
        return await controller.AttachAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Queue direct PCM without awaiting reasoning or storage. False means not accepted.
    /// Audio sent through WebRTC must not also be queued here.</summary>
    public bool TryAppendAudio(ReadOnlyMemory<byte> pcm16)
    {
        if (!options.DirectAudio) throw new InvalidOperationException("The host did not enroll direct audio transport.");
        if (pcm16.IsEmpty || pcm16.Length > 64 * 1024 || pcm16.Length % 2 != 0)
            throw new ArgumentException("A bounded PCM16 frame is required.", nameof(pcm16));
        lock (gate) return current?.Audio.TryWrite(pcm16.ToArray(), pcm16.Length) == true;
    }

    /// <summary>Queue typed user input on the independent control lane.</summary>
    public bool TryAppendText(string text) => TryControl(text, false);

    /// <summary>Queue explicit standalone speech without manufacturing a user message.</summary>
    public bool TryAppendSpeech(string text) => TryControl(text, true);

    /// <summary>Queue explicitly authored quiet reasoning status if the current V3 handoff permits it.</summary>
    public bool TryAppendReasoningStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            if (activeFeedback is not { } binding || binding.Connection.Protocol != "v3" ||
                stream.Status(binding.Handoff, text) is not { } chunk) return false;
            return binding.Run.Feedback.TryWrite(new(binding, chunk.Text, chunk.Channel, CodexRealtimeFeedbackKind.Status), Encoding.UTF8.GetByteCount(chunk.Text));
        }
    }

    private bool TryControl(string text, bool speech)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > 16 * 1024) throw new ArgumentException("Realtime control text exceeds its bound.", nameof(text));
        lock (gate) return current?.Control.TryWrite(new(text, speech), bytes) == true;
    }

    /// <summary>Interrupt the backing native turn without waiting for an already admitted tool.</summary>
    public void InterruptReasoning() => rpc.InterruptRealtimeReasoning(threadId);

    /// <summary>Run the sole transport reader with independent bounded consumers. An overload
    /// or consumer failure is observable; durable pending history is never silently replayed.</summary>
    public async Task<CodexRealtimeLifecycleStatus> RunAsync(
        Func<CodexRealtimeEvent, CancellationToken, Task>? observer = null,
        Func<CodexRealtimeEvent, CancellationToken, Task>? audioObserver = null,
        CancellationToken ct = default)
    {
        await ReadBoundAsync(ct).ConfigureAwait(false);
        var connection = controller.Connection ?? throw new InvalidOperationException("Attach the realtime session first.");
        var attachment = controller.AttachmentId ?? throw new InvalidOperationException("Missing attachment fence.");
        var run = new RunState();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is not null) throw new InvalidOperationException("A realtime coordinator pump is already running.");
            current = run;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var token = stop.Token;
        var workers = new[]
        {
            run.Handoffs.RunAsync((handoff, cancel) => RouteAsync(run, handoff, cancel), token),
            run.Audio.RunAsync((bytes, cancel) => connection.AppendAudioAsync(bytes, cancel), token),
            run.Control.RunAsync((command, cancel) => command.Speech
                ? SendStandaloneAsync(connection, command.Text, cancel)
                : connection.AppendUserTextAsync(command.Text, cancel), token),
            run.Feedback.RunAsync((feedback, cancel) => SendFeedbackAsync(feedback, cancel), token),
            run.Events.RunAsync((item, cancel) => observer is null ? Task.CompletedTask : observer(item, cancel).WaitAsync(cancel), token),
            run.AudioEvents.RunAsync((item, cancel) => audioObserver is null ? Task.CompletedTask : audioObserver(item, cancel).WaitAsync(cancel), token),
            FlushAsync(run, token),
        };
        Task<CodexRealtimeLifecycleStatus>? pump = null;
        try
        {
            pump = controller.PumpAsync((item, _) =>
        {
            var bytes = Encoding.UTF8.GetByteCount(item.Payload.GetRawText());
            if (item.Delegation is not null)
                run.Handoffs.Write(new(item, attachment, connection), bytes);
            if (item.Audio is not null && audioObserver is not null) run.AudioEvents.Write(item, bytes);
            if (observer is not null) run.Events.Write(item, bytes);
            return Task.CompletedTask;
            }, token);
            var ended = await Task.WhenAny(workers.Append(pump)).ConfigureAwait(false);
            if (ended != pump) await ended.ConfigureAwait(false);
            return await pump.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (activeFeedback?.Run == run) { stream.Retire(activeFeedback.Handoff); activeFeedback = null; }
                if (responseFeedback?.Run == run) responseFeedback = null;
                if (current == run) current = null;
            }
            try { await Task.WhenAll(pump is null ? workers : workers.Append(pump)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private async Task<CodexThreadDocument> ReadBoundAsync(CancellationToken ct)
    {
        var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
        if (thread.AccountId != accountId || thread.RealtimeSessions?.Any(s => s.SessionId == sessionId && s.OwnerId == ownerId) != true)
            throw new CodexRealtimeAccountMismatchException();
        return thread;
    }

    private async Task RouteAsync(RunState run, Handoff handoff, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await RouteOnceAsync(run, handoff, ct).ConfigureAwait(false); return; }
            catch (CodexThreadConflictException) when (attempt < 3) { }
        }
    }

    private async Task RouteOnceAsync(RunState run, Handoff handoff, CancellationToken ct)
    {
        var id = handoff.Event.Delegation?.Id ?? throw new InvalidOperationException("Missing delegation identity.");
        var thread = await ReadBoundAsync(ct).ConfigureAwait(false);
        var session = thread.RealtimeSessions!.Single(s => s.SessionId == sessionId);
        if (session.HandoffAdmissions?.Any(r => r.HandoffId == id) == true) return;
        if (session.AttachmentId != handoff.Attachment) throw new InvalidOperationException("Realtime attachment changed before handoff admission.");
        var input = CodexThreadStore.PrepareRealtimeHandoffInput(session, id);
        var submission = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(threadId + "\n" + sessionId + "\n" + id)).AsSpan(0, 16));
        if (input is null || options.ClientManagedHandoffs)
        {
            await store.RejectRealtimeHandoffAsync(thread.Id, thread.Revision, sessionId, ownerId, handoff.Attachment,
                id, submission, input?.Text ?? "", cancellationToken: ct).ConfigureAwait(false);
            return;
        }
        var request = new CodexRealtimeHandoffRequest(sessionId, ownerId, handoff.Attachment, id, submission, input.Text);
        var active = thread.Turns.LastOrDefault(t => t.Status == "inProgress");
        if (active is not null)
        {
            var steered = await rpc.SteerRealtimeAsync(threadId, request, ct, () => Activate(run, handoff, active.Id)).ConfigureAwait(false);
            if (steered is null)
                throw new InvalidOperationException("The active reasoning turn is not owned by this native RPC host.");
            return;
        }

        var resolved = reasoningOptions(thread);
        var identity = resolved.Identity ?? new CodexResponsesIdentity(
            CodexRealtimeProtocol.Text(thread.Settings, "nativeSessionId") ?? threadId.ToString(), threadId.ToString(), submission.ToString());
        if (identity.ThreadId != threadId.ToString()) throw new InvalidOperationException("Reasoning options refer to another thread.");
        resolved = resolved with { Identity = identity with { TurnId = submission.ToString(), RootTurnId = submission.ToString(), VoiceSessionId = sessionId } };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = rpc.RunOwnedAsync(thread, submission, resolved, [input.ToResponseItem()], async (native, notifyStarted, cancel) =>
        {
            var result = await agent.RunRealtimeHandoffAsync(threadId, native.Thread.Revision, native.OwnerId, request,
                native.Options, native.Control, execute, options.MaximumReasoningRequests,
                async (snapshot, _) =>
                {
                    Activate(run, handoff, submission);
                    await notifyStarted(snapshot).ConfigureAwait(false);
                    started.TrySetResult();
                },
                async (item, token) =>
                {
                    Validated(submission, item);
                    await native.ValidatedItem(item, token).ConfigureAwait(false);
                },
                item => ObserveReasoning(submission, item), cancel).ConfigureAwait(false);
            return result.Thread;
        }, lifetime.Token);
        lock (gate) ownedReasoning = task;
        _ = ObserveCompletionAsync(task, submission, started);
        try { await started.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (CodexThreadConflictException)
        {
            // Only a definite pre-admission CAS rejection can be reconsidered.
            await task.ConfigureAwait(false);
            throw;
        }
    }

    private async Task ObserveCompletionAsync(Task<CodexThreadDocument> task, Guid turnId, TaskCompletionSource started)
    {
        try
        {
            var document = await task.ConfigureAwait(false);
            lock (gate)
            {
                if (activeFeedback is { Completed: false } binding && binding.TurnId == turnId && document.Turns.Any(t => t.Id == binding.TurnId && t.Status == "completed"))
                {
                    if (binding.Connection.Protocol == "v2" && binding.HasOutput)
                        Queue(binding, options.ResponsesAsItems ? "" : "complete", null, CodexRealtimeFeedbackKind.Complete);
                    binding.Completed = true;
                }
            }
            started.TrySetResult();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (gate) reasoningFailure = error;
            started.TrySetException(error);
        }
    }

    private void Activate(RunState run, Handoff handoff, Guid turn)
    {
        lock (gate)
        {
            if (current != run) return;
            if (responseFeedback?.TurnId != turn) { responseFeedback = null; feedbackRequestId = null; }
            var token = stream.Activate(handoff.Event.Delegation!.Id);
            var prior = activeFeedback;
            var steer = handoff.Connection.Protocol == "v2" && prior is { Completed: false } && prior.TurnId == turn && prior.Run == run;
            var binding = new Binding(token, turn, handoff.Connection,
                handoff.Event.Admission ?? handoff.Connection.CaptureFeedbackAdmission(), run,
                steer ? prior!.WireHandoffId : token.Id);
            activeFeedback = binding;
            if (steer) Queue(binding, "", null, CodexRealtimeFeedbackKind.SteerAck, token.Id);
        }
    }

    private void ObserveReasoning(Guid turn, CodexResponseEvent item)
    {
        lock (gate)
        {
            // Local request IDs distinguish continuation requests even if the provider omits response.created.
            if (item.Type == "response.created" || responseFeedback is null ||
                (item.RequestId is not null && item.RequestId != feedbackRequestId))
            {
                responseFeedback = activeFeedback is { } binding && binding.TurnId == turn ? binding : null;
                feedbackRequestId = item.RequestId;
            }
            if (responseFeedback is not { } currentBinding || currentBinding.TurnId != turn ||
                !stream.IsCurrent(currentBinding.Handoff) || currentBinding.Connection.Protocol != "v3") return;
            if (item.Type == "response.output_item.added" && item.Payload.TryGetProperty("item", out var message) &&
                CodexRealtimeProtocol.Text(message, "role") == "assistant" && CodexRealtimeProtocol.Text(message, "type") == "message" &&
                CodexRealtimeProtocol.Text(message, "id") is { } id)
                stream.Register(currentBinding.Handoff, id, Phase(message), MessageText(message));
            else if (item.Type == "response.output_text.delta" && CodexRealtimeProtocol.Text(item.Payload, "item_id") is { } deltaId &&
                CodexRealtimeProtocol.Text(item.Payload, "delta") is { } delta)
                stream.Push(currentBinding.Handoff, deltaId, delta);
        }
    }

    private void Validated(Guid turn, JsonElement item)
    {
        if (CodexRealtimeProtocol.Text(item, "type") != "message" || CodexRealtimeProtocol.Text(item, "role") != "assistant") return;
        lock (gate)
        {
            var binding = responseFeedback ?? activeFeedback;
            if (binding is null || binding.TurnId != turn || !stream.IsCurrent(binding.Handoff)) return;
            var id = CodexRealtimeProtocol.Text(item, "id");
            var completed = id is not null ? stream.Finish(binding.Handoff, id) : default;
            if (completed.Chunk is { } chunk) Queue(binding, chunk.Text, chunk.Channel);
            if (!completed.HasOutput)
            {
                var text = MessageText(item);
                if (text.Length != 0)
                {
                    var channel = options.ChannelMode == CodexRealtimeReasoningChannelMode.Thinking ? null
                        : options.ChannelMode == CodexRealtimeReasoningChannelMode.Commentary || Phase(item) == CodexRealtimeMessagePhase.Commentary
                            ? "commentary" : "speakable";
                    var kind = options.ResponsesAsItems ? CodexRealtimeFeedbackKind.Item :
                        Phase(item) is CodexRealtimeMessagePhase.Commentary or CodexRealtimeMessagePhase.PartialAnswer
                            ? CodexRealtimeFeedbackKind.Progress : CodexRealtimeFeedbackKind.Final;
                    Queue(binding, CodexRealtimeStartupContext.Truncate(text, 1000), channel, kind);
                }
            }
        }
    }

    private static CodexRealtimeMessagePhase? Phase(JsonElement item) => CodexRealtimeProtocol.Text(item, "phase") switch
    {
        "commentary" => CodexRealtimeMessagePhase.Commentary,
        "partial_answer" => CodexRealtimeMessagePhase.PartialAnswer,
        "final_answer" => CodexRealtimeMessagePhase.FinalAnswer,
        _ => null,
    };

    private static string MessageText(JsonElement item) => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
        ? string.Concat(content.EnumerateArray().Where(x => CodexRealtimeProtocol.Text(x, "type") == "output_text")
            .Select(x => CodexRealtimeProtocol.Text(x, "text") ?? ""))
        : "";

    private void Queue(Binding binding, string text, string? channel,
        CodexRealtimeFeedbackKind kind = CodexRealtimeFeedbackKind.Progress, string? wireId = null)
    {
        binding.Run.Feedback.Write(new(binding, text, channel, kind, wireId), Encoding.UTF8.GetByteCount(text));
        if (kind is CodexRealtimeFeedbackKind.Progress or CodexRealtimeFeedbackKind.Final or CodexRealtimeFeedbackKind.Item)
            binding.HasOutput = true;
    }

    private async Task FlushAsync(RunState run, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CodexRealtimeFeedbackStream.FlushInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            lock (gate)
            {
                if (activeFeedback is not { } binding || binding.Run != run) continue;
                foreach (var chunk in stream.DrainDue(binding.Handoff)) Queue(binding, chunk.Text, chunk.Channel);
            }
        }
    }

    private async Task SendFeedbackAsync(Feedback feedback, CancellationToken ct)
    {
        if (!stream.IsCurrent(feedback.Binding.Handoff)) return;
        await feedback.Binding.Connection.SendReasoningFeedbackAsync(feedback.Binding.Admission, feedback.Kind,
            feedback.Text, feedback.WireId ?? feedback.Binding.WireHandoffId, feedback.Channel, options.ResponseItemPrefix, ct).ConfigureAwait(false);
    }

    private static Task SendStandaloneAsync(CodexRealtimeConnection connection, string text, CancellationToken ct)
        => connection.SendReasoningFeedbackAsync(connection.CaptureFeedbackAdmission(), CodexRealtimeFeedbackKind.Speech, text, null, "speakable", null, ct);

    /// <summary>Detach transport without ending the logical call or backing reasoning turn.</summary>
    public Task<CodexRealtimeLifecycleStatus> DetachAsync(CancellationToken ct = default) => controller.DetachAsync(ct);

    /// <summary>End the logical call through the existing durable lifecycle controller.</summary>
    public Task<CodexRealtimeLifecycleStatus> EndAsync(Guid operationId,
        Func<CodexRealtimeConnection?, CancellationToken, Task>? endProvider = null, CancellationToken ct = default)
        => controller.EndAsync(operationId, endProvider, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? reasoning;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            reasoning = ownedReasoning;
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        await controller.DisposeAsync().ConfigureAwait(false);
        if (reasoning is not null)
            try { await reasoning.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { lock (gate) reasoningFailure = error; }
        lifetime.Dispose();
    }
}
