using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Trusted marketplace/catalog metadata, not a plugin installation or an execution grant.</summary>
public sealed record CodexPluginDiscoveryCandidate(string Id, string Name, string? Description,
    string? RemotePluginId, bool Installed, bool InstallAllowed, bool DisabledByAdmin, bool HasSkills,
    IReadOnlyList<string> McpServerNames, IReadOnlyList<string> AppConnectorIds);

/// <summary>Host-evaluated connector discovery eligibility; discovery does not connect an account.</summary>
public sealed record CodexConnectorDiscoveryCandidate(string Id, string Name, string? Description,
    string? InstallUrl, bool Accessible, bool InstallEligible);

/// <summary>Captured, host-resolved discoverability configuration and account catalog state.</summary>
public sealed record CodexPluginDiscoveryPolicy(bool PluginsEnabled, bool RemoteCatalogEnabled,
    bool UsesCodexBackend, IReadOnlyCollection<string> ConfiguredPluginIds, IReadOnlyCollection<string> DisabledIds,
    IReadOnlyCollection<string> InstalledRemotePluginIds, IReadOnlyCollection<string> LoadedAppConnectorIds,
    string? ClientName = null);

/// <summary>Tool suggestion category.</summary>
public enum CodexPluginSuggestionKind
{
    /// <summary>Connector suggestion.</summary>
    Connector,
    /// <summary>Plugin suggestion.</summary>
    Plugin,
}

/// <summary>One discoverable install suggestion. No permission is implied by its presence.</summary>
public sealed record CodexPluginSuggestion(CodexPluginSuggestionKind Kind, string Id, string Name,
    string? Description, string? RemotePluginId, string? InstallUrl, bool HasSkills,
    IReadOnlyList<string> McpServerNames, IReadOnlyList<string> AppConnectorIds);

/// <summary>One explicit form request for a host to present. Confirmation is not proof of installation.</summary>
public sealed record CodexPluginInstallSuggestion(CodexPluginSuggestion Tool, string Reason, string SuggestionId,
    JsonElement Elicitation);

/// <summary>Read-only tool discovery and install suggestion contracts. The SDK never installs,
/// enables, authorizes or opens a plugin or connector from discovery or elicitation.</summary>
public sealed class CodexPluginSuggestionCatalog
{
    private static readonly HashSet<string> CuratedNames = new(StringComparer.Ordinal)
    {
        "github", "notion", "slack", "gmail", "google-calendar", "google-drive", "openai-developers",
        "canva", "teams", "sharepoint", "outlook-email", "outlook-calendar", "linear", "figma",
    };

    private CodexPluginSuggestionCatalog(CodexPluginSuggestion[] suggestions) => Suggestions = Array.AsReadOnly(suggestions);

    /// <summary>Immutable discovery suggestions from the captured host catalogs.</summary>
    public IReadOnlyList<CodexPluginSuggestion> Suggestions { get; }

