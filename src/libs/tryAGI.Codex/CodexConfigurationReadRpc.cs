using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Trusted source metadata for a configuration layer. SourceName is the upstream tagged ConfigLayerSource;
/// the host provides real path, profile, MDM or enterprise metadata rather than fabricating it from a display name.</summary>
public sealed record CodexConfigurationRpcLayerMetadata(CodexConfigurationLayerKind Kind, string LayerName,
    string Version, JsonElement SourceName);

/// <summary>One authorized configuration read. Raw configuration can contain secrets and is never directly returned by the facade.</summary>
public sealed record CodexConfigurationRpcSnapshot(
    [property: JsonIgnore] CodexThreadConfigurationSnapshot Snapshot,
    IReadOnlyList<CodexConfigurationRpcLayerMetadata> Metadata)
{
    /// <inheritdoc />
    public override string ToString() => "CodexConfigurationRpcSnapshot [redacted]";
}

/// <summary>One verified native feature state. Enabled must reflect a registered native consumer, not just a raw config override.</summary>
public sealed record CodexExperimentalFeatureState(string Name, string Stage, bool Enabled, bool DefaultEnabled,
    string? DisplayName = null, string? Description = null, string? Announcement = null);

/// <summary>Native read-only configuration RPCs for the currently supported configuration settings.
/// The host authorizes every requested cwd/thread, supplies authentic layer metadata, and resolves feature policy.
/// Secret-bearing raw configuration, unknown keys and unimplemented features are not advertised as effective settings.</summary>
public sealed class CodexConfigurationReadRpc
{
    private readonly Func<string?, CancellationToken, Task<CodexConfigurationRpcSnapshot>> read;
    private readonly Func<Guid?, CancellationToken, Task<IReadOnlyList<CodexExperimentalFeatureState>>>? features;

    // A positive allowlist: arbitrary headers, environment variables, command strings,
    // credentials, developer prompts and future config fields must never cross this facade.
    private static readonly string[] ScalarStrings = ["model", "review_model", "model_provider",
        "approval_policy", "approvals_reviewer", "sandbox_mode", "model_reasoning_effort",
        "model_reasoning_summary", "model_verbosity", "service_tier", "web_search", "forced_login_method",
        "model_auto_compact_token_limit_scope"];
    private static readonly string[] ScalarNumbers = ["model_context_window", "model_auto_compact_token_limit"];

    /// <summary>Creates a connection-authorized configuration read facade.</summary>
    public CodexConfigurationReadRpc(Func<string?, CancellationToken, Task<CodexConfigurationRpcSnapshot>> read,
        Func<Guid?, CancellationToken, Task<IReadOnlyList<CodexExperimentalFeatureState>>>? features = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        this.read = read;
        this.features = features;
    }

    /// <summary>Whether this instance implements the requested read RPC.</summary>
    public bool Handles(string method) => method is "config/read" or "configRequirements/read" ||
        method == "experimentalFeature/list" && features is not null;

