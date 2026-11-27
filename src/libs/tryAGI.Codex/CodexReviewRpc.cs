using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Validated native review target. The host must use the corresponding repository
/// operation without interpreting branch or commit names as shell commands.</summary>
/// <param name="Type">Pinned target discriminator.</param>
/// <param name="Branch">Base branch for a branch review.</param>
/// <param name="Sha">Commit identity for a commit review.</param>
/// <param name="Title">Optional display title.</param>
/// <param name="Instructions">Custom review instructions.</param>
public sealed record CodexReviewTarget(string Type, string? Branch = null, string? Sha = null,
    string? Title = null, string? Instructions = null)
{
    /// <inheritdoc />
    public override string ToString() => $"Review target {Type}";
}

/// <summary>Authenticated operation context, supplied by the host, not by model arguments.
/// OperationId is retained by the transport before dispatch and must not be reused for a
/// different action. Parameters are sensitive and must not be logged.</summary>
/// <param name="AccountId">Authenticated account.</param>
/// <param name="ConnectionId">Authenticated connection.</param>
/// <param name="ActorId">Authenticated requesting actor.</param>
/// <param name="OperationId">Stable host-generated operation identity.</param>
/// <param name="ThreadId">Native thread identity.</param>
/// <param name="Method">RPC method.</param>
/// <param name="Parameters">Exact validated request payload.</param>
public sealed record CodexReviewContext(string AccountId, string ConnectionId, string ActorId,
    Guid OperationId, Guid ThreadId, string Method, JsonElement Parameters)
{
    /// <inheritdoc />
    public override string ToString() => $"Review operation {OperationId} ({Method})";
}

/// <summary>Trusted result of resolving a recorded Guardian denial. A new pending approval must
/// already be bound to the exact action; a terminal denied approval cannot be resurrected.</summary>
/// <param name="EventSha256">Digest of the original recorded denial event.</param>
/// <param name="ApprovalId">Existing pending escalation approval.</param>
/// <param name="Action">Exact host-resolved action, including owner and execution attempt.</param>
/// <param name="ExpectedRevision">Pending approval revision.</param>
public sealed record CodexGuardianEscalation(string EventSha256, Guid ApprovalId,
    CodexApprovalAction Action, long ExpectedRevision);

/// <summary>Explicit native review and Guardian host integration. The adapter must use the
/// existing authorized turn runner and thread journal, preserve operation identity across
/// retries, and honor cancellation. No default reviewer, shell execution, paid-provider
/// fallback, remembered grant or confidential Guardian implementation is supplied by the SDK.</summary>
public interface ICodexReviewAdapter
{
    /// <summary>Starts an inline review through the native turn runner, committing its turn
    /// with the supplied operation ID before returning the App Server turn projection.
    /// A repeated or uncertain operation must not dispatch another review.</summary>
    Task<JsonElement> StartReviewAsync(CodexReviewContext context, long expectedThreadRevision,
        CodexReviewTarget target, CancellationToken cancellationToken);

    /// <summary>Obtains an assessment for the current thread, correlated to the operation ID.
    /// Returns the pinned raw ThreadGuardianReviewResponse. Assessment is advisory and never a grant.</summary>
    Task<JsonElement> ReviewGuardianAsync(CodexReviewContext context, long expectedThreadRevision,
        CancellationToken cancellationToken);

    /// <summary>Finds a previously recorded Guardian denial in the authorized thread journal.
    /// Resolve independently from the stored event, not from caller-supplied action data;
    /// return null when missing, changed, not denied or not eligible for escalation.</summary>
    Task<CodexGuardianEscalation?> ResolveDeniedActionAsync(CodexReviewContext context,
        CancellationToken cancellationToken);
}

/// <summary>Native review/start, thread/guardianReview and thread/approveGuardianDeniedAction
/// host. Review policy and the explicit approval store remain separate trust boundaries.
/// This host never treats an assessment, raw event, or transport response as execution permission.</summary>
public sealed class CodexReviewRpc
{
    private const string Review = "review/start";
    private const string Guardian = "thread/guardianReview";
    private const string Escalate = "thread/approveGuardianDeniedAction";
    private const int MaximumPayloadBytes = 262144;
    private readonly CodexThreadStore threads;
    private readonly CodexApprovalStore approvals;
    private readonly string connectionId;
    private readonly ICodexReviewAdapter adapter;
    private readonly Func<CodexReviewContext, CancellationToken, ValueTask> authorize;

