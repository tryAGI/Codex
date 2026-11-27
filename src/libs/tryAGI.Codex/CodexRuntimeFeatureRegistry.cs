using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>A feature with an actual registered native consumer. The process-wide runtime mutation
/// capability is additionally restricted by the pinned upstream allowlist and host policy.</summary>
public sealed record CodexRuntimeFeatureRegistration(string Name, string Stage, bool DefaultEnabled,
    bool AllowRuntimeChange = false, string? DisplayName = null, string? Description = null, string? Announcement = null);

/// <summary>A host-verified configuration and managed policy snapshot. A managed value has final precedence.
/// Revision must identify the policy/configuration generation, not a token refresh.</summary>
public sealed record CodexRuntimeFeaturePolicy(string Revision,
    IReadOnlyDictionary<string, bool> Configured, IReadOnlyDictionary<string, bool> Required);

/// <summary>Immutable process feature capture for consumers. The generation changes whenever policy or runtime state changes.</summary>
public sealed record CodexRuntimeFeatureSnapshot(long Generation, string PolicyRevision, IReadOnlyDictionary<string, bool> Effective,
    IReadOnlyList<CodexExperimentalFeatureState> States)
{
    /// <summary>Returns a feature's effective state, false for an unregistered key.</summary>
    public bool IsEnabled(string name) => Effective.TryGetValue(name, out var enabled) && enabled;
}

/// <summary>Process-scoped native feature registry. Config, thread defaults, runtime overrides and managed requirements
/// resolve in that order. Updating host policy and runtime overrides are serialized in the same gate.
/// Consumers must use fresh captures at admission; feature state alone never grants action, network or billing authority.</summary>
public sealed class CodexRuntimeFeatureRegistry
{
    private static readonly HashSet<string> UpstreamRuntimeKeys = new(StringComparer.Ordinal)
    {
        "api_key_cyber_access_programs", "api_key_model_discovery", "auth_elicitation",
        "background_paginated_rollout_migration", "codex_apps_mcp_2026_07_28", "daybreak_program_propagation",
        "mcp_2026_07_28", "mcp_connection_sharing", "mcp_lazy_stdio_startup", "mcp_shared_repl_cwd",
        "memories", "mentions_v2", "realtime_frames_local", "remote_control", "remote_plugin",
        "tool_suggest", "windows_sandbox_service"
    };

    private readonly object gate = new();
    private readonly CodexRuntimeFeatureRegistration[] definitions;
    private readonly Dictionary<string, CodexRuntimeFeatureRegistration> byName;
    private CodexRuntimeFeaturePolicy policy;
    private Dictionary<string, bool> runtime = new(StringComparer.Ordinal);
    private long generation;

