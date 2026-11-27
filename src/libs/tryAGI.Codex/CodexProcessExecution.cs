using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Actual exit status of a contained process after output collection settles.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="OutputDrainTimedOut">Whether post-exit output collection timed out, or null if unknown.</param>
public sealed record CodexProcessExit(int ExitCode, bool? OutputDrainTimedOut = null);

/// <summary>Explicit host-process capability, distinct from the command/exec sandbox capability.
/// The host must authorize the exact operation and contain all descendants, enforce the effective
/// filesystem/network/process policy and environment allowlist, and reject unsupported constraints.
/// Never implement this as a fallback for a rejected command/exec. On failure to start, the adapter
/// must settle partial-launch cleanup. Await all output callbacks; none may survive session disposal.
/// The request's ProcessId is the client processHandle, not an authorization token.</summary>
public interface ICodexProcessAdapter
{
    /// <summary>Starts a contained process under the host's effective policy.</summary>
    Task<ICodexProcessSession> StartAsync(CodexCommandRequest request,
        Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        CancellationToken cancellationToken);
}

/// <summary>A host-owned process session. Disposal must terminate and reap the entire owned tree,
/// including descendants of an already exited parent, and settle all output callbacks. Controls
/// must be safe when racing termination or disposal. Completion reports actual output drain state.</summary>
public interface ICodexProcessSession : IAsyncDisposable
{
    /// <summary>Actual process exit and drain status after output callbacks finish.</summary>
    Task<CodexProcessExit> Completion { get; }
    /// <summary>Writes exact stdin bytes, optionally closing stdin.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, bool closeStdin, CancellationToken cancellationToken);
    /// <summary>Resizes an actual PTY.</summary>
    ValueTask ResizeAsync(CodexCommandTerminalSize size, CancellationToken cancellationToken);
    /// <summary>Terminates the owned process tree.</summary>
    ValueTask TerminateAsync(CancellationToken cancellationToken);
}

/// <summary>Host-only failure report for an already acknowledged process. This is not a wire
/// notification or a fabricated exit status. The host must not expose raw exception details to clients.</summary>
/// <param name="ExecutionId">Host-generated attempt identity.</param>
/// <param name="ProcessHandle">Client correlation handle.</param>
/// <param name="Error">Actual execution, cleanup or delivery error.</param>
public sealed record CodexProcessFailure(Guid ExecutionId, string ProcessHandle, Exception Error);

/// <summary>Native connection-scoped process/spawn lifecycle. The response callback is an explicit
/// transport barrier: it must complete only when the response is ordered before subsequent
/// notifications on that authenticated connection. The embedding host owns adapter selection,
/// authorization and disconnect disposal; the SDK never starts an unsandboxed process itself.</summary>
public sealed class CodexProcessExecution : IAsyncDisposable
{
    private readonly string connectionId;
    private readonly ICodexProcessAdapter adapter;
    private readonly Func<string, JsonElement, CancellationToken, ValueTask> notify;
    private readonly Func<CodexProcessFailure, CancellationToken, ValueTask> reportFailure;
    private readonly int maximumConcurrentProcesses;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private Task? closing;
    private Exception? healthFailure;

