using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Model-facing visibility of a registered tool; visibility is not execution authorization.</summary>
public enum CodexToolExposure
{
    /// <summary>Registered but not visible to any model surface.</summary>
    Hidden,
    /// <summary>Initially visible and eligible for Code Mode.</summary>
    Direct,
    /// <summary>Searchable and eligible for Code Mode.</summary>
    Deferred,
    /// <summary>Initially visible but not eligible for Code Mode.</summary>
    DirectModelOnly,
    /// <summary>Searchable but not eligible for Code Mode.</summary>
    DeferredModelOnly,
    /// <summary>Eligible only for nested Code Mode calls.</summary>
    CodeModeOnly,
}

/// <summary>Stable namespace and name of a local tool.</summary>
public sealed record CodexToolIdentity
{
    /// <summary>Creates an identity using the same default namespace as agent admission.</summary>
    public CodexToolIdentity(string? toolNamespace, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
        if (toolNamespace is not null && toolNamespace.Length > 0 && string.IsNullOrWhiteSpace(toolNamespace))
            throw new ArgumentException("Tool namespace must not be whitespace.", nameof(toolNamespace));
        Namespace = CodexToolCalls.NormalizeNamespace(toolNamespace);
        Name = name;
    }

    /// <summary>Canonical namespace, including functions for flat tools.</summary>
    public string Namespace { get; }
    /// <summary>Exact case-sensitive tool name.</summary>
    public string Name { get; }
}

/// <summary>One function/custom tool definition and its host-selected exposure.</summary>
public sealed record CodexRegisteredTool(
    CodexToolIdentity Identity, JsonElement Definition, CodexToolExposure Exposure,
    string? NamespaceDescription = null, string? SearchText = null);

/// <summary>Immutable catalog of host-registered tools. Searching it never executes a tool,
/// installs a plugin, or grants approval. Pass explicitly selected model specs to CodexAgent;
/// the execution callback remains responsible for authorization.</summary>
public sealed class CodexToolRegistry
{
    /// <summary>Default result limit from the pinned Rust tool_search contract.</summary>
    public const int DefaultSearchLimit = 8;

    private readonly Dictionary<CodexToolIdentity, CodexRegisteredTool> tools = [];

