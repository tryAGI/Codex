using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Path convention of the host owning a plugin package.</summary>
public enum CodexPluginPathConvention
{
    /// <summary>POSIX resource paths.</summary>
    Posix,
    /// <summary>Windows resource paths.</summary>
    Windows,
}

/// <summary>Authority-bound relative resource locator. The filesystem adapter must verify
/// canonical containment (including symlinks) before accessing it. No file is opened by parsing.</summary>
public sealed record CodexPluginResource(CodexSkillPath Root, string RelativePath);

/// <summary>Optional MCP configuration declared inline or in a package file.</summary>
public sealed record CodexPluginMcpManifest(CodexPluginResource? Resource, JsonElement? Inline);

/// <summary>Optional hook declarations. Inline values are data, not executable permissions.</summary>
public sealed record CodexPluginHookManifest(IReadOnlyList<CodexPluginResource> Resources,
    IReadOnlyList<JsonElement> Inline);

/// <summary>Model/UI-facing manifest fields and normalized resource locators.</summary>
public sealed record CodexPluginInterface(string? DisplayName, string? ShortDescription,
    string? LongDescription, string? DeveloperName, string? Category, IReadOnlyList<string> Capabilities,
    string? WebsiteUrl, string? PrivacyPolicyUrl, string? TermsOfServiceUrl,
    IReadOnlyList<string> DefaultPrompts, string? BrandColor, CodexPluginResource? ComposerIcon,
    CodexPluginResource? Logo, CodexPluginResource? LogoDark, IReadOnlyList<CodexPluginResource> Screenshots);

