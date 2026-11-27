using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>A durable realtime input decision scoped to a logical session, provider handoff,
/// attachment, exact contextual text digest, and receiving reasoning turn.</summary>
public sealed record CodexStoredRealtimeHandoffAdmission(
    [property: JsonRequired] string HandoffId,
    [property: JsonRequired] Guid SubmissionId,
    [property: JsonRequired] Guid AttachmentId,
    [property: JsonRequired] string InputSha256,
    [property: JsonRequired] string State,
    Guid? TurnId = null, int? InputIndex = null);

/// <summary>New admission is distinct from observing a previously committed receipt.</summary>
public sealed record CodexRealtimeHandoffAdmission(bool Admitted, CodexThreadDocument Thread,
    CodexStoredRealtimeHandoffAdmission Receipt);

internal sealed record CodexRealtimeHandoffRequest(string SessionId, string OwnerId, Guid AttachmentId,
    string HandoffId, Guid SubmissionId, string ContextualInput);

public sealed partial class CodexThreadStore
{
    /// <summary>Read the canonical contextual user input from the exact persisted capture.
    /// This operation is pure and never starts or steers a reasoning turn.</summary>
    public static CodexRealtimeDelegationInput? PrepareRealtimeHandoffInput(CodexStoredRealtimeSession session, string handoffId)
    {
        ArgumentNullException.ThrowIfNull(session);
        Token(handoffId);
        var history = session.History ?? throw new InvalidOperationException("Handoff has no durable transcript history.");
        var capture = history.Handoffs.SingleOrDefault(h => h.HandoffId == handoffId)
            ?? throw new InvalidOperationException("Unknown persisted realtime handoff.");
        var frames = history.Frames.ToDictionary(f => f.Id, StringComparer.Ordinal);
        return CodexRealtimeDelegationInput.FromEntries(capture.InputTranscript,
            capture.FrameIds.Select(id =>
            {
                var frame = frames[id];
                return new CodexRealtimeDelegationEntry(frame.Id, frame.Role, frame.Text);
            }).ToArray(), preserveTranscript: history.Mode == CodexRealtimeHistoryMode.Canonical);
    }

