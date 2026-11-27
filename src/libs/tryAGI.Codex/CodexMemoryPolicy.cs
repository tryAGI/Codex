using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>The Rust memory pipeline and its generated-artifact namespace.</summary>
public enum CodexMemoryVersion
{
    /// <summary>Original memory pipeline.</summary>
    V1,
    /// <summary>Second-generation memory pipeline.</summary>
    V2,
}

/// <summary>
/// Thread-pinned memory feature policy. These are necessary gates, not account
/// authorization; the host must also authorize storage access and extraction.
/// </summary>
public sealed class CodexMemoryPolicy
{
    /// <summary>Creates the effective memory read policy for a newly started thread.</summary>
    public CodexMemoryPolicy(bool memoryToolEnabled, bool useMemories, bool dedicatedTools,
        CodexMemoryVersion version = CodexMemoryVersion.V1)
    {
        if (version is not (CodexMemoryVersion.V1 or CodexMemoryVersion.V2))
            throw new ArgumentOutOfRangeException(nameof(version));

        MemoryToolEnabled = memoryToolEnabled;
        UseMemories = useMemories;
        DedicatedTools = dedicatedTools;
        Version = version;
    }

    /// <summary>Whether the MemoryTool feature is enabled.</summary>
    public bool MemoryToolEnabled { get; }

    /// <summary>Whether effective configuration permits reading memories.</summary>
    public bool UseMemories { get; }

    /// <summary>Whether dedicated retrieval tools are configured.</summary>
    public bool DedicatedTools { get; }

    /// <summary>The immutable version selected when the thread started.</summary>
    public CodexMemoryVersion Version { get; }

    /// <summary>Relative namespace; the host resolves it under the authorized account root.</summary>
    public string DirectoryName => Version == CodexMemoryVersion.V1 ? "memories" : "memories_v2";

    /// <summary>Whether memory prompt context is enabled before host authorization.</summary>
    public bool IsReadPathEnabled => MemoryToolEnabled && UseMemories;

    /// <summary>Whether dedicated memory retrieval tools may be contributed.</summary>
    public bool AreDedicatedToolsEnabled => IsReadPathEnabled && DedicatedTools;

    /// <summary>
    /// Re-evaluates live feature flags without moving a running thread to another
    /// memory namespace. A new thread is required to select another version.
    /// </summary>
    public CodexMemoryPolicy WithConfiguration(bool memoryToolEnabled, bool useMemories, bool dedicatedTools)
        => new(memoryToolEnabled, useMemories, dedicatedTools, Version);

    /// <summary>
    /// Checks the Rust rollout extraction allowlist for a single response item.
    /// This does not grant permission to generate memory or execute any tool.
    /// Unknown discriminators and malformed message roles fail closed. The caller
    /// validates complete item payloads; the original history is never changed.
    /// </summary>
    public static bool IsExtractionCandidate(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
            return false;

        // Rust excludes developer messages, reasoning, compaction and transient metadata.
        return type.GetString() switch
        {
            "message" => item.TryGetProperty("role", out var role) &&
                role.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(role.GetString()) && role.GetString() != "developer",
            "agent_message" or "local_shell_call" or "function_call" or "tool_search_call" or
            "function_call_output" or "tool_search_output" or "custom_tool_call" or
            "custom_tool_call_output" or "web_search_call" => true,
            _ => false,
        };
    }
}
