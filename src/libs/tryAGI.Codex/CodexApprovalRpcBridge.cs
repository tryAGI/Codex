using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Durable binding of an approval to the exact JSON-RPC server request.
/// Raw parameters may contain secrets; only their digest is persisted here.</summary>
/// <param name="Method">Pinned approval method.</param>
/// <param name="RequestId">Exact transport identity, including numeric versus string type.</param>
/// <param name="ParametersSha256">Digest of the original parameters.</param>
/// <param name="AllowsAccept">Whether an exact one-action acceptance was offered.</param>
/// <param name="AllowsDecline">Whether decline was offered.</param>
/// <param name="AllowsCancel">Whether cancel was offered.</param>
public sealed record CodexApprovalRpcBinding(string Method, CodexToolRequestId RequestId,
    string ParametersSha256, bool AllowsAccept, bool AllowsDecline, bool AllowsCancel);

/// <summary>Durable registration result. Only Created authorizes initial request publication.
/// Reattachment never re-publishes or re-executes an uncertain effect.</summary>
/// <param name="Record">The stored approval request.</param>
/// <param name="Created">Whether this caller created the request.</param>
public sealed record CodexApprovalRpcAdmission(CodexApprovalRecord Record, bool Created);

/// <summary>Result of an exact correlated approval response.</summary>
public enum CodexApprovalRpcDisposition
{
    /// <summary>A decision was durably recorded; this is not dispatch permission.</summary>
    Applied,
    /// <summary>The request was already terminal or decided; no new decision was applied.</summary>
    AlreadyDecided,
    /// <summary>A recognized broader grant needs a separate trusted policy implementation.</summary>
    Unsupported,
}

/// <summary>Outcome of processing a server-request response. The response is retained as raw JSON
/// for the host, and must not be logged or treated as a grant when unsupported.</summary>
/// <param name="Disposition">Whether a new decision was committed.</param>
/// <param name="Record">Current approval record.</param>
/// <param name="Response">Exact response payload for the embedding host.</param>
public sealed record CodexApprovalRpcResult(CodexApprovalRpcDisposition Disposition,
    CodexApprovalRecord Record, JsonElement Response)
{
    /// <inheritdoc />
    public override string ToString() => $"Approval RPC {Disposition}, state {Record.State}";
}

/// <summary>Connection-scoped correlation for native approval server requests. The trusted host
/// supplies the resolved action and authenticates the reviewer. Numeric and string JSON-RPC IDs
/// never alias. The bridge only records decisions; the actual execution adapter must independently
/// consume the matching approval and enforce the current policy. There is no implicit session grant.</summary>
public sealed class CodexApprovalRpcBridge
{
    private const string Command = "item/commandExecution/requestApproval";
    private const string FileChange = "item/fileChange/requestApproval";
    private const string Permissions = "item/permissions/requestApproval";
    private const int MaximumPayloadBytes = 262144;
    private readonly CodexApprovalStore store;
    private readonly string connectionId;