    /// <summary>Creates a process registry from host-registered native consumers and their initial verified policy.</summary>
    public CodexRuntimeFeatureRegistry(IEnumerable<CodexRuntimeFeatureRegistration> registrations, CodexRuntimeFeaturePolicy initialPolicy)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        definitions = registrations.Take(4097).ToArray();
        if (definitions.Length > 4096 || definitions.Any(feature => !ValidName(feature.Name) ||
            feature.Stage is not ("beta" or "underDevelopment" or "stable" or "deprecated" or "removed") ||
            feature.Stage == "removed" && (feature.DefaultEnabled || feature.AllowRuntimeChange)) ||
            definitions.Select(feature => feature.Name).Distinct(StringComparer.Ordinal).Count() != definitions.Length)
            throw new ArgumentException("Invalid native feature registry.", nameof(registrations));
        byName = definitions.ToDictionary(feature => feature.Name, StringComparer.Ordinal);
        policy = CopyPolicy(initialPolicy);
    }

    /// <summary>Replaces the complete verified host policy without retaining runtime overrides for newly managed keys.
    /// A stale policy writer cannot restore an old permission. Re-read the host's current policy on conflict.</summary>
    public bool TryReplacePolicy(string expectedRevision, CodexRuntimeFeaturePolicy replacement)
    {
        var next = CopyPolicy(replacement);
        lock (gate)
        {
            if (policy.Revision != expectedRevision) return false;
            if (next.Revision == policy.Revision) throw new ArgumentException("A changed policy requires a new revision.", nameof(replacement));
            foreach (var name in next.Required.Keys) runtime.Remove(name);
            policy = next;
            generation = checked(generation + 1);
            return true;
        }
    }

    /// <summary>Hot-reloads configured feature defaults from an authenticated committed configuration stack.
    /// Preserves process runtime overrides and the existing authoritative managed policy. Legacy exact
    /// requirements must already be reflected in that policy; this method cannot grant new authority.
    /// Existing consumer captures remain immutable. Session-static model and thread settings are untouched.</summary>
    public bool TryReloadConfiguration(string expectedPolicyRevision, CodexThreadConfigurationSnapshot snapshot)
    {
        var resolved = CodexConfigurationResolver.Resolve(snapshot);
        var configured = ReadConfiguredFeatures(resolved.Config);
        var legacy = snapshot.Layers.Where(layer => layer.Kind is CodexConfigurationLayerKind.LegacyManagedFile or
            CodexConfigurationLayerKind.LegacyManagedMdm).ToArray();
        var required = legacy.Length == 0 ? new Dictionary<string, bool>(StringComparer.Ordinal) :
            ReadConfiguredFeatures(CodexConfigurationResolver.Resolve(new(snapshot.Revision, legacy)).Config);
        if (snapshot.Requirements is { } requirements && requirements.TryGetProperty("feature_requirements", out var managed))
        {
            if (managed.ValueKind != JsonValueKind.Object)
                throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigValidationError);
            foreach (var item in managed.EnumerateObject())
            {
                if (!byName.ContainsKey(item.Name)) continue;
                if (item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigValidationError);
                var enabled = item.Value.GetBoolean();
                if (required.TryGetValue(item.Name, out var prior) && prior != enabled)
                    throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigRequirementReadonly);
                required[item.Name] = enabled;
            }
        }

        lock (gate)
        {
            if (policy.Revision != expectedPolicyRevision) return false;
            foreach (var (key, value) in required)
                if (!policy.Required.TryGetValue(key, out var actual) || actual != value)
                    throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigRequirementReadonly);
            if (configured.Count == policy.Configured.Count && configured.All(item =>
                policy.Configured.TryGetValue(item.Key, out var value) && value == item.Value)) return true;
            policy = new(Guid.NewGuid().ToString("N"), new ReadOnlyDictionary<string, bool>(configured), policy.Required);
            generation = checked(generation + 1);
            return true;
        }
    }

    /// <summary>Post-commit consumer suitable for the file configuration storage reload callback.
    /// The authenticated host must hold its configuration-policy admission while invoking it.</summary>
    public Task ReloadConfigurationAsync(CodexThreadConfigurationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string expected;
        lock (gate) expected = policy.Revision;
        if (!TryReloadConfiguration(expected, snapshot))
            throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigVersionConflict);
        return Task.CompletedTask;
    }

    private Dictionary<string, bool> ReadConfiguredFeatures(JsonElement config)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!config.TryGetProperty("features", out var flags)) return result;
        if (flags.ValueKind != JsonValueKind.Object)
            throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigValidationError);
        foreach (var feature in definitions)
        {
            if (!flags.TryGetProperty(feature.Name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Object && !value.TryGetProperty("enabled", out value)) continue;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigValidationError);
            result.Add(feature.Name, value.GetBoolean());
        }
        return result;
    }

    /// <summary>Captures the current effective process state or an authenticated thread's configuration overrides.
    /// A thread cannot weaken process-managed requirements.</summary>
    public CodexRuntimeFeatureSnapshot Capture(IReadOnlyDictionary<string, bool>? threadConfiguration = null)
    {
        var thread = threadConfiguration is null ? null : CopyMap(threadConfiguration);
        lock (gate)
        {
            var values = new Dictionary<string, bool>(StringComparer.Ordinal);
            var states = new List<CodexExperimentalFeatureState>(definitions.Length);
            foreach (var feature in definitions)
            {
                var enabled = feature.DefaultEnabled;
                if (policy.Configured.TryGetValue(feature.Name, out var configured)) enabled = configured;
                if (thread is not null && thread.TryGetValue(feature.Name, out var threadValue)) enabled = threadValue;
                if (runtime.TryGetValue(feature.Name, out var overridden)) enabled = overridden;
                if (policy.Required.TryGetValue(feature.Name, out var required)) enabled = required;
                if (feature.Stage == "removed") enabled = false;
                values.Add(feature.Name, enabled);
                states.Add(new(feature.Name, feature.Stage, enabled, feature.DefaultEnabled,
                    feature.DisplayName, feature.Description, feature.Announcement));
            }
            return new(generation, policy.Revision, new ReadOnlyDictionary<string, bool>(values), states.AsReadOnly());
        }
    }

    /// <summary>Atomically changes only runtime-mutable registered keys against an authorized policy revision.
    /// Unknown and unsupported keys are omitted as in Rust. Managed conflicts reject the complete update.
    /// Returns only accepted keys; consumers use Capture to obtain the effective state.</summary>
    public IReadOnlyDictionary<string, bool> SetRuntimeEnablement(string expectedPolicyRevision,
        IReadOnlyDictionary<string, bool> requested)
    {
        var changes = CopyMap(requested);
        lock (gate)
        {
            if (policy.Revision != expectedPolicyRevision)
                throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigVersionConflict);
            var accepted = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var (key, enabled) in changes)
            {
                if (!UpstreamRuntimeKeys.Contains(key) || !byName.TryGetValue(key, out var definition) ||
                    !definition.AllowRuntimeChange || definition.Stage == "removed") continue;
                if (policy.Required.TryGetValue(key, out var required) && enabled != required)
                    throw new CodexConfigurationWriteException(CodexConfigurationWriteFailure.ConfigRequirementReadonly);
                accepted.Add(key, enabled);
            }
            var next = new Dictionary<string, bool>(runtime, StringComparer.Ordinal);
            foreach (var (key, enabled) in accepted)
            {
                // Do not leave a latent override behind a managed value.
                if (!policy.Required.ContainsKey(key)) next[key] = enabled;
            }
            if (next.Count != runtime.Count || next.Any(item => !runtime.TryGetValue(item.Key, out var value) || value != item.Value))
            {
                generation = checked(generation + 1);
                runtime = next;
            }
            return new ReadOnlyDictionary<string, bool>(accepted);
        }
    }

    private static CodexRuntimeFeaturePolicy CopyPolicy(CodexRuntimeFeaturePolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.Revision) || value.Revision.Length > 512 || value.Revision.Any(char.IsControl))
            throw new ArgumentException("Invalid feature policy revision.", nameof(value));
        return new(value.Revision, new ReadOnlyDictionary<string, bool>(CopyMap(value.Configured)),
            new ReadOnlyDictionary<string, bool>(CopyMap(value.Required)));
    }

    private static Dictionary<string, bool> CopyMap(IReadOnlyDictionary<string, bool> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > 4096) throw new ArgumentException("Feature map exceeds its bound.");
        var copy = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in source)
            if (!ValidName(item.Key) || !copy.TryAdd(item.Key, item.Value))
                throw new ArgumentException("Invalid feature key.");
        return copy;
    }

    private static bool ValidName(string? name) => name is { Length: > 0 and <= 128 } &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

