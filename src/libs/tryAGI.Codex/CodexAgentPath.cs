namespace tryAGI.Codex;

/// <summary>Rust MultiAgent V2 absolute and relative path grammar. A path is a name, not a filesystem location.</summary>
public static class CodexAgentPath
{
    /// <summary>Root of an ordinary named agent tree.</summary>
    public const string Root = "/root";

    /// <summary>Reserved standalone Morpheus path.</summary>
    public const string Morpheus = "/morpheus";

    private const int MaximumPathLength = 4096;

    /// <summary>Validate and return the exact absolute path, without case folding or normalization.</summary>
    public static string Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path is Root or Morpheus) return path;
        if (path.Length > MaximumPathLength || !path.StartsWith(Root + "/", StringComparison.Ordinal))
            throw new ArgumentException("Invalid absolute agent path.", nameof(path));
        ValidateRelative(path[(Root.Length + 1)..]);
        return path;
    }

    /// <summary>Return the final name segment of a valid path.</summary>
    public static string Name(string path)
    {
        Parse(path);
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>Append one valid child name. The root segment is reserved.</summary>
    public static string Join(string parent, string name)
    {
        Parse(parent);
        ArgumentNullException.ThrowIfNull(name);
        ValidateName(name);
        return Parse(parent + "/" + name);
    }

    /// <summary>Resolve an absolute reference or a descendant relative to the current agent.
    /// Dot segments, parent traversal, upper-case letters and empty segments are rejected.</summary>
    public static string Resolve(string current, string reference)
    {
        Parse(current);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.StartsWith('/')) return Parse(reference);
        ValidateRelative(reference);
        if (reference.Length > MaximumPathLength - current.Length - 1)
            throw new ArgumentException("Agent path exceeds capacity.", nameof(reference));
        return Parse(current + "/" + reference);
    }

    private static void ValidateRelative(string reference)
    {
        if (reference.Length > MaximumPathLength)
            throw new ArgumentException("Agent path exceeds capacity.", nameof(reference));
        foreach (var segment in reference.Split('/')) ValidateName(segment);
    }

    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name == "root" || name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("Agent names must use lower-case ASCII letters, digits and underscores; root is reserved.", nameof(name));
    }
}
