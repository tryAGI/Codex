using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    internal static bool ReadStateEligible(CodexThreadDocument thread)
    {
        var settings = thread.Settings;
        if (settings.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind != JsonValueKind.False
            && ephemeral.ValueKind != JsonValueKind.Null) return false;
        // The native RPC represents app-server sessions; non-user scopes have different attention policy.
        if (settings.TryGetProperty("threadSource", out var source)
            && source.ValueKind != JsonValueKind.Null
            && (source.ValueKind != JsonValueKind.String || source.GetString() != "user")) return false;
        if (settings.TryGetProperty("source", out var sessionSource) && sessionSource.ValueKind != JsonValueKind.Null
            && (sessionSource.ValueKind != JsonValueKind.String
                || sessionSource.GetString() is "unknown" or "subAgent" or "subagent" or "internal")) return false;
        return true;
    }

    internal static string ReadStateRevision(CodexThreadDocument thread)
        => thread.ReadReceipt?.Revision.ToString() ?? "initial:" + thread.Id.ToString();

    private static void ValidateReadReceipt(CodexStoredThreadReadReceipt? receipt)
    {
        if (receipt is null) return;
        if (receipt.Revision == Guid.Empty || receipt.LastPublishedTurnId == Guid.Empty
            || receipt.FirstUnreadTurn is { Length: > 0 } first
                && (!Guid.TryParseExact(first, "D", out var parsed) || parsed == Guid.Empty || parsed.ToString() != first))
            throw new InvalidDataException("Invalid native thread read receipt.");
    }

    internal sealed record ReadStateUpdate(bool Applied, CodexThreadDocument Thread);

    internal async Task<ReadStateUpdate> UpdateReadStateAsync(Guid id, string expectedRevision, bool unread, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision) || expectedRevision.Length > 128)
            throw new ArgumentException("Invalid read state revision.");
        for (var attempt = 0; ; attempt++)
        {
            var before = await ReadAsync(id, ct).ConfigureAwait(false);
            if (!ReadStateEligible(before)) throw new ArgumentException("Thread read state unavailable.");
            if (!string.Equals(ReadStateRevision(before), expectedRevision, StringComparison.Ordinal))
                return new(false, before);

            // Unlike thread metadata, a read mark does not change recency. Retry only unrelated journal
            // CAS conflicts, and never rebase the caller's actual read-state revision.
            var next = before with
            {
                Revision = checked(before.Revision + 1),
                ReadReceipt = new(Guid.NewGuid(), unread ? "" : null, before.ReadReceipt?.LastPublishedTurnId)
            };
            try { return new(true, await PublishAsync(before.Revision, next, ct).ConfigureAwait(false)); }
            catch (CodexThreadConflictException) when (attempt < 3) { }
        }
    }

    private static CodexThreadDocument PublishTerminalReadAttention(CodexThreadDocument thread, CodexStoredTurn turn)
    {
        if (!ReadStateEligible(thread) || thread.ReadReceipt?.LastPublishedTurnId == turn.Id)
            return thread;
        var finalText = LastAssistantText(turn);
        var visible = turn.Status switch
        {
            "failed" => true,
            "completed" => !string.IsNullOrWhiteSpace(finalText)
                && !finalText.Contains("<decision>DONT_NOTIFY</decision>", StringComparison.Ordinal),
            "interrupted" => finalText is not null,
            _ => false
        };
        if (!visible) return thread;
        // The receipt shares the terminal turn's CAS. Loading old history or recovering a lost owner
        // cannot re-publish attention. Keep the earliest unread position through subsequent results.
        return thread with
        {
            ReadReceipt = new(Guid.NewGuid(), thread.ReadReceipt?.FirstUnreadTurn ?? turn.Id.ToString(), turn.Id)
        };
    }

    private static string? LastAssistantText(CodexStoredTurn turn)
    {
        static string? StringProperty(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        foreach (var item in turn.Output.Reverse())
        {
            if (StringProperty(item, "role") != "assistant" || StringProperty(item, "type") is not (null or "message")
                || CodexRealtimeModeContext.IsModeFragment(item)) continue;
            if (!item.TryGetProperty("content", out var content)) return "";
            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray()
                    .Where(part => StringProperty(part, "type") == "output_text")
                    .Select(part => StringProperty(part, "text") ?? "")),
                _ => ""
            };
        }
        return null;
    }
}
