using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Skill discovery source scope, matching the App Server skill inventory.</summary>
public enum CodexSkillScope
{
    /// <summary>Repository-local skill.</summary>
    Repo,
    /// <summary>User configuration skill.</summary>
    User,
    /// <summary>Administrator-provided skill.</summary>
    Admin,
    /// <summary>Bundled system skill.</summary>
    System,
}

/// <summary>Canonical path identity on one host-resolved filesystem. Paths are opaque:
/// the filesystem adapter, not the SDK's local OS, determines their canonical spelling.</summary>
public sealed record CodexSkillPath(string FileSystemId, string Path);

/// <summary>One ordered, host-authorized discovery root. A disabled root is never loaded.</summary>
public sealed record CodexSkillRoot(CodexSkillPath Identity, CodexSkillScope Scope,
    string? PluginId = null, string? RemotePluginId = null, bool Enabled = true);

/// <summary>Exact filesystem and policy authority captured for a skill discovery step.
/// AccessKey and ConfigurationKey must change when grants or configuration change;
/// they are host-generated identities, not credentials or model-supplied values.</summary>
public sealed record CodexSkillDiscoveryContext(string AccountId, string ConnectionId, string WorkingDirectory,
    string AccessKey, string ConfigurationKey, string? ThreadId = null, string? EnvironmentId = null);

/// <summary>Validated metadata and canonical/logical paths returned by one root loader.
/// The host resolves path identity and optional YAML/interface/dependency metadata.</summary>
public sealed record CodexSkillDocument(string Name, string Description, string CanonicalPath,
    string? DiscoveryPath = null, string? ShortDescription = null, bool AllowImplicitInvocation = true,
    JsonElement? Metadata = null);

/// <summary>One discovered skill and its owning root. A logical alias never changes its authority.</summary>
public sealed record CodexSkillEntry(CodexSkillRoot Root, CodexSkillDocument Document)
{
    /// <summary>Canonical identity including its filesystem owner.</summary>
    public CodexSkillPath Identity => new(Root.Identity.FileSystemId, Document.CanonicalPath);
}

/// <summary>Sanitized discovery error for an exact source.</summary>
public sealed record CodexSkillDiscoveryError(CodexSkillPath Source, string Message);

/// <summary>Snapshot returned by a host-authorized root loader.</summary>
public sealed record CodexSkillRootInventory(IReadOnlyList<CodexSkillDocument> Documents,
    IReadOnlyList<CodexSkillDiscoveryError> Errors);

/// <summary>Exact context and root presented to the filesystem adapter.</summary>
public sealed record CodexSkillLoadRequest(CodexSkillDiscoveryContext Context, CodexSkillRoot Root);

/// <summary>Exact selected document presented to an authorized instruction reader.</summary>
public sealed record CodexSkillReadRequest(CodexSkillDiscoveryContext Context, CodexSkillEntry Skill);

/// <summary>Instruction text and source identity. Reading instructions does not execute scripts or install dependencies.</summary>
public sealed record CodexSkillInstruction(CodexSkillEntry Skill, string Contents);

/// <summary>Resolved instruction fragments and read failures, in selection order.</summary>
public sealed record CodexSkillInstructions(IReadOnlyList<CodexSkillInstruction> Items,
    IReadOnlyList<CodexSkillDiscoveryError> Errors);

/// <summary>Immutable, grant-bound skill inventory. Selects structured paths first,
/// then linked paths and unambiguous plain names, preserving Rust discovery order.</summary>
public sealed class CodexSkillSnapshot
{
    private readonly HashSet<CodexSkillPath> disabled;

    internal CodexSkillSnapshot(CodexSkillDiscoveryContext context, CodexSkillRoot[] roots,
        HashSet<CodexSkillPath> disabled, CodexSkillEntry[] entries, CodexSkillDiscoveryError[] errors)
    {
        Context = context;
        Roots = Array.AsReadOnly(roots);
        this.disabled = disabled;
        Entries = Array.AsReadOnly(entries);
        Errors = Array.AsReadOnly(errors);
    }

    /// <summary>Host authority used to produce the snapshot.</summary>
    public CodexSkillDiscoveryContext Context { get; }
    /// <summary>Ordered configured roots, including disabled roots.</summary>
    public IReadOnlyList<CodexSkillRoot> Roots { get; }
    /// <summary>Discovered canonical skill entries, in root order.</summary>
    public IReadOnlyList<CodexSkillEntry> Entries { get; }
    /// <summary>Discovery failures; unavailable roots never receive substitute authority.</summary>
    public IReadOnlyList<CodexSkillDiscoveryError> Errors { get; }

    /// <summary>Whether this exact entry belongs to the snapshot and is enabled.</summary>
    public bool IsEnabled(CodexSkillEntry entry) => Entries.Contains(entry) && !disabled.Contains(entry.Identity);

