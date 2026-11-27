using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Native configuration write error classifications from the App Server contract.</summary>
public enum CodexConfigurationWriteFailure
{
    /// <summary>The requested file or layer is not writable.</summary>
    ConfigLayerReadonly,
    /// <summary>A managed requirement prevents the requested edit.</summary>
    ConfigRequirementReadonly,
    /// <summary>Configuration changed after the caller's read or during admission.</summary>
    ConfigVersionConflict,
    /// <summary>The edit or complete candidate is invalid.</summary>
    ConfigValidationError,
    /// <summary>The requested path does not exist.</summary>
    ConfigPathNotFound,
    /// <summary>No native consumer is registered for the requested key.</summary>
    ConfigSchemaUnknownKey,
    /// <summary>The writable user layer is not available.</summary>
    UserLayerNotFound,
}

/// <summary>Safe typed configuration rejection. No raw configuration values or credentials are included.</summary>
public sealed class CodexConfigurationWriteException(CodexConfigurationWriteFailure failure)
    : InvalidOperationException("Configuration write rejected: " + failure)
{
    /// <summary>The classified App Server configuration failure.</summary>
    public CodexConfigurationWriteFailure Failure { get; } = failure;
}

/// <summary>An authenticated host's writable user-layer snapshot, including all higher-priority policy layers.
/// WritableFeatures contains only keys with real native consumers and explicit mutation admission.</summary>
public sealed record CodexConfigurationWriteSnapshot(
    [property: JsonIgnore] CodexConfigurationRpcSnapshot Configuration,
    string UserLayerName, string FilePath,
    IReadOnlySet<string> WritableFeatures, bool SupportsHotReload = false)
{
    /// <inheritdoc />
    public override string ToString() => "CodexConfigurationWriteSnapshot [redacted]";
}

/// <summary>One validated candidate. The host must persist precisely the candidate user config,
/// preserving unrelated source content and using the expected full stack and user-file revisions.</summary>
public sealed record CodexConfigurationWriteCandidate(string ExpectedStackRevision, string ExpectedUserVersion,
    string FilePath, [property: JsonIgnore] JsonElement UserConfig, bool ReloadUserConfig)
{
    /// <inheritdoc />
    public override string ToString() => "CodexConfigurationWriteCandidate [redacted]";
}