    /// <summary>Reads authorized config, requirements or registered native feature states without writing configuration.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported configuration RPC.");
        var p = parameters is { ValueKind: not JsonValueKind.Null } supplied ? supplied : Element(new JsonObject());
        Fields(p, method switch
        {
            "config/read" => ["cwd", "includeLayers"],
            "experimentalFeature/list" => ["threadId", "cursor", "limit"],
            _ => []
        });
        if (method == "experimentalFeature/list") return await ListFeaturesAsync(p, cancellationToken).ConfigureAwait(false);
        var cwd = Text(p, "cwd");
        if (cwd is not null && (cwd.Length > 4096 || !Path.IsPathFullyQualified(cwd))) throw Invalid("An absolute authorized cwd is required.");
        var includeLayers = Flag(p, "includeLayers");
        var view = await read(cwd, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = view.Snapshot;
        var resolved = CodexConfigurationResolver.Resolve(snapshot);
        if (method == "configRequirements/read") return Element(Requirements(snapshot.Requirements));

        // Resolve metadata using an exact layer identity rather than guessing source paths or versions.
        var descriptors = new Dictionary<(CodexConfigurationLayerKind Kind, string Name), CodexConfigurationRpcLayerMetadata>();
        foreach (var item in view.Metadata)
        {
            if (!Bounded(item.Version, 512) || !descriptors.TryAdd((item.Kind, item.LayerName), item))
                throw Invalid("Invalid configuration layer metadata.");
            ValidateSource(item);
        }
        if (descriptors.Count != snapshot.Layers.Count || snapshot.Layers.Any(layer => !descriptors.ContainsKey((layer.Kind, layer.Name))) ||
            snapshot.Layers.Select(layer => (layer.Kind, layer.Name)).Distinct().Count() != snapshot.Layers.Count)
            throw Invalid("Configuration layers and source metadata do not match.");
        var config = PublicConfig(resolved.Config);
        var origins = new JsonObject();
        foreach (var field in config)
            if (resolved.Origins.TryGetValue("/" + field.Key, out var origin) && origin.Kind != CodexConfigurationLayerKind.Package)
            {
                var descriptor = descriptors[(origin.Kind, origin.Name)];
                origins[field.Key] = Metadata(descriptor);
            }
        var response = new JsonObject { ["config"] = config, ["origins"] = origins };
        if (includeLayers)
        {
            var layers = new JsonArray();
            foreach (var layer in snapshot.Layers.Reverse().Where(layer => layer.Kind != CodexConfigurationLayerKind.Package))
            {
                var item = Metadata(descriptors[(layer.Kind, layer.Name)]);
                item["config"] = layer.Trusted ? PublicConfig(layer.Config) : new JsonObject();
                if (!layer.Trusted) item["disabledReason"] = "Project configuration is not trusted.";
                layers.Add((JsonNode)item);
            }
            response["layers"] = layers;
        }
        return Element(response);
    }

    internal static JsonObject Metadata(CodexConfigurationRpcLayerMetadata item) =>
        new() { ["name"] = JsonNode.Parse(item.SourceName.GetRawText()), ["version"] = item.Version };

    internal static void ValidateSource(CodexConfigurationRpcLayerMetadata item)
    {
        var name = item.SourceName;
        if (name.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(name.GetRawText()) > 8192)
            throw Invalid("Invalid configuration source name.");
        var type = Text(name, "type");
        var expected = item.Kind switch
        {
            CodexConfigurationLayerKind.Package => "packagedDefaults",
            CodexConfigurationLayerKind.Admin => "mdm",
            CodexConfigurationLayerKind.System => "system",
            CodexConfigurationLayerKind.Cloud => "enterpriseManaged",
            CodexConfigurationLayerKind.User or CodexConfigurationLayerKind.Profile => "user",
            CodexConfigurationLayerKind.Project => "project",
            CodexConfigurationLayerKind.Runtime => "sessionFlags",
            CodexConfigurationLayerKind.LegacyManagedFile => "legacyManagedConfigTomlFromFile",
            CodexConfigurationLayerKind.LegacyManagedMdm => "legacyManagedConfigTomlFromMdm",
            _ => throw Invalid("Invalid configuration layer kind.")
        };
        if (type != expected) throw Invalid("Configuration source kind mismatch.");
        string[] allowed = type switch
        {
            "packagedDefaults" or "system" or "legacyManagedConfigTomlFromFile" => ["type", "file"],
            "mdm" => ["type", "domain", "key"],
            "enterpriseManaged" => ["type", "id", "name"],
            "user" => ["type", "file", "profile"],
            "project" => ["type", "dotCodexFolder"],
            _ => ["type"]
        };
        Fields(name, allowed);
        foreach (var key in allowed.Where(key => key != "type"))
        {
            var value = Text(name, key);
            if (key == "profile" && item.Kind == CodexConfigurationLayerKind.User && value is null) continue;
            if (value is null || !Bounded(value, 4096) || (key is "file" or "dotCodexFolder") && !Path.IsPathFullyQualified(value))
                throw Invalid("Missing or invalid configuration source metadata.");
        }
    }

