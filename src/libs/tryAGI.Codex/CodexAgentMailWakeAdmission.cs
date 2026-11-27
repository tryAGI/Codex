using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Exact named graph and model-input preimage reserved for an idle mailbox wake.</summary>
/// <param name="GraphRevision">Observed topology and identity revision.</param>
/// <param name="InputFingerprint">Digest of the ordered, canonical model input items.</param>
public sealed record CodexAgentMailWakeAdmission(string GraphRevision, string InputFingerprint)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(GraphRevision) && GraphRevision.Length <= 1024 &&
        InputFingerprint is { Length: 64 } && InputFingerprint.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>Durable turn trigger binding the journal admission to the exact mailbox claim.</summary>
    public static string TurnTrigger(Guid claimId)
        => claimId != Guid.Empty ? "native:agent-mail:" + claimId.ToString("D") :
            throw new ArgumentException("A nonempty agent mail claim ID is required.", nameof(claimId));

    /// <summary>Check durable evidence of the exact reserved input prefix, not provider sampling.
    /// This also works after the admitted turn has ended or received later steering inputs.</summary>
    public bool Matches(CodexAgentMailClaim claim, CodexStoredTurn turn)
        => IsValid && claim.IsWake && claim.WakeAdmission == this && claim.Id != Guid.Empty &&
            claim.TurnId == turn.Id && claim.OwnerId == turn.OwnerId &&
            turn.TurnTrigger == TurnTrigger(claim.Id) && claim.MessageCount > 0 &&
            turn.Input.Length >= claim.MessageCount &&
            Fingerprint(turn.Input.Take(claim.MessageCount)) == InputFingerprint;

    internal static string Fingerprint(IEnumerable<JsonElement> input)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in input)
            digest.AppendData(Encoding.ASCII.GetBytes(CodexRetainedJournalMetadata.ItemHash(item)));
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    internal static JsonElement Project(CodexNamedAgentIdentity sender, CodexNamedAgentIdentity recipient,
        CodexAgentMail mail)
    {
        if (sender.ThreadId != mail.SenderThreadId || sender.RootThreadId != recipient.RootThreadId ||
            mail.Mode == CodexAgentMailMode.Result && sender.ParentThreadId != recipient.ThreadId)
            throw new InvalidOperationException("Agent mail sender or result routing disagrees with its recorded identity.");
        var kind = mail.Mode switch
        {
            CodexAgentMailMode.QueueOnly => "MESSAGE",
            CodexAgentMailMode.Followup => "NEW_TASK",
            CodexAgentMailMode.Result => "FINAL_ANSWER",
            _ => throw new ArgumentOutOfRangeException(nameof(mail)),
        };
        var prefix = $"Message Type: {kind}\nTask name: {recipient.Path}\nSender: {sender.Path}\nPayload:\n";
        var content = new JsonArray(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = mail.IsEncrypted ? prefix : prefix + mail.Content,
        });
        if (mail.IsEncrypted)
            content.Add((JsonNode)new JsonObject { ["type"] = "encrypted_content", ["encrypted_content"] = mail.Content });
        using var document = JsonDocument.Parse(new JsonObject
        {
            ["type"] = "agent_message",
            ["id"] = "amsg_" + mail.Id.ToString("D"),
            ["author"] = sender.Path,
            ["recipient"] = recipient.Path,
            ["content"] = content,
        }.ToJsonString());
        return document.RootElement.Clone();
    }
}

/// <summary>Explicit storage opt-in for actual native mailbox turn admission. In addition to the
/// graph, identity and mailbox contracts, the same adapter must atomically fence ordinary
/// thread CompareExchangeAsync when a new turn carries an agent-mail trigger. Admit only
/// a live authorized recipient/owner, matching graph revision, outstanding wake claim,
/// exact turn identity and bound input prefix; verify against the graph and mailbox in the
/// same transaction. Reject admission after release, close or deletion. Claim creation
/// must check the graph revision, and acknowledgement must verify the committed turn
/// against its binding; only positively fenced non-admission permits release.
/// This capability is not inferred from generic thread CAS support.</summary>
public interface ICodexAgentMailboxExecutionStorage : ICodexAgentMailboxStorage, ICodexAgentIdentityStorage
{
}