/// <summary>Parsed legacy plugin manifest with the original wire JSON and component resource locators.
/// Loading, executing, enabling and installing are separate explicitly authorized host operations.</summary>
public sealed record CodexPluginManifest(CodexSkillPath Root, string Name, string? Version, string? Description,
    IReadOnlyList<string> Keywords, IReadOnlyList<CodexPluginResource> Skills, CodexPluginResource? OnboardingSkill,
    CodexPluginMcpManifest? McpServers, CodexPluginResource? Apps, CodexPluginHookManifest? Hooks,
    CodexPluginInterface? Interface, JsonElement Raw, IReadOnlyList<string> Warnings)
{
    /// <summary>Display name falls back to the manifest name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Interface?.DisplayName) ? Name : Interface.DisplayName.Trim();

    /// <summary>Redacted representation; inline configuration may contain sensitive values.</summary>
    public override string ToString() => $"Plugin manifest {Name}";

    /// <summary>Parses legacy plugin JSON without filesystem, network or execution effects.
    /// Invalid optional resource paths are omitted and reported rather than escaping their root.
    /// The newer agent-plugin package format requires a separate loader.</summary>
    public static CodexPluginManifest Parse(CodexSkillPath root, string defaultName, JsonElement raw,
        CodexPluginPathConvention convention = CodexPluginPathConvention.Posix)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(root.FileSystemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(root.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultName);
        if (!Enum.IsDefined(convention)) throw new ArgumentOutOfRangeException(nameof(convention));
        RequireObject(raw);
        var warnings = new List<string>();
        var name = OptionalString(raw, "name");
        if (string.IsNullOrWhiteSpace(name)) name = defaultName;
        var version = OptionalString(raw, "version")?.Trim();
        if (string.IsNullOrEmpty(version)) version = null;

        CodexPluginResource? Resource(string field, JsonElement value, bool allowUnprefixed = false)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind != JsonValueKind.String)
            {
                warnings.Add(field + ": expected a relative resource path.");
                return null;
            }
            var path = value.GetString()!;
            if (allowUnprefixed && !path.StartsWith("./", StringComparison.Ordinal)) path = "./" + path;
            if (!path.StartsWith("./", StringComparison.Ordinal) || path.Length == 2)
            {
                warnings.Add(field + ": expected a nonempty ./ path.");
                return null;
            }
            var relative = path[2..];
            if (relative.StartsWith('/') || relative.StartsWith('\\') || relative.Contains('\0') ||
                relative.Contains(':') || convention == CodexPluginPathConvention.Posix && relative.Contains('\\'))
            {
                warnings.Add(field + ": path is rooted or ambiguous.");
                return null;
            }
            var segments = relative.Split(convention == CodexPluginPathConvention.Windows ? ['/', '\\'] : ['/']);
            if (segments.Any(segment => segment == ".." ||
                convention == CodexPluginPathConvention.Windows && segment != "." &&
                (segment.EndsWith(' ') || segment.EndsWith('.'))))
            {
                warnings.Add(field + ": parent traversal or ambiguous Windows path component is not allowed.");
                return null;
            }
            var normalized = string.Join("/", segments.Where(segment => segment.Length > 0 && segment != "."));
            if (normalized.Length == 0)
            {
                warnings.Add(field + ": path must identify a resource.");
                return null;
            }
            return new(root, normalized);
        }

        IReadOnlyList<CodexPluginResource> Resources(string field, JsonElement value)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
            if (value.ValueKind == JsonValueKind.String)
                return Resource(field, value) is { } resource ? Array.AsReadOnly(new[] { resource }) : [];
            if (value.ValueKind != JsonValueKind.Array ||
                value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                warnings.Add(field + ": expected a path or path array.");
                return [];
            }
            return Array.AsReadOnly(value.EnumerateArray().Select(item => Resource(field, item))
                .OfType<CodexPluginResource>().ToArray());
        }

        CodexPluginMcpManifest? mcp = null;
        var rawMcp = Property(raw, "mcpServers");
        if (rawMcp.ValueKind == JsonValueKind.Object)
        {
            RequireObject(rawMcp);
            mcp = new(null, rawMcp.Clone());
        }
        else if (rawMcp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
            Resource("mcpServers", rawMcp) is { } mcpResource) mcp = new(mcpResource, null);

        CodexPluginHookManifest? hooks = null;
        var rawHooks = Property(raw, "hooks");
        if (rawHooks.ValueKind == JsonValueKind.Object)
        {
            RequireObject(rawHooks);
            hooks = new([], Array.AsReadOnly(new[] { rawHooks.Clone() }));
        }
        else if (rawHooks.ValueKind == JsonValueKind.Array &&
            rawHooks.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object))
        {
            var inline = rawHooks.EnumerateArray().ToArray();
            foreach (var item in inline) RequireObject(item);
            if (inline.Length != 0) hooks = new([], Array.AsReadOnly(inline.Select(item => item.Clone()).ToArray()));
        }
        else if (Resources("hooks", rawHooks) is { Count: > 0 } hookResources)
            hooks = new(hookResources, []);

        CodexPluginResource? onboarding = null;
        var extensions = Property(raw, "extensions");
        if (extensions.ValueKind == JsonValueKind.Object)
        {
            RequireObject(extensions);
            var openAi = Property(extensions, "com.openai");
            if (openAi.ValueKind == JsonValueKind.Object)
            {
                RequireObject(openAi);
                onboarding = Resource("extensions.com.openai.onboardingSkill", Property(openAi, "onboardingSkill"), true);
            }
        }

        CodexPluginInterface? pluginInterface = null;
        var ui = Property(raw, "interface");
        if (ui.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            RequireObject(ui);
            var prompts = new List<string>();
            var rawPrompts = Property(ui, "defaultPrompt");
            if (rawPrompts.ValueKind == JsonValueKind.String) AddPrompt(rawPrompts);
            else if (rawPrompts.ValueKind == JsonValueKind.Array)
            {
                foreach (var prompt in rawPrompts.EnumerateArray())
                {
                    if (prompts.Count == 3)
                    {
                        warnings.Add("interface.defaultPrompt: at most three valid prompts are supported.");
                        break;
                    }
                    AddPrompt(prompt);
                }
            }
            else if (rawPrompts.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                warnings.Add("interface.defaultPrompt: expected a string or array.");

            void AddPrompt(JsonElement prompt)
            {
                if (prompt.ValueKind != JsonValueKind.String)
                {
                    warnings.Add("interface.defaultPrompt: expected a string.");
                    return;
                }
                var normalized = CodexSkillFrontmatter.SingleLine(prompt.GetString()!);
                var count = 0;
                foreach (var rune in normalized.EnumerateRunes()) count++;
                if (count is < 1 or > 512)
                {
                    warnings.Add("interface.defaultPrompt: expected one to 512 characters.");
                    return;
                }
                prompts.Add(normalized);
            }

            pluginInterface = new(OptionalString(ui, "displayName"), OptionalString(ui, "shortDescription"),
                OptionalString(ui, "longDescription"), OptionalString(ui, "developerName"), OptionalString(ui, "category"),
                StringList(ui, "capabilities"), AliasedString(ui, "websiteUrl", "websiteURL"),
                AliasedString(ui, "privacyPolicyUrl", "privacyPolicyURL"),
                AliasedString(ui, "termsOfServiceUrl", "termsOfServiceURL"), prompts.AsReadOnly(),
                OptionalString(ui, "brandColor"), Resource("interface.composerIcon", Property(ui, "composerIcon")),
                Resource("interface.logo", Property(ui, "logo")), Resource("interface.logoDark", Property(ui, "logoDark")),
                Resources("interface.screenshots", Property(ui, "screenshots")));
        }

        return new(root, name, version, OptionalString(raw, "description"), StringList(raw, "keywords"),
            Resources("skills", Property(raw, "skills")), onboarding, mcp, Resource("apps", Property(raw, "apps")),
            hooks, pluginInterface, raw.Clone(), warnings.AsReadOnly());
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Plugin manifest must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new CodexProtocolException("Plugin manifest has duplicate members.");
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.TryGetProperty(name, out var member) ? member : default;

    private static string? OptionalString(JsonElement value, string name)
    {
        var member = Property(value, name);
        if (member.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        return member.ValueKind == JsonValueKind.String ? member.GetString() :
            throw new CodexProtocolException($"Plugin manifest {name} must be text.");
    }

    private static string? AliasedString(JsonElement value, string name, string alias)
    {
        if (value.TryGetProperty(name, out _) && value.TryGetProperty(alias, out _))
            throw new CodexProtocolException($"Plugin manifest contains conflicting {name} aliases.");
        return OptionalString(value, name) ?? OptionalString(value, alias);
    }

    private static IReadOnlyList<string> StringList(JsonElement value, string name)
    {
        var member = Property(value, name);
        if (member.ValueKind == JsonValueKind.Undefined) return [];
        if (member.ValueKind != JsonValueKind.Array || member.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new CodexProtocolException($"Plugin manifest {name} must be a string array.");
        return Array.AsReadOnly(member.EnumerateArray().Select(item => item.GetString()!).ToArray());
    }
}
