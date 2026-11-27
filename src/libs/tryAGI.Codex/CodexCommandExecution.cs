using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>PTY dimensions in character cells.</summary>
/// <param name="Rows">Height.</param>
/// <param name="Cols">Width.</param>
public sealed record CodexCommandTerminalSize(ushort Rows, ushort Cols);

/// <summary>Output stream from the command adapter. PTYs use stdout only.</summary>
public enum CodexCommandStream
{
    /// <summary>Standard output.</summary>
    Stdout,
    /// <summary>Standard error.</summary>
    Stderr,
}

/// <summary>Validated, bounded execution request. The adapter must resolve and enforce the
/// host's effective permission profile and sandbox; these inputs do not grant authority.</summary>
/// <param name="ExecutionId">Unique host-generated execution attempt; never reused when a client process ID is reused.</param>
/// <param name="ProcessId">Client correlation ID, not authority.</param>
/// <param name="ConnectionId">Authenticated owner supplied by the host.</param>
/// <param name="Command">Exact argv, including the executable.</param>
/// <param name="Cwd">Requested absolute working directory, or host default.</param>
/// <param name="Environment">Environment overrides; null values remove variables.</param>
/// <param name="Tty">Use a real PTY.</param>
/// <param name="StreamStdin">Permit writes to stdin.</param>
/// <param name="StreamStdoutStderr">Deliver output notifications.</param>
/// <param name="Size">Initial terminal size, if specified.</param>
/// <param name="SandboxPolicy">Requested sandbox policy, if specified.</param>
/// <param name="PermissionProfile">Requested permission profile, if specified.</param>
/// <param name="OutputBytesCap">Maximum bytes retained per stream.</param>
/// <param name="Timeout">Maximum operation duration.</param>
public sealed record CodexCommandRequest(Guid ExecutionId, string? ProcessId, string ConnectionId, IReadOnlyList<string> Command,
    string? Cwd, IReadOnlyDictionary<string, string?> Environment, bool Tty, bool StreamStdin,
    bool StreamStdoutStderr, CodexCommandTerminalSize? Size, JsonElement? SandboxPolicy,
    string? PermissionProfile, int OutputBytesCap, TimeSpan Timeout);

/// <summary>An explicitly trusted, sandbox-enforcing host adapter. No default local executor
/// exists. Start must reject unsupported constraints, authorize the exact request (including cwd,
/// argv and environment), and clean up any partially started process tree on error or cancellation.
/// Output callbacks must be awaited and cease before Completion settles. Callback errors must fail
/// the execution. The adapter must never fall back to unsandboxed execution.</summary>
public interface ICodexCommandAdapter
{
    /// <summary>Starts a contained command and returns its owned session.</summary>
    Task<ICodexCommandSession> StartAsync(CodexCommandRequest request,
        Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        CancellationToken cancellationToken);
}

/// <summary>Owns an admitted process and all descendants. Disposal must terminate and await
/// cleanup even after the parent exits; errors must be surfaced, not treated as successful cleanup.
/// Controls must be safe when racing completion or disposal.</summary>
public interface ICodexCommandSession : IAsyncDisposable
{
    /// <summary>Exit code after all output callbacks have finished.</summary>
    Task<int> Completion { get; }
    /// <summary>Writes bytes and optionally closes stdin.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, bool closeStdin, CancellationToken cancellationToken);
    /// <summary>Resizes the real PTY.</summary>
    ValueTask ResizeAsync(CodexCommandTerminalSize size, CancellationToken cancellationToken);
    /// <summary>Terminates the contained process; Completion still reports the final result.</summary>
    ValueTask TerminateAsync(CancellationToken cancellationToken);
}

