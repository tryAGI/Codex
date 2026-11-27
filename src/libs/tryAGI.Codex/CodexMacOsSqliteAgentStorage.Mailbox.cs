using System.Text.Json;
using Sql = tryAGI.Codex.CodexMacOsAgentSqlite.Connection;

namespace tryAGI.Codex;

/// <summary>Durable state of one exact local host wake admission grant.</summary>
public enum CodexAgentWakeGrantState
{
    /// <summary>Granted, possibly reserved or admitted, but not yet acknowledged.</summary>
    Granted = 0,
    /// <summary>Explicitly fenced before admission; cannot be reused.</summary>
    Fenced = 1,
    /// <summary>Exact turn admission acknowledged in the mailbox transaction.</summary>
    Consumed = 2,
}

public sealed partial class CodexMacOsSqliteAgentStorage
{
    private sealed record WakeGrant(Guid ClaimId, Guid Recipient, Guid TurnId, string Owner, long State);

    /// <summary>Persist one host-authorized idle wake grant for the exact recipient, claim,
    /// turn and owner. This is an admission grant, not a worker lease or a model call.
    /// Only the trusted host may invoke it, after obtaining the execution authority.
    /// False means no new grant; inspect the original claim rather than minting another ID
    /// after an ambiguous commit. Grants never expire automatically.</summary>
    public Task<bool> AuthorizeWakeAsync(Guid rootThreadId, Guid recipientThreadId, Guid claimId, Guid turnId,
        string ownerId, CancellationToken cancellationToken = default)
    {
        Id(rootThreadId); Id(recipientThreadId); Id(claimId); Id(turnId); Token(ownerId);
        if (rootThreadId == recipientThreadId) throw new ArgumentException("Root cannot be woken by follow-up mail.");
        return database.WriteAsync(sql =>
        {
            if (!OpenPath(sql, rootThreadId, recipientThreadId) ||
                Edge(sql, recipientThreadId)?.Identity is null ||
                Header(sql, recipientThreadId) is not { Active: false, Archived: false })
                return false;
            var box = ReadMailbox(sql, rootThreadId, recipientThreadId);
            if (!box.HasWakeRequest) return false;
            // Unique claim, recipient/turn and active-recipient constraints are durable
            // across processes; revoked and consumed grants cannot be reused.
            var changed = sql.Change("""
                INSERT INTO agent_grants(claim,recipient,turn,owner,state) VALUES(?1,?2,?3,?4,0)
                ON CONFLICT DO NOTHING;
                """, claimId, recipientThreadId, turnId, ownerId);
            if (changed is not (0 or 1)) throw new InvalidDataException("Unexpected grant insertion count.");
            return changed == 1;
        }, cancellationToken);
    }

    /// <summary>Inspect the exact original grant after an ambiguous write, without loading
    /// history or making it executable again. Null means no matching recorded grant.</summary>
    public Task<CodexAgentWakeGrantState?> ReadWakeAuthorizationAsync(Guid rootThreadId, Guid recipientThreadId,
        Guid claimId, Guid turnId, string ownerId, CancellationToken cancellationToken = default)
    {
        Id(rootThreadId); Id(recipientThreadId); Id(claimId); Id(turnId); Token(ownerId);
        return database.ReadAsync<CodexAgentWakeGrantState?>(sql =>
        {
            if (!Member(sql, rootThreadId, recipientThreadId) ||
                Grant(sql, claimId) is not { } grant || grant.Recipient != recipientThreadId ||
                grant.TurnId != turnId || grant.Owner != ownerId) return null;
            if (grant.State is < 0 or > 2) throw new InvalidDataException("Invalid persisted wake grant state.");
            return (CodexAgentWakeGrantState)grant.State;
        }, cancellationToken);
    }

