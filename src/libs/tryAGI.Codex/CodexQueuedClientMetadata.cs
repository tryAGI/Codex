using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>The exact account-bound queued submission whose extra provider metadata needs
/// host authorization. Client metadata is not authorization or a routing identity.</summary>
/// <param name="AccountId">Authenticated journal account.</param>
/// <param name="ThreadId">Thread owning the queued submission.</param>
/// <param name="SubmissionId">Stable queued submission identity.</param>
/// <param name="ExpectedRevision">Observed journal revision, rechecked before admission.</param>
/// <param name="Metadata">Original durable client metadata.</param>
public sealed record CodexQueuedMetadataContext(string AccountId, Guid ThreadId, Guid SubmissionId,
    long ExpectedRevision, JsonElement Metadata);

internal static class CodexQueuedClientMetadata
{
    // Pinned Rust core/src/responses_metadata.rs reserves these for Core, not the client.
    // Also reserve native routing and policy fields, case-insensitively.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "guardian_credits_requested", "guardian_recorded_items", "mcp_attribution",
        "installation_id", "session_id", "thread_id", "agent_name", "turn_id",
        "window_id", "window_number", "context_window_id", "request_kind", "compaction",
        "code_mode_tool_names", "tool_namespaces_info", "turn_started_at_unix_ms",
        "history_ingest_requested", "analytics_enabled", "history_initialization",
        "forked_from_thread_id", "forked_from_ordinal_exclusive", "parent_thread_id",
        "parent_turn_id", "root_turn_id", "subagent_kind", "thread_source", "turn_trigger",
        "sandbox", "sandbox_mode", "auto_review_enabled", "node_repl_auto_review_required",
        "node_repl_disabled", "workspaces", "model", "reasoning_effort", "codex_version",
        "user_input_requested_during_turn", "workspace_kind", "account_id", "routing_hint",
        "authorization", "approval_policy", "approvals_reviewer",
        "x-codex-installation-id", "x-codex-window-id", "x-codex-turn-metadata",
        "x-codex-parent-thread-id", "x-openai-subagent", "x-client-request-id",
        "session-id", "thread-id"
    };

    internal static JsonElement Filter(JsonElement approved)
    {
        if (approved.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(approved.GetRawText()) > 65_536)
            throw new ArgumentException("Approved queued metadata must be a string map.");
        var result = new JsonObject();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in approved.EnumerateObject())
        {
            var key = property.Name;
            if (!names.Add(key) || names.Count > 64 || property.Value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Approved queued metadata must have unique string fields.");
            if (Reserved.Contains(key)) continue;
            var value = property.Value.GetString()!;
            if (key.Length is < 1 or > 64 || !char.IsAsciiLetter(key[0])
                || key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '.' or '-'))
                || Encoding.UTF8.GetByteCount(value) > 128 || value.Any(char.IsControl)
                || result.Count >= 16)
                throw new ArgumentException("Approved queued metadata exceeds the native metadata bounds.");
            result[key] = value;
        }
        using var document = JsonDocument.Parse(result.ToJsonString());
        return document.RootElement.Clone();
    }
}