/// <summary>Connection-scoped native command/exec RPC host. Create one for each authenticated
/// connection and await disposal on disconnect. The adapter is a mandatory trust boundary.</summary>
public sealed class CodexCommandExecution : IAsyncDisposable
{
    private readonly string connectionId;
    private readonly ICodexCommandAdapter adapter;
    private readonly Func<string, JsonElement, CancellationToken, ValueTask> notify;
    private readonly int maximumConcurrentCommands;
    private readonly object gate = new();
    private readonly Dictionary<string, Running> running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private Task? closing;
    private Exception? cleanupFailure;

    private sealed class Running(CodexCommandRequest request)
    {
        internal CodexCommandRequest Request { get; } = request;
        internal TaskCompletionSource<ICodexCommandSession> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Creates a host with explicit execution and notification adapters.</summary>
    public CodexCommandExecution(string connectionId, ICodexCommandAdapter adapter,
        Func<string, JsonElement, CancellationToken, ValueTask> notify, int maximumConcurrentCommands = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentCommands, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentCommands, 64);
        this.connectionId = connectionId;
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
        this.maximumConcurrentCommands = maximumConcurrentCommands;
    }

    /// <summary>Native sandboxed command methods supported by this host.</summary>
    public static bool Handles(string method) => method is "command/exec" or "command/exec/write"
        or "command/exec/resize" or "command/exec/terminate";

    /// <summary>Serves one command request. Cancellation of command/exec cancels the owned process.</summary>
    public async Task<JsonElement> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported command method.");
        if (parameters.ValueKind != JsonValueKind.Object) throw Invalid("Expected command parameters.");
        EnsureUniqueFields(parameters);
        if (method == "command/exec")
            return await ExecuteAsync(Read(parameters, CodexCommandJson.Default.CommandParams), cancellationToken).ConfigureAwait(false);

