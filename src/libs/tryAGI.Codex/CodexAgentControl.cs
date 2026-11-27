using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>One live turn's bounded mailbox. Updates affect subsequent request captures only.
/// Interruption fences new tools while allowing already-admitted callbacks to finish.</summary>
public sealed class CodexAgentControl : IDisposable
{
    private readonly object gate = new();
    private sealed record PendingInput(JsonElement Item, string? ClientId, bool Durable = false);
    private readonly SemaphoreSlim realtimeGate = new(1, 1);
    private Func<CodexRealtimeHandoffRequest, CancellationToken, Task<CodexRealtimeHandoffAdmission>>? realtimeAdmission;
    private bool reservingRealtime;
    private Func<CodexRealtimeReasoningObserver?>? captureRealtimeObserver;
    private CodexRealtimeReasoningObserver? capturedRealtimeObserver;

    internal void BindRealtimeObserver(Func<CodexRealtimeReasoningObserver?> capture)
        => captureRealtimeObserver = capture;

    internal void ObserveRealtimeProvider(CodexResponseEvent item) => capturedRealtimeObserver?.Provider(item);
    internal void ObserveRealtimeValidated(JsonElement item) => capturedRealtimeObserver?.Validated(item);
    private readonly Queue<PendingInput> input = new();
    private readonly List<string?> clientIds = [];
    private bool inputInitialized;
    internal string? TurnTrigger { get; private set; }
    internal string?[] ClientIds { get { lock (gate) return clientIds.ToArray(); } }

    internal void InitializeInput(IReadOnlyList<JsonElement> items, string? clientId = null, string? turnTrigger = null)
    {
        lock (gate)
        {
            if (inputInitialized) return;
            foreach (var item in items)
                if (IsUser(item)) clientIds.Add(clientId);
            TurnTrigger = turnTrigger; inputInitialized = true;
        }
    }
    private static bool IsUser(JsonElement item) => item.TryGetProperty("role", out var role) && role.GetString() == "user";
    private readonly CancellationTokenSource lifetime = new();
    private CodexResponseOptions? next;
    private bool closed, interrupted, disposed;
    private int tools, queuedBytes, cancelers;
    /// <summary>Cancelled when the turn is interrupted and no tool is still running.</summary>
    public CancellationToken Token => lifetime.Token;

    /// <summary>Queues extra user input for the running turn; it is sent with the next model request.</summary>
    /// <param name="items">Input items, at most 128.</param>
    /// <param name="clientUserMessageId">Client ID of the message, for deduplication.</param>
    /// <exception cref="InvalidOperationException">The turn no longer accepts input.</exception>
    public void Steer(IReadOnlyList<JsonElement> items, string? clientUserMessageId = null)
    {
        if (items.Count == 0 || items.Count > 128) throw new ArgumentException("Steering requires bounded nonempty input.");
        var bytes = items.Sum(x => System.Text.Encoding.UTF8.GetByteCount(x.GetRawText()));
        lock (gate)
        {
            if (closed || interrupted) throw new InvalidOperationException("Turn no longer accepts input.");
            if (reservingRealtime) throw new InvalidOperationException("A durable realtime steering admission is in progress.");
            if (input.Count + items.Count > 128 || queuedBytes + bytes > 1_048_576)
                throw new ArgumentException("Pending steering exceeds its bound.");
            foreach (var item in items) input.Enqueue(new(item.Clone(), clientUserMessageId));
            queuedBytes += bytes;
        }
    }

    /// <summary>Changes the options used by the turn's next model requests.</summary>
    /// <param name="update">Produces the new options from the current ones.</param>
    /// <param name="initial">Options to start from when no update was made yet.</param>
    /// <returns><see langword="false"/> when the turn has already ended or been interrupted.</returns>
    public bool Update(Func<CodexResponseOptions, CodexResponseOptions> update, CodexResponseOptions initial)
    {
        lock (gate)
        {
            if (closed || interrupted) return false;
            next = update(next ?? initial);
            return true;
        }
    }

    internal void BindRealtimeAdmission(Func<CodexRealtimeHandoffRequest, CancellationToken, Task<CodexRealtimeHandoffAdmission>> admit)
    {
        lock (gate) realtimeAdmission = admit;
    }

