using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>A persisted reference to a backing turn artifact, not a copy of its content.</summary>
public sealed record CodexStoredRealtimePromotion(
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] Guid TurnId,
    [property: JsonRequired] string ItemId,
    [property: JsonRequired] string Presentation,
    int? Index = null,
    CodexStoredRealtimeArtifactPreview? Preview = null);

internal static class CodexRealtimePresentation
{
    // Pinned Rust v0.172.0: core/src/realtime_history/presentation.rs.
    internal static IEnumerable<(string Kind, int? Index)> AssistantPresentations(string text)
    {
        var first = text.TrimStart().Split('\n')[0].TrimEnd('\r');
        if (first.StartsWith('[') && first.IndexOf(']') is var bracket && bracket >= 0)
        {
            first = first[(bracket + 1)..].TrimStart();
            if (first.Length == 0)
                first = text.TrimStart().Split('\n').Skip(1).FirstOrDefault()?.TrimEnd('\r') ?? "";
        }
        if (first == "::codex-realtime-inline{}" && text.Contains('\n'))
        {
            yield return ("inlineMarkdown", null);
            yield break;
        }

        var inFence = false;
        var index = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (!inFence && (trimmed.StartsWith("::codex-inline-vis{", StringComparison.Ordinal) ||
                trimmed.StartsWith("\uE200visualize\uE202{", StringComparison.Ordinal)))
                yield return ("inlineVisualization", index++);
        }
    }

    internal static IEnumerable<CodexStoredRealtimePromotion> Eligible(CodexStoredTurn turn, string session)
    {
        var index = 0;
        foreach (var item in CodexThreadStore.RealtimeTimeline(turn, true))
        {
            // The fallback identity must match the existing native thread item projection.
            var itemId = CodexRealtimeProtocol.Text(item, "id") ?? turn.Id + ":" + index;
            index++;
            var type = CodexRealtimeProtocol.Text(item, "type");
            var role = CodexRealtimeProtocol.Text(item, "role");
            if (role == "assistant" && type is null or "message")
            {
                var content = item.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.Array
                    ? string.Concat(value.EnumerateArray().Where(x => CodexRealtimeProtocol.Text(x, "type") == "output_text")
                        .Select(x => CodexRealtimeProtocol.Text(x, "text") ?? ""))
                    : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                foreach (var (kind, position) in AssistantPresentations(content))
                    yield return new(session, turn.Id, itemId, kind, position);
            }
            else if (type is "function_call" or "custom_tool_call")
            {
                if (turn.NativeBackingArtifacts?.Any(a => a.ItemId == itemId && CodexThreadStore.IsNativeMcpArtifact(a)) == true)
                    continue;
                var call = CodexRealtimeProtocol.Text(item, "call_id");
                // Do not promote an unknown dispatch, an unsuccessful tool or a bare receipt.
                var receipt = turn.Dispatches.SingleOrDefault(d => d.Kind == "tool" && d.CallId == call);
                if (receipt is { State: "succeeded", Result: { } result } &&
                    CodexRealtimeProtocol.Text(result, "call_id") == call &&
                    result.TryGetProperty("output", out _) &&
                    (!result.TryGetProperty("success", out var success) || success.ValueKind == JsonValueKind.True))
                    yield return new(session, turn.Id, itemId, "wholeItem");
            }
        }
        foreach (var artifact in turn.NativeBackingArtifacts ?? [])
        {
            var type = CodexRealtimeProtocol.Text(artifact.Item, "type");
            if (type == "imageGeneration" ||
                artifact.Stage == CodexNativeArtifactStage.Completed &&
                (type == "subAgentActivity" && CodexRealtimeProtocol.Text(artifact.Item, "kind") == "started" ||
                    type == "mcpToolCall" && CodexRealtimeProtocol.Text(artifact.Item, "server") == "codex_app" &&
                    CodexRealtimeProtocol.Text(artifact.Item, "status") == "completed"))
                yield return new(session, turn.Id, artifact.ItemId, "wholeItem");
        }
    }

    internal static JsonObject Json(CodexStoredRealtimePromotion promotion)
    {
        var presentation = new JsonObject { ["type"] = promotion.Presentation };
        if (promotion.Index is { } index) presentation["index"] = index;
        return new JsonObject
        {
            ["type"] = "bemItemPromoted", ["turn_id"] = promotion.TurnId.ToString(),
            ["item_id"] = promotion.ItemId, ["presentation"] = presentation,
        };
    }
}

