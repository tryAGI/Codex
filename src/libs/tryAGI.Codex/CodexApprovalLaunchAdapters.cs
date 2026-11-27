using System.Collections.ObjectModel;
using System.Text;

namespace tryAGI.Codex;

/// <summary>Host-resolved enforcement identity for one launch. The policy digest must bind
/// the selected adapter, canonical cwd, resolved environment, effective permissions and
/// any other authority-bearing defaults or configuration revision. It is never a model
/// supplied permission profile or an unchecked description of the policy.</summary>
/// <param name="EffectivePolicySha256">SHA-256 of the exact resolved enforcement plan.</param>
/// <param name="ThreadId">Owning thread, if applicable.</param>
/// <param name="TurnId">Owning turn, if applicable.</param>
/// <param name="ItemId">Owning item, if applicable.</param>
/// <param name="EnvironmentId">Resolved execution environment, if applicable.</param>
public sealed record CodexApprovalLaunchScope(string EffectivePolicySha256, string? ThreadId = null,
    string? TurnId = null, string? ItemId = null, string? EnvironmentId = null);

/// <summary>Trusted preparation of one execution attempt. Preparation may resolve and
/// obtain approval, but must not launch the process. The callback receives the frozen
/// request and newly consumed record and must enforce the very same resolved policy and
/// deadline at the actual OS admission boundary, or refuse. Never re-resolve to a different
/// environment or silently select an unsandboxed fallback.</summary>
/// <typeparam name="TSession">The corresponding contained session capability.</typeparam>
/// <param name="ApprovalId">Host-retained exact approval identity for this attempt.</param>
/// <param name="Scope">Immutable resolved enforcement scope.</param>
/// <param name="StartAsync">Host-selected contained launch callback.</param>
public sealed record CodexApprovedLaunch<TSession>(Guid ApprovalId, CodexApprovalLaunchScope Scope,
    Func<CodexCommandRequest, CodexApprovalRecord,
        Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask>,
        CancellationToken, Task<TSession>> StartAsync) where TSession : class;