    /// <summary>Creates an authenticated review host with an explicit authorization callback,
    /// existing native thread and approval stores, and an explicitly trusted adapter.</summary>
    public CodexReviewRpc(CodexThreadStore threads, CodexApprovalStore approvals, string connectionId,
        ICodexReviewAdapter adapter, Func<CodexReviewContext, CancellationToken, ValueTask> authorize)
    {
        this.threads = threads ?? throw new ArgumentNullException(nameof(threads));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        if (!ValidIdentity(connectionId)) throw new ArgumentException("Invalid connection identity.", nameof(connectionId));
        this.connectionId = connectionId;
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
    }

    /// <summary>Native review RPCs served by this configured host.</summary>
    public static bool Handles(string method) => method is Review or Guardian or Escalate;

    /// <summary>Serves a native review request. The transport must retain its authenticated actor
    /// and stable operation identity before calling; neither may come from request parameters.
    /// A lost response or cancellation never authorizes an automatic retry or replay.</summary>
    public async Task<JsonElement> HandleAsync(string method, JsonElement parameters, Guid operationId,
        string actorId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported review method.");
        if (operationId == Guid.Empty || !ValidIdentity(actorId)) throw Invalid("Invalid authenticated review identity.");
        ValidateObject(parameters);
        var threadText = RequiredText(parameters, "threadId", 64);
        if (!Guid.TryParseExact(threadText, "D", out var threadId) || threadId == Guid.Empty)
            throw Invalid("Invalid native thread ID.");

        CodexReviewTarget? target = null;
        JsonElement deniedEvent = default;
        if (method == Review)
        {
            Fields(parameters, "threadId", "target", "delivery");
            if (OptionalText(parameters, "delivery", 32) is { } delivery && delivery != "inline")
                throw Invalid("Only inline review delivery is supported.");
            if (!parameters.TryGetProperty("target", out var rawTarget)) throw Invalid("Missing review target.");
            target = Target(rawTarget);
        }
        else if (method == Guardian) Fields(parameters, "threadId");
        else
        {
            Fields(parameters, "threadId", "event");
            if (!parameters.TryGetProperty("event", out deniedEvent)) throw Invalid("Missing Guardian event.");
            ValidateObject(deniedEvent);
        }

        var context = new CodexReviewContext(approvals.AccountId, connectionId, actorId,
            operationId, threadId, method, parameters.Clone());
        await authorize(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var before = await threads.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CheckAccount(before);

        if (method == Review)
        {
            // A committed operation ID is a fence, not authority to start a second review.
            if (before.Turns.Any(turn => turn.Id == operationId))
                throw new CodexThreadRpcException(-32010, "Review operation already has a journal turn; reconcile it without replay.");
            var projected = await adapter.StartReviewAsync(context, before.Revision, target!, cancellationToken).ConfigureAwait(false);
            ValidateObject(projected);
            if (!Guid.TryParseExact(RequiredText(projected, "id", 64), "D", out var projectedId) || projectedId != operationId ||
                !projected.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                !ValidTurnStatus(RequiredText(projected, "status", 64)))
                throw new InvalidDataException("Review adapter returned an invalid or mismatched turn projection.");
            var after = await threads.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
            CheckAccount(after);
            if (after.Revision <= before.Revision || !after.Turns.Any(turn => turn.Id == operationId))
                throw new InvalidDataException("Review adapter did not commit the correlated native turn.");
            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(new ReviewStartResult(projected.Clone(), threadText),
                CodexReviewRpcJson.Default.ReviewStartResult);
        }
        if (method == Guardian)
        {
            var assessment = await adapter.ReviewGuardianAsync(context, before.Revision, cancellationToken).ConfigureAwait(false);
            ValidateGuardian(assessment, operationId);
            cancellationToken.ThrowIfCancellationRequested();
            return assessment.Clone();
        }

        var digest = CodexApprovalAction.Fingerprint(Encoding.UTF8.GetBytes(deniedEvent.GetRawText()));
        var escalation = await adapter.ResolveDeniedActionAsync(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (escalation is null || escalation.EventSha256 != digest ||
            escalation.Action.ConnectionId != connectionId ||
            !Guid.TryParseExact(escalation.Action.ThreadId, "D", out var actionThread) || actionThread != threadId)
            throw new CodexThreadRpcException(-32000, "Guardian denial is missing, mismatched or not eligible for escalation.");
        var current = await approvals.ReadAsync(escalation.ApprovalId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Action != escalation.Action || current.State != CodexApprovalState.Pending ||
            current.Revision != escalation.ExpectedRevision || current.RpcBinding is not null)
            throw new CodexThreadRpcException(-32000, "Guardian escalation is not an exact independent pending approval.");
        // A user override is an ordinary authenticated decision on a new exact action.
        // It never updates a persistent policy, dispatches the action or revives a denied record.
        var decision = await approvals.DecideAsync(current.Id, current.Action, current.Revision,
            true, actorId, cancellationToken).ConfigureAwait(false);
        if (decision.State != CodexApprovalState.Approved)
            throw new CodexThreadRpcException(-32000, "Guardian escalation expired or was denied.");
        return JsonSerializer.SerializeToElement(new CommandEmpty(), CodexCommandJson.Default.CommandEmpty);
    }

    private void CheckAccount(CodexThreadDocument thread)
    {
        if (thread.AccountId != approvals.AccountId) throw new UnauthorizedAccessException("Thread account mismatch.");
    }

    private static CodexReviewTarget Target(JsonElement value)
    {
        ValidateObject(value);
        var type = RequiredText(value, "type", 64);
        switch (type)
        {
            case "uncommittedChanges":
                Fields(value, "type");
                return new(type);
            case "baseBranch":
                Fields(value, "type", "branch");
                return new(type, Branch: RequiredText(value, "branch", 4096));
            case "commit":
                Fields(value, "type", "sha", "title");
                return new(type, Sha: RequiredText(value, "sha", 4096), Title: OptionalText(value, "title", 4096));
            case "custom":
                Fields(value, "type", "instructions");
                return new(type, Instructions: RequiredText(value, "instructions", 131072));
            default: throw Invalid("Unknown review target.");
        }
    }

    private static void ValidateGuardian(JsonElement value, Guid operationId)
    {
        ValidateObject(value);
        if (!Guid.TryParseExact(RequiredText(value, "callId", 64), "D", out var id) || id != operationId)
            throw new InvalidDataException("Guardian adapter returned a mismatched call ID.");
        if (!value.TryGetProperty("assessment", out var assessment) || assessment.ValueKind == JsonValueKind.Null) return;
        ValidateObject(assessment);
        if (RequiredText(assessment, "outcome", 32) is not ("allow" or "deny") ||
            RequiredText(assessment, "riskLevel", 32) is not ("low" or "medium" or "high" or "critical") ||
            RequiredText(assessment, "userAuthorization", 32) is not ("unknown" or "low" or "medium" or "high"))
            throw new InvalidDataException("Guardian adapter returned an unknown assessment classification.");
        _ = RequiredText(assessment, "rationale", 131072);
    }

    private static bool ValidTurnStatus(string status) => status is "inProgress" or "completed" or "interrupted" or "failed";

    private static void ValidateObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumPayloadBytes)
            throw Invalid("Invalid or oversized review object.");
        CodexCommandExecution.EnsureUniqueFields(value);
    }

    private static void Fields(JsonElement value, params string[] allowed)
    {
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw Invalid("Unsupported review parameter.");
    }

    private static string RequiredText(JsonElement value, string name, int max) =>
        OptionalText(value, name, max) is { } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw Invalid("Missing or empty review text.");

    private static string? OptionalText(JsonElement value, string name, int max)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text ||
            text.Length > max || text.Contains('\0'))
            throw Invalid("Invalid review text.");
        return text;
    }

    private static bool ValidIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);

    private static CodexThreadRpcException Invalid(string message) => new(-32600, message);
}

internal sealed record ReviewStartResult(JsonElement Turn, string ReviewThreadId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReviewStartResult))]
internal sealed partial class CodexReviewRpcJson : JsonSerializerContext;