/// <summary>Host-owned authenticated config storage. No path is chosen from an untrusted RPC value.</summary>
public interface ICodexConfigurationWriteStorage
{
    /// <summary>Reads a consistent thread-agnostic configuration stack and canonical writable user-file identity.</summary>
    Task<CodexConfigurationWriteSnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Validates the complete native candidate and current managed policy, then atomically persists it if
    /// the stack revision and user-file version still match. The host owns TOML-preserving persistence, filesystem
    /// authorization, cross-process coordination and optional runtime reload. Returns the actual new user version,
    /// or null on a conflict. Exceptions after an attempted commit are uncertain; do not retry automatically.</summary>
    Task<string?> TryCommitAsync(CodexConfigurationWriteCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>Native user configuration value/batch writes for supported scalar settings and registered feature toggles.
/// Unknown paths, arbitrary credential-bearing settings and managed requirement edits are rejected before persistence.</summary>
public sealed class CodexConfigurationWriteRpc
{
    private readonly ICodexConfigurationWriteStorage storage;
    private static readonly HashSet<string> StructuredFeatures = new(StringComparer.Ordinal)
    {
        "code_mode", "guardianv3", "multi_agent_v2", "network_proxy", "sleep_tool", "runtime_agent_instances"
    };

    private sealed record Edit(string[] Path, JsonElement Value);

    /// <summary>Creates an authenticated user-config writer with a durable host CAS adapter.</summary>
    public CodexConfigurationWriteRpc(ICodexConfigurationWriteStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        this.storage = storage;
    }

    /// <summary>Whether this facade implements the given configuration mutation RPC.</summary>
    public static bool Handles(string method) => method is "config/value/write" or "config/batchWrite";

    /// <summary>Validates and applies the complete batch in memory before one durable compare-and-swap.
    /// Host validation remains authoritative for model/provider eligibility, native feature policy and execution settings.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported configuration write RPC.");
        var p = parameters is { ValueKind: JsonValueKind.Object } supplied ? supplied : throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var batch = method == "config/batchWrite";
        Fields(p, batch ? ["edits", "filePath", "expectedVersion", "reloadUserConfig"] :
            ["keyPath", "value", "mergeStrategy", "filePath", "expectedVersion"]);
        var path = OptionalText(p, "filePath");
        var expectedVersion = OptionalText(p, "expectedVersion");
        var reload = batch && Flag(p, "reloadUserConfig");
        Edit[] edits;
        if (batch)
        {
            if (!p.TryGetProperty("edits", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 128)
                throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
            edits = items.EnumerateArray().Select(item =>
            {
                Fields(item, ["keyPath", "value", "mergeStrategy"]);
                return ParseEdit(item);
            }).ToArray();
        }
        else edits = [ParseEdit(p)];

        // Only simple paths are accepted by this native subset; no arbitrary file/path discovery.
        var view = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = view.Configuration.Snapshot;
        var priorEffective = CodexConfigurationResolver.Resolve(snapshot).Config;
        if (!Path.IsPathFullyQualified(view.FilePath) || path is not null &&
            (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), view.FilePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw Reject(CodexConfigurationWriteFailure.ConfigLayerReadonly);
        if (reload && !view.SupportsHotReload) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);

        var metadata = new Dictionary<(CodexConfigurationLayerKind Kind, string Name), CodexConfigurationRpcLayerMetadata>();
        foreach (var item in view.Configuration.Metadata)
        {
            CodexConfigurationReadRpc.ValidateSource(item);
            if (!Bounded(item.Version, 4096) || !metadata.TryAdd((item.Kind, item.LayerName), item))
                throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        }
        if (metadata.Count != snapshot.Layers.Count ||
            snapshot.Layers.Any(layer => !metadata.ContainsKey((layer.Kind, layer.Name))) ||
            snapshot.Layers.Select(layer => (layer.Kind, layer.Name)).Distinct().Count() != snapshot.Layers.Count)
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var layers = snapshot.Layers.ToArray();
        var userIndex = Array.FindIndex(layers, layer => layer.Kind == CodexConfigurationLayerKind.User &&
            layer.Name == view.UserLayerName && layer.Trusted);
        if (userIndex < 0) throw Reject(CodexConfigurationWriteFailure.UserLayerNotFound);
        var user = layers[userIndex];
        var userMetadata = metadata[(user.Kind, user.Name)];
        if (!userMetadata.SourceName.TryGetProperty("file", out var userFile) || userFile.GetString() != view.FilePath)
            throw Reject(CodexConfigurationWriteFailure.ConfigLayerReadonly);
        if (expectedVersion is not null && expectedVersion != userMetadata.Version)
            throw Reject(CodexConfigurationWriteFailure.ConfigVersionConflict);
        var requirements = snapshot.Requirements;
        if (requirements is { } required &&
            (required.ValueKind != JsonValueKind.Object ||
            required.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != required.EnumerateObject().Count()))
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var candidate = JsonNode.Parse(user.Config.GetRawText()) as JsonObject ??
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);

        // Validate every edit before persistence. A rejected item cannot commit earlier items.
        foreach (var edit in edits)
        {
            ValidateEdit(edit, view.WritableFeatures);
            if (requirements is { } managed && (managed.TryGetProperty(edit.Path[0], out _) ||
                edit.Path[0] == "features" && managed.TryGetProperty("feature_requirements", out _)))
                throw Reject(CodexConfigurationWriteFailure.ConfigRequirementReadonly);
            var preserveBroker = edit.Path.Length >= 2 && edit.Path[0] == "features" &&
                edit.Path[1] == "network_proxy" && edit.Value.ValueKind == JsonValueKind.Null &&
                HasBrokerSettings(priorEffective);
            Apply(candidate, edit, preserveBroker);
        }
        var userConfig = Element(candidate);
        layers[userIndex] = user with { Config = userConfig };
        var effective = CodexConfigurationResolver.Resolve(snapshot with { Layers = layers });
        JsonObject? overridden = null;
        foreach (var edit in edits)
        {
            var userValue = ReadSemantic(userConfig, edit.Path);
            var effectiveValue = ReadSemantic(effective.Config, edit.Path);
            if (effectiveValue is { } observed && (edit.Path.Length == 1
                ? observed.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                : observed.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)))
                throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
            if (Equal(userValue, effectiveValue)) continue;
            // The edited keys are scalar; structured feature toggles use the semantic enabled leaf.
            var pointer = edit.Path[0] == "features" ? "/features/" + edit.Path[1] : "/" + edit.Path[0];
            if (edit.Path[0] == "features" && !effective.Origins.ContainsKey(pointer)) pointer += "/enabled";
            if (!effective.Origins.TryGetValue(pointer, out var origin)) continue;
            var originIndex = Array.FindLastIndex(layers, layer => layer.Kind == origin.Kind && layer.Name == origin.Name);
            if (originIndex <= userIndex) continue;
            var overriding = metadata[(origin.Kind, origin.Name)];
            overridden = new JsonObject
            {
                ["message"] = "A higher-precedence configuration layer overrides this setting.",
                ["overridingLayer"] = CodexConfigurationReadRpc.Metadata(overriding),
                ["effectiveValue"] = effectiveValue is { } value ? JsonNode.Parse(value.GetRawText()) : null
            };
            break;
        }

        string version = userMetadata.Version;
        if (edits.Length > 0 || reload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? confirmed;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                confirmed = await storage.TryCommitAsync(new(snapshot.Revision, userMetadata.Version,
                    view.FilePath, userConfig, reload), deadline.Token).ConfigureAwait(false);
            }
            catch (CodexConfigurationWriteException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new CodexThreadRpcException(-32000, "Configuration commit outcome is unconfirmed."); }
            if (confirmed is null) throw Reject(CodexConfigurationWriteFailure.ConfigVersionConflict);
            if (!Bounded(confirmed, 4096)) throw new CodexThreadRpcException(-32000, "Configuration commit version is unconfirmed.");
            version = confirmed;
            cancellationToken.ThrowIfCancellationRequested();
        }
        return Element(new JsonObject
        {
            ["status"] = overridden is null ? "ok" : "okOverridden", ["version"] = version,
            ["filePath"] = view.FilePath, ["overriddenMetadata"] = overridden
        });
    }

    // Storage adapters recheck that a candidate is reachable through the same native
    // scalar/toggle mutations, even when invoked directly rather than through RPC.
    internal static void ValidateCandidate(JsonElement before, JsonElement candidate, JsonElement effective,
        JsonElement? requirements, IReadOnlySet<string> writableFeatures)
    {
        if (before.ValueKind != JsonValueKind.Object || candidate.ValueKind != JsonValueKind.Object)
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var source = JsonNode.Parse(before.GetRawText())!.AsObject();
        var target = JsonNode.Parse(candidate.GetRawText())!.AsObject();
        var draft = (JsonObject)source.DeepClone();

        void ApplyChecked(string[] path, JsonNode? value)
        {
            using var document = JsonDocument.Parse(value?.ToJsonString() ?? "null");
            var edit = new Edit(path, document.RootElement);
            ValidateEdit(edit, writableFeatures);
            if (requirements is { } managed && (managed.TryGetProperty(path[0], out _) ||
                path[0] == "features" && managed.TryGetProperty("feature_requirements", out _)))
                throw Reject(CodexConfigurationWriteFailure.ConfigRequirementReadonly);
            var preserveBroker = path.Length >= 2 && path[0] == "features" && path[1] == "network_proxy" &&
                edit.Value.ValueKind == JsonValueKind.Null && HasBrokerSettings(effective);
            Apply(draft, edit, preserveBroker);
        }

        foreach (var key in source.Select(item => item.Key).Concat(target.Select(item => item.Key)).Distinct(StringComparer.Ordinal))
        {
            var oldValue = source[key];
            var newValue = target[key];
            if (JsonNode.DeepEquals(oldValue, newValue)) continue;
            if (key != "features") { ApplyChecked([key], newValue); continue; }
            if (oldValue is not null and not JsonObject || newValue is not JsonObject newFeatures)
                throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
            var oldFeatures = oldValue as JsonObject ?? new JsonObject();
            foreach (var name in oldFeatures.Select(item => item.Key).Concat(newFeatures.Select(item => item.Key)).Distinct(StringComparer.Ordinal))
            {
                var oldFeature = oldFeatures[name];
                var newFeature = newFeatures[name];
                if (JsonNode.DeepEquals(oldFeature, newFeature)) continue;
                if (newFeature is JsonObject structured)
                    ApplyChecked(["features", name, "enabled"], structured["enabled"]);
                else ApplyChecked(["features", name], newFeature);
            }
        }
        if (!JsonNode.DeepEquals(draft, target)) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
    }

    private static Edit ParseEdit(JsonElement p)
    {
        var key = OptionalText(p, "keyPath") ?? throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var strategy = OptionalText(p, "mergeStrategy");
        if (strategy is not ("replace" or "upsert") || !p.TryGetProperty("value", out var value))
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var segments = key.Split('.');
        if (segments.Length is < 1 or > 3 || segments.Any(segment => segment.Length is < 1 or > 128 ||
            segment.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))))
            throw Reject(CodexConfigurationWriteFailure.ConfigSchemaUnknownKey);
        // For scalar values, replace and upsert have the same semantics.
        return new(segments, value.Clone());
    }

    private static void ValidateEdit(Edit edit, IReadOnlySet<string> writableFeatures)
    {
        var path = edit.Path;
        var value = edit.Value;
        if (path[0] == "features" && path.Length is 2 or 3 && writableFeatures.Contains(path[1]) &&
            (path.Length == 2 || path[2] == "enabled" && StructuredFeatures.Contains(path[1])))
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
            return;
        }
        if (path.Length != 1 || path[0] is not ("model" or "model_reasoning_effort" or "model_reasoning_summary" or "model_verbosity" or "service_tier"))
            throw Reject(CodexConfigurationWriteFailure.ConfigSchemaUnknownKey);
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String || !Bounded(value.GetString(), 512))
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var text = value.GetString();
        var valid = path[0] switch
        {
            "model" => true,
            "model_reasoning_effort" => text is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max",
            "model_reasoning_summary" => text is "auto" or "concise" or "detailed" or "none",
            "model_verbosity" => text is "low" or "medium" or "high",
            "service_tier" => text is "auto" or "default" or "flex" or "priority",
            _ => false
        };
        if (!valid) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
    }

    private static bool HasBrokerSettings(JsonElement config) =>
        config.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object &&
        features.TryGetProperty("network_proxy", out var proxy) && proxy.ValueKind == JsonValueKind.Object &&
        (proxy.TryGetProperty("credentials", out _) || proxy.TryGetProperty("credential_broker", out _));

    private static void Apply(JsonObject config, Edit edit, bool preserveBroker)
    {
        var path = edit.Path;
        var deletion = edit.Value.ValueKind == JsonValueKind.Null;
        if (path.Length == 1)
        {
            if (deletion) config.Remove(path[0]);
            else config[path[0]] = JsonNode.Parse(edit.Value.GetRawText());
            return;
        }
        if (config["features"] is null)
        {
            if (deletion && !preserveBroker) return;
            config["features"] = new JsonObject();
        }
        if (config["features"] is not JsonObject features) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var feature = path[1];
        var structured = StructuredFeatures.Contains(feature);
        // Preserve structured feature parameters, including credential broker settings. Null
        // is a removal of the enablement override, not deletion of the entire feature table.
        if (structured && features[feature] is JsonObject table)
        {
            if (deletion && feature == "network_proxy" && (preserveBroker || table.ContainsKey("credentials") || table.ContainsKey("credential_broker")))
                table["enabled"] = false;
            else if (deletion) table.Remove("enabled");
            else table["enabled"] = edit.Value.GetBoolean();
        }
        else if (preserveBroker)
            features[feature] = false;
        else if (structured && path.Length == 3 && !deletion)
            features[feature] = new JsonObject { ["enabled"] = edit.Value.GetBoolean() };
        else if (deletion) features.Remove(feature);
        else features[feature] = JsonNode.Parse(edit.Value.GetRawText());
    }

    private static JsonElement? ReadSemantic(JsonElement config, string[] path)
    {
        if (!config.TryGetProperty(path[0], out var value)) return null;
        if (path.Length == 1) return value;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(path[1], out var feature)) return null;
        if (feature.ValueKind == JsonValueKind.Object)
            return feature.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False ? enabled : null;
        return feature.ValueKind is JsonValueKind.True or JsonValueKind.False ? feature : null;
    }

    private static bool Equal(JsonElement? a, JsonElement? b) => a is { } left
        ? b is { } right && JsonElement.DeepEquals(left, right) : b is null;
    private static bool Bounded(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static string? OptionalText(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !Bounded(value.GetString(), 4096))
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        return value.GetString();
    }
    private static bool Flag(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        return value.GetBoolean();
    }
    private static void Fields(JsonElement p, string[] allowed)
    {
        if (p.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(p.GetRawText()) > 65536)
            throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in p.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw Reject(CodexConfigurationWriteFailure.ConfigValidationError);
    }
    private static JsonElement Element(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexConfigurationWriteException Reject(CodexConfigurationWriteFailure failure) => new(failure);
}