    /// <summary>Filters trusted local/remote plugin candidates using pinned Rust discovery rules.
    /// Connectors must already be deemed install-eligible by the host's connector policy.
    /// Duplicate suggestion identities are rejected as ambiguous.</summary>
    public static CodexPluginSuggestionCatalog Discover(CodexPluginDiscoveryPolicy policy,
        IEnumerable<CodexPluginDiscoveryCandidate> plugins, IEnumerable<CodexConnectorDiscoveryCandidate> connectors)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(connectors);
        var configured = policy.ConfiguredPluginIds.ToHashSet(StringComparer.Ordinal);
        var disabled = policy.DisabledIds.ToHashSet(StringComparer.Ordinal);
        var installed = policy.InstalledRemotePluginIds.ToHashSet(StringComparer.Ordinal);
        var loadedConnectors = policy.LoadedAppConnectorIds.ToHashSet(StringComparer.Ordinal);
        var results = new List<CodexPluginSuggestion>();
        if (policy.PluginsEnabled && policy.ClientName != "codex-tui")
        {
            foreach (var plugin in plugins)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(plugin.Id);
                ArgumentException.ThrowIfNullOrWhiteSpace(plugin.Name);
                if (plugin.Installed || !plugin.InstallAllowed || plugin.DisabledByAdmin || disabled.Contains(plugin.Id)) continue;
                var selected = configured.Contains(plugin.Id) || IsFallback(plugin.Id);
                if (plugin.RemotePluginId is { } remote)
                {
                    if (!policy.RemoteCatalogEnabled || !policy.UsesCodexBackend || installed.Contains(remote) ||
                        disabled.Contains(remote)) continue;
                    selected |= configured.Contains(remote) || plugin.AppConnectorIds.Any(loadedConnectors.Contains);
                }
                if (!selected) continue;
                results.Add(new(CodexPluginSuggestionKind.Plugin, plugin.Id, plugin.Name, plugin.Description,
                    plugin.RemotePluginId, null, plugin.HasSkills, Array.AsReadOnly(plugin.McpServerNames.ToArray()),
                    Array.AsReadOnly(plugin.AppConnectorIds.ToArray())));
            }
        }
        foreach (var connector in connectors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connector.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(connector.Name);
            if (connector.Accessible || !connector.InstallEligible || disabled.Contains(connector.Id)) continue;
            results.Add(new(CodexPluginSuggestionKind.Connector, connector.Id, connector.Name, connector.Description,
                null, connector.InstallUrl, false, [], []));
        }
        if (results.GroupBy(item => (item.Kind, item.Id)).Any(group => group.Count() > 1))
            throw new CodexProtocolException("Ambiguous plugin or connector suggestion identity.");
        return new(results.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Prepares the pinned install-only form for a currently discoverable tool.
    /// The host supplies the suggestion ID, obtains explicit native consent and revalidates
    /// authority before any subsequent side effect. This method has no side effects.</summary>
    public CodexPluginInstallSuggestion PrepareInstall(CodexPluginSuggestionKind kind, string id,
        string action, string reason, string suggestionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionId);
        if (action != "install") throw new CodexProtocolException("Plugin install requests currently support only install.");
        var tool = Suggestions.SingleOrDefault(item => item.Kind == kind && item.Id == id)
            ?? throw new UnauthorizedAccessException("Requested plugin or connector is not discoverable.");
        var meta = new JsonObject
        {
            ["codex_approval_kind"] = "tool_suggestion", ["persist"] = "always",
            ["tool_type"] = kind == CodexPluginSuggestionKind.Plugin ? "plugin" : "connector",
            ["suggest_type"] = "install", ["suggest_reason"] = reason, ["tool_id"] = tool.Id, ["tool_name"] = tool.Name,
        };
        if (kind == CodexPluginSuggestionKind.Plugin)
        {
            meta["suggestion_id"] = suggestionId;
            if (tool.RemotePluginId is not null) meta["remote_plugin_id"] = tool.RemotePluginId;
            meta["app_connector_ids"] = new JsonArray(tool.AppConnectorIds.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
        else if (tool.InstallUrl is not null) meta["install_url"] = tool.InstallUrl;
        var form = new JsonObject
        {
            ["mode"] = "form", ["message"] = reason, ["_meta"] = meta,
            ["requestedSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        };
        using var document = JsonDocument.Parse(form.ToJsonString());
        return new(tool, reason, suggestionId, document.RootElement.Clone());
    }

    /// <summary>Verifies completion from fresh trusted host inventory, not from confirmation alone.
    /// A remote plugin without connector dependencies still requires installed-plugin evidence.</summary>
    public bool VerifyInstall(CodexPluginInstallSuggestion suggestion, bool userConfirmed,
        IReadOnlyCollection<string> installedPluginIds, IReadOnlyCollection<string> accessibleConnectorIds)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(installedPluginIds);
        ArgumentNullException.ThrowIfNull(accessibleConnectorIds);
        var tool = suggestion.Tool;
        if (!userConfirmed || !Suggestions.Contains(tool)) return false;
        var accessible = accessibleConnectorIds.ToHashSet(StringComparer.Ordinal);
        if (tool.Kind == CodexPluginSuggestionKind.Connector) return accessible.Contains(tool.Id);
        if (tool.RemotePluginId is not null && tool.AppConnectorIds.Count != 0)
            return tool.AppConnectorIds.All(accessible.Contains);
        return installedPluginIds.Contains(tool.RemotePluginId ?? tool.Id, StringComparer.Ordinal);
    }

    private static bool IsFallback(string id)
    {
        var separator = id.LastIndexOf('@');
        if (separator <= 0 || separator == id.Length - 1) return false;
        var name = id[..separator];
        var marketplace = id[(separator + 1)..];
        return marketplace switch
        {
            "openai-curated" or "openai-api-curated" => CuratedNames.Contains(name),
            "openai-curated-remote" => CuratedNames.Contains(name),
            "openai-bundled" => name is "chrome" or "computer-use",
            _ => false,
        };
    }
}