    /// <summary>Validates and snapshots definitions, rejecting ambiguous identities before exposure.</summary>
    public CodexToolRegistry(IEnumerable<CodexRegisteredTool> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var item in registrations)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Identity);
            if (!Enum.IsDefined(item.Exposure)) throw new ArgumentException("Unknown tool exposure.", nameof(registrations));
            if (item.Definition.ValueKind != JsonValueKind.Object ||
                CodexRealtimeProtocol.Text(item.Definition, "type") is not ("function" or "custom") ||
                CodexRealtimeProtocol.Text(item.Definition, "name") != item.Identity.Name)
                throw new ArgumentException("Tool definition must match its function/custom identity.", nameof(registrations));
            if (!tools.TryAdd(item.Identity, item with { Definition = item.Definition.Clone() }))
                throw new ArgumentException("Ambiguous duplicate registered tool identity.", nameof(registrations));
        }
    }

    /// <summary>All registrations in host-supplied order, including hidden tools.</summary>
    public IReadOnlyList<CodexRegisteredTool> Registrations => tools.Values.ToArray();

    /// <summary>Builds initial model specs plus deferred tools explicitly selected by the host.
    /// Discovery alone does not add tools to this list.</summary>
    public IReadOnlyList<JsonElement> CreateModelTools(IEnumerable<CodexToolIdentity>? selectedDeferred = null)
    {
        var selected = new HashSet<CodexToolIdentity>();
        foreach (var identity in selectedDeferred ?? [])
        {
            if (!tools.TryGetValue(identity, out var item) || !IsDeferred(item.Exposure))
                throw new ArgumentException("Selected tool is not a registered deferred tool.", nameof(selectedDeferred));
            selected.Add(identity);
        }
        var result = BuildSpecs(tools.Values.Where(item =>
            item.Exposure is CodexToolExposure.Direct or CodexToolExposure.DirectModelOnly ||
            selected.Contains(item.Identity)), search: false);
        // Reuse the exact local-call identity checks enforced by CodexAgent.
        _ = CodexToolCalls.Advertised(result, allowCustom: true);
        return result;
    }

    /// <summary>Ranks deferred entries by deterministic term relevance, excluding model-only
    /// entries when searching for nested Code Mode. Search results are not execution receipts.</summary>
    public IReadOnlyList<CodexRegisteredTool> Search(string query, int limit = DefaultSearchLimit, bool codeMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var terms = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        var candidates = tools.Values.Where(item => IsDeferred(item.Exposure) &&
            (!codeMode || item.Exposure == CodexToolExposure.Deferred))
            .Select(item => (Item: item, Words: Tokenize(item.SearchText ?? SearchText(item)).ToArray())).ToArray();
        if (terms.Length == 0 || candidates.Length == 0) return [];
        var averageLength = Math.Max(1, candidates.Average(x => x.Words.Length));
        var weights = terms.Select(term =>
        {
            var frequency = candidates.Count(x => x.Words.Contains(term, StringComparer.Ordinal));
            return Math.Log(1 + (candidates.Length - frequency + 0.5) / (frequency + 0.5));
        }).ToArray();
        return candidates.Select(candidate =>
        {
            var score = 0.0;
            for (var i = 0; i < terms.Length; i++)
            {
                var frequency = candidate.Words.Count(word => word == terms[i]);
                score += weights[i] * frequency * 2.2 /
                    (frequency + 1.2 * (0.25 + 0.75 * candidate.Words.Length / averageLength));
            }
            return (candidate.Item, Score: score);
        }).Where(x => x.Score > 0).OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Identity.Namespace, StringComparer.Ordinal)
            .ThenBy(x => x.Item.Identity.Name, StringComparer.Ordinal)
            .Take(limit).Select(x => x.Item).ToArray();
    }

    /// <summary>Produces Rust-shaped loadable namespace specs with deferred loading and
    /// coalescing, without changing registry visibility or admission.</summary>
    public IReadOnlyList<JsonElement> SearchModelTools(string query, int limit = DefaultSearchLimit) =>
        BuildSpecs(Search(query, limit), search: true);

    /// <summary>Catalog entries eligible for a separately authorized Code Mode adapter.</summary>
    public IReadOnlyList<CodexRegisteredTool> CodeModeTools() => tools.Values.Where(item =>
        item.Exposure is CodexToolExposure.Direct or CodexToolExposure.Deferred or CodexToolExposure.CodeModeOnly).ToArray();

    private static bool IsDeferred(CodexToolExposure exposure) =>
        exposure is CodexToolExposure.Deferred or CodexToolExposure.DeferredModelOnly;

    private static string SearchText(CodexRegisteredTool item) =>
        string.Join(" ", item.Identity.Namespace, item.Identity.Name, item.NamespaceDescription,
            CodexRealtimeProtocol.Text(item.Definition, "description"), item.Definition.GetRawText());

    private static IEnumerable<string> Tokenize(string value)
    {
        var start = -1;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && char.IsLetterOrDigit(value[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return value[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private static IReadOnlyList<JsonElement> BuildSpecs(IEnumerable<CodexRegisteredTool> entries, bool search)
    {
        var specs = new List<JsonNode>();
        var namespaces = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var definition = JsonNode.Parse(entry.Definition.GetRawText())!.AsObject();
            if (search)
            {
                definition["defer_loading"] = true;
                definition.Remove("output_schema");
            }
            else
            {
                // The caller explicitly selected this specification for the current turn.
                definition.Remove("defer_loading");
            }
            var name = entry.Identity.Namespace;
            if (!search && name == "functions")
            {
                specs.Add(definition);
                continue;
            }
            if (!namespaces.TryGetValue(name, out var children))
            {
                children = [];
                namespaces.Add(name, children);
                var description = string.IsNullOrWhiteSpace(entry.NamespaceDescription)
                    ? name == "functions" ? "" : $"Tools in the {name} namespace."
                    : entry.NamespaceDescription;
                specs.Add(new JsonObject
                {
                    ["type"] = "namespace", ["name"] = name, ["description"] = description, ["tools"] = children,
                });
            }
            children.Add((JsonNode)definition);
        }
        return specs.Select(spec =>
        {
            using var document = JsonDocument.Parse(spec.ToJsonString());
            return document.RootElement.Clone();
        }).ToArray();
    }
}