    internal bool Matches(CodexSkillDiscoveryContext context, CodexSkillRoot[] roots, HashSet<CodexSkillPath> disabledPaths) =>
        Context == context && Roots.SequenceEqual(roots) && disabled.SetEquals(disabledPaths);

    /// <summary>Collects explicit skill selections. The optional host path resolver maps
    /// native paths or file URIs to the exact discovered identity; it must not read files.
    /// A path selecting more than one filesystem is rejected as ambiguous.</summary>
    public IReadOnlyList<CodexSkillEntry> Select(IReadOnlyList<CodexUserInput> inputs,
        IReadOnlyDictionary<string, int>? connectorSlugCounts = null,
        Func<string, CodexSkillPath?>? resolvePath = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var selected = new List<CodexSkillEntry>();
        var seenPaths = new HashSet<CodexSkillPath>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var blockedNames = new HashSet<string>(StringComparer.Ordinal);
        var enabled = Entries.Where(IsEnabled).ToArray();
        var nameCounts = enabled.GroupBy(entry => entry.Document.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        void Add(CodexSkillEntry entry)
        {
            if (!seenPaths.Add(entry.Identity)) return;
            seenNames.Add(entry.Document.Name);
            selected.Add(entry);
        }

        CodexSkillEntry? FindPath(string raw)
        {
            if (raw.StartsWith("skill://", StringComparison.Ordinal)) raw = raw[8..];
            if (IsOtherToolPath(raw)) return null;
            var resolved = resolvePath?.Invoke(raw);
            if (resolvePath is not null && resolved is null) return null;
            var matches = Entries.Where(entry => resolved is not null
                ? entry.Identity == resolved || entry.Root.Identity.FileSystemId == resolved.FileSystemId &&
                    entry.Document.DiscoveryPath == resolved.Path
                : entry.Document.CanonicalPath == raw || entry.Document.DiscoveryPath == raw).ToArray();
            return matches.Length == 1 && IsEnabled(matches[0]) ? matches[0] : null;
        }

        foreach (var skill in inputs.OfType<CodexSkillInput>())
        {
            blockedNames.Add(skill.Name);
            if (FindPath(skill.Path) is { } entry) Add(entry);
        }
        foreach (var input in inputs.OfType<CodexTextInput>())
        {
            var mentions = ExtractMentions(input.Text);
            var paths = mentions.Paths.Select(FindPath).Where(entry => entry is not null)
                .Select(entry => entry!.Identity).ToHashSet();
            foreach (var entry in enabled)
                if (paths.Contains(entry.Identity)) Add(entry);
            foreach (var entry in enabled)
            {
                var name = entry.Document.Name;
                if (!mentions.Names.Contains(name) || blockedNames.Contains(name) ||
                    nameCounts[name] != 1 || seenNames.Contains(name) ||
                    connectorSlugCounts?.GetValueOrDefault(name.ToLowerInvariant()) > 0) continue;
                Add(entry);
            }
        }
        return selected.AsReadOnly();
    }

    /// <summary>Identifies an already observed authorized read of a skill document.
    /// A disabled skill or one forbidding implicit invocation is never selected.</summary>
    public CodexSkillEntry? FindImplicitDocument(CodexSkillPath path) =>
        Entries.FirstOrDefault(entry => entry.Identity == path && IsEnabled(entry) && entry.Document.AllowImplicitInvocation);

    private static bool IsOtherToolPath(string path) => path.StartsWith("app://", StringComparison.Ordinal) ||
        path.StartsWith("mcp://", StringComparison.Ordinal) || path.StartsWith("plugin://", StringComparison.Ordinal);

    private static bool NameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ':';

    private static bool CommonEnvironmentVariable(string name) => name.ToUpperInvariant() is
        "PATH" or "HOME" or "USER" or "SHELL" or "PWD" or "TMPDIR" or "TEMP" or "TMP" or "LANG" or "TERM" or "XDG_CONFIG_HOME";

    private static (HashSet<string> Names, HashSet<string> Paths) ExtractMentions(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < text.Length;)
        {
            var linked = text[index] == '[' && index + 1 < text.Length && text[index + 1] == '$';
            var sigil = linked ? index + 1 : index;
            if (text[sigil] != '$' || sigil + 1 == text.Length || !NameCharacter(text[sigil + 1]))
            { index++; continue; }
            var end = sigil + 1;
            while (end < text.Length && NameCharacter(text[end])) end++;
            var name = text[(sigil + 1)..end];
            if (linked && end < text.Length && text[end] == ']')
            {
                var open = end + 1;
                while (open < text.Length && char.IsWhiteSpace(text[open])) open++;
                if (open < text.Length && text[open] == '(')
                {
                    var close = text.IndexOf(')', open + 1);
                    if (close >= 0)
                    {
                        var path = text[(open + 1)..close].Trim();
                        if (path.Length > 0)
                        {
                            if (!CommonEnvironmentVariable(name)) paths.Add(path);
                            index = close + 1;
                            continue;
                        }
                    }
                }
            }
            if (!CommonEnvironmentVariable(name)) names.Add(name);
            index = end;
        }
        return (names, paths);
    }
}

