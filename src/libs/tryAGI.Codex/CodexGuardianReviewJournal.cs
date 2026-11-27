using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Account- and connection-bound Guardian review identity, resolved by the host.
/// The display action does not replace the exact executable action fingerprint.</summary>
/// <param name="AccountId">Authenticated account.</param>
/// <param name="ConnectionId">Authenticated connection.</param>
/// <param name="ReviewId">Stable Guardian review ID.</param>
/// <param name="ThreadId">Native thread ID.</param>
/// <param name="TurnId">Native turn ID.</param>
/// <param name="TargetItemId">Reviewed item, if the wire action has one.</param>
/// <param name="Action">Independently resolved exact action.</param>
public sealed record CodexGuardianReviewScope(string AccountId, string ConnectionId, string ReviewId,
    string ThreadId, string TurnId, string? TargetItemId, CodexApprovalAction Action);

/// <summary>Durable Guardian review history. Raw wire payloads can contain sensitive data;
/// the host owns encrypted storage, authorization and retention. A confirmed notification
/// is a transport receipt, never execution permission.</summary>
/// <param name="Scope">Immutable review identity.</param>
/// <param name="Revision">Atomic storage revision.</param>
/// <param name="Status">Pinned lifecycle status.</param>
/// <param name="Started">Original started notification payload.</param>
/// <param name="Completed">Original terminal payload, if recorded.</param>
/// <param name="StartedConfirmed">The start notification was accepted by the host transport.</param>
/// <param name="CompletedConfirmed">The terminal notification was accepted by the host transport.</param>
/// <param name="DenialEventSha256">Digest of a trusted original denial event eligible for lookup.</param>
/// <param name="EscalationApprovalId">One exact pending approval bound to this denial, if any.</param>
public sealed record CodexGuardianReviewRecord(CodexGuardianReviewScope Scope, long Revision, string Status,
    JsonElement Started, JsonElement? Completed = null, bool StartedConfirmed = false,
    bool CompletedConfirmed = false, string? DenialEventSha256 = null, Guid? EscalationApprovalId = null)
{
    /// <inheritdoc />
    public override string ToString() => $"Guardian review {Scope.ReviewId} ({Status}, revision {Revision})";
}

/// <summary>Host-owned durable, atomic Guardian review storage. Success means the new
/// revision was durably committed. An exception may represent an ambiguous commit;
/// callers must not automatically publish or execute an uncertain effect.</summary>
public interface ICodexGuardianReviewStorage
{
    /// <summary>Reads the exact account/review key.</summary>
    Task<CodexGuardianReviewRecord?> ReadAsync(string accountId, string reviewId, CancellationToken cancellationToken);

    /// <summary>Atomically creates or replaces the exact revision.</summary>
    Task<bool> CompareExchangeAsync(string accountId, string reviewId, long? expectedRevision,
        CodexGuardianReviewRecord record, CancellationToken cancellationToken);
}

/// <summary>Result of recording a lifecycle event; Applied is true only for the CAS winner.
/// NotificationConfirmed is a transport receipt, not proof of client display or action authorization.</summary>
/// <param name="Record">The durable state.</param>
/// <param name="Applied">Whether this call committed the event.</param>
/// <param name="NotificationConfirmed">Whether the corresponding event publication is confirmed.</param>
public sealed record CodexGuardianReviewMutation(CodexGuardianReviewRecord Record, bool Applied, bool NotificationConfirmed);

/// <summary>Native Guardian review history and notification boundary. Only trusted host reviewer
/// events may enter this journal. Approved assessments never create or consume execution grants.
/// A lifecycle event is durably committed before its notification. Duplicate or uncertain
/// mutations do not replay notifications. A terminal event whose start delivery is uncertain
/// is retained without emitting an out-of-order notification; reconcile explicitly from storage.</summary>
public sealed class CodexGuardianReviewJournal
{
    private const int MaximumPayloadBytes = 262144;
    private const string StartedMethod = "item/autoApprovalReview/started";
    private const string CompletedMethod = "item/autoApprovalReview/completed";
    private readonly ICodexGuardianReviewStorage storage;
    private readonly CodexApprovalStore approvals;
    private readonly string accountId;
    private readonly string connectionId;
    private readonly Func<CodexGuardianReviewScope, CancellationToken, ValueTask> authorize;
    private readonly Func<string, JsonElement, CancellationToken, ValueTask> notify;

