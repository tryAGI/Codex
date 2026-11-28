using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

// One response owns its interrupt signal. Keep the receive operation alive while
// sending the control frame; cancelling ReceiveAsync on ClientWebSocket aborts the socket.
internal sealed class CodexResponsesInterruption(CodexResponsesCorrelation correlation,
    CancellationToken callerCancellation, CancellationToken drain, Action<string> mark)
{
    internal JsonElement? TerminalReceipt { get; private set; }
    internal bool InterruptSent { get; private set; }
    internal bool HandshakeComplete { get; private set; }
    internal bool Acknowledged => acknowledged;
    internal bool CancelRequested => callerCancellation.IsCancellationRequested;
    private string? createdId;
    private bool acknowledged;
    private string? terminalFrame;
    private bool finished;
    private bool failed;

    internal static bool IsInterruptedTerminal(string type, JsonElement payload) =>
        type == "response.incomplete" && payload.TryGetProperty("response", out var response) &&
        response.ValueKind == JsonValueKind.Object &&
        response.TryGetProperty("incomplete_details", out var details) &&
        CodexRealtimeProtocol.Text(details, "reason") == "interrupted";

    internal async IAsyncEnumerable<string> ReadAsync(
        Func<CancellationToken, IAsyncEnumerable<string>> source,
        Func<JsonObject, CancellationToken, Task> send,
        Func<string, bool> consumeLateDrainAcknowledgement,
        [EnumeratorCancellation] CancellationToken deadline)
    {
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, drain);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wakeRegistration = wake.Token.Register(() => signal.TrySetResult());
        using var cancelRegistration = callerCancellation.Register(() => cleanup.CancelAfter(TimeSpan.FromSeconds(5)));
        await using var reader = source(cleanup.Token).GetAsyncEnumerator();
        Task<bool>? pending = null;

        async Task<string?> NextAsync()
        {
            try
            {
                while (!finished)
                {
                    if (!InterruptSent && createdId is not null && wake.IsCancellationRequested && terminalFrame is null)
                    {
                        InterruptSent = true; // Ambiguous send is not retryable.
                        mark("transport.websocket_interrupt_sent");
                        await send(new JsonObject
                        {
                            ["type"] = "response.interrupt", ["response_id"] = createdId,
                            ["mode"] = "discard_partial_items",
                        }, cleanup.Token).ConfigureAwait(false);
                    }
                    if (terminalFrame is not null && (!InterruptSent || !CancelRequested || acknowledged))
                    {
                        finished = true;
                        HandshakeComplete = true;
                        return terminalFrame;
                    }

                    pending ??= reader.MoveNextAsync().AsTask();
                    // The signal may already be completed when receive admission returns.
                    if (!InterruptSent && createdId is not null)
                    {
                        await Task.WhenAny(pending, signal.Task).ConfigureAwait(false);
                        if (!pending.IsCompleted) continue;
                    }
                    if (!await pending.ConfigureAwait(false))
                    {
                        mark("response.stream_ended");
                        throw new CodexProtocolException("Responses WebSocket stream ended before terminal completion or interrupt acknowledgement.");
                    }
                    pending = null;
                    var raw = reader.Current;
                    using var document = JsonDocument.Parse(raw);
                    var payload = document.RootElement;
                    var type = CodexRealtimeProtocol.Text(payload, "type") ??
                        throw new CodexProtocolException("Responses event omitted its type.");
                    // Drain does not wait for an acknowledgement in Rust. A late acknowledgement
                    // may be consumed only for a specifically retired drain on this socket.
                    if (type is "response.interrupt.accepted" or "response.interrupt.failed" &&
                        CodexRealtimeProtocol.Text(payload, "response_id") is { } lateId &&
                        consumeLateDrainAcknowledgement(lateId))
                    {
                        mark("transport.websocket_late_drain_acknowledgement");
                        continue;
                    }
                    if (correlation.Observe(type, payload) is { } failure)
                    {
                        mark("response.protocol_rejected." + failure);
                        throw new CodexProtocolException("Responses stream identity rejected (" + failure + ").");
                    }
                    if (type == "response.created" && payload.TryGetProperty("response", out var created))
                        createdId = CodexRealtimeProtocol.Text(created, "id") ?? createdId;
                    if (type is "response.interrupt.accepted" or "response.interrupt.failed")
                    {
                        if (!InterruptSent || acknowledged || createdId is null ||
                            CodexRealtimeProtocol.Text(payload, "response_id") != createdId)
                            throw new CodexProtocolException("Unexpected Responses interruption acknowledgement.");
                        if (type == "response.interrupt.failed")
                            throw new CodexProtocolException("Responses interruption was rejected.");
                        acknowledged = true;
                        mark("transport.websocket_interrupt_accepted");
                        continue;
                    }
                    if (type == "response.completed" || InterruptSent && IsInterruptedTerminal(type, payload))
                    {
                        if (terminalFrame is not null)
                            throw new CodexProtocolException("Duplicate terminal response while awaiting interruption acknowledgement.");
                        if (!payload.TryGetProperty("response", out var response) ||
                            response.ValueKind != JsonValueKind.Object ||
                            CodexRealtimeProtocol.Text(response, "id") is null)
                            throw new CodexProtocolException("Invalid terminal Responses interruption receipt.");
                        TerminalReceipt = payload.Clone();
                        terminalFrame = raw;
                        continue;
                    }
                    if (type is "error" or "response.failed" or "response.incomplete")
                    {
                        finished = true; // The client classifies the failure; the lease is retired.
                        return raw;
                    }
                    if (!CancelRequested) return raw;
                    // Cancelled output must not reach the agent or release tool dispatch.
                }
                return null;
            }
            catch
            {
                failed = true;
                throw;
            }
        }

        try
        {
            while (await NextAsync().ConfigureAwait(false) is { } raw)
                yield return raw;
        }
        finally
        {
            // A consumer may stop inside an observer after cancelling the turn. Give the
            // same response its bounded cleanup opportunity without yielding any more output.
            if (CancelRequested && !finished && !failed)
            {
                try { while (await NextAsync().ConfigureAwait(false) is not null) { } }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            if (pending is not null)
            {
                cleanup.Cancel();
                try { await pending.ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }
    }
}
