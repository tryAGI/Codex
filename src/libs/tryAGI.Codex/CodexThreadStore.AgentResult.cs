using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Queue a successful child's exact durable result for its immediate parent.
    /// Repeated delivery at an observed mailbox revision is idempotent, including after consumption.
    /// This never starts a parent turn or calls a model. The same adapter must verify the bound
    /// terminal result against the original child journal inside its mailbox transaction.</summary>
    public async Task<CodexAgentMailbox> DeliverNamedAgentCompletedResultAsync(Guid rootThreadId,
        Guid childThreadId, Guid childTurnId, long expectedChildRevision, long expectedParentMailboxRevision,
        CancellationToken cancellationToken = default)
    {
        Id(childTurnId);
        var directory = await ReadAgentIdentitiesAsync(rootThreadId, cancellationToken).ConfigureAwait(false);
        var child = directory.GetRecorded(childThreadId);
        if (child.ParentThreadId is not { } parent)
            throw new ArgumentException("The root agent has no parent mailbox.", nameof(childThreadId));
        _ = directory.GetRecorded(parent);
        var thread = await CurrentAsync(childThreadId, expectedChildRevision, cancellationToken).ConfigureAwait(false);
        var turn = thread.Turns.SingleOrDefault(item => item.Id == childTurnId)
            ?? throw new KeyNotFoundException("Child turn not found.");
        var mail = CodexAgentCompletedResult.FromCompletedTurn(childThreadId, turn);
        return await EnqueueAgentMailAsync(rootThreadId, parent, expectedParentMailboxRevision, mail,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Project an actually accepted named child spawn into the existing parent backing
    /// turn and Realtime history. The immutable spawn origin supplies the admitted dispatch;
    /// callers cannot select a different parent, owner, turn, or activity identity.
    /// A completed Started activity describes accepted creation, not child task completion.</summary>
    public async Task<CodexThreadAdmission> ObserveNamedAgentStartedAsync(Guid rootThreadId, Guid childThreadId,
        long expectedParentRevision, CancellationToken cancellationToken = default)
    {
        var directory = await ReadAgentIdentitiesAsync(rootThreadId, cancellationToken).ConfigureAwait(false);
        var child = directory.GetRecorded(childThreadId);
        if (child is not { ParentThreadId: { } parent, SpawnOrigin: { } origin })
            throw new InvalidOperationException("Named child has no durable initial spawn evidence.");
        return await ObserveNativeBackingArtifactAsync(parent, expectedParentRevision, origin.ParentTurnId,
            origin.ParentOwnerId, origin.SourceDispatchId, CodexNativeArtifactStage.Completed,
            NamedAgentActivityItem(child, "started", "subagent-started-" + child.ThreadId.ToString("D"),
                origin.InitialModel, origin.InitialReasoningEffort), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Project a successful initial child turn onto its exact spawning parent turn.
    /// Follow-up initiators need their own admission binding and must not reuse this original
    /// dispatch. Result delivery and activity observation are independently retryable CAS writes.</summary>
    public async Task<CodexThreadAdmission> ObserveNamedAgentInitialCompletionAsync(Guid rootThreadId,
        Guid childThreadId, Guid childTurnId, long expectedChildRevision, long expectedParentRevision,
        CancellationToken cancellationToken = default)
    {
        Id(childTurnId);
        var directory = await ReadAgentIdentitiesAsync(rootThreadId, cancellationToken).ConfigureAwait(false);
        var child = directory.GetRecorded(childThreadId);
        if (child is not { ParentThreadId: { } parent, SpawnOrigin: { } origin })
            throw new InvalidOperationException("Named child has no durable initial spawn evidence.");
        var thread = await CurrentAsync(childThreadId, expectedChildRevision, cancellationToken).ConfigureAwait(false);
        var turn = thread.Turns.FirstOrDefault();
        if (turn is null || turn.Id != childTurnId || turn.RootTurnId != origin.RootTurnId ||
            turn.TurnTrigger is not { } trigger || !trigger.StartsWith("native:agent-mail:", StringComparison.Ordinal) ||
            !Guid.TryParse(trigger["native:agent-mail:".Length..], out var claimId) || claimId == Guid.Empty ||
            turn.Input.Count(item => CodexRealtimeProtocol.Text(item, "type") == "agent_message" &&
                CodexRealtimeProtocol.Text(item, "id") == "amsg_" + origin.InitialMessageId.ToString("D") &&
                CodexRealtimeProtocol.Text(item, "author") == directory.GetRecorded(parent).Path &&
                CodexRealtimeProtocol.Text(item, "recipient") == child.Path) != 1)
            throw new InvalidOperationException("Child turn is not the original named spawn task.");
        _ = CodexAgentCompletedResult.FromCompletedTurn(childThreadId, turn);
        return await ObserveNativeBackingArtifactAsync(parent, expectedParentRevision, origin.ParentTurnId,
            origin.ParentOwnerId, origin.SourceDispatchId, CodexNativeArtifactStage.Completed,
            NamedAgentActivityItem(child, "completed", "subagent-completed-" + childTurnId.ToString("D"), null, null),
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement NamedAgentActivityItem(CodexNamedAgentIdentity child, string kind, string id,
        string? model, string? reasoningEffort)
    {
        // Use the existing canonical backing item rather than a new Realtime history type.
        using var document = JsonDocument.Parse(new JsonObject
        {
            ["type"] = "subAgentActivity",
            ["id"] = id,
            ["kind"] = kind,
            ["agentThreadId"] = child.ThreadId.ToString("D"),
            ["agentPath"] = child.Path,
            ["model"] = model,
            ["reasoningEffort"] = reasoningEffort,
        }.ToJsonString());
        return document.RootElement.Clone();
    }
}