    internal async Task<CodexRealtimeHandoffAdmission> SteerRealtimeAsync(CodexRealtimeHandoffRequest request,
        CancellationToken ct, Action? onAdmitted = null)
    {
        await realtimeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Func<CodexRealtimeHandoffRequest, CancellationToken, Task<CodexRealtimeHandoffAdmission>> admit;
            var item = new CodexRealtimeDelegationInput(request.ContextualInput, []).ToResponseItem();
            var bytes = System.Text.Encoding.UTF8.GetByteCount(item.GetRawText());
            lock (gate)
            {
                if (closed || interrupted) throw new InvalidOperationException("Turn no longer accepts input.");
                if (input.Any(i => !i.Durable))
                    throw new InvalidOperationException("Ordinary pending steering must be captured before durable realtime steering.");
                if (input.Count >= 128 || bytes > 1_048_576 - queuedBytes)
                    throw new InvalidOperationException("Pending steering exceeds its bound.");
                admit = realtimeAdmission ?? throw new InvalidOperationException("This turn has no native durable steering executor.");
                reservingRealtime = true;
            }
            var receipt = await admit(request, ct).ConfigureAwait(false);
            lock (gate)
            {
                // The receipt remains durable if interruption wins before the next capture.
                if (receipt.Admitted && !closed && !interrupted)
                {
                    input.Enqueue(new(item, request.SubmissionId.ToString(), true));
                    queuedBytes += bytes;
                }
            }
            // Publish the new feedback generation before capture can acquire realtimeGate.
            if (receipt.Admitted) onAdmitted?.Invoke();
            return receipt;
        }
        finally
        {
            lock (gate) reservingRealtime = false;
            realtimeGate.Release();
        }
    }

    internal async Task<CodexResponseOptions> CaptureAsync(CodexResponseOptions initial, List<JsonElement> history, CancellationToken ct)
    {
        await realtimeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = Capture(initial, history);
            // Capture the exact voice generation with the input, before a later steer can replace it.
            capturedRealtimeObserver = captureRealtimeObserver?.Invoke();
            return settings;
        }
        finally { realtimeGate.Release(); }
    }

    internal async Task<bool> TryFinishAsync(CancellationToken ct)
    {
        await realtimeGate.WaitAsync(ct).ConfigureAwait(false);
        try { return TryFinish(); }
        finally { realtimeGate.Release(); }
    }

    internal CodexResponseOptions Capture(CodexResponseOptions initial, List<JsonElement> history)
    {
        lock (gate)
        {
            Token.ThrowIfCancellationRequested();
            while (input.TryDequeue(out var item))
            {
                history.Add(item.Item);
                if (IsUser(item.Item)) clientIds.Add(item.ClientId);
            }
            queuedBytes = 0;
            return next ?? initial;
        }
    }

    internal bool TryFinish()
    {
        lock (gate)
        {
            if (input.Count != 0 && !interrupted) return false;
            closed = true;
            return true;
        }
    }

    internal void Seal() { lock (gate) closed = true; }

    /// <summary>Stops the turn: no new tool starts, and the current request is cancelled once running tools finish.</summary>
    public void Interrupt()
    {
        bool cancel;
        lock (gate) { if (closed) return; interrupted = true; cancel = tools == 0; }
        if (cancel) Cancel();
    }

    internal async Task<T> ToolAsync<T>(Func<CancellationToken, Task<T>> callback, CancellationToken ct)
    {
        lock (gate)
        {
            if (closed || interrupted) throw new OperationCanceledException(Token);
            ct.ThrowIfCancellationRequested();
            tools++;
        }
        try { return await callback(ct).ConfigureAwait(false); }
        finally
        {
            bool cancel;
            lock (gate) { tools--; cancel = interrupted && tools == 0; }
            if (cancel) Cancel();
        }
    }

    private void Cancel()
    {
        lock (gate) { if (disposed) return; cancelers++; }
        try { lifetime.Cancel(); }
        finally
        {
            bool release;
            lock (gate) { cancelers--; release = closed && cancelers == 0 && !disposed; if (release) disposed = true; }
            if (release) lifetime.Dispose();
        }
    }
    /// <inheritdoc />
    public void Dispose()
    {
        bool release;
        lock (gate) { closed = true; release = cancelers == 0 && !disposed; if (release) disposed = true; }
        if (release) lifetime.Dispose();
    }
}
