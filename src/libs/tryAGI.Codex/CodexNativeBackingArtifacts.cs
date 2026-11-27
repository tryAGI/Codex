using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Observed lifecycle of a host-produced canonical backing item.</summary>
public enum CodexNativeArtifactStage
{
    /// <summary>The item started, but no terminal result is claimed.</summary>
    Started,
    /// <summary>The host observed the exact terminal item, including any failure.</summary>
    Completed,
}

/// <summary>Canonical public backing item with an observed source dispatch and stable
/// position in the original turn. This record is not a model response or dispatch authorization.</summary>
public sealed record CodexStoredNativeBackingArtifact(
    [property: JsonRequired] string ItemId,
    [property: JsonRequired] string SourceDispatchId,
    [property: JsonRequired] int OutputBoundary,
    [property: JsonRequired] CodexNativeArtifactStage Stage,
    [property: JsonRequired] JsonElement Item,
    long? StartedAtMs = null,
    long? CompletedAtMs = null);

public sealed partial class CodexThreadStore
{
    private const int MaximumNativeArtifactsPerTurn = 256;
    private const int MaximumNativeArtifactBytes = 2 * 1024 * 1024;

    /// <summary>Record an actual host-observed image generation, sub-agent activity, or MCP call
    /// in canonical App Server item form. The trusted host must supply the exact turn owner
    /// and previously admitted source dispatch. This method never generates images, spawns an
    /// agent, invokes an MCP server, or grants execution authority. Repeated observations are
    /// idempotent; a terminal item cannot be rewritten.</summary>
    public async Task<CodexThreadAdmission> ObserveNativeBackingArtifactAsync(Guid threadId, long expectedRevision,
        Guid turnId, string ownerId, string sourceDispatchId, CodexNativeArtifactStage stage,
        JsonElement item, CancellationToken cancellationToken = default)
    {
        var before = RequireUnarchived(await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false));
        var turn = before.Turns.SingleOrDefault(t => t.Id == turnId && t.OwnerId == ownerId)
            ?? throw new InvalidOperationException("Native backing artifact turn or owner does not match.");
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("A canonical item object is required.", nameof(item));
        var itemId = CodexRealtimeProtocol.Text(item, "id");
        var current = turn.NativeBackingArtifacts ?? [];
        var previous = current.SingleOrDefault(a => a.ItemId == itemId);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var artifact = new CodexStoredNativeBackingArtifact(itemId ?? "", sourceDispatchId,
            previous?.OutputBoundary ?? turn.Output.Length, stage, item.Clone(),
            previous?.StartedAtMs ?? (stage == CodexNativeArtifactStage.Started ? now : null),
            stage == CodexNativeArtifactStage.Completed ? Math.Max(now, previous?.StartedAtMs ?? now) : null);
        ValidateNativeArtifact(turn, artifact);
        if (previous is not null)
        {
            if (previous.SourceDispatchId != sourceDispatchId || !SameNativeArtifactIdentity(previous.Item, item))
                throw new InvalidOperationException("Native backing artifact identity changed.");
            if (previous.Stage == stage && JsonElement.DeepEquals(previous.Item, item)) return new(false, before);
            if (previous.Stage == CodexNativeArtifactStage.Completed || stage != CodexNativeArtifactStage.Completed)
                throw new InvalidOperationException("Native backing artifact lifecycle cannot be rewritten.");
        }

        // A non-app MCP call is never promoted by Rust. Its native type must be observed
        // before generic tool promotion; never retroactively invalidate a published item.
        var type = CodexRealtimeProtocol.Text(item, "type");
        if (type == "mcpToolCall" && (CodexRealtimeProtocol.Text(item, "server") != "codex_app" ||
            stage != CodexNativeArtifactStage.Completed || CodexRealtimeProtocol.Text(item, "status") != "completed") &&
            (before.RealtimeSessions ?? []).Any(s => s.History?.Events.Any(e => e.Promotion is { } p &&
                p.TurnId == turnId && p.ItemId == itemId && p.Presentation == "wholeItem") == true))
            throw new InvalidOperationException("MCP presentation classification must precede generic tool promotion.");

