using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>The source of an effective raw configuration leaf.</summary>
public sealed record CodexConfigurationValueOrigin(CodexConfigurationLayerKind Kind, string Name);

/// <summary>Host-only merged raw settings and their provenance. This is not an authorization decision
/// or proof that an unknown key has a native execution consumer. Raw settings can contain secrets.</summary>
public sealed record CodexResolvedConfiguration(
    string Revision, JsonElement Config, IReadOnlyDictionary<string, CodexConfigurationValueOrigin> Origins)
{
    /// <summary>Explicit configured feature enablement; null means no override, not disabled.</summary>
    public bool? GetFeatureOverride(string name)
    {
        if (!Config.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object ||
            !features.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Object && !value.TryGetProperty("enabled", out value)) return null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }

    /// <inheritdoc />
    public override string ToString() => "CodexResolvedConfiguration [redacted]";
}

/// <summary>Resolves ordered configuration layers without performing I/O, activating features,
/// or granting execution permissions. Origins use escaped JSON pointer paths.</summary>
public static class CodexConfigurationResolver
{
    private static readonly HashSet<string> StructuredFeatures = new(StringComparer.Ordinal)
    {
        "code_mode", "guardianv3", "multi_agent_v2", "network_proxy", "sleep_tool", "runtime_agent_instances"
    };

