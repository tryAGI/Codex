using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace tryAGI.Codex;

// All paths are authenticated host inputs, never RPC config values. No environment,
// network, subprocess, or server-operator home is consulted by this reader.
/// <summary>Which on-disk Codex configuration files to read.</summary>
/// <param name="CodexHome">Absolute <c>CODEX_HOME</c> holding the user's <c>config.toml</c>.</param>
public sealed record CodexFileConfigurationOptions(string CodexHome)
{
    /// <summary>Optional configuration shipped with the host.</summary>
    public string? PackagedConfigFile { get; init; }
    /// <summary>Optional machine-wide configuration file.</summary>
    public string? SystemConfigFile { get; init; }
    /// <summary>Optional administrator-managed configuration file.</summary>
    public string? AdminConfigFile { get; init; }
    /// <summary>Configuration files delivered by a cloud service.</summary>
    public IReadOnlyList<string> CloudConfigFiles { get; init; } = [];
    /// <summary><c>requirements.toml</c> files whose constraints settings must satisfy.</summary>
    public IReadOnlyList<string> RequirementsFiles { get; init; } = [];
    /// <summary>Profile to apply from the user configuration.</summary>
    public string? Profile { get; init; }
    /// <summary>Skip <c>.codex/config.toml</c> files in the working tree.</summary>
    public bool IgnoreProjectConfig { get; init; }
}