    private static JsonObject PublicConfig(JsonElement raw)
    {
        var result = new JsonObject();
        foreach (var key in ScalarStrings)
            if (raw.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                result[key] = JsonNode.Parse(value.GetRawText());
        foreach (var key in ScalarNumbers)
            if (raw.TryGetProperty(key, out var value) && (value.ValueKind == JsonValueKind.Null ||
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _)))
                result[key] = JsonNode.Parse(value.GetRawText());
        if (raw.TryGetProperty("allow_login_shell", out var shell) && shell.ValueKind is JsonValueKind.True or JsonValueKind.False)
            result["allow_login_shell"] = shell.GetBoolean();
        return result;
    }

    private static JsonObject Requirements(JsonElement? requirements)
    {
        if (requirements is null) return new() { ["requirements"] = null, ["supportsIndependentSpeedModes"] = null };
        var raw = requirements.Value;
        if (raw.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(raw.GetRawText()) > 65536)
            throw Invalid("Invalid managed configuration requirements.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonObject();
        foreach (var requirement in raw.EnumerateObject())
        {
            if (!seen.Add(requirement.Name) || requirement.Value.ValueKind != JsonValueKind.Array ||
                requirement.Value.GetArrayLength() is < 1 or > 64 || requirement.Value.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                throw Invalid("Invalid managed allowed values.");
            var key = requirement.Name switch
            {
                "approval_policy" => "allowedApprovalPolicies",
                "approvals_reviewer" => "allowedApprovalsReviewers",
                "sandbox_mode" => "allowedSandboxModes",
                _ => null
            };
            if (key is not null) result[key] = JsonNode.Parse(requirement.Value.GetRawText());
        }
        return new() { ["requirements"] = result, ["supportsIndependentSpeedModes"] = null };
    }

    private async Task<JsonElement> ListFeaturesAsync(JsonElement p, CancellationToken ct)
    {
        var thread = Text(p, "threadId");
        Guid? threadId = thread is null ? null : Guid.TryParse(thread, out var id) && id != Guid.Empty
            ? id : throw Invalid("Invalid loaded thread ID.");
        var cursor = Text(p, "cursor");
        var start = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0))
            throw Invalid("Invalid feature cursor.");
        var limit = 4096;
        if (p.TryGetProperty("limit", out var count) && count.ValueKind != JsonValueKind.Null)
        {
            if (count.ValueKind != JsonValueKind.Number || !count.TryGetUInt32(out var value)) throw Invalid("Invalid feature page size.");
            limit = (int)Math.Clamp(value, 1u, 4096u);
        }
        var states = await features!(threadId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (states.Count > 4096 || states.Select(state => state.Name).Distinct(StringComparer.Ordinal).Count() != states.Count ||
            states.Any(state => !Bounded(state.Name, 128) || state.Stage is not ("beta" or "underDevelopment" or "stable" or "deprecated" or "removed") ||
                state.Stage == "removed" && state.Enabled))
            throw Invalid("Invalid native feature registry.");
        if (states.Count == 0) return Element(new JsonObject { ["data"] = new JsonArray(), ["nextCursor"] = null });
        if (start > states.Count) throw Invalid("Feature cursor exceeds the registry.");
        var end = Math.Min(start + limit, states.Count);
        var items = new JsonArray();
        for (var index = start; index < end; index++)
        {
            var state = states[index];
            var beta = state.Stage == "beta";
            items.Add((JsonNode)new JsonObject { ["name"] = state.Name, ["stage"] = state.Stage,
                ["displayName"] = beta ? state.DisplayName : null, ["description"] = beta ? state.Description : null,
                ["announcement"] = beta ? state.Announcement : null, ["enabled"] = state.Enabled, ["defaultEnabled"] = state.DefaultEnabled });
        }
        return Element(new JsonObject { ["data"] = items, ["nextCursor"] = end < states.Count ? end.ToString(CultureInfo.InvariantCulture) : null });
    }

    private static void Fields(JsonElement p, string[] allowed)
    {
        if (p.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(p.GetRawText()) > 8192) throw Invalid("Invalid configuration parameters.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in p.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal)) throw Invalid("Unknown or duplicate configuration field.");
    }
    private static string? Text(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !Bounded(value.GetString(), 4096)) throw Invalid("Invalid configuration string.");
        return value.GetString();
    }
    private static bool Flag(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Invalid configuration flag.");
        return value.GetBoolean();
    }
    private static bool Bounded(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static JsonElement Element(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
}
