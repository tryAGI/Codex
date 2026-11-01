using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>One live turn's bounded mailbox. Updates affect subsequent request captures only.
/// Interruption fences new tools while allowing already-admitted callbacks to finish.</summary>
public sealed class CodexAgentControl : IDisposable
{
    private readonly object gate = new();
    private sealed record PendingInput(JsonElement Item, string? ClientId);
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
    public CancellationToken Token => lifetime.Token;

    public void Steer(IReadOnlyList<JsonElement> items, string? clientUserMessageId = null)
    {
        if (items.Count == 0 || items.Count > 128) throw new ArgumentException("Steering requires bounded nonempty input.");
        var bytes = items.Sum(x => System.Text.Encoding.UTF8.GetByteCount(x.GetRawText()));
        lock (gate)
        {
            if (closed || interrupted) throw new InvalidOperationException("Turn no longer accepts input.");
            if (input.Count + items.Count > 128 || queuedBytes + bytes > 1_048_576)
                throw new ArgumentException("Pending steering exceeds its bound.");
            foreach (var item in items) input.Enqueue(new(item.Clone(), clientUserMessageId));
            queuedBytes += bytes;
        }
    }

    public bool Update(Func<CodexResponseOptions, CodexResponseOptions> update, CodexResponseOptions initial)
    {
        lock (gate)
        {
            if (closed || interrupted) return false;
            next = update(next ?? initial);
            return true;
        }
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
    public void Dispose()
    {
        bool release;
        lock (gate) { closed = true; release = cancelers == 0 && !disposed; if (release) disposed = true; }
        if (release) lifetime.Dispose();
    }
}