    /// <summary>Creates a journal for one authenticated account and connection with explicit
    /// authorization, durable storage and transport. The transport callback must return only
    /// after accepting the event in order; a failed or uncertain send is not automatically retried.</summary>
    public CodexGuardianReviewJournal(ICodexGuardianReviewStorage storage, CodexApprovalStore approvals,
        string connectionId, Func<CodexGuardianReviewScope, CancellationToken, ValueTask> authorize,
        Func<string, JsonElement, CancellationToken, ValueTask> notify)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        accountId = approvals.AccountId;
        ValidateIdentity(accountId);
        ValidateIdentity(connectionId);
        this.connectionId = connectionId;
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    /// <summary>Records one exact in-progress review. Only the durable CAS winner may publish
    /// its notification; duplicates must match the original scope and raw payload.</summary>
    public async Task<CodexGuardianReviewMutation> StartAsync(JsonElement payload,
        CodexApprovalAction action, CancellationToken cancellationToken = default)
    {
        var scope = Scope(payload, action, terminal: false, out var status);
        await AuthorizeAsync(scope, cancellationToken).ConfigureAwait(false);
        var existing = await ReadCoreAsync(scope.ReviewId, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return SameStart(existing, scope, payload);
        var pending = new CodexGuardianReviewRecord(scope, 1, status, payload.Clone());
        cancellationToken.ThrowIfCancellationRequested();
        if (!await storage.CompareExchangeAsync(accountId, scope.ReviewId, null, pending, cancellationToken).ConfigureAwait(false))
        {
            existing = await ReadCoreAsync(scope.ReviewId, cancellationToken).ConfigureAwait(false);
            return existing is not null ? SameStart(existing, scope, payload) : throw Conflict();
        }
        cancellationToken.ThrowIfCancellationRequested();
        await notify(StartedMethod, pending.Started.Clone(), cancellationToken).ConfigureAwait(false);
        return new(await ConfirmAsync(scope, terminal: false, cancellationToken).ConfigureAwait(false), true, true);
    }

    /// <summary>Records one exact terminal assessment. A trusted optional denial event is
    /// fingerprinted for escalation lookup; it cannot be supplied by an untrusted client.
    /// Unknown decisions, action changes, invalid timestamps and conflicting completions fail closed.</summary>
    public async Task<CodexGuardianReviewMutation> CompleteAsync(JsonElement payload,
        CodexApprovalAction action, JsonElement? denialEvent = null, CancellationToken cancellationToken = default)
    {
        var scope = Scope(payload, action, terminal: true, out var status);
        string? denialDigest = null;
        if (denialEvent is { } evidence)
        {
            if (status != "denied") throw Invalid("Only a denied assessment can bind denial evidence.");
            ValidateDenialEvidence(scope, payload, evidence);
            denialDigest = Fingerprint(evidence);
        }
        await AuthorizeAsync(scope, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await ReadCoreAsync(scope.ReviewId, cancellationToken).ConfigureAwait(false) ?? throw Conflict();
            if (current.Scope != scope ||
                Number(current.Started, "startedAtMs") != Number(payload, "startedAtMs") ||
                !Exact(current.Started.GetProperty("action"), payload.GetProperty("action")))
                throw Conflict();
            if (current.Completed is { } completed)
            {
                if (!Exact(completed, payload) || current.DenialEventSha256 != denialDigest) throw Conflict();
                return new(current, false, current.CompletedConfirmed);
            }

            var next = current with
            {
                Revision = checked(current.Revision + 1),
                Status = status,
                Completed = payload.Clone(),
                DenialEventSha256 = denialDigest,
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (!await storage.CompareExchangeAsync(accountId, scope.ReviewId, current.Revision, next, cancellationToken).ConfigureAwait(false))
                continue;
            // Another host may have committed the start without knowing whether it was
            // published. Retain the terminal decision, but do not overtake an uncertain start.
            if (!next.StartedConfirmed) return new(next, true, false);
            cancellationToken.ThrowIfCancellationRequested();
            await notify(CompletedMethod, next.Completed.Value.Clone(), cancellationToken).ConfigureAwait(false);
            return new(await ConfirmAsync(scope, terminal: true, cancellationToken).ConfigureAwait(false), true, true);
        }
        throw Conflict();
    }

    /// <summary>Returns the authorized durable review without publishing anything.</summary>
    public async Task<CodexGuardianReviewRecord?> ReadAsync(string reviewId, CancellationToken cancellationToken = default)
    {
        var current = await ReadCoreAsync(reviewId, cancellationToken).ConfigureAwait(false);
        if (current is null) return null;
        await AuthorizeAsync(current.Scope, cancellationToken).ConfigureAwait(false);
        return current;
    }

    /// <summary>Resolves one previously recorded denial to an existing exact pending approval.
    /// The first binding is durable and cannot be changed to another approval ID. The caller
    /// must still authenticate a human decision using CodexApprovalStore; this operation grants
    /// nothing. The supplied denial event is checked against the independently stored digest.</summary>
    public async Task<CodexGuardianEscalation?> BindDeniedEscalationAsync(string reviewId, Guid threadId,
        JsonElement denialEvent, Guid pendingApprovalId, CancellationToken cancellationToken = default)
    {
        if (threadId == Guid.Empty || pendingApprovalId == Guid.Empty) throw Invalid("Invalid escalation identity.");
        ValidateObject(denialEvent);
        var digest = Fingerprint(denialEvent);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await ReadAsync(reviewId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Status != "denied" || current.DenialEventSha256 != digest ||
                !Guid.TryParseExact(current.Scope.ThreadId, "D", out var recordedThread) || recordedThread != threadId ||
                (current.EscalationApprovalId is { } bound && bound != pendingApprovalId))
                return null;
            var pending = await approvals.ReadAsync(pendingApprovalId, cancellationToken).ConfigureAwait(false);
            if (pending is null || pending.AccountId != accountId || pending.Action != current.Scope.Action ||
                pending.State != CodexApprovalState.Pending || pending.RpcBinding is not null)
                return null;
            if (current.EscalationApprovalId is null)
            {
                var next = current with { Revision = checked(current.Revision + 1), EscalationApprovalId = pendingApprovalId };
                cancellationToken.ThrowIfCancellationRequested();
                if (!await storage.CompareExchangeAsync(accountId, reviewId, current.Revision, next, cancellationToken).ConfigureAwait(false))
                    continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(digest, pendingApprovalId, pending.Action, pending.Revision);
        }
        throw Conflict();
    }

    private async Task<CodexGuardianReviewRecord> ConfirmAsync(CodexGuardianReviewScope scope, bool terminal,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await ReadCoreAsync(scope.ReviewId, ct).ConfigureAwait(false) ?? throw Conflict();
            if (current.Scope != scope || (terminal && (current.Completed is null || !current.StartedConfirmed)))
                throw Conflict();
            if (terminal ? current.CompletedConfirmed : current.StartedConfirmed) return current;
            var next = terminal
                ? current with { Revision = checked(current.Revision + 1), CompletedConfirmed = true }
                : current with { Revision = checked(current.Revision + 1), StartedConfirmed = true };
            ct.ThrowIfCancellationRequested();
            if (await storage.CompareExchangeAsync(accountId, scope.ReviewId, current.Revision, next, ct).ConfigureAwait(false))
                return next;
        }
        throw Conflict();
    }

    private async Task<CodexGuardianReviewRecord?> ReadCoreAsync(string reviewId, CancellationToken ct)
    {
        ValidateIdentity(reviewId);
        ct.ThrowIfCancellationRequested();
        var record = await storage.ReadAsync(accountId, reviewId, ct).ConfigureAwait(false);
        if (record is null) return null;
        if (record.Scope.AccountId != accountId || record.Scope.ConnectionId != connectionId ||
            record.Scope.ReviewId != reviewId || record.Revision < 1 ||
            Scope(record.Started, record.Scope.Action, terminal: false, out _) != record.Scope ||
            (record.CompletedConfirmed && (!record.StartedConfirmed || record.Completed is null)) ||
            (record.DenialEventSha256 is not null && (record.Status != "denied" || !Digest(record.DenialEventSha256))) ||
            (record.EscalationApprovalId is { } escalation && (escalation == Guid.Empty || record.DenialEventSha256 is null)))
            throw new InvalidDataException("Invalid Guardian review history.");
        if (record.Completed is { } completed)
        {
            if (Scope(completed, record.Scope.Action, terminal: true, out var status) != record.Scope ||
                status != record.Status || Number(record.Started, "startedAtMs") != Number(completed, "startedAtMs") ||
                !Exact(record.Started.GetProperty("action"), completed.GetProperty("action")))
                throw new InvalidDataException("Invalid terminal Guardian history.");
        }
        else if (record.Status != "inProgress" || record.DenialEventSha256 is not null)
            throw new InvalidDataException("Invalid pending Guardian history.");
        return record with { Started = record.Started.Clone(), Completed = record.Completed?.Clone() };
    }

    private async ValueTask AuthorizeAsync(CodexGuardianReviewScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await authorize(scope, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private CodexGuardianReviewScope Scope(JsonElement payload, CodexApprovalAction action, bool terminal, out string status)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidateObject(payload);
        var reviewId = Identity(payload, "reviewId");
        var threadId = Identity(payload, "threadId");
        var turnId = Identity(payload, "turnId");
        var itemId = OptionalText(payload, "targetItemId", 512);
        if (itemId is not null) ValidateIdentity(itemId);
        if (action.ConnectionId != connectionId || action.ThreadId != threadId || action.TurnId != turnId ||
            !Digest(action.ActionSha256))
            throw Invalid("Guardian action owner or identity mismatch.");
        ValidateIdentity(action.ActionId);
        var wireAction = Object(payload, "action");
        var type = Identity(wireAction, "type");
        var kind = ValidateAction(wireAction, type);
        if (action.Kind != kind || (type == "networkAccess" ? itemId is not null : itemId != action.ItemId))
            throw Invalid("Guardian action kind or item mismatch.");

        var started = Number(payload, "startedAtMs");
        var review = Object(payload, "review");
        status = Identity(review, "status");
        if (terminal)
        {
            if (status is not ("approved" or "denied" or "timedOut" or "aborted") ||
                Number(payload, "completedAtMs") < started || Identity(payload, "decisionSource") != "agent")
                throw Invalid("Invalid terminal Guardian decision.");
        }
        else if (status != "inProgress" || payload.TryGetProperty("completedAtMs", out _) ||
            payload.TryGetProperty("decisionSource", out _))
            throw Invalid("Invalid started Guardian review.");
        var risk = OptionalText(review, "riskLevel", 32);
        var authorization = OptionalText(review, "userAuthorization", 32);
        if (risk is not (null or "low" or "medium" or "high" or "critical") ||
            authorization is not (null or "unknown" or "low" or "medium" or "high"))
            throw Invalid("Unknown Guardian classification.");
        _ = OptionalText(review, "rationale", 131072);
        return new(accountId, connectionId, reviewId, threadId, turnId, itemId, action);
    }

    private static void ValidateDenialEvidence(CodexGuardianReviewScope scope, JsonElement completed, JsonElement evidence)
    {
        ValidateObject(evidence);
        if (Identity(evidence, "id") != scope.ReviewId || Identity(evidence, "turn_id") != scope.TurnId ||
            OptionalText(evidence, "target_item_id", 512) != scope.TargetItemId ||
            Number(evidence, "started_at_ms") != Number(completed, "startedAtMs") ||
            Number(evidence, "completed_at_ms") != Number(completed, "completedAtMs") ||
            Identity(evidence, "status") != "denied" || Identity(evidence, "decision_source") != "agent")
            throw Invalid("Recorded denial evidence does not match the terminal review.");
        var raw = Object(evidence, "action");
        var wire = completed.GetProperty("action");
        var expected = Identity(wire, "type") switch
        {
            "writeStdin" => "write_stdin",
            "applyPatch" => "apply_patch",
            "networkAccess" => "network_access",
            "mcpToolCall" => "mcp_tool_call",
            "requestPermissions" => "request_permissions",
            var type => type,
        };
        if (Identity(raw, "type") != expected) throw Invalid("Recorded denial action type mismatch.");

        // Check the identity-bearing fields shared by core and App Server. PathUri and
        // permission materialization remain the trusted adapter's responsibility.
        string[] fields = expected switch
        {
            "command" => ["command", "cwd"],
            "execve" => ["program", "argv", "cwd"],
            "write_stdin" => ["stdin"],
            "apply_patch" => ["cwd", "files"],
            "network_access" => ["target", "host", "port"],
            "mcp_tool_call" => ["server"],
            _ => [],
        };
        foreach (var field in fields)
            if (!raw.TryGetProperty(field, out var rawValue) || !wire.TryGetProperty(field, out var wireValue) ||
                !Exact(rawValue, wireValue))
                throw Invalid("Recorded denial action payload mismatch.");
        if (expected is "command" or "execve")
        {
            var source = Identity(wire, "source") == "unifiedExec" ? "unified_exec" : "shell";
            if (Identity(raw, "source") != source) throw Invalid("Recorded denial command source mismatch.");
        }
        if (expected == "write_stdin" &&
            (Identity(raw, "approval_id") != Identity(wire, "approvalId") ||
                Identity(raw, "process_id") != Identity(wire, "processId")))
            throw Invalid("Recorded denial terminal identity mismatch.");
        if (expected == "mcp_tool_call" && Identity(raw, "tool_name") != Identity(wire, "toolName"))
            throw Invalid("Recorded denial tool identity mismatch.");
    }

    private static CodexApprovalKind ValidateAction(JsonElement action, string type)
    {
        switch (type)
        {
            case "command":
                Source(action);
                _ = Text(action, "command", 131072);
                _ = Identity(action, "cwd", 4096);
                return CodexApprovalKind.Command;
            case "execve":
                Source(action);
                _ = Identity(action, "program", 4096);
                _ = Identity(action, "cwd", 4096);
                Strings(action, "argv");
                return CodexApprovalKind.Command;
            case "writeStdin":
                _ = Identity(action, "approvalId");
                _ = Identity(action, "processId");
                _ = Identity(action, "cwd", 4096);
                _ = Text(action, "stdin", 131072);
                return CodexApprovalKind.Command;
            case "applyPatch":
                _ = Identity(action, "cwd", 4096);
                Strings(action, "files");
                return CodexApprovalKind.FileChange;
            case "networkAccess":
                _ = Identity(action, "host", 4096);
                _ = Identity(action, "target", 4096);
                if (Identity(action, "protocol") is not ("http" or "https" or "socks5Tcp" or "socks5Udp") ||
                    Number(action, "port") > ushort.MaxValue)
                    throw Invalid("Unknown Guardian network action.");
                return CodexApprovalKind.Permissions;
            case "mcpToolCall":
                _ = Identity(action, "server");
                _ = Identity(action, "toolName");
                _ = OptionalText(action, "connectorId", 4096);
                _ = OptionalText(action, "connectorName", 4096);
                _ = OptionalText(action, "toolTitle", 4096);
                return CodexApprovalKind.Tool;
            case "requestPermissions":
                _ = Object(action, "permissions");
                _ = OptionalText(action, "reason", 131072);
                return CodexApprovalKind.Permissions;
            default: throw Invalid("Unknown Guardian action type.");
        }
    }

    private static void Source(JsonElement action)
    {
        if (Identity(action, "source") is not ("shell" or "unifiedExec"))
            throw Invalid("Unknown Guardian command source.");
    }

    private static void Strings(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array ||
            field.GetArrayLength() > 4096 || field.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String ||
                x.GetString() is not { Length: <= 131072 } text || text.Contains('\0')))
            throw Invalid("Invalid Guardian action array.");
    }

