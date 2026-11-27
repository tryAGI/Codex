using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Rust thread memory extraction mode. This is independent of read-context preferences.</summary>
public enum CodexThreadMemoryMode
{
    /// <summary>Eligible for memory extraction, subject to host policy.</summary>
    Enabled,
    /// <summary>Exclude this thread from memory extraction.</summary>
    Disabled,
}

/// <summary>Authorized observation of durable V2 memory progress and artifact validity.</summary>
/// <param name="V2ConsolidatedThreads">Maximum distinct thread count from successful consolidation.</param>
/// <param name="HasV1Workspace">Whether a V1 memory workspace exists.</param>
/// <param name="HasValidV2Summary">Whether the current V2 summary meets the pinned Rust format.</param>
public sealed record CodexMemoryReadinessObservation(uint V2ConsolidatedThreads, bool HasV1Workspace, bool HasValidV2Summary);

/// <summary>Typed result of memory/status.</summary>
/// <param name="V2ConsolidatedThreads">Maximum distinct thread count from successful consolidation.</param>
/// <param name="V2Ready">Whether V2 may be selected for a new thread.</param>
public sealed record CodexMemoryStatusResult(uint V2ConsolidatedThreads, bool V2Ready);

/// <summary>Typed thread/memory/read result.</summary>
/// <param name="Summary">Original summary from the pinned thread version, or null.</param>
public sealed record CodexThreadMemoryReadResult(string? Summary);

/// <summary>Trusted host authorization context, never accepted as wire parameters.</summary>
/// <param name="AccountId">Authenticated account.</param>
/// <param name="Method">Exact App Server method.</param>
/// <param name="ThreadId">Thread for read or extraction-mode mutation.</param>
/// <param name="Mode">Requested extraction mode, if any.</param>
/// <param name="OperationId">Stable host-supplied mutation identity, if any.</param>
public sealed record CodexMemoryRpcAccess(string AccountId, string Method, Guid? ThreadId = null,
    CodexThreadMemoryMode? Mode = null, Guid? OperationId = null);

/// <summary>Definitive settlement of a memory lifecycle mutation.</summary>
public enum CodexMemoryLifecycleSettlement
{
    /// <summary>Committed, or an exact idempotent replay of a committed mutation.</summary>
    Applied,
    /// <summary>Thread is absent from this authorized account.</summary>
    NotFound,
    /// <summary>Authorization or current source policy rejected the mutation.</summary>
    Denied,
    /// <summary>Known concurrency conflict, with no effect.</summary>
    Conflict,
}

/// <summary>Receipt for one exact lifecycle mutation.</summary>
/// <param name="Access">Original authorized mutation identity and contents.</param>
/// <param name="Settlement">Definitive settlement.</param>
public sealed record CodexMemoryLifecycleReceipt(CodexMemoryRpcAccess Access, CodexMemoryLifecycleSettlement Settlement);

/// <summary>
/// Durable host adapter for lifecycle RPCs. The host must enforce the authenticated account
/// and current authorization in the same transaction as mutation and receipt publication.
/// This contract must never rewrite or delete the native thread journal.
/// </summary>
public interface ICodexMemoryLifecycleStorage
{
    /// <summary>
    /// Observes readiness for one account. Implementations should read indexed progress and
    /// validated artifact metadata from one consistent snapshot, without reading thread history.
    /// </summary>
    ValueTask<CodexMemoryReadinessObservation> ReadReadinessAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies thread/memoryMode/set to a stored or loaded thread, or memory/reset to both
    /// memory pipeline namespaces. Mode changes affect extraction eligibility, not the read
    /// preference. Reset clears generated memory and extraction/consolidation data, fences
    /// in-flight workers, and preserves original history and existing thread modes. Do not
    /// create an unused V1 workspace. Persist operation ID and exact receipt atomically;
    /// identical replays return the original receipt. Unknown outcomes throw.
    /// </summary>
    ValueTask<CodexMemoryLifecycleReceipt> ApplyAsync(CodexMemoryRpcAccess access, CancellationToken cancellationToken);
}