/// <summary>Authenticated native experimentalFeature/enablement/set RPC. The host admission callback
/// binds the request to the current policy generation and may reject the entire mutation.
/// No background job or provider connection is started by setting a flag.</summary>
public sealed class CodexRuntimeFeatureRpc
{
    private readonly CodexRuntimeFeatureRegistry registry;
    private readonly Func<IReadOnlyDictionary<string, bool>, CancellationToken, Task<string>> authorize;

    /// <summary>Registers a process-scoped feature registry and explicit host mutation admission.</summary>
    public CodexRuntimeFeatureRpc(CodexRuntimeFeatureRegistry registry,
        Func<IReadOnlyDictionary<string, bool>, CancellationToken, Task<string>> authorize)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(authorize);
        this.registry = registry;
        this.authorize = authorize;
    }

    /// <summary>Whether this facade handles the runtime feature mutation method.</summary>
    public static bool Handles(string method) => method == "experimentalFeature/enablement/set";

    /// <summary>Validates an exact feature map and applies it atomically after host authorization.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported runtime feature method.");
        if (parameters is not { ValueKind: JsonValueKind.Object } p || Encoding.UTF8.GetByteCount(p.GetRawText()) > 65536)
            throw Invalid();
        var fields = p.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "enablement" || fields[0].Value.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var changes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var field in fields[0].Value.EnumerateObject())
        {
            if (changes.Count >= 4096 || field.Name.Length is < 1 or > 128 ||
                field.Name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) ||
                field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !changes.TryAdd(field.Name, field.Value.GetBoolean())) throw Invalid();
        }
        cancellationToken.ThrowIfCancellationRequested();
        var revision = await authorize(new ReadOnlyDictionary<string, bool>(changes), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = registry.SetRuntimeEnablement(revision, changes);
        var result = new JsonObject();
        foreach (var (key, enabled) in accepted) result[key] = enabled;
        using var document = JsonDocument.Parse(new JsonObject { ["enablement"] = result }.ToJsonString());
        return document.RootElement.Clone();
    }

    private static CodexThreadRpcException Invalid() => new(-32602, "Invalid runtime feature enablement.");
}