    private static CodexGuardianReviewMutation SameStart(CodexGuardianReviewRecord record, CodexGuardianReviewScope scope,
        JsonElement payload) => record.Scope == scope && Exact(record.Started, payload)
            ? new(record, false, record.StartedConfirmed) : throw Conflict();

    private static void ValidateObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumPayloadBytes)
            throw Invalid("Invalid or oversized Guardian payload.");
        CodexCommandExecution.EnsureUniqueFields(value);
    }

    private static JsonElement Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object
            ? field : throw Invalid("Missing Guardian object.");

    private static long Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
        field.TryGetInt64(out var number) && number >= 0 ? number : throw Invalid("Invalid Guardian numeric field.");

    private static string Text(JsonElement value, string name, int max) =>
        OptionalText(value, name, max) ?? throw Invalid("Missing Guardian text.");

    private static string Identity(JsonElement value, string name, int max = 512)
    {
        var text = Text(value, name, max);
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
            throw Invalid("Invalid Guardian identity.");
        return text;
    }

    private static string? OptionalText(JsonElement value, string name, int max)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text ||
            text.Length > max || text.Contains('\0'))
            throw Invalid("Invalid Guardian text.");
        return text;
    }

    private static void ValidateIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw Invalid("Invalid Guardian identity.");
    }

    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Fingerprint(JsonElement value) =>
        CodexApprovalAction.Fingerprint(Encoding.UTF8.GetBytes(value.GetRawText()));
    private static bool Exact(JsonElement a, JsonElement b) => JsonElement.DeepEquals(a, b);
    private static InvalidOperationException Conflict() => new("Guardian review is missing, stale or mismatched.");
    private static CodexThreadRpcException Invalid(string message) => new(-32600, message);
}

/// <summary>Source-generated persistence metadata for durable Guardian review records.
/// The host must encrypt and authorize these potentially sensitive payloads.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodexGuardianReviewRecord))]
public sealed partial class CodexGuardianReviewJson : JsonSerializerContext;