public sealed partial class CodexThreadStore
{
    private static CodexStoredRealtimeSession? PresentationSession(CodexThreadDocument document)
        => document.RealtimeSessions?.LastOrDefault(s => s.UsedAttachmentIds is { Length: > 0 } &&
            s.State is "attached" or "detached" && s.EndOperationId is null);

    private static CodexThreadDocument BindRealtimePresentation(CodexThreadDocument document, Guid turnId, string? sessionId)
    {
        if (sessionId is null) return document;
        var session = document.RealtimeSessions!.Single(s => s.SessionId == sessionId);
        if ((session.PresentationTurnIds ?? []).Contains(turnId)) return document;
        return ReplaceRealtimeSession(document, session with { PresentationTurnIds = [.. session.PresentationTurnIds ?? [], turnId] });
    }

    private CodexThreadDocument PromoteRealtimeArtifacts(CodexThreadDocument document, CodexStoredTurn before, CodexStoredTurn after)
    {
        if (!ReferenceEquals(before.Output, after.Output))
            document = DropRedundantArtifactPreviewUpdates(document, after);
        var bound = document.RealtimeSessions?.LastOrDefault(s => s.PresentationTurnIds?.Contains(after.Id) == true);
        if (bound is null) return document;
        var prior = CodexRealtimePresentation.Eligible(before, bound.SessionId).ToHashSet();
        static (Guid, string, string, int?) Key(CodexStoredRealtimePromotion p) => (p.TurnId, p.ItemId, p.Presentation, p.Index);
        var observed = document.RealtimeSessions!.SelectMany(s => s.History?.Events ?? [])
            .Where(e => e.Promotion is not null).Select(e => Key(e.Promotion!)).ToHashSet();
        foreach (var promotion in CodexRealtimePresentation.Eligible(after, bound.SessionId))
        {
            if (prior.Contains(promotion) || !observed.Add(Key(promotion))) continue;
            var active = PresentationSession(document);
            var native = after.NativeBackingArtifacts?.SingleOrDefault(a => a.ItemId == promotion.ItemId);
            // Image generation (including the canonical projection of its extension item)
            // and sub-agent start activity may be presented after voice closure.
            if (promotion.Presentation == "wholeItem" && active is null &&
                (native is null || CodexThreadStore.IsNativeMcpArtifact(native))) continue;
            // Late artifacts keep the original session attribution; the latest session owns
            // the split boundary and physical append position, even if that session ended.
            var carrier = document.RealtimeSessions![^1];
            var history = carrier.History ?? EmptyRealtimeHistory();
            var entry = new CodexStoredRealtimeHistoryEvent("promotion:" + Guid.NewGuid().ToString("N"), "promotion",
                NextRealtimeObservationTime(history), document.Turns.Length,
                CommitRevision: checked(document.Revision + 1), Promotion: promotion);
            document = ReplaceRealtimeSession(document, carrier with { History = AppendRealtimeHistory(carrier, history, entry) });
        }
        return document;
    }