        var updated = turn with
        {
            NativeBackingArtifacts = previous is null ? [.. current, artifact] :
                current.Select(a => a.ItemId == itemId ? artifact : a).ToArray(),
        };
        ValidateNativeBackingArtifacts(updated);
        var next = PromoteRealtimeArtifacts(Replace(before, updated), turn, updated);
        return new(true, await PublishAsync(before.Revision, next with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, cancellationToken).ConfigureAwait(false));
    }

    private static void ValidateNativeBackingArtifacts(CodexStoredTurn turn)
    {
        if (turn.NativeBackingArtifacts is not { } artifacts) return;
        if (artifacts.Length > MaximumNativeArtifactsPerTurn)
            throw new InvalidDataException("Native backing artifacts exceed the turn's public presentation budget.");
        long bytes = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var mcpSources = new HashSet<string>(StringComparer.Ordinal);
        var boundary = 0;
        foreach (var artifact in artifacts)
        {
            ValidateNativeArtifact(turn, artifact);
            bytes += Encoding.UTF8.GetByteCount(artifact.Item.GetRawText());
            if (bytes > MaximumNativeArtifactBytes)
                throw new InvalidDataException("Native backing artifacts exceed the turn's public presentation budget.");
            if (!ids.Add(artifact.ItemId) || artifact.OutputBoundary < boundary ||
                CodexRealtimeProtocol.Text(artifact.Item, "type") == "mcpToolCall" && !mcpSources.Add(artifact.SourceDispatchId))
                throw new InvalidDataException("Native backing artifact identity or order changed.");
            boundary = artifact.OutputBoundary;
        }
    }

    private static void ValidateNativeArtifact(CodexStoredTurn turn, CodexStoredNativeBackingArtifact artifact)
    {
        if (artifact is null || !ValidToken(artifact.ItemId) || !ValidToken(artifact.SourceDispatchId) ||
            artifact.OutputBoundary < 0 || artifact.OutputBoundary > turn.Output.Length ||
            !Enum.IsDefined(artifact.Stage) || artifact.Item.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(artifact.Item.GetRawText()) > MaximumNativeArtifactBytes ||
            CodexRealtimeProtocol.Text(artifact.Item, "id") != artifact.ItemId ||
            artifact.StartedAtMs is < 0 || artifact.CompletedAtMs is < 0 ||
            artifact.Stage == CodexNativeArtifactStage.Started && (artifact.StartedAtMs is null || artifact.CompletedAtMs is not null) ||
            artifact.Stage == CodexNativeArtifactStage.Completed &&
                (artifact.CompletedAtMs is null || artifact.CompletedAtMs < artifact.StartedAtMs))
            throw new InvalidDataException("Invalid native backing artifact lifecycle or identity.");
        var source = turn.Dispatches.SingleOrDefault(d => d.CallId == artifact.SourceDispatchId);
        if (source is null || source.Result is { ValueKind: JsonValueKind.Object } result &&
            result.TryGetProperty("notDispatched", out var notSent) && notSent.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Native backing artifact has no admitted source dispatch.");
        var item = artifact.Item;
        var type = CodexRealtimeProtocol.Text(item, "type");
        var status = CodexRealtimeProtocol.Text(item, "status");
        var raw = RealtimeTimeline(turn, true).Select((value, index) =>
            (Id: CodexRealtimeProtocol.Text(value, "id") ?? turn.Id + ":" + index, Item: value))
            .Where(x => x.Id == artifact.ItemId).ToArray();
        if (type == "mcpToolCall")
        {
            if (source.Kind != "tool" || raw.Length != 1 ||
                CodexRealtimeProtocol.Text(raw[0].Item, "type") is not ("function_call" or "custom_tool_call") ||
                CodexRealtimeProtocol.Text(raw[0].Item, "call_id") != source.CallId ||
                !ValidToken(CodexRealtimeProtocol.Text(item, "server") ?? "") ||
                !ValidToken(CodexRealtimeProtocol.Text(item, "tool") ?? "") ||
                !item.TryGetProperty("arguments", out _) ||
                (artifact.Stage == CodexNativeArtifactStage.Started ? status != "inProgress" : status is not ("completed" or "failed")) ||
                status == "completed" && source.State == "failed")
                throw new InvalidDataException("MCP artifact does not match the admitted backing tool item.");
            if (item.TryGetProperty("result", out var mcpResult) && mcpResult.ValueKind != JsonValueKind.Null &&
                (mcpResult.ValueKind != JsonValueKind.Object || !mcpResult.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array))
                throw new InvalidDataException("MCP result is not a canonical observed content list.");
            return;
        }
        if (raw.Length != 0) throw new InvalidDataException("Native backing artifact collides with a model item.");
        if (type == "imageGeneration")
        {
            var hasFile = CodexRealtimeProtocol.Text(item, "fileId") is { Length: > 0 };
            var hasResult = item.TryGetProperty("result", out var image) && image.ValueKind == JsonValueKind.String;
            if (!ValidToken(status ?? "") || !hasFile && !hasResult ||
                artifact.Stage == CodexNativeArtifactStage.Completed && status is "inProgress" or "in_progress")
                throw new InvalidDataException("Image artifact lacks an observed status or image reference.");
            return;
        }
        if (type == "subAgentActivity" &&
            CodexRealtimeProtocol.Text(item, "kind") is "started" or "interacted" or "interrupted" or "completed" &&
            ValidToken(CodexRealtimeProtocol.Text(item, "agentThreadId") ?? "") &&
            item.TryGetProperty("agentPath", out var path) && path.ValueKind == JsonValueKind.String &&
            path.GetString()!.Length <= 512)
            return;
        throw new InvalidDataException("Unsupported canonical native backing artifact.");
    }

    private static bool SameNativeArtifactIdentity(JsonElement before, JsonElement after)
    {
        var type = CodexRealtimeProtocol.Text(before, "type");
        if (type != CodexRealtimeProtocol.Text(after, "type")) return false;
        static bool Equal(JsonElement a, JsonElement b, string name)
            => a.TryGetProperty(name, out var first) == b.TryGetProperty(name, out var second) &&
                (first.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(first, second));
        return type switch
        {
            "mcpToolCall" => Equal(before, after, "server") && Equal(before, after, "tool") && Equal(before, after, "arguments"),
            "subAgentActivity" => Equal(before, after, "kind") && Equal(before, after, "agentThreadId") && Equal(before, after, "agentPath"),
            "imageGeneration" => CodexRealtimeProtocol.Text(before, "generationId") is not { } id ||
                id == CodexRealtimeProtocol.Text(after, "generationId"),
            _ => false,
        };
    }

    internal static JsonObject NativeArtifactItem(CodexStoredNativeBackingArtifact artifact)
        => JsonNode.Parse(artifact.Item.GetRawText())!.AsObject();

    internal static bool IsNativeMcpArtifact(CodexStoredNativeBackingArtifact artifact)
        => CodexRealtimeProtocol.Text(artifact.Item, "type") == "mcpToolCall";
}