/// <summary>Deterministic exact-action fingerprint shared by approval preparation and
/// admission. This is a versioned binary identity, not a display command or shell string.
/// Null and empty values are distinct; environment keys are sorted ordinally. A command
/// approval cannot be reused for process/spawn even if all arguments are identical.</summary>
public static class CodexApprovalLaunchIdentity
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Computes the exact launch action using the host-generated execution ID.
    /// Use the same frozen request and immutable scope when admitting the approval.
    /// The resolved policy digest must describe the actual enforcement, including host defaults.</summary>
    public static CodexApprovalAction CreateAction(string method, CodexCommandRequest request, CodexApprovalLaunchScope scope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        if (method is not ("command/exec" or "process/spawn"))
            throw new ArgumentException("Unknown launch family.", nameof(method));
        if (request.ExecutionId == Guid.Empty || string.IsNullOrWhiteSpace(request.ConnectionId) ||
            scope.EffectivePolicySha256 is not { Length: 64 } hash ||
            hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Launch requires a host attempt identity and resolved policy fingerprint.");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Utf8, leaveOpen: true))
        {
            writer.Write("codex-approval-launch-v1");
            writer.Write(method);
            writer.Write(request.ExecutionId.ToByteArray());
            writer.Write(request.ConnectionId);
            Optional(writer, request.ProcessId);
            writer.Write(request.Command.Count);
            foreach (var arg in request.Command) writer.Write(arg);
            Optional(writer, request.Cwd);
            writer.Write(request.Environment.Count);
            foreach (var pair in request.Environment.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                writer.Write(pair.Key);
                Optional(writer, pair.Value);
            }
            writer.Write(request.Tty);
            writer.Write(request.StreamStdin);
            writer.Write(request.StreamStdoutStderr);
            writer.Write(request.Size is not null);
            if (request.Size is { } size)
            {
                writer.Write(size.Rows);
                writer.Write(size.Cols);
            }
            Optional(writer, request.SandboxPolicy?.GetRawText());
            Optional(writer, request.PermissionProfile);
            writer.Write(request.OutputBytesCap);
            writer.Write(request.Timeout.Ticks);
            writer.Write(hash);
            Optional(writer, scope.ThreadId);
            Optional(writer, scope.TurnId);
            Optional(writer, scope.ItemId);
            Optional(writer, scope.EnvironmentId);
        }
        return new(CodexApprovalKind.Command, request.ConnectionId, request.ExecutionId.ToString("D"),
            CodexApprovalAction.Fingerprint(bytes.ToArray()), scope.ThreadId, scope.TurnId,
            scope.ItemId, scope.EnvironmentId);
    }

    private static void Optional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }

    internal static CodexCommandRequest Freeze(CodexCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Interactive input is a distinct action, not authorized by a launch approval.
        // Until a separate writeStdin gate exists, refuse all interactive launches.
        if (request.Tty || request.StreamStdin || request.Size is not null)
            throw new PlatformNotSupportedException("Approval-gated launch does not authorize interactive stdin or PTY.");
        return request with
        {
            Command = Array.AsReadOnly(request.Command.ToArray()),
            Environment = new ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?>(request.Environment, StringComparer.Ordinal)),
            SandboxPolicy = request.SandboxPolicy?.Clone(),
        };
    }

    internal static async Task<TSession> StartAsync<TSession>(string method, CodexApprovalDispatcher dispatcher,
        Func<CodexCommandRequest, CancellationToken, ValueTask<CodexApprovedLaunch<TSession>>> prepare,
        CodexCommandRequest request, Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        CancellationToken ct) where TSession : class
    {
        ArgumentNullException.ThrowIfNull(output);
        ct.ThrowIfCancellationRequested();
        var exact = Freeze(request);
        var plan = await prepare(exact, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("No approved launch plan.");
        ct.ThrowIfCancellationRequested();
        if (plan.ApprovalId == Guid.Empty || plan.StartAsync is null || plan.Scope is null)
            throw new UnauthorizedAccessException("Invalid approved launch plan.");
        var action = CreateAction(method, exact, plan.Scope);
        // StartAsync can only be called by the winner of the durable consume. The
        // contained adapter owns cleanup even when the launch response is ambiguous.
        return await dispatcher.DispatchAsync(plan.ApprovalId, action,
            (receipt, token) => plan.StartAsync(exact, receipt, output, token), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Contained launch returned no session; the consumed approval cannot be retried.");
    }
}

/// <summary>Opt-in command/exec adapter integrating exact-action approval at the existing
/// native command host's launch boundary. All starts require a trusted prepared plan and a
/// durable approval. The caller owns the selected enforcing adapter; no default is supplied.</summary>
public sealed class CodexApprovalCommandAdapter : ICodexCommandAdapter
{
    private readonly CodexApprovalDispatcher dispatcher;
    private readonly Func<CodexCommandRequest, CancellationToken, ValueTask<CodexApprovedLaunch<ICodexCommandSession>>> prepare;

    /// <summary>Wraps the native command launch boundary with mandatory durable approval.</summary>
    public CodexApprovalCommandAdapter(CodexApprovalDispatcher dispatcher,
        Func<CodexCommandRequest, CancellationToken, ValueTask<CodexApprovedLaunch<ICodexCommandSession>>> prepare)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
    }

    /// <inheritdoc />
    public Task<ICodexCommandSession> StartAsync(CodexCommandRequest request,
        Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        CancellationToken cancellationToken) =>
        CodexApprovalLaunchIdentity.StartAsync("command/exec", dispatcher, prepare, request, output, cancellationToken);
}

/// <summary>Opt-in process/spawn adapter integrating exact-action approval at the existing
/// native process host's launch boundary. This never converts an approved command to a
/// process capability or substitutes a command adapter for a contained process adapter.</summary>
public sealed class CodexApprovalProcessAdapter : ICodexProcessAdapter
{
    private readonly CodexApprovalDispatcher dispatcher;
    private readonly Func<CodexCommandRequest, CancellationToken, ValueTask<CodexApprovedLaunch<ICodexProcessSession>>> prepare;

    /// <summary>Wraps the native process launch boundary with mandatory durable approval.</summary>
    public CodexApprovalProcessAdapter(CodexApprovalDispatcher dispatcher,
        Func<CodexCommandRequest, CancellationToken, ValueTask<CodexApprovedLaunch<ICodexProcessSession>>> prepare)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
    }

    /// <inheritdoc />
    public Task<ICodexProcessSession> StartAsync(CodexCommandRequest request,
        Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        CancellationToken cancellationToken) =>
        CodexApprovalLaunchIdentity.StartAsync("process/spawn", dispatcher, prepare, request, output, cancellationToken);
}
