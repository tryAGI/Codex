using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Provenance of a model catalog, not an account entitlement.</summary>
public enum CodexModelCatalogOrigin
{
    /// <summary>Observed from the selected provider.</summary>
    Provider,
    /// <summary>Metadata bundled and reviewed by the embedding host.</summary>
    Bundled,
    /// <summary>Metadata explicitly supplied by trusted host configuration.</summary>
    ExplicitConfig,
}

/// <summary>A host-registered provider catalog. The source must authorize and resolve the current owner on every read.</summary>
public sealed class CodexModelCatalogSource
{
    private readonly Func<CancellationToken, Task<CodexModelCatalogSnapshot>> read;
    /// <summary>Exact provider ID.</summary>
    public string ProviderId { get; }
    /// <summary>Credential type selected by the host; never inferred from the requested model.</summary>
    public CodexProviderAuthMode AuthMode { get; }
    /// <summary>Origin of this source's metadata.</summary>
    public CodexModelCatalogOrigin Origin { get; }

    /// <summary>Registers an authorized provider reader. A failed read never switches providers.</summary>
    public CodexModelCatalogSource(string providerId, CodexProviderAuthMode authMode,
        Func<CancellationToken, Task<CodexModelCatalogSnapshot>> read, CodexModelCatalogOrigin origin = CodexModelCatalogOrigin.Provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(read);
        if (providerId.Length > 128 || providerId.Any(char.IsControl) || !Enum.IsDefined(authMode) || !Enum.IsDefined(origin))
            throw new ArgumentException("Invalid model catalog source.");
        ProviderId = providerId;
        AuthMode = authMode;
        Origin = origin;
        this.read = read;
    }

    /// <summary>Registers a static catalog. The native parser retains raw metadata without making a network request.</summary>
    public static CodexModelCatalogSource ForStatic(string providerId, CodexProviderAuthMode authMode, string version,
        JsonElement catalog, CodexModelCatalogOrigin origin = CodexModelCatalogOrigin.ExplicitConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (origin == CodexModelCatalogOrigin.Provider) throw new ArgumentException("Static metadata is not a provider observation.", nameof(origin));
        if (Encoding.UTF8.GetByteCount(catalog.GetRawText()) > 16_777_216) throw new ArgumentException("Catalog is too large.", nameof(catalog));
        var snapshot = new CodexModelCatalogSnapshot(providerId, version, DateTimeOffset.UtcNow, null,
            CodexClient.ParseModelCatalog(catalog));
        return new(providerId, authMode, ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }, origin);
    }

    /// <summary>Reads only this source. Source identity must match its registration.</summary>
    public async Task<CodexModelCatalogSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var value = await read(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(value.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new CodexProtocolException("Model catalog provider identity mismatch.");
        if (value.Models.Count > 4096 || value.Models.Select(model => model.Model).Distinct(StringComparer.Ordinal).Count() != value.Models.Count)
            throw new CodexProtocolException("Invalid or duplicate model catalog entries.");
        return value;
    }
}

/// <summary>Native App Server model/list facade. Sources are supplied only by the authenticated host.</summary>
public sealed class CodexModelCatalogRpc
{
    private readonly Dictionary<string, CodexModelCatalogSource> sources;
    private readonly string defaultProviderId;