    /// <summary>Revoke an exact unadmitted wake grant. Invoke only when the host has
    /// positively fenced the prior execution owner, not merely when a timeout elapsed.
    /// This transaction refuses any grant whose target turn already exists. The same
    /// claim may subsequently be released through the existing mailbox store.</summary>
    public Task<bool> FenceUnadmittedWakeAsync(Guid rootThreadId, Guid recipientThreadId, Guid claimId, Guid turnId,
        string ownerId, CancellationToken cancellationToken = default)
    {
        Id(rootThreadId); Id(recipientThreadId); Id(claimId); Id(turnId); Token(ownerId);
        return database.WriteAsync(sql =>
        {
            if (!Member(sql, rootThreadId, recipientThreadId) ||
                Grant(sql, claimId) is not { } grant || grant.Recipient != recipientThreadId ||
                grant.TurnId != turnId || grant.Owner != ownerId || grant.State is not (0 or 1))
                return false;
            if (ReadDocument(sql, recipientThreadId).Turns.Any(turn => turn.Id == turnId)) return false;
            if (grant.State == 1) return true;
            ExpectOne(sql.Change("UPDATE agent_grants SET state=1 WHERE claim=?1 AND state=0;", claimId));
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CodexAgentMailbox> ReadAgentMailboxAsync(string accountId, Guid rootThreadId, Guid recipientThreadId,
        CancellationToken cancellationToken)
    {
        Account(accountId);
        Id(rootThreadId); Id(recipientThreadId);
        return database.ReadAsync(sql => ReadMailbox(sql, rootThreadId, recipientThreadId), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CompareExchangeAgentMailboxAsync(long expectedRevision, CodexAgentMailbox next,
        CodexAgentMailboxOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        Account(next.AccountId);
        Id(next.RootThreadId); Id(next.RecipientThreadId);
        if (expectedRevision < 0 || next.Revision != checked(expectedRevision + 1) || !Enum.IsDefined(operation))
            throw new ArgumentException("Mailbox CAS requires the exact next revision and operation.", nameof(next));
        var proposed = CodexThreadStore.ValidateAgentMailboxSnapshot(next, accountId, next.RootThreadId, next.RecipientThreadId);
        return database.WriteAsync(sql =>
        {
            var root = proposed.RootThreadId;
            var recipient = proposed.RecipientThreadId;
            var before = ReadMailbox(sql, root, recipient);
            if (before.Revision != expectedRevision) return false;
            if (operation != CodexAgentMailboxOperation.Release && !OpenPath(sql, root, recipient)) return false;
            switch (operation)
            {
                case CodexAgentMailboxOperation.Enqueue:
                    if (proposed.Claim != before.Claim || !proposed.Receipts.SequenceEqual(before.Receipts) ||
                        proposed.Pending.Count != before.Pending.Count + 1 ||
                        !proposed.Pending.Take(before.Pending.Count).SequenceEqual(before.Pending))
                        return false;
                    var mail = proposed.Pending[^1];
                    if (!Member(sql, root, mail.SenderThreadId) || Header(sql, mail.SenderThreadId) is null ||
                        mail.SenderThreadId != root && Edge(sql, mail.SenderThreadId)?.Identity is null ||
                        recipient != root && Edge(sql, recipient)?.Identity is null ||
                        mail.Mode != CodexAgentMailMode.Result && !OpenPath(sql, root, mail.SenderThreadId))
                        return false;
                    if (mail.Mode == CodexAgentMailMode.Result)
                    {
                        if (Edge(sql, mail.SenderThreadId)?.Edge.ParentThreadId != recipient) return false;
                        if (mail.CompletedResult is { } result)
                        {
                            var source = ReadDocument(sql, mail.SenderThreadId);
                            var turn = source.Turns.SingleOrDefault(item => item.Id == result.TurnId);
                            if (turn is null || !CodexAgentCompletedResult.Matches(mail, turn)) return false;
                        }
                    }
                    break;
                case CodexAgentMailboxOperation.Claim:
                    // Active-turn and unbound claims deliberately fail closed. Their
                    // separate durable steering admission is not implemented here.
                    if (before.Claim is not null || proposed.Claim is not { IsWake: true, WakeAdmission: { } binding } claim ||
                        !proposed.Pending.SequenceEqual(before.Pending) || !proposed.Receipts.SequenceEqual(before.Receipts) ||
                        recipient == root || Header(sql, recipient) is not { Active: false, Archived: false } ||
                        !GrantMatches(sql, recipient, claim, 0) ||
                        Revision(RequireRoot(sql, root)) != binding.GraphRevision)
                        return false;
                    var child = Edge(sql, recipient)?.Identity;
                    if (child is null) return false;
                    var input = before.Pending.Take(claim.MessageCount).Select(item =>
                        CodexAgentMailWakeAdmission.Project(Identity(sql, root, item.SenderThreadId), child, item));
                    if (CodexAgentMailWakeAdmission.Fingerprint(input) != binding.InputFingerprint) return false;
                    break;
                case CodexAgentMailboxOperation.Acknowledge:
                    if (before.Claim is not { WakeAdmission: { } admission } outstanding ||
                        proposed.Claim is not null || !GrantMatches(sql, recipient, outstanding, 0) ||
                        !proposed.Pending.SequenceEqual(before.Pending.Skip(outstanding.MessageCount)) ||
                        !proposed.Receipts.SequenceEqual(before.Receipts.Concat(before.Pending.Take(outstanding.MessageCount)
                            .Select(item => new CodexAgentMailReceipt(item.Id, CodexThreadStore.AgentMailFingerprint(item))))))
                        return false;
                    var admitted = ReadDocument(sql, recipient).Turns.SingleOrDefault(turn => turn.Id == outstanding.TurnId);
                    if (admitted is null || !admission.Matches(outstanding, admitted)) return false;
                    ExpectOne(sql.Change("UPDATE agent_grants SET state=2 WHERE claim=?1 AND state=0;", outstanding.Id));
                    break;
                case CodexAgentMailboxOperation.Release:
                    if (before.Claim is not { } reserved || proposed.Claim is not null ||
                        !GrantMatches(sql, recipient, reserved, 1) ||
                        !proposed.Pending.SequenceEqual(before.Pending) || !proposed.Receipts.SequenceEqual(before.Receipts) ||
                        ReadDocument(sql, recipient).Turns.Any(turn => turn.Id == reserved.TurnId))
                        return false;
                    break;
                default:
                    return false;
            }
            WriteMailbox(sql, proposed, expectedRevision);
            return true;
        }, cancellationToken);
    }

    private CodexAgentMailbox ReadMailbox(Sql sql, Guid root, Guid recipient)
    {
        if (!Member(sql, root, recipient) || Header(sql, recipient) is null)
            throw new KeyNotFoundException("Agent mailbox recipient not found in the requested tree.");
        var box = sql.Query("SELECT revision,body FROM agent_mail WHERE recipient=?1;", row =>
        {
            var value = JsonSerializer.Deserialize(row.Blob(1), CodexAgentStorageJson.Default.CodexAgentMailbox)
                ?? throw new InvalidDataException("Missing persisted agent mailbox.");
            if (value.Revision != row.Number(0) || value.Revision < 1)
                throw new InvalidDataException("Persisted agent mailbox revision mismatch.");
            return value;
        }, 1, recipient).SingleOrDefault() ?? new CodexAgentMailbox(accountId, root, recipient, 0, [], []);
        return CodexThreadStore.ValidateAgentMailboxSnapshot(box, accountId, root, recipient);
    }

    private static void WriteMailbox(Sql sql, CodexAgentMailbox value, long expectedRevision)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CodexAgentStorageJson.Default.CodexAgentMailbox);
        if (expectedRevision == 0)
            ExpectOne(sql.Change("INSERT INTO agent_mail(recipient,revision,body) VALUES(?1,?2,?3);",
                value.RecipientThreadId, value.Revision, bytes));
        else
            ExpectOne(sql.Change("UPDATE agent_mail SET revision=?2,body=?3 WHERE recipient=?1 AND revision=?4;",
                value.RecipientThreadId, value.Revision, bytes, expectedRevision));
    }

    private bool CheckWakeAdmission(Sql sql, Guid root, CodexNamedAgentIdentity child, CodexStoredTurn turn)
    {
        var mailbox = ReadMailbox(sql, root, child.ThreadId);
        if (mailbox.Claim is not { WakeAdmission: { } binding } claim ||
            !GrantMatches(sql, child.ThreadId, claim, 0) ||
            Revision(RequireRoot(sql, root)) != binding.GraphRevision || !binding.Matches(claim, turn) ||
            turn.Input.Length != claim.MessageCount)
            return false;
        // The initial child's root correlation is part of the atomic journal admission.
        if (child.SpawnOrigin is { } origin &&
            mailbox.Pending.Take(claim.MessageCount).Any(mail => mail.Id == origin.InitialMessageId) &&
            turn.RootTurnId != origin.RootTurnId)
            return false;
        return true;
    }

    private static WakeGrant? Grant(Sql sql, Guid claim)
        => sql.Query("SELECT claim,recipient,turn,owner,state FROM agent_grants WHERE claim=?1;",
            row => new WakeGrant(row.Id(0), row.Id(1), row.Id(2), CodexMacOsAgentSqlite.Text(row.Blob(3)), row.Number(4)), 1, claim)
            .SingleOrDefault();

    private static bool GrantMatches(Sql sql, Guid recipient, CodexAgentMailClaim claim, long state)
        => Grant(sql, claim.Id) is { } grant && grant.Recipient == recipient && grant.TurnId == claim.TurnId &&
            grant.Owner == claim.OwnerId && grant.State == state;

    private static CodexNamedAgentIdentity Identity(Sql sql, Guid root, Guid id)
        => id == root ? new(root, null, root, CodexAgentPath.Root) :
            Edge(sql, id) is { Root: var tree, Identity: { } identity } && tree == root ? identity :
            throw new InvalidDataException("Mailbox sender has no recorded named identity.");

    private bool Settled(Sql sql, Guid id)
    {
        var header = Header(sql, id);
        if (header is { Active: true }) return false;
        if (sql.Query("SELECT 1 FROM agent_grants WHERE recipient=?1 AND state=0;", row => row.Number(0), 1, id).Count != 0)
            return false;
        if (header is null) return true;
        var root = Edge(sql, id)?.Root ?? id;
        // Lifecycle mutations, unlike ordinary graph and mailbox reads, must also
        // verify that no existing voice attachment retains execution ownership.
        return ReadMailbox(sql, root, id).Claim is null && !CodexThreadStore.HasOpenRealtime(ReadDocument(sql, id));
    }
}