    /// <summary>Merges trusted layers and records the source of each effective leaf.</summary>
    public static CodexResolvedConfiguration Resolve(CodexThreadConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.Revision) || snapshot.Revision.Length > 512 ||
            snapshot.Revision.Any(char.IsControl) || snapshot.Layers.Count > 32)
            throw Invalid("Invalid configuration snapshot.");
        var raw = new JsonObject();
        var origins = new Dictionary<string, CodexConfigurationValueOrigin>(StringComparer.Ordinal);
        var previous = CodexConfigurationLayerKind.Package;
        var bytes = 0;
        foreach (var layer in snapshot.Layers.ToArray())
        {
            if (!Enum.IsDefined(layer.Kind) || layer.Kind < previous || string.IsNullOrWhiteSpace(layer.Name) ||
                layer.Name.Length > 512 || layer.Name.Any(char.IsControl))
                throw Invalid("Configuration layers must be bounded and ordered by precedence.");
            previous = layer.Kind;
            if (!layer.Trusted)
            {
                if (layer.Kind != CodexConfigurationLayerKind.Project)
                    throw Invalid("Only project configuration can be disabled by project trust.");
                continue;
            }
            if (layer.Config.ValueKind != JsonValueKind.Object) throw Invalid("Invalid configuration layer object.");
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(layer.Config.GetRawText()));
            if (bytes > 1_048_576) throw Invalid("Configuration layers exceed their aggregate bound.");
            Validate(layer.Config, 0);
            Merge(raw, layer.Config, "", new(layer.Kind, layer.Name), origins);
        }
        using var document = JsonDocument.Parse(raw.ToJsonString());
        return new(snapshot.Revision, document.RootElement.Clone(),
            new ReadOnlyDictionary<string, CodexConfigurationValueOrigin>(origins));
    }

    private static void Validate(JsonElement value, int depth)
    {
        if (depth > 32) throw Invalid("Configuration nesting exceeds its bound.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!seen.Add(field.Name)) throw Invalid("Duplicate configuration setting.");
                Validate(field.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Validate(item, depth + 1);
    }

    private static void Merge(JsonObject target, JsonElement source, string path, CodexConfigurationValueOrigin origin,
        Dictionary<string, CodexConfigurationValueOrigin> origins)
    {
        foreach (var field in source.EnumerateObject())
        {
            var key = field.Name;
            var pointer = Child(path, key);
            var incoming = field.Value;
            target.TryGetPropertyValue(key, out var existing);

            // The pinned Rust merge treats structured feature booleans as an update of
            // the enabled member rather than replacing the whole feature configuration.
            if (IsStructuredFeature(pointer))
            {
                if (existing is JsonValue scalar && scalar.TryGetValue<bool>(out var enabled) &&
                    incoming.ValueKind == JsonValueKind.Object)
                {
                    origins.Remove(pointer, out var inheritedOrigin);
                    if (inheritedOrigin is not null) origins[Child(pointer, "enabled")] = inheritedOrigin;
                    existing = new JsonObject { ["enabled"] = enabled };
                    target[key] = existing;
                }
                else if (existing is JsonObject feature && incoming.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var enabledPath = Child(pointer, "enabled");
                    if (feature.TryGetPropertyValue("enabled", out var oldEnabled)) RemoveOrigins(oldEnabled, enabledPath, origins);
                    feature["enabled"] = incoming.GetBoolean();
                    origins.Remove(pointer);
                    origins[enabledPath] = origin;
                    continue;
                }
            }

            // This provider selection is an indivisible identity, not a recursive overlay.
            if (pointer == "/tui/status_line_provider")
            {
                if (origin.Kind is CodexConfigurationLayerKind.Package or CodexConfigurationLayerKind.Project) continue;
                RemoveOrigins(existing, pointer, origins);
                var value = JsonNode.Parse(incoming.GetRawText());
                target[key] = value;
                RecordOrigins(value, pointer, origin, origins);
                continue;
            }
            if (pointer == "/tui" && origin.Kind is CodexConfigurationLayerKind.Package or CodexConfigurationLayerKind.Project &&
                incoming.ValueKind == JsonValueKind.Object && incoming.EnumerateObject().All(value => value.Name == "status_line_provider"))
                continue;

            if (incoming.ValueKind == JsonValueKind.Object)
            {
                if (existing is not JsonObject nested)
                {
                    RemoveOrigins(existing, pointer, origins);
                    nested = new JsonObject();
                    target[key] = nested;
                }
                // Replacing the filter representation also removes incompatible lower fields.
                if (pointer == "/shell_environment_policy")
                {
                    if (incoming.TryGetProperty("filters", out _))
                    {
                        RemoveMember(nested, pointer, "exclude", origins);
                        RemoveMember(nested, pointer, "include_only", origins);
                    }
                    else if (incoming.TryGetProperty("exclude", out _) || incoming.TryGetProperty("include_only", out _))
                        RemoveMember(nested, pointer, "filters", origins);
                }
                Merge(nested, incoming, pointer, origin, origins);
                if (nested.Count == 0)
                {
                    if (!origins.ContainsKey(pointer)) origins[pointer] = origin;
                }
                else origins.Remove(pointer);
            }
            else
            {
                RemoveOrigins(existing, pointer, origins);
                target[key] = JsonNode.Parse(incoming.GetRawText());
                origins[pointer] = origin;
            }
        }
    }

    private static bool IsStructuredFeature(string pointer)
    {
        var parts = pointer.Split('/');
        return (parts.Length == 3 && parts[1] == "features" ||
                parts.Length == 5 && parts[1] == "profiles" && parts[3] == "features") &&
            StructuredFeatures.Contains(parts[^1]);
    }

    private static string Child(string path, string key) =>
        path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static void RemoveMember(JsonObject target, string path, string key,
        Dictionary<string, CodexConfigurationValueOrigin> origins)
    {
        if (target.TryGetPropertyValue(key, out var value))
        {
            RemoveOrigins(value, Child(path, key), origins);
            target.Remove(key);
        }
    }

    private static void RemoveOrigins(JsonNode? node, string pointer, Dictionary<string, CodexConfigurationValueOrigin> origins)
    {
        origins.Remove(pointer);
        if (node is JsonObject table)
            foreach (var field in table) RemoveOrigins(field.Value, Child(pointer, field.Key), origins);
    }

    private static void RecordOrigins(JsonNode? node, string pointer, CodexConfigurationValueOrigin origin,
        Dictionary<string, CodexConfigurationValueOrigin> origins)
    {
        if (node is JsonObject table && table.Count != 0)
            foreach (var field in table) RecordOrigins(field.Value, Child(pointer, field.Key), origin, origins);
        else origins[pointer] = origin;
    }

    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
}