    private static void ValidateRealtimePromotions(CodexThreadDocument document)
    {
        var sessions = document.RealtimeSessions ?? [];
        if (sessions.Length == 0) return;
        var turns = document.Turns.ToDictionary(t => t.Id);
        var bySession = sessions.ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        var known = new HashSet<(Guid, string, string, int?)>();
        var eligible = new Dictionary<(Guid, string), HashSet<CodexStoredRealtimePromotion>>();
        var previewProofs = new Dictionary<(Guid, string), CodexStoredRealtimeArtifactPreview>();
        var previewBytes = new Dictionary<Guid, long>();
        foreach (var session in sessions)
        {
            if (session.StartedRevision > document.Revision || session.ClosedRevision > document.Revision ||
                session.PresentationTurnIds is { } ids &&
                (ids.Length > MaximumTurns || ids.Distinct().Count() != ids.Length || ids.Any(id => !turns.ContainsKey(id))))
                throw new InvalidDataException("Invalid realtime session presentation binding.");
        }
        foreach (var item in sessions.SelectMany(s => s.History?.Events ?? []))
        {
            if (item.Kind != "promotion") continue;
            if (item.Promotion is not { } promotion || !bySession.TryGetValue(promotion.SessionId, out var session) ||
                session.PresentationTurnIds?.Contains(promotion.TurnId) != true ||
                !turns.TryGetValue(promotion.TurnId, out var turn) ||
                !known.Add((promotion.TurnId, promotion.ItemId, promotion.Presentation, promotion.Index)))
                throw new InvalidDataException("Realtime promotion has invalid backing identity.");
            if (promotion.Preview is { } preview)
            {
                if (!CodexRealtimeArtifactStream.ValidId(preview.RequestId) ||
                    preview.Text is null || System.Text.Encoding.UTF8.GetByteCount(preview.Text) > CodexRealtimeArtifactStream.MaximumPreviewBytes ||
                    preview.Phase is not (null or "commentary" or "partial_answer" or "final_answer") ||
                    !turn.Dispatches.Any(d => d.Kind == "inference" && d.CallId == preview.RequestId) ||
                    !CodexRealtimePresentation.AssistantPresentations(preview.Text).Contains((promotion.Presentation, promotion.Index)))
                    throw new InvalidDataException("Realtime streaming presentation lacks its admitted public preview.");
                previewProofs[(promotion.TurnId, promotion.ItemId)] = preview;
                previewBytes[promotion.TurnId] = previewBytes.GetValueOrDefault(promotion.TurnId) +
                    System.Text.Encoding.UTF8.GetByteCount(preview.Text);
                continue;
            }
            var key = (promotion.TurnId, promotion.SessionId);
            if (!eligible.TryGetValue(key, out var candidates))
                eligible.Add(key, candidates = CodexRealtimePresentation.Eligible(turn, promotion.SessionId).ToHashSet());
            if (!candidates.Contains(promotion))
                throw new InvalidDataException("Realtime promotion does not reference an eligible backing artifact.");
        }

        var updated = new HashSet<(Guid, string)>();

        foreach (var session in sessions)
        foreach (var update in session.ArtifactPreviewUpdates ?? [])
        {
            if (update is null || !CodexRealtimeArtifactStream.ValidId(update.ItemId) || update.Preview is not { } current ||
                current.Text is null || !previewProofs.TryGetValue((update.TurnId, update.ItemId), out var proof) ||
                !updated.Add((update.TurnId, update.ItemId)) ||
                current.RequestId != proof.RequestId || current.Phase != proof.Phase ||
                !current.Text.StartsWith(proof.Text, StringComparison.Ordinal) ||
                session.History?.Events.Any(e => e.Promotion is { Preview: not null } p &&
                    p.TurnId == update.TurnId && p.ItemId == update.ItemId) != true)
                throw new InvalidDataException("Realtime preview update does not extend its original presentation proof.");
            var bytes = System.Text.Encoding.UTF8.GetByteCount(current.Text);
            var total = previewBytes.GetValueOrDefault(update.TurnId) + bytes;
            if (bytes > CodexRealtimeArtifactStream.MaximumPreviewBytes || total > MaximumArtifactPreviewBytesPerTurn)
                throw new InvalidDataException("Realtime preview update capacity exceeded.");
            previewBytes[update.TurnId] = total;
        }
    }
}