/// <summary>Readiness rules from the pinned Rust memory workspace and status processor.</summary>
public static class CodexMemoryReadiness
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] RequiredHeadings =
        ["## User Profile", "## User preferences", "## General Tips", "## What's in Memory"];

    /// <summary>Validates the V2 generated summary before marking its artifact ready.</summary>
    public static bool IsValidV2Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        try
        {
            if (Utf8.GetByteCount(summary) >= 10_000) return false;
        }
        catch (EncoderFallbackException) { return false; }
        var lines = summary.Split('\n');
        var first = lines[0];
        if (lines.Length > 1 && first.EndsWith('\r')) first = first[..^1];
        return first == "v1" && RequiredHeadings.All(heading =>
            lines.Any(line => string.Equals(line.Trim(), heading, StringComparison.Ordinal)));
    }

    /// <summary>Computes readiness without exposing the generated summary to the caller.</summary>
    public static CodexMemoryStatusResult Evaluate(CodexMemoryReadinessObservation observation, uint minimum = 20)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (minimum is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(minimum));
        return new(observation.V2ConsolidatedThreads, !observation.HasV1Workspace ||
            observation.V2ConsolidatedThreads >= minimum && observation.HasValidV2Summary);
    }
}

/// <summary>
/// Opt-in, account-bound App Server memory RPC facade. Loaded-thread resolution, permission
/// checks and durable lifecycle effects are host-owned capabilities. A missing adapter never
/// acknowledges unsupported writes. No provider calls, journal replay or implicit thread load.
/// </summary>
public sealed class CodexMemoryRpc
{
    private readonly string accountId;
    private readonly Func<Guid, CancellationToken, ValueTask<CodexMemoryBackend?>> resolveLoadedThread;
    private readonly Func<CodexMemoryRpcAccess, CancellationToken, ValueTask> authorize;
    private readonly ICodexMemoryLifecycleStorage? lifecycle;

    /// <summary>Creates a memory RPC facade for an authenticated account.</summary>
    public CodexMemoryRpc(string accountId,
        Func<Guid, CancellationToken, ValueTask<CodexMemoryBackend?>> resolveLoadedThread,
        Func<CodexMemoryRpcAccess, CancellationToken, ValueTask> authorize,
        ICodexMemoryLifecycleStorage? lifecycle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        this.accountId = accountId;
        this.resolveLoadedThread = resolveLoadedThread ?? throw new ArgumentNullException(nameof(resolveLoadedThread));
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        this.lifecycle = lifecycle;
    }

    /// <summary>Reports only methods implemented with the configured host capabilities.</summary>
    public bool Handles(string method) => method == "thread/memory/read" ||
        lifecycle is not null && method is ("memory/status" or "memory/reset" or "thread/memoryMode/set");