    /// <summary>Creates an explicitly configured provider catalog facade.</summary>
    public CodexModelCatalogRpc(IEnumerable<CodexModelCatalogSource> sources, string defaultProviderId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderId);
        this.sources = new(StringComparer.Ordinal);
        foreach (var source in sources) this.sources.Add(source.ProviderId, source);
        if (!this.sources.ContainsKey(defaultProviderId)) throw Invalid("Unknown default model provider.");
        this.defaultProviderId = defaultProviderId;
    }

    /// <summary>Whether the facade implements this RPC.</summary>
    public static bool Handles(string method) => method == "model/list";

    /// <summary>Lists models of the selected provider using the pinned App Server v2 projection and cursor semantics.
    /// Never changes the active provider, selects another credential, or starts a login.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported model catalog method.");
        var p = parameters ?? Element(new JsonObject());
        if (p.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(p.GetRawText()) > 8192)
            throw Invalid("Invalid model/list parameters.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in p.EnumerateObject())
            if (!seen.Add(field.Name) || field.Name is not ("modelProvider" or "cursor" or "limit" or "includeHidden"))
                throw Invalid("Unknown or duplicate model/list parameter.");
        var provider = Text(p, "modelProvider") ?? defaultProviderId;
        if (!sources.TryGetValue(provider, out var source))
            throw new CodexProviderRouteException(CodexProviderRouteFailure.UnknownProvider);
        var includeHidden = false;
        if (p.TryGetProperty("includeHidden", out var hidden) && hidden.ValueKind != JsonValueKind.Null)
        {
            if (hidden.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Invalid includeHidden.");
            includeHidden = hidden.GetBoolean();
        }
        int? limit = null;
        if (p.TryGetProperty("limit", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var count))
                throw Invalid("Invalid model list limit.");
            limit = (int)Math.Clamp(count, 1u, 4096u);
        }
        var start = 0;
        var cursor = Text(p, "cursor");
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0))
            throw Invalid("Invalid model list cursor.");

        // A dynamic paid provider is never discovered implicitly. An explicit host-selected
        // static catalog is safe to inspect without opening a provider connection.
        if (Text(p, "modelProvider") is null && source.Origin == CodexModelCatalogOrigin.Provider &&
            source.AuthMode != CodexProviderAuthMode.ChatGptSubscription && source.AuthMode != CodexProviderAuthMode.None)
            throw new CodexProviderRouteException(CodexProviderRouteFailure.ExplicitPaidProviderRequired);

        var snapshot = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        var eligible = snapshot.Models
            .Where(model => source.AuthMode == CodexProviderAuthMode.ChatGptSubscription || model.SupportedInApi == true)
            .ToArray();
        // Rust's provider-backed catalog preserves display order independently
        // of the priority used to select the default. Static catalogs keep their
        // existing priority order; pagination always follows display order.
        var prioritized = eligible.OrderBy(Priority).ToArray();
        var defaultModel = prioritized.FirstOrDefault(model => model.Visibility == "list") ?? prioritized.FirstOrDefault();
        var models = source.Origin == CodexModelCatalogOrigin.Provider ? eligible : prioritized;
        var listed = models.Where(model => includeHidden || model.Visibility == "list").ToArray();
        if (listed.Length == 0) return Element(new JsonObject { ["data"] = new JsonArray(), ["nextCursor"] = null });
        if (start > listed.Length) throw Invalid("Model list cursor exceeds the catalog.");
        var end = Math.Min(listed.Length, start + (limit ?? listed.Length));
        var data = new JsonArray();
        foreach (var model in listed[start..end]) data.Add((JsonNode)Project(model, ReferenceEquals(model, defaultModel)));
        return Element(new JsonObject
        {
            ["data"] = data,
            ["nextCursor"] = end < listed.Length ? end.ToString(CultureInfo.InvariantCulture) : null
        });
    }

    private static int Priority(CodexModelCatalogEntry model) =>
        model.Raw.ValueKind == JsonValueKind.Object && model.Raw.TryGetProperty("priority", out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var priority) ? priority : int.MaxValue;

    private static JsonObject Project(CodexModelCatalogEntry model, bool isDefault)
    {
        var efforts = new JsonArray();
        foreach (var effort in model.SupportedReasoningEfforts ?? [])
            efforts.Add((JsonNode)new JsonObject { ["reasoningEffort"] = effort.Effort, ["description"] = effort.Description ?? "" });
        var tiers = new JsonArray();
        if (Property(model.Raw, "service_tiers") is { ValueKind: JsonValueKind.Array } rawTiers)
            foreach (var tier in rawTiers.EnumerateArray())
                if (tier.ValueKind == JsonValueKind.Object && PropertyText(tier, "id") is { } id)
                    tiers.Add((JsonNode)new JsonObject { ["id"] = id, ["name"] = PropertyText(tier, "name") ?? "", ["description"] = PropertyText(tier, "description") ?? "" });
        var upgrade = Property(model.Raw, "upgrade");
        var upgradeId = upgrade is { ValueKind: JsonValueKind.Object } u ? PropertyText(u, "model") : null;
        JsonObject? upgradeInfo = null;
        if (upgradeId is not null && upgrade is { } rawUpgrade)
        {
            long? retirement = DateTimeOffset.TryParse(PropertyText(rawUpgrade, "retirement_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date) ? date.ToUnixTimeSeconds() : null;
            upgradeInfo = new JsonObject { ["model"] = upgradeId, ["upgradeCopy"] = null, ["modelLink"] = null,
                ["migrationMarkdown"] = PropertyText(rawUpgrade, "migration_markdown"), ["retirementAt"] = retirement };
        }
        var availability = Property(model.Raw, "availability_nux");
        var nux = availability is { ValueKind: JsonValueKind.Object } available && PropertyText(available, "message") is { } message
            ? new JsonObject { ["message"] = message } : null;
        var access = Property(model.Raw, "available_access_programs");
        JsonObject? programs = null;
        if (access is { ValueKind: JsonValueKind.Object } a && Property(a, "cyber") is { ValueKind: JsonValueKind.Array } cyber)
        {
            var known = cyber.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()).Where(item => item is "standard" or "daybreak_blue" or "daybreak_red");
            var availabilityName = PropertyText(a, "daybreak_availability") switch
            {
                "available" => "available",
                "enrollment_required" => "enrollmentRequired",
                "unavailable_for_model" => "unavailableForModel",
                _ => "default"
            };
            programs = new JsonObject { ["cyber"] = Strings(known.Select(item => item switch { "daybreak_blue" => "daybreakBlue", "daybreak_red" => "daybreakRed", _ => "standard" })), ["daybreakAvailability"] = availabilityName };
        }
        return new JsonObject
        {
            ["id"] = model.Model, ["model"] = model.Model, ["upgrade"] = upgradeId, ["upgradeInfo"] = upgradeInfo,
            ["availabilityNux"] = nux, ["displayName"] = model.DisplayName ?? model.Model, ["description"] = model.Description ?? "",
            ["modelSpecialty"] = PropertyText(model.Raw, "model_specialty"), ["hidden"] = model.Visibility != "list",
            ["supportedReasoningEfforts"] = efforts, ["defaultReasoningEffort"] = model.DefaultReasoningEffort ?? "none",
            // The pinned Rust wire schema supplies these legacy defaults when the catalog omits modalities.
            // The source snapshot still retains the distinction between missing and advertised metadata.
            ["inputModalities"] = Strings(model.InputModalities ?? ["text", "image"]),
            ["supportsPersonality"] = false, ["multiAgentVersion"] = PropertyText(model.Raw, "multi_agent_version"),
            ["additionalSpeedTiers"] = Strings(Property(model.Raw, "additional_speed_tiers") is { ValueKind: JsonValueKind.Array } speed
                ? speed.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!) : []),
            ["serviceTiers"] = tiers, ["defaultServiceTier"] = PropertyText(model.Raw, "default_service_tier"),
            ["availableAccessPrograms"] = programs, ["isDefault"] = isDefault
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
    private static string? PropertyText(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid("Invalid model/list string.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128 || text.Any(char.IsControl)) throw Invalid("Invalid model/list string.");
        return text;
    }
    private static JsonElement Element(JsonNode value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
}
