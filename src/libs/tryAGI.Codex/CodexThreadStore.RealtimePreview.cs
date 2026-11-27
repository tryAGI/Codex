using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    private const int MaximumArtifactPreviewsPerTurn = 128;
    private const int MaximumArtifactPreviewBytesPerTurn = 512 * 1024;

    // The first directive seals speech and records immutable presentation proof. Later
    // public chunks replace one bounded checkpoint, without adding timeline positions or
    // changing model input/output, dispatch receipts, or the compaction source.
    internal async Task<CodexThreadDocument> RecordRealtimeArtifactPreviewAsync(Guid threadId, long expectedRevision,
        Guid turnId, string ownerId, CodexRealtimeArtifactCandidate candidate, CancellationToken ct)
    {
        var before = await CurrentAsync(threadId, expectedRevision, ct).ConfigureAwait(false);
        var turn = Active(before, turnId, ownerId);
        if (!CodexRealtimeArtifactStream.ValidId(candidate.ItemId) ||
            !turn.Dispatches.Any(d => d.Kind == "inference" && d.CallId == candidate.Preview.RequestId && d.State == "dispatching"))
            throw new InvalidOperationException("Realtime preview does not belong to an active admitted inference.");
        var session = before.RealtimeSessions?.LastOrDefault(s => s.PresentationTurnIds?.Contains(turnId) == true);
        if (session is null) return before;
        // Match completed promotion deduplication; a provider item ID never creates a second
        // copy of a presentation already published for this same turn.
        var previous = before.RealtimeSessions!.SelectMany(s => s.History?.Events ?? [])
            .Select(e => e.Promotion).Where(p => p is not null && p.TurnId == turnId).ToArray();
        var previews = previous.Where(p => p!.Preview is not null).Select(p => p!).ToArray();
        var updates = before.RealtimeSessions!.SelectMany(s => s.ArtifactPreviewUpdates ?? []).Where(u => u.TurnId == turnId).ToArray();
        var proof = previews.LastOrDefault(p => p.ItemId == candidate.ItemId);
        var update = updates.SingleOrDefault(u => u.ItemId == candidate.ItemId);
        var prior = update?.Preview ?? proof?.Preview;
        if (prior is not null &&
            (prior.RequestId != candidate.Preview.RequestId || prior.Phase != candidate.Preview.Phase ||
                !candidate.Preview.Text.StartsWith(prior.Text, StringComparison.Ordinal) || prior.Text == candidate.Preview.Text))
            return before;
        var remainingCount = MaximumArtifactPreviewsPerTurn - previews.Length;
        var remainingBytes = MaximumArtifactPreviewBytesPerTurn -
            previews.Sum(p => System.Text.Encoding.UTF8.GetByteCount(p.Preview!.Text)) -
            updates.Where(u => u.ItemId != candidate.ItemId).Sum(u => System.Text.Encoding.UTF8.GetByteCount(u.Preview.Text));
        var bytes = System.Text.Encoding.UTF8.GetByteCount(candidate.Preview.Text);
        if (bytes > remainingBytes || bytes > CodexRealtimeArtifactStream.MaximumPreviewBytes) return before;
        if (turn.Output.Any(x => CodexRealtimeProtocol.Text(x, "id") == candidate.ItemId)) return before;
        var next = before;
        foreach (var (kind, index) in CodexRealtimePresentation.AssistantPresentations(candidate.Preview.Text))
        {
            if (previous.Any(p => p!.ItemId == candidate.ItemId && p.Presentation == kind && p.Index == index)) continue;
            if (remainingCount-- <= 0 || bytes > remainingBytes) break;
            remainingBytes -= bytes;
            var carrier = next.RealtimeSessions![^1];
            var history = carrier.History ?? EmptyRealtimeHistory();
            var promotion = new CodexStoredRealtimePromotion(session.SessionId, turnId, candidate.ItemId, kind, index, candidate.Preview);
            var entry = new CodexStoredRealtimeHistoryEvent("promotion:" + Guid.NewGuid().ToString("N"), "promotion",
                NextRealtimeObservationTime(history), before.Turns.Length,
                CommitRevision: checked(before.Revision + 1), Promotion: promotion);
            next = ReplaceRealtimeSession(next, carrier with { History = AppendRealtimeHistory(carrier, history, entry) });
        }
        // A newer promotion already includes the latest text. Otherwise keep precisely one
        // checkpoint in the session that owns the first immutable promotion proof.
        if (proof is not null && (ReferenceEquals(next, before) || update is not null))
        {
            var holder = next.RealtimeSessions!.SingleOrDefault(s => s.ArtifactPreviewUpdates?.Any(u =>
                u.TurnId == turnId && u.ItemId == candidate.ItemId) == true) ??
                next.RealtimeSessions!.First(s => s.History?.Events.Any(e => e.Promotion is { Preview: not null } p &&
                    p.TurnId == turnId && p.ItemId == candidate.ItemId) == true);
            var records = (holder.ArtifactPreviewUpdates ?? []).Where(u => u.TurnId != turnId || u.ItemId != candidate.ItemId).ToArray();
            if (ReferenceEquals(next, before))
                records = [.. records, new(turnId, candidate.ItemId, candidate.Preview)];
            next = ReplaceRealtimeSession(next, holder with { ArtifactPreviewUpdates = records.Length == 0 ? null : records });
        }
        if (ReferenceEquals(next, before)) return before;
        return await PublishAsync(before.Revision, next with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, ct).ConfigureAwait(false);
    }

    // A promoted item is queryable even before its validated terminal output exists.
    // Never expose provider reasoning/arguments, or copy this preview into model history.
    internal static IEnumerable<JsonObject> ProjectRealtimeArtifactPreviews(CodexThreadDocument document, Guid turnId)
    {
        var turn = document.Turns.Single(t => t.Id == turnId);
        var completed = turn.Output.Select(item => CodexRealtimeProtocol.Text(item, "id")).ToHashSet(StringComparer.Ordinal);
        var previews = (document.RealtimeSessions ?? []).SelectMany(s => s.History?.Events ?? [])
            .Where(e => e.Promotion is { Preview: not null } p && p.TurnId == turnId && !completed.Contains(p.ItemId))
            .Select(e => e.Promotion!).GroupBy(p => p.ItemId, StringComparer.Ordinal);
        var updates = (document.RealtimeSessions ?? []).SelectMany(s => s.ArtifactPreviewUpdates ?? [])
            .Where(u => u.TurnId == turnId).ToDictionary(u => u.ItemId, StringComparer.Ordinal);
        foreach (var group in previews)
        {
            var preview = updates.TryGetValue(group.Key, out var update) ? update.Preview : group.Last().Preview!;
            var item = new JsonObject { ["type"] = "agentMessage", ["id"] = group.Key, ["text"] = preview.Text };
            if (preview.Phase is not null) item["phase"] = preview.Phase;
            yield return item;
        }
    }
}