    /// <summary>Creates a bridge for one authenticated connection and account-bound durable store.</summary>
    public CodexApprovalRpcBridge(CodexApprovalStore store, string connectionId)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        if (!ValidIdentity(connectionId)) throw new ArgumentException("Invalid connection identity.", nameof(connectionId));
        this.connectionId = connectionId;
    }

    /// <summary>Returns whether the method is one of the implemented v2 approval families.</summary>
    public static bool Handles(string method) => method is Command or FileChange or Permissions;

    /// <summary>Atomically registers exact transport and action identities before publication.
    /// The action must be independently resolved by the trusted host, not inferred from display
    /// fields alone. Reused IDs cannot be rebound to a changed payload, method, action or expiry.</summary>
    public Task<CodexApprovalRpcAdmission> AdmitAsync(JsonElement requestId, string method,
        JsonElement parameters, CodexApprovalAction action, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(action);
        var kind = Kind(method);
        if (action.ConnectionId != connectionId || action.Kind != kind)
            throw Invalid("Approval action does not match its authenticated connection or method.");
        var id = ParseId(requestId);
        ValidatePayload(parameters);
        if (RequiredIdentity(parameters, "threadId") != action.ThreadId ||
            RequiredIdentity(parameters, "turnId") != action.TurnId ||
            RequiredIdentity(parameters, "itemId") != action.ItemId)
            throw Invalid("Approval action does not match the request's thread, turn or item.");
        if (!parameters.TryGetProperty("startedAtMs", out var started) ||
            started.ValueKind != JsonValueKind.Number || !started.TryGetInt64(out var startedAt) || startedAt < 0)
            throw Invalid("Invalid approval start timestamp.");
        if (method != FileChange && OptionalIdentity(parameters, "environmentId") != action.EnvironmentId)
            throw Invalid("Approval environment mismatch.");

        var accept = method != Permissions;
        var decline = true;
        var cancel = method != Permissions;
        if (method == Command)
        {
            var commandKind = OptionalIdentity(parameters, "kind") ?? "command";
            if (commandKind is not ("command" or "humanCommand" or "writeStdin"))
                throw Invalid("Unsupported command approval kind.");
            _ = OptionalIdentity(parameters, "approvalId");
            if (parameters.TryGetProperty("availableDecisions", out var choices) && choices.ValueKind != JsonValueKind.Null)
            {
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 32)
                    throw Invalid("Invalid approval choices.");
                accept = decline = cancel = false;
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.String) continue;
                    switch (choice.GetString())
                    {
                        case "accept": accept = true; break;
                        case "decline": decline = true; break;
                        case "cancel": cancel = true; break;
                    }
                }
            }
        }
        if (method == Permissions && (!parameters.TryGetProperty("permissions", out var requested) ||
            requested.ValueKind != JsonValueKind.Object))
            throw Invalid("Missing permission request profile.");

        var binding = new CodexApprovalRpcBinding(method, id,
            CodexApprovalAction.Fingerprint(Encoding.UTF8.GetBytes(parameters.GetRawText())), accept, decline, cancel);
        return store.AdmitAsync(DurableId(id), action, expiresAt, binding, cancellationToken);
    }

    /// <summary>Records a response for the exact registered request. The actor must be authenticated
    /// by the host; the store authorizes each actual mutation. Session-wide approvals, policy
    /// amendments and nonempty permission grants remain unsupported rather than becoming one-action
    /// acceptance. A duplicate response cannot reapply or revive a decision.</summary>
    public async Task<CodexApprovalRpcResult> RespondAsync(JsonElement requestId, string method,
        CodexApprovalAction expectedAction, long expectedRevision, string actorId,
        JsonElement response, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePayload(response);
        var record = await MatchAsync(requestId, method, expectedAction, cancellationToken).ConfigureAwait(false);
        var binding = record.RpcBinding!;
        if (record.State != CodexApprovalState.Pending)
            return new(CodexApprovalRpcDisposition.AlreadyDecided, record, response.Clone());
        if (record.Revision != expectedRevision) throw Invalid("Stale approval request revision.");

        var decision = Classify(method, response, binding);
        if (decision is null)
            return new(CodexApprovalRpcDisposition.Unsupported, record, response.Clone());
        CodexApprovalRecord changed;
        try
        {
            changed = decision == CodexApprovalState.Cancelled
                ? await store.CancelAsync(record.Id, expectedAction, expectedRevision, actorId, cancellationToken).ConfigureAwait(false)
                : await store.DecideAsync(record.Id, expectedAction, expectedRevision,
                    decision == CodexApprovalState.Approved, actorId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A concurrent decision is terminal; never retry the mutation. An ambiguous storage
            // exception is not interpreted as fresh authorization.
            var current = await MatchAsync(requestId, method, expectedAction, cancellationToken).ConfigureAwait(false);
            if (current.State == CodexApprovalState.Pending) throw;
            return new(CodexApprovalRpcDisposition.AlreadyDecided, current, response.Clone());
        }
        return new(CodexApprovalRpcDisposition.Applied, changed, response.Clone());
    }

    /// <summary>Cancels a pending or approved correlated request on disconnect or explicit
    /// cancellation. The trusted host supplies the actor and durable revision; the store
    /// independently authorizes cancellation. Consumed actions need separate execution cancellation.</summary>
    public async Task<CodexApprovalRecord> CancelAsync(JsonElement requestId, string method,
        CodexApprovalAction expectedAction, long expectedRevision, string actorId,
        CancellationToken cancellationToken = default)
    {
        var record = await MatchAsync(requestId, method, expectedAction, cancellationToken).ConfigureAwait(false);
        return await store.CancelAsync(record.Id, expectedAction, expectedRevision, actorId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodexApprovalRecord> MatchAsync(JsonElement requestId, string method,
        CodexApprovalAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        var id = ParseId(requestId);
        if (action.ConnectionId != connectionId || action.Kind != Kind(method))
            throw Invalid("Approval correlation mismatch.");
        var record = await store.ReadAsync(DurableId(id), ct).ConfigureAwait(false);
        if (record is null || record.Action != action || record.RpcBinding is not { } binding ||
            binding.RequestId != id || binding.Method != method)
            throw Invalid("Unknown or mismatched approval request.");
        return record;
    }

    private static CodexApprovalState? Classify(string method, JsonElement response, CodexApprovalRpcBinding binding)
    {
        if (method == Permissions)
        {
            foreach (var property in response.EnumerateObject())
                if (property.Name is not ("permissions" or "scope" or "strictAutoReview"))
                    throw Invalid("Unknown permission response field.");
            if (!response.TryGetProperty("permissions", out var grants) || grants.ValueKind != JsonValueKind.Object)
                throw Invalid("Missing granted permissions.");
            var scope = OptionalIdentity(response, "scope") ?? "turn";
            if (scope is not ("turn" or "session")) throw Invalid("Unknown permission grant scope.");
            if (response.TryGetProperty("strictAutoReview", out var strict) &&
                strict.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw Invalid("Invalid strict review flag.");
            // The existing store cannot represent a broader or partial grant. Only an empty
            // turn grant with no strict-review change represents a denial.
            return !grants.EnumerateObject().Any() && scope == "turn" &&
                (!response.TryGetProperty("strictAutoReview", out strict) || strict.ValueKind is JsonValueKind.False or JsonValueKind.Null)
                ? CodexApprovalState.Denied : null;
        }

        if (response.EnumerateObject().Count() != 1 || !response.TryGetProperty("decision", out var decision))
            throw Invalid("Invalid approval decision response.");
        if (decision.ValueKind == JsonValueKind.String)
            return decision.GetString() switch
            {
                "accept" when binding.AllowsAccept => CodexApprovalState.Approved,
                "decline" when binding.AllowsDecline => CodexApprovalState.Denied,
                "cancel" when binding.AllowsCancel => CodexApprovalState.Cancelled,
                "acceptForSession" => null,
                _ => throw Invalid("Unknown or unavailable approval decision."),
            };
        if (method == Command && decision.ValueKind == JsonValueKind.Object &&
            decision.EnumerateObject().Count() == 1 &&
            (decision.TryGetProperty("acceptWithExecpolicyAmendment", out var amendment) ||
                decision.TryGetProperty("applyNetworkPolicyAmendment", out amendment)) &&
            amendment.ValueKind == JsonValueKind.Object)
            return null;
        throw Invalid("Unknown or unsupported approval decision shape.");
    }

    private Guid DurableId(CodexToolRequestId id)
    {
        // The account/connection and exact ID type define the transport namespace. Length
        // prefixes prevent concatenation ambiguity, and the method is deliberately not part
        // of the key: reusing a transport ID for a different method must conflict.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("codex-approval-rpc-v1");
            writer.Write(store.AccountId);
            writer.Write(connectionId);
            writer.Write(id.IsNumeric);
            writer.Write(id.Value);
        }
        return new Guid(SHA256.HashData(stream.ToArray()).AsSpan(0, 16));
    }

    private static CodexToolRequestId ParseId(JsonElement id)
    {
        CodexToolRequestId parsed;
        try { parsed = CodexToolRequestId.Parse(id); }
        catch (CodexProtocolException) { throw Invalid("Invalid approval request ID."); }
        if (!ValidIdentity(parsed.Value)) throw Invalid("Invalid approval request ID.");
        return parsed;
    }

    private static CodexApprovalKind Kind(string method) => method switch
    {
        Command => CodexApprovalKind.Command,
        FileChange => CodexApprovalKind.FileChange,
        Permissions => CodexApprovalKind.Permissions,
        _ => throw new CodexThreadRpcException(-32601, "Unsupported approval method."),
    };

    private static void ValidatePayload(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumPayloadBytes)
            throw Invalid("Invalid or oversized approval payload.");
        CodexCommandExecution.EnsureUniqueFields(value);
    }

    private static string RequiredIdentity(JsonElement value, string property) =>
        OptionalIdentity(value, property) ?? throw Invalid("Missing approval identity.");

    private static string? OptionalIdentity(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String || !ValidIdentity(field.GetString()))
            throw Invalid("Invalid approval identity.");
        return field.GetString();
    }

    private static bool ValidIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);

    private static CodexThreadRpcException Invalid(string message) => new(-32600, message);
}