/// <summary>One step-scoped discovery cache with explicit host filesystem adapters.
/// Changing context, configured roots, disabled identities, or invalidating the cache
/// prevents stale discovery or instruction reads from becoming model context.</summary>
public sealed class CodexSkillCatalog
{
    private readonly object gate = new();
    private readonly SemaphoreSlim loading = new(1, 1);
    private CodexSkillSnapshot? current;
    private PendingScope? desired;
    private long generation;

    private sealed record PendingScope(CodexSkillDiscoveryContext Context, CodexSkillRoot[] Roots, HashSet<CodexSkillPath> Disabled)
    {
        internal bool Matches(CodexSkillDiscoveryContext context, CodexSkillRoot[] roots, HashSet<CodexSkillPath> disabled) =>
            Context == context && Roots.SequenceEqual(roots) && Disabled.SetEquals(disabled);
    }

    /// <summary>Loads enabled roots in configuration order. The loader must enforce the
    /// exact captured filesystem grants, canonicalize paths, and return only documents
    /// within the authorized root (including symlink policy).</summary>
    public async Task<CodexSkillSnapshot> DiscoverAsync(CodexSkillDiscoveryContext context,
        IReadOnlyList<CodexSkillRoot> roots, IReadOnlyCollection<CodexSkillPath> disabledPaths,
        Func<CodexSkillLoadRequest, CancellationToken, Task<CodexSkillRootInventory>> load,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(disabledPaths);
        ArgumentNullException.ThrowIfNull(load);
        var rootSnapshot = roots.ToArray();
        var disabled = disabledPaths.ToHashSet();
        foreach (var root in rootSnapshot)
        {
            ArgumentNullException.ThrowIfNull(root);
            ValidatePath(root.Identity);
            if (!Enum.IsDefined(root.Scope)) throw new ArgumentException("Unknown skill scope.", nameof(roots));
        }
        foreach (var path in disabled) ValidatePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        long epoch;
        lock (gate)
        {
            if (current is { } cached && cached.Matches(context, rootSnapshot, disabled)) return cached;
            if (desired is null || !desired.Matches(context, rootSnapshot, disabled))
            {
                // Fence old authority before waiting for an earlier scan to settle.
                desired = new(context, rootSnapshot, disabled);
                current = null;
                generation++;
            }
            epoch = generation;
        }
        await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (generation != epoch) throw new InvalidOperationException("Skill discovery authority changed before loading.");
                if (current is { } cached && cached.Matches(context, rootSnapshot, disabled)) return cached;
            }

            var entries = new List<CodexSkillEntry>();
            var errors = new List<CodexSkillDiscoveryError>();
            var seenRoots = new HashSet<CodexSkillPath>();
            var seenDocuments = new HashSet<CodexSkillPath>();
            foreach (var root in rootSnapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!root.Enabled || !seenRoots.Add(root.Identity)) continue;
                CodexSkillRootInventory inventory;
                try
                {
                    inventory = await load(new(context, root), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    errors.Add(new(root.Identity, "Skill root was not readable with the captured authority."));
                    continue;
                }
                foreach (var error in inventory.Errors)
                    errors.Add(error);
                foreach (var document in inventory.Documents)
                {
                    if (document is null || string.IsNullOrWhiteSpace(document.CanonicalPath) ||
                        string.IsNullOrWhiteSpace(document.Name) || string.IsNullOrWhiteSpace(document.Description))
                    {
                        errors.Add(new(root.Identity, "Skill metadata was missing required fields."));
                        continue;
                    }
                    var sanitized = document with
                    {
                        Name = CodexSkillFrontmatter.SingleLine(document.Name),
                        Description = CodexSkillFrontmatter.SingleLine(document.Description),
                        ShortDescription = document.ShortDescription is { } shortDescription
                            ? CodexSkillFrontmatter.SingleLine(shortDescription) : null,
                        Metadata = document.Metadata?.Clone(),
                    };
                    if (!CodexSkillFrontmatter.ValidName(sanitized.Name))
                    {
                        errors.Add(new(new(root.Identity.FileSystemId, document.CanonicalPath), "Skill name exceeds 64 characters."));
                        continue;
                    }
                    var entry = new CodexSkillEntry(root, sanitized);
                    if (seenDocuments.Add(entry.Identity)) entries.Add(entry);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new CodexSkillSnapshot(context, rootSnapshot, disabled, entries.ToArray(), errors.ToArray());
            lock (gate)
            {
                if (generation != epoch) throw new InvalidOperationException("Skill discovery authority changed during loading.");
                current = result;
                return result;
            }
        }
        finally { loading.Release(); }
    }