    private sealed class Entry : IDisposable
    {
        internal Entry(CodexCommandRequest request, CancellationToken lifetimeToken)
        {
            Request = request;
            Deadline = new(request.Timeout);
            Stop = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, Deadline.Token);
        }
        internal CodexCommandRequest Request { get; }
        internal CancellationTokenSource Deadline { get; }
        internal CancellationTokenSource Stop { get; }
        internal TaskCompletionSource<ICodexProcessSession> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? OutputFailure;
        public void Dispose() { Stop.Dispose(); Deadline.Dispose(); }
    }

    /// <summary>Creates a process host. Both execution and failure reporting are explicit host capabilities.</summary>
    public CodexProcessExecution(string connectionId, ICodexProcessAdapter adapter,
        Func<string, JsonElement, CancellationToken, ValueTask> notify,
        Func<CodexProcessFailure, CancellationToken, ValueTask> reportFailure,
        int maximumConcurrentProcesses = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentProcesses, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentProcesses, 64);
        this.connectionId = connectionId;
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        this.maximumConcurrentProcesses = maximumConcurrentProcesses;
    }

    /// <summary>Native process methods supported by this explicitly configured host.</summary>
    public static bool Handles(string method) => method is "process/spawn" or "process/writeStdin"
        or "process/resizePty" or "process/kill";

    /// <summary>Handles a request and sends its result through the supplied transport barrier.
    /// If the response callback throws, treat it as a transport failure, not authority to send a second response.
    /// After a successful spawn response,
    /// execution is connection-owned rather than tied to the initiating request cancellation token.</summary>
    public async Task HandleAsync(string method, JsonElement parameters,
        Func<JsonElement, CancellationToken, ValueTask> respond, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(respond);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported process method.");
        if (parameters.ValueKind != JsonValueKind.Object) throw CodexCommandExecution.Invalid("Expected process parameters.");
        CodexCommandExecution.EnsureUniqueFields(parameters);
        if (method == "process/spawn")
        {
            var input = CodexCommandExecution.Read(parameters, CodexProcessJson.Default.ProcessSpawnParams);
            if (string.IsNullOrWhiteSpace(input.ProcessHandle) || string.IsNullOrWhiteSpace(input.Cwd))
                throw CodexCommandExecution.Invalid("processHandle and absolute cwd are required.");
            // Rust distinguishes absent (default) from explicit null (unlimited).
            // An unbounded request cannot be silently interpreted as a supported one.
            if ((parameters.TryGetProperty("timeoutMs", out var timeout) && timeout.ValueKind == JsonValueKind.Null) ||
                (parameters.TryGetProperty("outputBytesCap", out var cap) && cap.ValueKind == JsonValueKind.Null))
                throw new CodexThreadRpcException(-32010, "Unbounded native process execution is not supported.");
            var request = CodexCommandExecution.Validate(connectionId, new CommandParams(input.Command, input.ProcessHandle,
                input.Tty, input.StreamStdin, input.StreamStdoutStderr, input.OutputBytesCap,
                TimeoutMs: input.TimeoutMs, Cwd: input.Cwd, Env: input.Env, Size: input.Size));
            await SpawnAsync(request, respond, cancellationToken).ConfigureAwait(false);
            return;
        }

        var control = CodexCommandExecution.Read(parameters, CodexProcessJson.Default.ProcessControlParams);
        if (string.IsNullOrWhiteSpace(control.ProcessHandle)) throw CodexCommandExecution.Invalid("Missing processHandle.");
        Entry entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing is not null, this);
            if (!running.TryGetValue(control.ProcessHandle, out entry!))
                throw CodexCommandExecution.Invalid("Unknown active processHandle.");
        }
        if (method == "process/writeStdin")
        {
            if (!entry.Request.StreamStdin || control.Size is not null)
                throw CodexCommandExecution.Invalid("Stdin streaming is not enabled or fields are invalid.");
            var data = CodexCommandExecution.Decode(control.DeltaBase64);
            if (data.Length == 0 && !control.CloseStdin) throw CodexCommandExecution.Invalid("No stdin data or close requested.");
            var session = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.WriteAsync(data, control.CloseStdin, cancellationToken).ConfigureAwait(false);
        }
        else if (method == "process/resizePty")
        {
            if (!entry.Request.Tty || control.DeltaBase64 is not null || control.CloseStdin ||
                !CodexCommandExecution.ValidSize(control.Size))
                throw CodexCommandExecution.Invalid("Resize requires a PTY and valid size.");
            var session = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.ResizeAsync(control.Size!, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (control.Size is not null || control.DeltaBase64 is not null || control.CloseStdin)
                throw CodexCommandExecution.Invalid("Invalid kill fields.");
            var session = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.TerminateAsync(cancellationToken).ConfigureAwait(false);
        }
        await respond(Empty(), cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new CommandEmpty(), CodexCommandJson.Default.CommandEmpty);

    private async Task SpawnAsync(CodexCommandRequest request,
        Func<JsonElement, CancellationToken, ValueTask> respond, CancellationToken ct)
    {
        var handle = request.ProcessId!;
        Entry entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing is not null, this);
            if (healthFailure is not null) throw new CodexThreadRpcException(-32010, "Prior process cleanup or reporting failed.");
            if (running.ContainsKey(handle)) throw CodexCommandExecution.Invalid("Duplicate active processHandle.");
            if (running.Count >= maximumConcurrentProcesses) throw new CodexThreadRpcException(-32010, "Process capacity exhausted.");
            entry = new Entry(request, lifetime.Token);
            running.Add(handle, entry);
        }
        var output = new ProcessOutput(request, notify);
        ICodexProcessSession? session = null;
        try
        {
            // The startup request can cancel admission, but not an acknowledged process.
            using var startup = ct.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), entry.Stop);
            session = await adapter.StartAsync(request, async (stream, bytes, _) =>
            {
                try { await output.AcceptAsync(stream, bytes, entry.Stop.Token).ConfigureAwait(false); }
                catch (Exception error)
                {
                    if (error is not OperationCanceledException || !entry.Stop.IsCancellationRequested)
                        Interlocked.CompareExchange(ref entry.OutputFailure, error, null);
                    await entry.Stop.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }, entry.Stop.Token).ConfigureAwait(false);
            entry.Stop.Token.ThrowIfCancellationRequested();
            entry.Ready.TrySetResult(session);
            await respond(Empty(), entry.Stop.Token).ConfigureAwait(false);
        }
        catch
        {
            try { if (session is not null) await CleanupAsync(session).ConfigureAwait(false); }
            finally { Release(entry, output); }
            throw;
        }
        // The task is supervised by its entry until cleanup and the terminal notification settle.
        // It never reports a second RPC response or leaves an unobserved fault.
        _ = ObserveAsync(entry, session, output);
    }

    private async Task ObserveAsync(Entry entry, ICodexProcessSession session, ProcessOutput output)
    {
        CodexProcessExit? exit = null;
        Exception? failure = null;
        try
        {
            await output.OpenAsync(entry.Stop.Token).ConfigureAwait(false);
            exit = await session.Completion.WaitAsync(entry.Stop.Token).ConfigureAwait(false);
            failure = Volatile.Read(ref entry.OutputFailure);
        }
        catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested)
        {
            failure = Volatile.Read(ref entry.OutputFailure);
            if (failure is null && entry.Deadline.IsCancellationRequested && !lifetime.IsCancellationRequested &&
                !output.DeliveryFailed)
            {
                // Upstream's timeout exit status, emitted only after successful tree cleanup.
                exit = new(124);
            }
            else if (failure is null && !lifetime.IsCancellationRequested)
                failure = new OperationCanceledException("Process cancelled after admission.");
        }
        catch (Exception error) { failure = error; }

        try { await CleanupAsync(session).ConfigureAwait(false); }
        catch (Exception error) { failure = error; exit = null; }
        if (session.Completion.IsFaulted) _ = session.Completion.Exception;
        failure ??= Volatile.Read(ref entry.OutputFailure);
        if (output.DeliveryFailed && failure is null)
            failure = new IOException("Process output delivery failed or is uncertain.");
        try
        {
            if (!lifetime.IsCancellationRequested)
            {
                if (failure is not null)
                    await reportFailure(new(entry.Request.ExecutionId, entry.Request.ProcessId!, failure), lifetime.Token).ConfigureAwait(false);
                else if (exit is not null)
                {
                    // A partially delivered delta is never retried. If the deadline elapsed before
                    // delivery started, the remaining bounded pre-ack output is still safe to send.
                    await output.OpenAsync(lifetime.Token).ConfigureAwait(false);
                    await notify("process/exited", output.Result(exit), lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Poison(error); }
        finally { Release(entry, output); }
    }

    private async ValueTask CleanupAsync(ICodexProcessSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Poison(error); throw; }
    }

    private void Poison(Exception error)
    {
        lock (gate) healthFailure ??= error;
    }

    private void Release(Entry entry, ProcessOutput output)
    {
        entry.Ready.TrySetCanceled();
        output.Dispose();
        entry.Dispose();
        lock (gate)
        {
            running.Remove(entry.Request.ProcessId!);
            entry.Done.TrySetResult();
        }
    }

    /// <summary>Stops admission, cancels all owned processes and waits for confirmed tree cleanup.</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closing ??= CloseAsync(running.Values.Select(x => x.Done.Task).ToArray());
            return new(closing);
        }
    }

    private async Task CloseAsync(Task[] pending)
    {
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { Poison(error); }
        // A failing cancellation callback must not skip owned process cleanup.
        await Task.WhenAll(pending).ConfigureAwait(false);
        lifetime.Dispose();
        if (healthFailure is { } failure) throw new InvalidOperationException("Process cleanup or reporting failed.", failure);
    }

    private sealed class ProcessOutput(CodexCommandRequest request,
        Func<string, JsonElement, CancellationToken, ValueTask> notify) : IDisposable
    {
        private const int ChunkSize = 65536;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly MemoryStream[] buffers = [new(), new()];
        private readonly int[] counts = new int[2];
        private readonly bool[] capped = new bool[2];
        private readonly int[] sent = new int[2];
        private readonly bool[] capSent = new bool[2];
        private bool open;
        private int deliveryFailed;
        internal bool DeliveryFailed => Volatile.Read(ref deliveryFailed) != 0;

        internal async ValueTask AcceptAsync(CodexCommandStream stream, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (stream is not (CodexCommandStream.Stdout or CodexCommandStream.Stderr) ||
                (request.Tty && stream != CodexCommandStream.Stdout))
                throw new InvalidOperationException("Adapter reported an invalid process stream.");
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var index = (int)stream;
                if (capped[index] || bytes.IsEmpty) return;
                var take = Math.Min(bytes.Length, request.OutputBytesCap - counts[index]);
                counts[index] += take;
                capped[index] = counts[index] == request.OutputBytesCap;
                if (!request.StreamStdoutStderr || !open)
                    buffers[index].Write(bytes.Span[..take]);
                else
                    await SendAsync(index, bytes[..take], capped[index], ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        internal async Task OpenAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (open) return;
                if (request.StreamStdoutStderr)
                {
                    for (var i = 0; i < 2; i++)
                    {
                        var buffer = buffers[i];
                        while (sent[i] < buffer.Length)
                        {
                            var size = Math.Min(ChunkSize, (int)buffer.Length - sent[i]);
                            var last = sent[i] + size == buffer.Length && capped[i];
                            await SendDeltaAsync(i, buffer.GetBuffer().AsMemory(sent[i], size), last, ct).ConfigureAwait(false);
                            sent[i] += size;
                            capSent[i] |= last;
                        }
                        if (capped[i] && !capSent[i])
                        {
                            await SendDeltaAsync(i, ReadOnlyMemory<byte>.Empty, true, ct).ConfigureAwait(false);
                            capSent[i] = true;
                        }
                    }
                    foreach (var buffer in buffers) buffer.Dispose();
                }
                open = true;
            }
            finally { gate.Release(); }
        }

        private async ValueTask SendAsync(int index, ReadOnlyMemory<byte> bytes, bool capReached, CancellationToken ct)
        {
            if (bytes.IsEmpty)
            {
                if (capReached) await SendDeltaAsync(index, bytes, true, ct).ConfigureAwait(false);
                return;
            }
            for (var offset = 0; offset < bytes.Length;)
            {
                var length = Math.Min(ChunkSize, bytes.Length - offset);
                await SendDeltaAsync(index, bytes.Slice(offset, length),
                    capReached && offset + length == bytes.Length, ct).ConfigureAwait(false);
                offset += length;
            }
        }

        private async ValueTask SendDeltaAsync(int index, ReadOnlyMemory<byte> bytes, bool capReached, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await notify("process/outputDelta", JsonSerializer.SerializeToElement(
                    new ProcessDelta(request.ProcessId!, index == 0 ? "stdout" : "stderr", Convert.ToBase64String(bytes.Span), capReached),
                    CodexProcessJson.Default.ProcessDelta), ct).ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref deliveryFailed, 1);
                throw;
            }
        }

        internal JsonElement Result(CodexProcessExit exit) => JsonSerializer.SerializeToElement(
            new ProcessExited(request.ProcessId!, exit.ExitCode, exit.OutputDrainTimedOut,
                Text(0), capped[0], Text(1), capped[1]), CodexProcessJson.Default.ProcessExited);

        private string Text(int index) => request.StreamStdoutStderr ? "" :
            Encoding.UTF8.GetString(buffers[index].GetBuffer(), 0, (int)buffers[index].Length);

        public void Dispose() { foreach (var buffer in buffers) buffer.Dispose(); gate.Dispose(); }
    }
}

internal sealed record ProcessSpawnParams([property: JsonRequired] string[] Command,
    [property: JsonRequired] string ProcessHandle, [property: JsonRequired] string Cwd,
    bool Tty = false, bool StreamStdin = false, bool StreamStdoutStderr = false,
    int? OutputBytesCap = null, long? TimeoutMs = null, Dictionary<string, string?>? Env = null,
    CodexCommandTerminalSize? Size = null);
internal sealed record ProcessControlParams([property: JsonRequired] string ProcessHandle,
    string? DeltaBase64 = null, bool CloseStdin = false, CodexCommandTerminalSize? Size = null);
internal sealed record ProcessDelta(string ProcessHandle, string Stream, string DeltaBase64, bool CapReached);
internal sealed record ProcessExited(string ProcessHandle, int ExitCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OutputDrainTimedOut,
    string Stdout, bool StdoutCapReached, string Stderr, bool StderrCapReached);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProcessSpawnParams))]
[JsonSerializable(typeof(ProcessControlParams))]
[JsonSerializable(typeof(ProcessDelta))]
[JsonSerializable(typeof(ProcessExited))]
internal sealed partial class CodexProcessJson : JsonSerializerContext;