    /// <summary>Reads the original summary of an already started or resumed thread.</summary>
    public async Task<CodexThreadMemoryReadResult> ReadThreadAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        CheckThread(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        await authorize(new(accountId, "thread/memory/read", threadId), cancellationToken).ConfigureAwait(false);
        var backend = await resolveLoadedThread(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("Thread must already be started or resumed.");
        if (!backend.IsBoundTo(accountId, threadId))
            throw new UnauthorizedAccessException("Loaded memory backend has a different authority.");
        return new(await backend.ReadRpcSummaryAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Returns V2 progress and readiness with the Rust threshold default of 20.</summary>
    public async Task<CodexMemoryStatusResult> StatusAsync(uint minimum = 20, CancellationToken cancellationToken = default)
    {
        var storage = RequireLifecycle();
        if (minimum is < 1 or > 4096) throw Invalid("minConsolidatedThreads must be between 1 and 4096.");
        cancellationToken.ThrowIfCancellationRequested();
        await authorize(new(accountId, "memory/status"), cancellationToken).ConfigureAwait(false);
        var observed = await storage.ReadReadinessAsync(accountId, cancellationToken).ConfigureAwait(false);
        return CodexMemoryReadiness.Evaluate(observed, minimum);
    }

    /// <summary>Sets extraction eligibility through the host's authorized durable mutation.</summary>
    public Task SetThreadModeAsync(Guid threadId, CodexThreadMemoryMode mode, Guid operationId,
        CancellationToken cancellationToken = default)
    {
        CheckThread(threadId);
        if (mode is not (CodexThreadMemoryMode.Enabled or CodexThreadMemoryMode.Disabled))
            throw Invalid("Invalid thread memory mode.");
        return ApplyAsync(new(accountId, "thread/memoryMode/set", threadId, mode, operationId), cancellationToken);
    }

    /// <summary>Explicitly resets both memory pipelines, not thread history.</summary>
    public Task ResetAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        ApplyAsync(new(accountId, "memory/reset", OperationId: operationId), cancellationToken);

    /// <summary>
    /// Dispatches exact Rust App Server JSON shapes. Mutating methods require an out-of-band
    /// stable host operation ID, retained across ambiguous outcomes; it is not a wire field.
    /// </summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null,
        Guid? operationId = null, CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported memory RPC.");
        var p = parameters is { ValueKind: not JsonValueKind.Null } supplied ? supplied : Element(new());
        switch (method)
        {
            case "thread/memory/read":
                Fields(p, "threadId");
                var read = await ReadThreadAsync(Thread(p), cancellationToken).ConfigureAwait(false);
                return Element(new() { ["summary"] = read.Summary });
            case "memory/status":
                Fields(p, "minConsolidatedThreads");
                uint minimum = 20;
                if (p.TryGetProperty("minConsolidatedThreads", out var value) && value.ValueKind != JsonValueKind.Null &&
                    (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out minimum)))
                    throw Invalid("Invalid minConsolidatedThreads.");
                var status = await StatusAsync(minimum, cancellationToken).ConfigureAwait(false);
                return Element(new() { ["v2ConsolidatedThreads"] = status.V2ConsolidatedThreads, ["v2Ready"] = status.V2Ready });
            case "thread/memoryMode/set":
                Fields(p, "threadId", "mode");
                if (!p.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String)
                    throw Invalid("Invalid thread memory mode.");
                var parsed = mode.GetString() switch
                {
                    "enabled" => CodexThreadMemoryMode.Enabled,
                    "disabled" => CodexThreadMemoryMode.Disabled,
                    _ => throw Invalid("Invalid thread memory mode."),
                };
                await SetThreadModeAsync(Thread(p), parsed, operationId ?? Guid.Empty, cancellationToken).ConfigureAwait(false);
                return Element(new());
            case "memory/reset":
                Fields(p);
                await ResetAsync(operationId ?? Guid.Empty, cancellationToken).ConfigureAwait(false);
                return Element(new());
            default:
                throw new CodexThreadRpcException(-32601, "Unsupported memory RPC.");
        }
    }

    private async Task ApplyAsync(CodexMemoryRpcAccess access, CancellationToken ct)
    {
        var storage = RequireLifecycle();
        if (access.OperationId is null || access.OperationId == Guid.Empty)
            throw Invalid("Stable host operation ID is required for a memory mutation.");
        ct.ThrowIfCancellationRequested();
        await authorize(access, ct).ConfigureAwait(false);
        var receipt = await storage.ApplyAsync(access, ct).ConfigureAwait(false);
        if (receipt.Access != access) throw new InvalidDataException("Memory mutation receipt identity mismatch.");
        switch (receipt.Settlement)
        {
            case CodexMemoryLifecycleSettlement.Applied: return;
            case CodexMemoryLifecycleSettlement.NotFound: throw Invalid("Thread is unavailable.");
            case CodexMemoryLifecycleSettlement.Denied: throw new UnauthorizedAccessException("Memory mutation denied.");
            case CodexMemoryLifecycleSettlement.Conflict: throw new CodexThreadRpcException(-32000, "Memory mutation conflicted.");
            default: throw new InvalidDataException("Unknown memory mutation settlement.");
        }
    }

    private ICodexMemoryLifecycleStorage RequireLifecycle() =>
        lifecycle ?? throw new CodexThreadRpcException(-32601, "Memory lifecycle storage unavailable.");

    private static void CheckThread(Guid threadId)
    {
        if (threadId == Guid.Empty) throw Invalid("Invalid threadId.");
    }

    private static Guid Thread(JsonElement p)
    {
        if (!p.TryGetProperty("threadId", out var field) || field.ValueKind != JsonValueKind.String ||
            !field.TryGetGuid(out var id) || id == Guid.Empty) throw Invalid("Invalid threadId.");
        return id;
    }

    private static void Fields(JsonElement p, params string[] allowed)
    {
        if (p.ValueKind != JsonValueKind.Object) throw Invalid("Expected memory RPC object parameters.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in p.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw Invalid("Unexpected or duplicate memory RPC parameter.");
    }

    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);

    private static JsonElement Element(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }
}
