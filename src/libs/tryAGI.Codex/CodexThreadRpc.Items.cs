using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private async Task<JsonElement> ReadItemsAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "turnId", "itemIds"]);
        if (!p.TryGetProperty("turnId", out var turn) || turn.ValueKind != JsonValueKind.String)
            throw Invalid("turnId must be a string.");
        if (!p.TryGetProperty("itemIds", out var ids) || ids.ValueKind != JsonValueKind.Array
            || ids.GetArrayLength() is < 1 or > 100
            || ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String))
            throw Invalid("itemIds must contain 1 to 100 string IDs.");

        var thread = await store.ReadAsync(Id(p, "threadId"), ct).ConfigureAwait(false);
        if (Flag(thread.Settings, "ephemeral"))
            throw Invalid("Ephemeral threads do not support persisted item reads.");

        var selected = ids.EnumerateArray().Select(id => id.GetString()!).ToHashSet(StringComparer.Ordinal);
        var data = new JsonArray();
        // Missing IDs (including turns outside a fork's retained history) are not errors.
        // Filter in canonical stored order; duplicates in the request do not duplicate results.
        foreach (var storedTurn in thread.Turns.Where(t => t.Id.ToString() == turn.GetString()))
        {
            var previews = CodexThreadStore.ProjectRealtimeArtifactPreviews(thread, storedTurn.Id)
                .Where(item => selected.Contains(item["id"]!.GetValue<string>())).ToArray();
            foreach (var item in Items(storedTurn).Concat(previews))
            {
                ct.ThrowIfCancellationRequested();
                if (item["id"]?.GetValue<string>() is not { } id || !selected.Contains(id)) continue;
                data.Add((JsonNode)new JsonObject { ["turnId"] = storedTurn.Id.ToString(), ["item"] = item,
                    ["startedAtMs"] = null, ["completedAtMs"] = null });
            }
        }

        return Element(new JsonObject { ["data"] = data });
    }
}
