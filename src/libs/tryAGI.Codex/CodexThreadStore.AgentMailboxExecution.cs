using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Whether the storage adapter explicitly supports graph-fenced wake admission.</summary>
    public bool SupportsAgentMailboxExecution => storage is ICodexAgentMailboxExecutionStorage;

    /// <summary>Explicit host-authorized follow-up wake using the existing native thread agent.
    /// Queue-only mail does not start an idle turn. The caller chooses and retains the claim,
    /// turn and owner IDs before invoking. A previously claimed delivery is never re-executed
    /// by this method: reconcile an ambiguous outcome instead. Budget and tool authorization
    /// remain the existing runner's responsibility.</summary>
    /// <param name="agent">Existing thread agent bound to this journal.</param>
    /// <param name="rootThreadId">Agent-tree root.</param>
    /// <param name="recipientThreadId">Open named child.</param>
    /// <param name="expectedMailboxRevision">Exact observed mailbox revision.</param>
    /// <param name="expectedThreadRevision">Exact observed recipient journal revision.</param>
    /// <param name="claimId">Stable new reservation ID.</param>
    /// <param name="turnId">Stable new target turn ID.</param>
    /// <param name="ownerId">Authorized host execution owner.</param>
    /// <param name="options">Resolved and authorized Responses settings, including exact child/parent and turn identity.</param>
    /// <param name="execute">Existing authorized tool executor.</param>
    /// <param name="maximumRequests">Existing runner request bound.</param>
    /// <param name="cancellationToken">Cancels execution; does not prove non-admission.</param>
    /// <returns>Completed run, or null if no follow-up was pending.</returns>
    public async Task<CodexThreadAgentResult?> RunAgentMailboxWakeAsync(CodexThreadAgent agent,
        Guid rootThreadId, Guid recipientThreadId, long expectedMailboxRevision, long expectedThreadRevision,
        Guid claimId, Guid turnId, string ownerId, CodexResponseOptions options,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute, int maximumRequests = 8,
        CancellationToken cancellationToken = default)
    {
        RequireMailboxExecution();
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(execute);
        Id(claimId); Id(turnId); Token(ownerId);
        if (!agent.IsBoundTo(this)) throw new ArgumentException("Agent is bound to another thread store.", nameof(agent));
        if (maximumRequests is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        if (rootThreadId == recipientThreadId)
            throw new ArgumentException("Follow-up tasks cannot target the root agent.", nameof(recipientThreadId));
        var before = await ReadExpectedAgentMailboxAsync(rootThreadId, recipientThreadId, expectedMailboxRevision,
            cancellationToken).ConfigureAwait(false);
        if (before.Claim is not null) throw new InvalidOperationException("Unsettled mail claim must be reconciled before a wake.");
        var prefix = new List<CodexAgentMail>();
        foreach (var mail in before.Pending)
        {
            prefix.Add(mail);
            if (mail.Mode == CodexAgentMailMode.Followup) break;
        }
        if (prefix.Count == 0 || prefix[^1].Mode != CodexAgentMailMode.Followup) return null;

        var directory = await ReadAgentIdentitiesAsync(rootThreadId, cancellationToken).ConfigureAwait(false);
        var recipient = directory.GetActive(recipientThreadId);
        if (options.Identity is not { } identity || !Guid.TryParse(identity.ThreadId, out var wireThread) ||
            wireThread != recipientThreadId || !Guid.TryParse(identity.TurnId, out var wireTurn) || wireTurn != turnId ||
            !Guid.TryParse(identity.ParentThreadId, out var wireParent) || wireParent != recipient.ParentThreadId ||
            identity.QueuedTurn || identity.GoalTurn)
            throw new ArgumentException("Wake requires exact host-owned recipient, turn and parent identities.", nameof(options));
        identity.Validate();
        if (recipient.SpawnOrigin is { } origin && prefix.Any(mail => mail.Id == origin.InitialMessageId) &&
            (!Guid.TryParse(identity.ParentTurnId, out var spawningTurn) || spawningTurn != origin.ParentTurnId ||
             !Guid.TryParse(identity.RootTurnId, out var rootTurn) || rootTurn != origin.RootTurnId ||
             AgentMailFingerprint(prefix.Single(mail => mail.Id == origin.InitialMessageId)) != origin.InitialMessageFingerprint))
            throw new ArgumentException("Initial named-agent wake must retain the original spawning and root turn identities.", nameof(options));
        var thread = await CurrentAsync(recipientThreadId, expectedThreadRevision, cancellationToken).ConfigureAwait(false);
        RequireDurableThread(thread);
        RequireUnarchived(thread);
        if (thread.Turns.LastOrDefault()?.Status == "inProgress" || thread.Turns.Any(turn => turn.Id == turnId))
            throw new InvalidOperationException("Wake requires a fresh idle turn identity.");

        var descendantGoal = await CaptureDescendantGoalAttributionAsync(directory, recipient, cancellationToken).ConfigureAwait(false);
        var input = prefix.Select(mail => CodexAgentMailWakeAdmission.Project(directory.GetRecorded(mail.SenderThreadId),
            recipient, mail)).ToArray();
        var binding = new CodexAgentMailWakeAdmission(directory.Graph.Revision, CodexAgentMailWakeAdmission.Fingerprint(input));
        var claim = new CodexAgentMailClaim(claimId, ownerId, turnId, input.Length, true) { WakeAdmission = binding };
        // Only this successful, fresh claim permits invoking the runner. No replay after a lost reply.
        await WriteAgentMailboxAsync(before, before with { Claim = claim }, CodexAgentMailboxOperation.Claim,
            cancellationToken).ConfigureAwait(false);

        using var control = new CodexAgentControl { CapturedDescendantGoal = descendantGoal };
        control.InitializeInput(input, turnTrigger: CodexAgentMailWakeAdmission.TurnTrigger(claimId));
        return await agent.RunAsync(recipientThreadId, thread.Revision, turnId, ownerId, options, input, execute,
            maximumRequests, turnStarted: async (admitted, ct) =>
            {
                var current = await ReadAgentMailboxAsync(rootThreadId, recipientThreadId, ct).ConfigureAwait(false);
                if (current.Claim != claim)
                    throw new InvalidOperationException("Wake claim changed before admission acknowledgement.");
                await AcknowledgeObservedWakeAsync(current, admitted, ct).ConfigureAwait(false);
            }, cancellationToken: cancellationToken, control: control).ConfigureAwait(false);
    }

    /// <summary>Reconcile an unsettled bound wake claim against its existing durable turn.
    /// This never loads a runtime, dispatches a model, starts another turn or releases an
    /// unproven claim. False means no exact admission evidence; only an explicit host-fenced
    /// release can subsequently make the reservation available again.</summary>
    public async Task<bool> ReconcileAgentMailboxWakeAsync(Guid rootThreadId, Guid recipientThreadId,
        long expectedMailboxRevision, Guid claimId, string ownerId, CancellationToken cancellationToken = default)
    {
        RequireMailboxExecution();
        Id(claimId); Token(ownerId);
        var before = await ReadExpectedAgentMailboxAsync(rootThreadId, recipientThreadId, expectedMailboxRevision,
            cancellationToken).ConfigureAwait(false);
        if (before.Claim is not { WakeAdmission: not null } claim || claim.Id != claimId || claim.OwnerId != ownerId)
            throw new InvalidOperationException("Expected bound wake reservation and execution owner do not match.");
        var thread = await ReadAsync(recipientThreadId, cancellationToken).ConfigureAwait(false);
        var turn = thread.Turns.SingleOrDefault(item => item.Id == claim.TurnId);
        if (turn is null) return false;
        await AcknowledgeObservedWakeAsync(before, thread, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void RequireMailboxExecution()
    {
        if (!SupportsAgentMailboxExecution)
            throw new InvalidOperationException("Graph-fenced native agent mailbox execution is unavailable.");
    }

    private async Task AcknowledgeObservedWakeAsync(CodexAgentMailbox mailbox, CodexThreadDocument thread, CancellationToken ct)
    {
        if (mailbox.Claim is not { WakeAdmission: { } binding } claim ||
            thread.Id != mailbox.RecipientThreadId || thread.AccountId != mailbox.AccountId ||
            thread.Turns.SingleOrDefault(turn => turn.Id == claim.TurnId) is not { } admitted ||
            !binding.Matches(claim, admitted))
            throw new InvalidOperationException("Exact durable wake admission could not be verified.");
        await SettleObservedAgentMailAsync(mailbox, claim, acknowledge: true, ct).ConfigureAwait(false);
    }
}