    /// <summary>Invalidates the current snapshot and any in-flight discovery or instruction read.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            current = null;
            desired = null;
        }
    }

    /// <summary>Reads selected instructions only from the current matching snapshot.
    /// The host reader must recheck current grants before each read. No filesystem
    /// or instruction authority is inferred from a model-supplied path.</summary>
    public async Task<CodexSkillInstructions> ReadInstructionsAsync(CodexSkillDiscoveryContext context,
        CodexSkillSnapshot snapshot, IReadOnlyList<CodexSkillEntry> selected,
        Func<CodexSkillReadRequest, CancellationToken, Task<string>> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(read);
        var selection = selected.ToArray();
        EnsureCurrent();
        if (selection.Any(entry => !snapshot.IsEnabled(entry)))
            throw new UnauthorizedAccessException("A selected skill is not enabled in the current snapshot.");
        var instructions = new List<CodexSkillInstruction>();
        var errors = new List<CodexSkillDiscoveryError>();
        var seen = new HashSet<CodexSkillPath>();
        foreach (var skill in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent();
            if (!seen.Add(skill.Identity)) continue;
            try
            {
                var contents = await read(new(context, skill), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCurrent();
                instructions.Add(new(skill, contents));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                EnsureCurrent();
                errors.Add(new(skill.Identity, "Skill instructions could not be read with current authority."));
            }
        }
        EnsureCurrent();
        return new(instructions.AsReadOnly(), errors.AsReadOnly());

        void EnsureCurrent()
        {
            lock (gate)
                if (snapshot.Context != context || !ReferenceEquals(current, snapshot))
                    throw new InvalidOperationException("Skill instruction snapshot is stale.");
        }
    }

    private static void ValidatePath(CodexSkillPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(path.FileSystemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path.Path);
    }

    private static void ValidateContext(CodexSkillDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ConnectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.AccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ConfigurationKey);
    }
}

/// <summary>Validated frontmatter metadata independent of a YAML implementation.</summary>
public sealed record CodexSkillFrontmatterMetadata(string Name, string Description, string? ShortDescription);

/// <summary>Extracts Rust-compatible SKILL.md frontmatter and validates normalized metadata.
/// A host-provided non-executing YAML decoder must parse the extracted YAML to a JSON object.</summary>
public static class CodexSkillFrontmatter
{
    /// <summary>Extracts a leading delimited block, then validates decoded scalar metadata.
    /// Missing names fall back to the canonical directory name; description is required.</summary>
    public static CodexSkillFrontmatterMetadata Parse(string contents, string defaultName,
        Func<string, JsonElement> decodeYaml)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(defaultName);
        ArgumentNullException.ThrowIfNull(decodeYaml);
        using var reader = new StringReader(contents);
        if (reader.ReadLine()?.Trim() != "---") throw new FormatException("Missing skill YAML frontmatter.");
        var lines = new List<string>();
        var closed = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim() == "---") { closed = true; break; }
            lines.Add(line);
        }
        if (!closed || lines.Count == 0) throw new FormatException("Missing skill YAML frontmatter.");
        var parsed = decodeYaml(string.Join("\n", lines));
        if (parsed.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid skill frontmatter metadata.");
        var name = OptionalScalar(parsed, "name");
        if (string.IsNullOrEmpty(name)) name = SingleLine(defaultName);
        var description = OptionalScalar(parsed, "description");
        if (!ValidName(name)) throw new FormatException("Invalid skill name.");
        if (string.IsNullOrEmpty(description)) throw new FormatException("Skill description is required.");
        string? shortDescription = null;
        if (parsed.TryGetProperty("metadata", out var metadata))
        {
            if (metadata.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid skill metadata.");
            shortDescription = OptionalScalar(metadata, "short-description");
        }
        return new(name, description, string.IsNullOrEmpty(shortDescription) ? null : shortDescription);
    }

    internal static string SingleLine(string value) => string.Join(" ", value.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries));

    internal static bool ValidName(string name)
    {
        var length = 0;
        foreach (var rune in name.EnumerateRunes()) if (++length > 64) return false;
        return length != 0;
    }

    private static string? OptionalScalar(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.Null) return null;
        if (member.ValueKind != JsonValueKind.String) throw new FormatException("Invalid skill frontmatter scalar.");
        return SingleLine(member.GetString()!);
    }
}
