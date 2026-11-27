using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    private const int MaximumPendingAgentMail = 256;
    private const int MaximumAgentMailIdentities = 4096;
    private const int MaximumAgentMailBytes = 65_536;
    private const int MaximumMailboxBytes = 2_097_152;
    private static readonly UTF8Encoding AgentMailEncoding = new(false, true);

    /// <summary>Whether the existing storage adapter supports durable inter-agent mail.</summary>
    public bool SupportsAgentMailbox => storage is ICodexAgentMailboxStorage;

    /// <summary>Reads and validates a mailbox without starting a turn or loading the recipient runtime.</summary>
    public async Task<CodexAgentMailbox> ReadAgentMailboxAsync(Guid rootThreadId, Guid recipientThreadId,
        CancellationToken cancellationToken = default)
    {
        Id(rootThreadId);
        Id(recipientThreadId);
        var mailbox = await RequireAgentMailboxStorage().ReadAgentMailboxAsync(accountId, rootThreadId,
            recipientThreadId, cancellationToken).ConfigureAwait(false);
        if (mailbox is null || mailbox.AccountId != accountId || mailbox.RootThreadId != rootThreadId ||
            mailbox.RecipientThreadId != recipientThreadId || mailbox.Revision < 0 ||
            mailbox.Pending is null || mailbox.Receipts is null ||
            mailbox.Pending.Count > MaximumPendingAgentMail || mailbox.Receipts.Count > MaximumAgentMailIdentities)
            throw new InvalidDataException("Invalid native agent mailbox snapshot.");

        var pending = mailbox.Pending.ToArray();
        var receipts = mailbox.Receipts.ToArray();
        if (pending.Length > MaximumPendingAgentMail || receipts.Length > MaximumAgentMailIdentities - pending.Length ||
            mailbox.Revision == 0 && (pending.Length != 0 || receipts.Length != 0 || mailbox.Claim is not null))
            throw new InvalidDataException("Invalid native agent mailbox capacity or initial revision.");

        var identities = new HashSet<Guid>();
        long bytes = 0;
        foreach (var mail in pending)
        {
            if (!ValidAgentMail(mail, rootThreadId, recipientThreadId) || !identities.Add(mail.Id))
                throw new InvalidDataException("Invalid or duplicate pending agent mail.");
            bytes += Encoding.UTF8.GetByteCount(mail.Content);
        }
        if (bytes > MaximumMailboxBytes)
            throw new InvalidDataException("Native agent mailbox content exceeds capacity.");

        foreach (var receipt in receipts)
            if (receipt is null || receipt.Id == Guid.Empty || !identities.Add(receipt.Id) ||
                receipt.Fingerprint is not { Length: 64 } ||
                !receipt.Fingerprint.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
                throw new InvalidDataException("Invalid or duplicate agent mail receipt.");

        if (mailbox.Claim is { } claim &&
            (claim.Id == Guid.Empty || claim.TurnId == Guid.Empty || !ValidToken(claim.OwnerId) ||
             claim.MessageCount < 1 || claim.MessageCount > pending.Length ||
             claim.WakeAdmission is { } binding && (!claim.IsWake || !binding.IsValid) ||
             pending.Take(claim.MessageCount - 1).Any(mail => mail.Mode == CodexAgentMailMode.Followup) ||
             claim.IsWake && pending[claim.MessageCount - 1].Mode != CodexAgentMailMode.Followup))
            throw new InvalidDataException("Invalid native agent mail reservation.");

        return mailbox with { Pending = Array.AsReadOnly(pending), Receipts = Array.AsReadOnly(receipts) };
    }

    /// <summary>Durably append an attributed delivery. Identical retries at the currently observed
    /// revision are no-ops, including after consumption; reusing an ID with different content fails.
    /// This method never wakes a recipient or calls a model.</summary>
    public async Task<CodexAgentMailbox> EnqueueAgentMailAsync(Guid rootThreadId, Guid recipientThreadId,
        long expectedRevision, CodexAgentMail mail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (!ValidAgentMail(mail, rootThreadId, recipientThreadId))
            throw new ArgumentException("Invalid agent mail identity, content or delivery intent.", nameof(mail));
        var before = await ReadExpectedAgentMailboxAsync(rootThreadId, recipientThreadId, expectedRevision,
            cancellationToken).ConfigureAwait(false);
        var existing = before.Pending.SingleOrDefault(item => item.Id == mail.Id);
        var receipt = before.Receipts.SingleOrDefault(item => item.Id == mail.Id);
        if (existing is not null || receipt is not null)
        {
            if (existing is not null && existing != mail || receipt is not null && receipt.Fingerprint != AgentMailFingerprint(mail))
                throw new ArgumentException("Agent delivery identity is already bound to different content.", nameof(mail));
            return before;
        }
        if (before.Pending.Count >= MaximumPendingAgentMail ||
            before.Pending.Count + before.Receipts.Count >= MaximumAgentMailIdentities ||
            before.Pending.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Content)) +
                Encoding.UTF8.GetByteCount(mail.Content) > MaximumMailboxBytes)
            throw new InvalidOperationException("Native agent mailbox capacity reached.");

        return await WriteAgentMailboxAsync(before, before with { Pending = Array.AsReadOnly<CodexAgentMail>([.. before.Pending, mail]) },
            CodexAgentMailboxOperation.Enqueue, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reserve a pending prefix using stable claim and target turn IDs. A wake claim must
    /// include a follow-up and stops exactly at its first follow-up; later mail stays pending.
    /// An existing turn may claim a queue-only prefix. No eligible prefix is a no-op.</summary>
    public async Task<CodexAgentMailbox> ClaimAgentMailAsync(Guid rootThreadId, Guid recipientThreadId,
        long expectedRevision, Guid claimId, string ownerId, Guid turnId, bool wake,
        int maximumMessages = MaximumPendingAgentMail, CancellationToken cancellationToken = default)
    {
        Id(claimId);
        Id(turnId);
        Token(ownerId);
        if (maximumMessages is < 1 or > MaximumPendingAgentMail)
            throw new ArgumentOutOfRangeException(nameof(maximumMessages));
        if (wake && rootThreadId == recipientThreadId)
            throw new ArgumentException("Follow-up tasks cannot target the root agent.", nameof(recipientThreadId));
        var before = await ReadExpectedAgentMailboxAsync(rootThreadId, recipientThreadId, expectedRevision,
            cancellationToken).ConfigureAwait(false);
        if (before.Claim is not null)
            throw new InvalidOperationException("Agent mail already has an unsettled reservation.");
        var count = 0;
        var foundFollowup = false;
        foreach (var mail in before.Pending.Take(maximumMessages))
        {
            count++;
            if (mail.Mode != CodexAgentMailMode.Followup) continue;
            foundFollowup = true;
            break;
        }
        if (count == 0 || wake && !foundFollowup) return before;

        var claim = new CodexAgentMailClaim(claimId, ownerId, turnId, count, wake);
        return await WriteAgentMailboxAsync(before, before with { Claim = claim },
            CodexAgentMailboxOperation.Claim, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consume the exact reserved prefix only after the storage adapter verifies durable
    /// admission of its claim/turn/messages. Later deliveries remain pending; consumed IDs remain deduplicated.</summary>
    public Task<CodexAgentMailbox> AcknowledgeAgentMailAsync(Guid rootThreadId, Guid recipientThreadId,
        long expectedRevision, Guid claimId, string ownerId, CancellationToken cancellationToken = default)
        => SettleAgentMailAsync(rootThreadId, recipientThreadId, expectedRevision, claimId, ownerId, true, cancellationToken);

    /// <summary>Return a reservation to the head without reordering it, only after the storage
    /// adapter proves non-admission and fences the previous execution authority.</summary>
    public Task<CodexAgentMailbox> ReleaseAgentMailAsync(Guid rootThreadId, Guid recipientThreadId,
        long expectedRevision, Guid claimId, string ownerId, CancellationToken cancellationToken = default)
        => SettleAgentMailAsync(rootThreadId, recipientThreadId, expectedRevision, claimId, ownerId, false, cancellationToken);

    private async Task<CodexAgentMailbox> SettleAgentMailAsync(Guid rootThreadId, Guid recipientThreadId,
        long revision, Guid claimId, string ownerId, bool acknowledge, CancellationToken ct)
    {
        Id(claimId);
        Token(ownerId);
        var before = await ReadExpectedAgentMailboxAsync(rootThreadId, recipientThreadId, revision, ct).ConfigureAwait(false);
        if (before.Claim is not { } claim || claim.Id != claimId || claim.OwnerId != ownerId)
            throw new InvalidOperationException("Expected agent mail reservation and execution owner do not match.");

        return await SettleObservedAgentMailAsync(before, claim, acknowledge, ct).ConfigureAwait(false);
    }

    private async Task<CodexAgentMailbox> SettleObservedAgentMailAsync(CodexAgentMailbox before,
        CodexAgentMailClaim claim, bool acknowledge, CancellationToken ct)
    {
        var next = before with { Claim = null };
        if (acknowledge)
        {
            next = next with
            {
                Pending = Array.AsReadOnly(before.Pending.Skip(claim.MessageCount).ToArray()),
                Receipts = Array.AsReadOnly(before.Receipts.Concat(before.Pending.Take(claim.MessageCount)
                    .Select(mail => new CodexAgentMailReceipt(mail.Id, AgentMailFingerprint(mail)))).ToArray()),
            };
        }
        return await WriteAgentMailboxAsync(before, next, acknowledge ? CodexAgentMailboxOperation.Acknowledge :
            CodexAgentMailboxOperation.Release, ct).ConfigureAwait(false);
    }

    private ICodexAgentMailboxStorage RequireAgentMailboxStorage()
        => storage as ICodexAgentMailboxStorage
            ?? throw new InvalidOperationException("Durable agent mailbox storage is unavailable.");

    private async Task<CodexAgentMailbox> ReadExpectedAgentMailboxAsync(Guid root, Guid recipient, long revision,
        CancellationToken ct)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        var before = await ReadAgentMailboxAsync(root, recipient, ct).ConfigureAwait(false);
        if (before.Revision != revision) throw new CodexThreadConflictException();
        return before;
    }

    private async Task<CodexAgentMailbox> WriteAgentMailboxAsync(CodexAgentMailbox before, CodexAgentMailbox next,
        CodexAgentMailboxOperation operation, CancellationToken ct)
    {
        var mailboxStorage = RequireAgentMailboxStorage();
        next = next with { Revision = checked(before.Revision + 1) };
        ct.ThrowIfCancellationRequested();
        bool committed;
        try
        {
            committed = await mailboxStorage.CompareExchangeAgentMailboxAsync(before.Revision, next, operation, ct)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Do not re-read a newer revision and replay a possibly committed operation.
            throw new IOException("Agent mailbox commit is unconfirmed.", error);
        }
        if (!committed) throw new CodexThreadConflictException();
        return next;
    }

    private static bool ValidAgentMail(CodexAgentMail? mail, Guid root, Guid recipient)
        => mail is not null && mail.Id != Guid.Empty && mail.SenderThreadId != Guid.Empty &&
           Enum.IsDefined(mail.Mode) && (mail.Mode != CodexAgentMailMode.Result || mail.SenderThreadId != recipient) &&
           (mail.Mode != CodexAgentMailMode.Followup || root != recipient) &&
           mail.Content is { Length: <= MaximumAgentMailBytes } &&
           (mail.Content.Length > 0 || mail.CompletedResult is not null) &&
           (mail.CompletedResult is not { } result || mail.Mode == CodexAgentMailMode.Result && !mail.IsEncrypted &&
               result.IsValid && mail.Id == CodexAgentCompletedResult.DeliveryId(mail.SenderThreadId, result.TurnId)) &&
           ValidAgentMailText(mail.Content);

    private static bool ValidAgentMailText(string content)
    {
        try { return AgentMailEncoding.GetByteCount(content) <= MaximumAgentMailBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    private static string AgentMailFingerprint(CodexAgentMail mail)
    {
        // Fixed-width sender/intent/encryption prefixes prevent ambiguous encodings.
        var prefix = string.Concat(mail.SenderThreadId.ToString("N"), ((int)mail.Mode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            mail.IsEncrypted ? "1" : "0");
        // Strict UTF-8 rejects unpaired surrogates instead of silently replacing them.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (mail.CompletedResult is { } result)
        {
            // Separate the new bound domain from all legacy mail fingerprints.
            var binding = string.Concat("bound-result-v1:", result.TurnId.ToString("N"), result.RootTurnId.ToString("N"),
                result.OutputSha256, result.ResponseId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", result.ResponseId);
            hash.AppendData(Encoding.ASCII.GetBytes(binding));
        }
        hash.AppendData(Encoding.ASCII.GetBytes(prefix));
        hash.AppendData(AgentMailEncoding.GetBytes(mail.Content));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
