using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Persists an explicit display name using the same revision fence as the thread journal.</summary>
    public Task<CodexThreadDocument> SetNameAsync(Guid threadId, long expectedRevision, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Trim();
        if (normalized.Length == 0) throw new ArgumentException("Thread name must not be empty.", nameof(name));
        return ChangeAsync(threadId, expectedRevision, thread => RequireUnarchived(thread) with { Name = normalized }, cancellationToken);
    }

    internal Task<CodexThreadDocument> UpdateMetadataAsync(Guid id, long revision, JsonElement? git, bool? daybreakEnabled, CancellationToken ct)
        => ChangeAsync(id, revision, thread =>
        {
            if (thread.Settings.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind == JsonValueKind.True)
                throw new ArgumentException("Ephemeral threads do not support persisted metadata.");
            var info = thread.GitInfo;
            if (git is { } patch)
            {
                var previous = info ?? new CodexStoredGitInfo();
                info = new CodexStoredGitInfo
                {
                    Sha = PatchGitField(patch, "sha", previous.Sha),
                    Branch = PatchGitField(patch, "branch", previous.Branch),
                    OriginUrl = PatchGitField(patch, "originUrl", previous.OriginUrl)
                };
                if (info.Sha is null && info.Branch is null && info.OriginUrl is null) info = null;
            }
            return thread with { GitInfo = info, DaybreakEnabled = daybreakEnabled ?? thread.DaybreakEnabled };
        }, ct);

    private static string? PatchGitField(JsonElement patch, string field, string? previous)
    {
        if (!patch.TryGetProperty(field, out var value)) return previous;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()?.Trim() is not { Length: > 0 } text)
            throw new ArgumentException("gitInfo." + field + " must be a nonempty string or null.");
        return text;
    }
}