    /// <summary>Atomically accept the exact observed handoff into a new or existing turn. A null
    /// expectedActiveTurnId requires an idle thread and uses submissionId as its new turn ID.
    /// A non-null expectedActiveTurnId fences steering to that exact live turn and owner.
    /// Only a newly returned receipt can be used by an owning runner; an ambiguous commit or
    /// repeated receipt never authorizes another dispatch.</summary>
    public Task<CodexRealtimeHandoffAdmission> AdmitRealtimeHandoffAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, string handoffId, Guid submissionId,
        Guid? expectedActiveTurnId, string turnOwnerId, string contextualInput, CancellationToken cancellationToken = default)
        => DecideRealtimeHandoffAsync(threadId, expectedRevision, sessionId, ownerId, attachmentId,
            handoffId, submissionId, contextualInput, expectedActiveTurnId, turnOwnerId, null, cancellationToken);

    /// <summary>Reject or retire an observed handoff without starting inference. Its transcript
    /// remains available for the history carryover path. A conflicting prior decision is refused.</summary>
    public Task<CodexRealtimeHandoffAdmission> RejectRealtimeHandoffAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, string handoffId, Guid submissionId,
        string contextualInput, bool retired = false, CancellationToken cancellationToken = default)
        => DecideRealtimeHandoffAsync(threadId, expectedRevision, sessionId, ownerId, attachmentId,
            handoffId, submissionId, contextualInput, null, null, retired ? "retired" : "rejected", cancellationToken);

    private async Task<CodexRealtimeHandoffAdmission> DecideRealtimeHandoffAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, string handoffId, Guid submissionId,
        string contextualInput, Guid? expectedActiveTurnId, string? turnOwnerId, string? rejection, CancellationToken ct)
    {
        Token(handoffId);
        Id(submissionId);
        ArgumentNullException.ThrowIfNull(contextualInput);
        if (Encoding.UTF8.GetByteCount(contextualInput) > MaximumDocumentBytes)
            throw new ArgumentException("Realtime handoff input exceeds the durable document bound.", nameof(contextualInput));
        var digest = HandoffDigest(contextualInput);
        var before = await CurrentAsync(threadId, expectedRevision, ct).ConfigureAwait(false);
        var session = FindRealtimeSession(before, sessionId, ownerId);
        RequireRealtimeAttachment(session, attachmentId);
        if (session.EndOperationId is not null) throw new CodexThreadConflictException();
        var receipts = session.HandoffAdmissions ?? [];
        if (receipts.FirstOrDefault(r => r.HandoffId == handoffId) is { } prior)
        {
            if (prior.SubmissionId != submissionId || prior.InputSha256 != digest ||
                (rejection is null ? prior.State is not ("start" or "steer") : prior.State != rejection))
                throw new InvalidOperationException("Realtime handoff identity is already bound to another input or decision.");
            return new(false, before, prior);
        }
        if (receipts.Length >= MaximumItems || before.RealtimeSessions!.Any(s =>
                (s.HandoffAdmissions ?? []).Any(r => r.SubmissionId == submissionId)) ||
            before.Turns.Any(t => t.Id == submissionId))
            throw new InvalidOperationException("Realtime submission identity or capacity conflict.");

        var canonical = PrepareRealtimeHandoffInput(session, handoffId);
        if (contextualInput != (canonical?.Text ?? "") || rejection is null && canonical is null)
            throw new InvalidOperationException("Realtime handoff input does not match the durable capture.");
        var next = before;
        Guid? receivingTurn = null;
        int? inputIndex = null;
        var state = rejection;
        if (rejection is null)
        {
            Token(turnOwnerId ?? throw new ArgumentNullException(nameof(turnOwnerId)));
            // A capture from an old transport can be retired by the current owner, not dispatched.
            var observation = session.History!.Events.FirstOrDefault(e => e.Kind == "observation" &&
                e.Payload is { } payload && e.EventType is { } type &&
                new CodexRealtimeEvent(type, payload, e.ReceivedAfterConnectMs ?? 0).Delegation?.Id == handoffId);
            if (observation?.AttachmentId != attachmentId)
                throw new InvalidOperationException("Realtime handoff belongs to a retired attachment.");
            var item = canonical!.ToResponseItem();
            if (expectedActiveTurnId is { } target)
            {
                var active = Active(before, target, turnOwnerId);
                inputIndex = active.Input.Length;
                var ids = active.ClientUserMessageIds ?? active.Input
                    .Where(x => CodexRealtimeProtocol.Text(x, "role") == "user").Select(_ => (string?)null).ToArray();
                next = Replace(before, active with { Input = [.. active.Input, item],
                    ClientUserMessageIds = [.. ids, submissionId.ToString()],
                    RealtimeSteeringInputIndices = [.. active.RealtimeSteeringInputIndices ?? [], inputIndex.Value] });
                receivingTurn = target;
                state = "steer";
            }
            else
            {
                if (before.Turns.LastOrDefault()?.Status == "inProgress")
                    throw new InvalidOperationException("A realtime start must not replace an active reasoning turn.");
                var turn = new CodexStoredTurn(submissionId, submissionId, turnOwnerId, "inProgress",
                    time.GetUtcNow(), [item], [], [], ClientUserMessageIds: [submissionId.ToString()], TurnTrigger: "realtime")
                { GoalLifecycle = CaptureTurnGoal(before) };
                next = BeginGoalExecutionClock(PrepareGoalTurn(before with { Turns = [.. before.Turns, turn] }, null, null), submissionId);
                receivingTurn = submissionId;
                inputIndex = 0;
                state = "start";
            }
        }

        // The input, transcript acceptance and receipt become visible in one CAS. No provider or
        // tool callback occurs here; a lost CAS reply must be reconciled by reading this receipt.
        if (receivingTurn is { } boundTurn) next = BindRealtimePresentation(next, boundTurn, sessionId);
        next = ResolveRealtimeHandoffHistory(next, sessionId, handoffId, receivingTurn);
        var updated = FindRealtimeSession(next, sessionId, ownerId);
        var receipt = new CodexStoredRealtimeHandoffAdmission(handoffId, submissionId, attachmentId, digest,
            state ?? throw new InvalidOperationException("Missing realtime handoff decision."), receivingTurn, inputIndex);
        next = ReplaceRealtimeSession(next, updated with { HandoffAdmissions = [.. receipts, receipt] });
        var committed = await PublishAsync(before.Revision, next with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, ct).ConfigureAwait(false);
        return new(true, committed, receipt);
    }

    internal static IEnumerable<JsonElement> RealtimeTimeline(CodexStoredTurn turn, bool includeUncaptured)
    {
        if (turn.RealtimeSteeringInputIndices is not { Length: > 0 } steering)
            return turn.Input.Concat(turn.Output);
        var indices = steering.ToHashSet();
        var initial = turn.Input.Where((_, index) => !indices.Contains(index));
        var missing = new List<JsonElement>();
        var position = 0;
        foreach (var item in turn.Output)
            if (position < steering.Length && JsonElement.DeepEquals(item, turn.Input[steering[position]])) position++;
        if (includeUncaptured)
            for (; position < steering.Length; position++) missing.Add(turn.Input[steering[position]]);
        return initial.Concat(turn.Output).Concat(missing);
    }

    private static void ValidateRealtimeHandoffTurnInputs(CodexThreadDocument document)
    {
        foreach (var turn in document.Turns)
        {
            if (turn.RealtimeSteeringInputIndices is not { } indices) continue;
            if (indices.Length > MaximumItems || !indices.SequenceEqual(indices.Distinct().Order()) ||
                indices.Any(i => i < 0 || i >= turn.Input.Length ||
                    CodexRealtimeProtocol.Text(turn.Input[i], "role") != "user"))
                throw new InvalidDataException("Invalid durable realtime steering indices.");
            if (turn.Status == "completed")
            {
                var position = 0;
                foreach (var item in turn.Output)
                    if (position < indices.Length && JsonElement.DeepEquals(item, turn.Input[indices[position]])) position++;
                if (position != indices.Length)
                    throw new InvalidDataException("Completed turn omitted an admitted realtime steering input.");
            }
        }
        foreach (var receipt in (document.RealtimeSessions ?? []).SelectMany(s => s.HandoffAdmissions ?? []))
        {
            if (receipt.TurnId is not { } target || receipt.InputIndex is not { } index) continue;
            var turn = document.Turns.SingleOrDefault(t => t.Id == target);
            if (turn is null || index >= turn.Input.Length ||
                CodexRealtimeProtocol.Text(turn.Input[index], "role") != "user" ||
                CodexRealtimeProtocol.Text(turn.Input[index], "content") is not { } content ||
                HandoffDigest(content) != receipt.InputSha256 ||
                receipt.State == "steer" && !(turn.RealtimeSteeringInputIndices ?? []).Contains(index))
                throw new InvalidDataException("Realtime handoff receipt does not bind its stored reasoning input.");
        }
    }

    private static string HandoffDigest(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void ValidateRealtimeHandoffAdmissions(CodexStoredRealtimeSession session)
    {
        if (session.HandoffAdmissions is not { } receipts) return;
        if (receipts.Length > MaximumItems) throw new InvalidDataException("Realtime handoff receipt limit exceeded.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var submissions = new HashSet<Guid>();
        foreach (var receipt in receipts)
        {
            if (receipt is null || !ValidToken(receipt.HandoffId) || !ids.Add(receipt.HandoffId) ||
                receipt.SubmissionId == Guid.Empty || !submissions.Add(receipt.SubmissionId) ||
                receipt.AttachmentId == Guid.Empty || !(session.UsedAttachmentIds ?? []).Contains(receipt.AttachmentId) ||
                receipt.InputSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
                receipt.State is not ("start" or "steer" or "rejected" or "retired"))
                throw new InvalidDataException("Invalid realtime handoff admission receipt.");
            var accepted = receipt.State is "start" or "steer";
            if (accepted != (receipt.TurnId is not null) || accepted != (receipt.InputIndex is not null) ||
                receipt.TurnId == Guid.Empty || receipt.InputIndex is < 0 ||
                receipt.State == "start" && (receipt.TurnId != receipt.SubmissionId || receipt.InputIndex != 0))
                throw new InvalidDataException("Invalid realtime handoff receiving turn.");
            var capture = session.History?.Handoffs.SingleOrDefault(h => h.HandoffId == receipt.HandoffId);
            if (capture is null || capture.State != (accepted ? "accepted" : "rejected") ||
                capture.TurnId != receipt.TurnId ||
                HandoffDigest(PrepareRealtimeHandoffInput(session, receipt.HandoffId)?.Text ?? "") != receipt.InputSha256)
                throw new InvalidDataException("Realtime handoff receipt contradicts its durable transcript capture.");
        }
    }
}
