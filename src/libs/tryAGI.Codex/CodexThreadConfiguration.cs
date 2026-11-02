using System.Text.Json;

namespace tryAGI.Codex;

// Sources are supplied by the authenticated host, never by an RPC config value.

/// <summary>Where a configuration layer comes from, lowest precedence first.</summary>
public enum CodexConfigurationLayerKind
{
    /// <summary>Defaults shipped with the host package.</summary>
    Package,

    /// <summary>Administrator-managed configuration.</summary>
    Admin,

    /// <summary>Machine-wide configuration.</summary>
    System,

    /// <summary>Configuration delivered by a cloud service.</summary>
    Cloud,

    /// <summary>The user's <c>config.toml</c> in <c>CODEX_HOME</c>.</summary>
    User,

    /// <summary>A named profile selected from the user configuration.</summary>
    Profile,

    /// <summary>A trusted project's <c>.codex/config.toml</c>.</summary>
    Project,

    /// <summary>Overrides supplied with the request.</summary>
    Runtime,
}

/// <summary>One configuration layer, already converted from TOML to JSON.</summary>
/// <param name="Kind">Layer source.</param>
/// <param name="Name">Display name, for example the file path.</param>
/// <param name="Config">Configuration values.</param>
/// <param name="Trusted">Untrusted layers cannot set security-sensitive keys.</param>
public sealed record CodexThreadConfigurationLayer(CodexConfigurationLayerKind Kind, string Name, JsonElement Config,
    bool Trusted = true);

/// <summary>What a configuration source is asked to resolve.</summary>
/// <param name="Method">The thread RPC method being served.</param>
/// <param name="ThreadId">The thread, when it already exists.</param>
/// <param name="WorkingDirectory">Absolute working directory of the thread.</param>
public sealed record CodexThreadConfigurationContext(string Method, Guid? ThreadId, string WorkingDirectory);

/// <summary>The resolved configuration layers for one thread.</summary>
/// <param name="Revision">Opaque value that changes whenever any layer changes.</param>
/// <param name="Layers">Layers, lowest precedence first.</param>
/// <param name="Requirements">Constraints from <c>requirements.toml</c> that settings must satisfy.</param>
public sealed record CodexThreadConfigurationSnapshot(string Revision, IReadOnlyList<CodexThreadConfigurationLayer> Layers,
    JsonElement? Requirements = null);
