namespace tryAGI.Codex;

public sealed partial record CodexThreadDocument
{
    /// <summary>Explicit display name, independent of the first user message.</summary>
    public string? Name { get; init; }
}
