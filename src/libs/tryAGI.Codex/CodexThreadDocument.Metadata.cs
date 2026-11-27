namespace tryAGI.Codex;

public sealed partial record CodexThreadDocument
{
    /// <summary>Explicit display name, independent of the first user message.</summary>
    public string? Name { get; init; }

    /// <summary>Stored Git metadata, independent of model settings.</summary>
    public CodexStoredGitInfo? GitInfo { get; init; }

    /// <summary>Saved client preference; never grants execution privileges.</summary>
    public bool? DaybreakEnabled { get; init; }
}

/// <summary>Persisted Git identity with credentials removed from the remote.</summary>
public sealed record CodexStoredGitInfo
{
    /// <summary>Commit identity.</summary>
    public string? Sha { get; init; }
    /// <summary>Branch name.</summary>
    public string? Branch { get; init; }
    private string? originUrl;
    /// <summary>Sanitized remote, preserving the Git path spelling.</summary>
    public string? OriginUrl { get => originUrl; init => originUrl = value is null ? null : CodexGitRemote.Sanitize(value); }
}
