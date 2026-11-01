namespace tryAGI.Codex;

/// <summary>Trusted host limits for one rich tool output; not model-supplied parameters or provider guarantees.</summary>
public sealed record CodexToolImageLimits
{
    public int MaximumImages { get; init; } = 4;
    public int MaximumTotalBytes { get; init; } = 524288;
    public const int HardMaximumBytes = 16 * 1024 * 1024;

    public void Validate()
    {
        if (MaximumImages is < 1 or > 16 || MaximumTotalBytes is < 1 or > HardMaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(CodexToolImageLimits), "Image limits require 1..16 images and 1 byte..16 MiB total.");
    }
}