        var control = Read(parameters, CodexCommandJson.Default.ControlParams);
        if (string.IsNullOrWhiteSpace(control.ProcessId)) throw Invalid("Missing processId.");
        Running entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing is not null, this);
            if (!running.TryGetValue("client:" + control.ProcessId, out entry!)) throw Invalid("Unknown active processId.");
        }

        if (method == "command/exec/write")
        {
            if (!entry.Request.StreamStdin || control.Size is not null) throw Invalid("Stdin streaming is not enabled or fields are invalid.");
            var data = Decode(control.DeltaBase64);
            if (data.Length == 0 && !control.CloseStdin) throw Invalid("No stdin data or close requested.");
            var session = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.WriteAsync(data, control.CloseStdin, cancellationToken).ConfigureAwait(false);
        }
        else if (method == "command/exec/resize")
        {
            if (!entry.Request.Tty || control.DeltaBase64 is not null || control.CloseStdin || !ValidSize(control.Size))
                throw Invalid("Resize requires a PTY and valid size.");
            var session = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.ResizeAsync(control.Size!, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (control.Size is not null || control.DeltaBase64 is not null || control.CloseStdin)
                throw Invalid("Invalid terminate fields.");
            var session = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.TerminateAsync(cancellationToken).ConfigureAwait(false);
        }
        return JsonSerializer.SerializeToElement(new CommandEmpty(), CodexCommandJson.Default.CommandEmpty);
    }

    private async Task<JsonElement> ExecuteAsync(CommandParams input, CancellationToken cancellationToken)
    {
        var request = Validate(input);
        var key = input.ProcessId is null ? "internal:" + Guid.NewGuid().ToString("N") : "client:" + input.ProcessId;
        var entry = new Running(request);
        CancellationTokenSource linked;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing is not null, this);
            if (cleanupFailure is not null) throw new CodexThreadRpcException(-32010, "Prior command cleanup failed; connection is unsafe.");
            if (running.ContainsKey(key)) throw Invalid("Duplicate active processId.");
            if (running.Count >= maximumConcurrentCommands) throw new CodexThreadRpcException(-32010, "Command capacity exhausted.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            running.Add(key, entry);
        }
        using (linked)
        {
            linked.CancelAfter(request.Timeout);
            using var output = new CommandOutput(request, input.ProcessId, notify);
            ICodexCommandSession? session = null;
            try
            {
                session = await adapter.StartAsync(request, async (stream, bytes, token) =>
                {
                    try { await output.AcceptAsync(stream, bytes, linked.Token).ConfigureAwait(false); }
                    catch { await linked.CancelAsync().ConfigureAwait(false); throw; }
                }, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                entry.Ready.TrySetResult(session);
                var exit = await session.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
                return output.Result(exit);
            }
            finally
            {
                try
                {
                    if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    lock (gate) cleanupFailure ??= error;
                    throw;
                }
                finally
                {
                    // Retain the identity and capacity until tree cleanup settles.
                    entry.Ready.TrySetCanceled();
                    lock (gate) { running.Remove(key); entry.Done.TrySetResult(); }
                }
            }
        }
    }

    private CodexCommandRequest Validate(CommandParams input)
    {
        if (input.Command is not { Length: > 0 and <= 1024 } || string.IsNullOrWhiteSpace(input.Command[0]) ||
            input.Command.Any(x => x is null || x.Contains('\0')) || input.Command.Sum(x => (long)x.Length) > 131072)
            throw Invalid("Invalid or oversized argv.");
        if (input.ProcessId is { } id && (string.IsNullOrWhiteSpace(id) || id.Length > 256))
            throw Invalid("Invalid processId.");
        if ((input.Tty || input.StreamStdin || input.StreamStdoutStderr) && input.ProcessId is null)
            throw Invalid("Streaming requires processId.");
        if (input.Size is not null && (!input.Tty || !ValidSize(input.Size)))
            throw Invalid("Terminal size requires a PTY.");
        if (input.Cwd is { } cwd && (!Path.IsPathFullyQualified(cwd) || cwd.Contains('\0')))
            throw Invalid("Working directory must be absolute.");
        if (input.SandboxPolicy is not null && input.PermissionProfile is not null)
            throw Invalid("sandboxPolicy and permissionProfile are mutually exclusive.");
        if (input.SandboxPolicy is { ValueKind: not JsonValueKind.Object } || input.PermissionProfile is { Length: 0 })
            throw Invalid("Invalid sandbox or permission profile.");
        if ((input.DisableOutputCap && input.OutputBytesCap is not null) || (input.DisableTimeout && input.TimeoutMs is not null))
            throw Invalid("Conflicting execution bounds.");
        if (input.DisableOutputCap || input.DisableTimeout)
            throw new CodexThreadRpcException(-32010, "Unbounded native execution is not supported by this host.");
        if (input.OutputBytesCap is < 0 or > 8388608 || input.TimeoutMs is <= 0 or > 300000)
            throw Invalid("Execution bounds exceed host limits.");
        var env = input.Env ?? new Dictionary<string, string?>();
        if (env.Count > 256 || env.Any(x => string.IsNullOrEmpty(x.Key) || x.Key.Contains('=') ||
            x.Key.Contains('\0') || x.Value?.Contains('\0') == true) ||
            env.Sum(x => (long)x.Key.Length + (x.Value?.Length ?? 0)) > 131072)
            throw Invalid("Invalid or oversized environment.");
        return new(Guid.NewGuid(), input.ProcessId, connectionId, Array.AsReadOnly(input.Command), input.Cwd,
            new ReadOnlyDictionary<string, string?>(env), input.Tty, input.Tty || input.StreamStdin,
            input.Tty || input.StreamStdoutStderr, input.Size, input.SandboxPolicy?.Clone(), input.PermissionProfile,
            input.OutputBytesCap ?? 1048576, TimeSpan.FromMilliseconds(input.TimeoutMs ?? 60000));
    }

    private static bool ValidSize(CodexCommandTerminalSize? size) => size is { Rows: > 0, Cols: > 0 };
    private static CodexThreadRpcException Invalid(string message) => new(-32600, message);

    private static T Read<T>(JsonElement parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try { return parameters.Deserialize(type) ?? throw Invalid("Missing command parameters."); }
        catch (JsonException) { throw Invalid("Malformed or unsupported command parameters."); }
    }

    private static void EnsureUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw Invalid("Duplicate command parameter.");
                EnsureUniqueFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) EnsureUniqueFields(item);
    }

    private static byte[] Decode(string? base64)
    {
        if (base64 is null) return [];
        if (base64.Length > 1398104) throw Invalid("Stdin chunk is too large.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw Invalid("Invalid stdin base64."); }
        if (bytes.Length > 1048576 || Convert.ToBase64String(bytes) != base64) throw Invalid("Invalid stdin base64.");
        return bytes;
    }

    /// <summary>Stops admission and waits for every owned process tree to be disposed.</summary>
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
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(pending).ConfigureAwait(false);
        lifetime.Dispose();
        if (cleanupFailure is { } failure) throw new InvalidOperationException("Command process cleanup could not be verified.", failure);
    }

    private sealed class CommandOutput(CodexCommandRequest request, string? processId,
        Func<string, JsonElement, CancellationToken, ValueTask> notify) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly MemoryStream stdout = new(), stderr = new();
        private readonly int[] counts = new int[2];
        private readonly bool[] capped = new bool[2];

        internal async ValueTask AcceptAsync(CodexCommandStream stream, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            if (stream is not (CodexCommandStream.Stdout or CodexCommandStream.Stderr) ||
                (request.Tty && stream != CodexCommandStream.Stdout))
                throw new InvalidOperationException("Adapter reported an invalid stream.");
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var index = (int)stream;
                if (capped[index] || bytes.IsEmpty) return;
                var take = Math.Min(bytes.Length, request.OutputBytesCap - counts[index]);
                counts[index] += take;
                capped[index] = counts[index] == request.OutputBytesCap;
                if (request.StreamStdoutStderr)
                {
                    var delta = new CommandDelta(processId!, index == 0 ? "stdout" : "stderr",
                        Convert.ToBase64String(bytes.Span[..take]), capped[index]);
                    await notify("command/exec/outputDelta",
                        JsonSerializer.SerializeToElement(delta, CodexCommandJson.Default.CommandDelta), token).ConfigureAwait(false);
                }
                else if (take != 0) (index == 0 ? stdout : stderr).Write(bytes.Span[..take]);
            }
            finally { gate.Release(); }
        }

        internal JsonElement Result(int exitCode) => JsonSerializer.SerializeToElement(
            new CommandResult(exitCode, Encoding.UTF8.GetString(stdout.GetBuffer(), 0, (int)stdout.Length),
                Encoding.UTF8.GetString(stderr.GetBuffer(), 0, (int)stderr.Length)), CodexCommandJson.Default.CommandResult);

        public void Dispose() { stdout.Dispose(); stderr.Dispose(); gate.Dispose(); }
    }
}

internal sealed record CommandParams([property: JsonRequired] string[] Command,
    string? ProcessId = null, bool Tty = false, bool StreamStdin = false, bool StreamStdoutStderr = false,
    int? OutputBytesCap = null, bool DisableOutputCap = false, bool DisableTimeout = false,
    long? TimeoutMs = null, string? Cwd = null, Dictionary<string, string?>? Env = null,
    CodexCommandTerminalSize? Size = null, JsonElement? SandboxPolicy = null, string? PermissionProfile = null);
internal sealed record ControlParams([property: JsonRequired] string ProcessId, string? DeltaBase64 = null,
    bool CloseStdin = false, CodexCommandTerminalSize? Size = null);
internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
internal sealed record CommandDelta(string ProcessId, string Stream, string DeltaBase64, bool CapReached);
internal sealed record CommandEmpty;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CommandParams))]
[JsonSerializable(typeof(ControlParams))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(CommandDelta))]
[JsonSerializable(typeof(CommandEmpty))]
internal sealed partial class CodexCommandJson : JsonSerializerContext;
