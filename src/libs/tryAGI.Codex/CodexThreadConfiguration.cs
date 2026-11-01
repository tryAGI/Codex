using System.Text.Json;

namespace tryAGI.Codex;

// Sources are supplied by the authenticated host, never by an RPC config value.
public enum CodexConfigurationLayerKind { Package, Admin, System, Cloud, User, Profile, Project, Runtime }
public sealed record CodexThreadConfigurationLayer(CodexConfigurationLayerKind Kind, string Name, JsonElement Config,
    bool Trusted = true);
public sealed record CodexThreadConfigurationContext(string Method, Guid? ThreadId, string WorkingDirectory);
public sealed record CodexThreadConfigurationSnapshot(string Revision, IReadOnlyList<CodexThreadConfigurationLayer> Layers,
    JsonElement? Requirements = null);