/// <summary>Reads Codex <c>config.toml</c> layers from disk the way the Codex CLI does, for <see cref="CodexThreadRpc"/>.</summary>
/// <param name="options">Files to read.</param>
public sealed class CodexFileConfigurationSource(CodexFileConfigurationOptions options)
{
    private const int MaximumBytes = 1_048_576;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] ProjectDenied = ["openai_base_url", "chatgpt_base_url", "apps_mcp_product_sku",
        "responses_api_metadata", "model_provider", "model_providers", "notify", "profile", "profiles",
        "experimental_realtime_webrtc_call_base_url", "experimental_realtime_ws_base_url", "otel"];

    /// <summary>Reads all layers for one thread. Symbolic links are rejected; file sizes and counts are bounded.</summary>
    /// <param name="context">Thread and working directory being resolved.</param>
    /// <param name="account">Layers and requirements already supplied by the host for the account.</param>
    /// <param name="ct">Cancels the read.</param>
    public async Task<CodexThreadConfigurationSnapshot> LoadAsync(CodexThreadConfigurationContext context,
        CodexThreadConfigurationSnapshot account, CancellationToken ct)
    {
        var home = Absolute(options.CodexHome);
        var layers = new List<CodexThreadConfigurationLayer>();
        var discovery = new JsonObject();
        var requirements = account.Requirements is { } required ? JsonNode.Parse(required.GetRawText())?.AsObject() ?? new() : new JsonObject();
        using var revision = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        revision.AppendData(Utf8.GetBytes(account.Revision));
        var total = 0; var files = 0;

        async Task<JsonObject?> Read(string path, bool mandatory = false)
        {
            ct.ThrowIfCancellationRequested(); path = Absolute(path);
            if (++files > 64) throw Invalid("Too many configuration files.");
            RejectLinks(path);
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumBytes) throw Invalid("Configuration file exceeds its bound.");
                var buffer = new byte[MaximumBytes + 1]; var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                total = checked(total + count);
                if (count > MaximumBytes || total > MaximumBytes) throw Invalid("Configuration sources exceed their aggregate bound.");
                revision.AppendData(Utf8.GetBytes(path)); revision.AppendData(buffer.AsSpan(0, count));
                var table = TomlSerializer.Deserialize(Utf8.GetString(buffer, 0, count).TrimStart('\uFEFF'), CodexConfigurationTomlContext.Default.TomlTable)
                    ?? throw Invalid("Configuration TOML has no root table.");
                return Convert(table, 0) as JsonObject ?? throw Invalid("Invalid configuration root.");
            }
            catch (FileNotFoundException) when (!mandatory) { return null; }
            catch (DirectoryNotFoundException) when (!mandatory) { return null; }
            catch (TomlException) { throw Invalid("Invalid TOML configuration source."); }
            catch (DecoderFallbackException) { throw Invalid("Configuration sources must be valid UTF-8."); }
        }

        void Add(CodexConfigurationLayerKind kind, string name, JsonObject config, bool trusted = true)
        {
            if (layers.Count >= 32) throw Invalid("Too many configuration layers.");
            // Discovery-only keys have real consumers here, not in model options.
            if (trusted)
                foreach (var key in new[] { "projects", "project_root_markers", "profile", "profiles" })
                    if (config.TryGetPropertyValue(key, out var value))
                    {
                        if (value is JsonObject incoming && discovery[key] is JsonObject existing) MergeDiscovery(existing, incoming, 0);
                        else discovery[key] = value?.DeepClone();
                    }
            foreach (var key in new[] { "projects", "project_root_markers", "profile", "profiles" }) config.Remove(key);
            layers.Add(new(kind, name, Element(config), trusted));
        }

        async Task FileLayer(string? path, CodexConfigurationLayerKind kind, bool mandatory = false)
        {
            if (path is not null && await Read(path, mandatory).ConfigureAwait(false) is { } config) Add(kind, path, config);
        }

        await FileLayer(options.PackagedConfigFile, CodexConfigurationLayerKind.Package, true).ConfigureAwait(false);
        await FileLayer(options.AdminConfigFile, CodexConfigurationLayerKind.Admin).ConfigureAwait(false);
        await FileLayer(options.SystemConfigFile, CodexConfigurationLayerKind.System).ConfigureAwait(false);
        foreach (var path in options.CloudConfigFiles) await FileLayer(path, CodexConfigurationLayerKind.Cloud, true).ConfigureAwait(false);
        var userFile = Path.Combine(home, "config.toml");
        await FileLayer(userFile, CodexConfigurationLayerKind.User).ConfigureAwait(false);
        foreach (var layer in account.Layers)
        {
            if (layer.Kind != CodexConfigurationLayerKind.User || !layer.Trusted) throw Invalid("Account defaults must be a trusted user layer.");
            Add(layer.Kind, layer.Name, JsonNode.Parse(layer.Config.GetRawText()) as JsonObject ?? throw Invalid("Invalid account layer."));
            revision.AppendData(Utf8.GetBytes(layer.Config.GetRawText()));
        }
        var profile = options.Profile ?? discovery["profile"]?.GetValue<string>();
        if (profile is not null)
        {
            if (profile.Length is < 1 or > 64 || profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw Invalid("Invalid configuration profile name.");
            var legacy = (discovery["profiles"] as JsonObject)?[profile] as JsonObject;
            if (options.Profile is not null && (legacy is not null || discovery["profile"]?.GetValue<string>() == profile))
                throw Invalid("File profiles cannot be mixed with legacy profile selection.");
            if (options.Profile is null && legacy is not null) Add(CodexConfigurationLayerKind.Profile, "profiles." + profile, (JsonObject)legacy.DeepClone());
            else await FileLayer(Path.Combine(home, profile + ".config.toml"), CodexConfigurationLayerKind.Profile, true).ConfigureAwait(false);
        }
        if (!options.IgnoreProjectConfig)
        {
            var cwd = Absolute(context.WorkingDirectory);
            var markers = discovery["project_root_markers"] is JsonArray array
                ? array.Select(node => node is JsonValue marker && marker.TryGetValue<string>(out var text)
                    ? text : throw Invalid("Invalid root marker.")).ToArray() : [".git"];
            if (markers.Length > 32 || markers.Any(m => string.IsNullOrWhiteSpace(m) || m.Contains('/') || m.Contains('\\') || m is "." or ".."))
                throw Invalid("Invalid project root markers.");
            var ancestors = new List<string>(); var root = cwd;
            for (var directory = new DirectoryInfo(cwd); directory is not null; directory = directory.Parent)
            {
                if (ancestors.Count >= 128) throw Invalid("Project discovery exceeds its bound.");
                ancestors.Add(directory.FullName);
                if (markers.Any(marker => marker == ".git" && Directory.Exists(Path.Combine(directory.FullName, marker))
                    ? File.Exists(Path.Combine(directory.FullName, marker, "HEAD"))
                    : Path.Exists(Path.Combine(directory.FullName, marker)))) { root = directory.FullName; break; }
            }
            // With no marker the pinned loader treats cwd as project root.
            ancestors = ancestors.Take(ancestors.IndexOf(root) + 1).Reverse().ToList();
            var projects = discovery["projects"] as JsonObject;
            foreach (var directory in ancestors)
            {
                var path = Path.Combine(directory, ".codex", "config.toml");
                if (Absolute(Path.GetDirectoryName(path) ?? throw Invalid("Missing config parent.")) == home || !File.Exists(path)) continue;
                var trust = projects?[directory] as JsonObject ?? projects?[root] as JsonObject;
                var trusted = trust?["trust_level"]?.GetValue<string>() == "trusted";
                revision.AppendData(Utf8.GetBytes(path + (trusted ? ":trusted" : ":disabled")));
                if (!trusted) { Add(CodexConfigurationLayerKind.Project, path, new(), false); continue; }
                var project = await Read(path, true).ConfigureAwait(false) ?? throw Invalid("Missing project configuration.");
                // Match the pinned loader's security boundary; rejected project keys
                // cannot redirect credentials/providers even in a trusted repository.
                foreach (var key in ProjectDenied)
                    if (project.ContainsKey(key)) throw Invalid("Project-local setting must be configured in a trusted user source: " + key);
                Add(CodexConfigurationLayerKind.Project, path, project);
            }
        }
        foreach (var path in options.RequirementsFiles)
        {
            var values = await Read(path, true).ConfigureAwait(false) ?? throw Invalid("Missing requirements source.");
            foreach (var item in values)
            {
                var name = item.Key switch
                {
                    "allowed_approval_policies" => "approval_policy", "allowed_sandbox_modes" => "sandbox_mode",
                    "allowed_approvals_reviewers" => "approvals_reviewer", _ => throw Invalid("Unsupported managed requirement: " + item.Key)
                };
                if (item.Value is not JsonArray allowed || allowed.Count is < 1 or > 64 || allowed.Any(value => value is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
                    throw Invalid("Invalid managed allowed values.");
                var previous = requirements[name] as JsonArray;
                var intersection = new JsonArray(allowed.Where(value => previous is null || previous.Any(old => JsonNode.DeepEquals(old, value))).Select(value => value?.DeepClone()).ToArray());
                if (intersection.Count == 0) throw Invalid("Managed requirements have no common allowed value.");
                requirements[name] = intersection;
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(System.Convert.ToHexString(revision.GetHashAndReset()), layers, requirements.Count == 0 ? null : Element(requirements));
    }

    private static JsonNode? Convert(object? value, int depth)
    {
        if (depth > 32) throw Invalid("Configuration exceeds its depth bound.");
        switch (value)
        {
            case TomlTable table:
                var result = new JsonObject();
                foreach (var item in table) result.Add(item.Key, Convert(item.Value, depth + 1));
                return result;
            case TomlArray array: return new JsonArray(array.Select(item => Convert(item, depth + 1)).ToArray());
            case TomlTableArray array: return new JsonArray(array.Select(item => Convert(item, depth + 1)).ToArray());
            case string text: return JsonValue.Create(text);
            case bool boolean: return JsonValue.Create(boolean);
            case long number: return JsonValue.Create(number);
            case double number when double.IsFinite(number): return JsonValue.Create(number);
            default: throw Invalid("Unsupported native TOML scalar.");
        }
    }

    private static string Absolute(string path) => Path.IsPathFullyQualified(path) && path.Length <= 4096 && !path.Any(char.IsControl)
        ? Path.GetFullPath(path) : throw Invalid("Configuration paths must be absolute and bounded.");
    private static void MergeDiscovery(JsonObject target, JsonObject source, int depth)
    {
        if (depth > 32) throw Invalid("Configuration discovery exceeds its depth bound.");
        foreach (var item in source)
        {
            if (item.Value is JsonObject incoming && target[item.Key] is JsonObject existing) MergeDiscovery(existing, incoming, depth + 1);
            else target[item.Key] = item.Value?.DeepClone();
        }
    }
    private static void RejectLinks(string path)
    {
        for (FileSystemInfo? item = new FileInfo(path); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.LinkTarget is not null) throw Invalid("Configuration symlinks require host path resolution.");
    }
    private static JsonElement Element(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
}

[TomlSerializable(typeof(TomlTable))]
[TomlSourceGenerationOptions(MaxDepth = 32)]
internal partial class CodexConfigurationTomlContext : TomlSerializerContext;
