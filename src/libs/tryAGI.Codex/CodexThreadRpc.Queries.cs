using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private async Task<JsonElement> ReadAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "includeTurns"]);
        var thread = await store.ReadAsync(Id(p, "threadId"), ct).ConfigureAwait(false);
        if (Flag(thread.Settings, "ephemeral") && Flag(p, "includeTurns")) throw Invalid("Ephemeral threads do not support includeTurns.");
        return Element(new JsonObject { ["thread"] = Thread(thread, Flag(p, "includeTurns")) });
    }

    private async Task<JsonElement> ListAsync(JsonElement p, bool onlyLoaded, CancellationToken ct)
    {
        ValidateFields(p, onlyLoaded ? ["cursor", "limit"] : ["cursor", "limit", "sortKey", "sortDirection", "cwd", "searchTerm", "sectionId", "modelProviders", "archived", "sourceKinds", "originators", "useStateDbOnly"]);
        IReadOnlyList<CodexThreadDocument> documents = await list(ct).ConfigureAwait(false);
        if (onlyLoaded) documents = documents.Concat(store.EphemeralThreads).DistinctBy(d => d.Id).ToArray();
        if (documents.Count > 1000) throw new CodexThreadRpcException(-32010, "Native discovery exceeds its bounded catalog; no incomplete success returned.");
        IEnumerable<CodexThreadDocument> selected = documents;
        if (onlyLoaded) selected = selected.Where(d => loaded.ContainsKey(d.Id));
        if (Flag(p, "archived")) selected = [];
        if (p.TryGetProperty("sectionId", out _))
        {
            var section = Text(p, "sectionId");
            selected = selected.Where(d => Text(d.Settings, "sectionId") == section);
        }
        if (p.TryGetProperty("cwd", out var cwd) && cwd.ValueKind != JsonValueKind.Null)
        {
            var paths = cwd.ValueKind == JsonValueKind.String ? new[] { cwd.GetString() }
                : cwd.ValueKind == JsonValueKind.Array && cwd.GetArrayLength() <= 100 && cwd.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                    ? cwd.EnumerateArray().Select(x => x.GetString()).ToArray() : throw Invalid("Bounded cwd path or path array required.");
            selected = selected.Where(d => paths.Contains(Text(d.Settings, "cwd"), StringComparer.Ordinal));
        }
        if (Text(p, "searchTerm") is { } term) selected = selected.Where(d => Preview(d).Contains(term, StringComparison.OrdinalIgnoreCase));
        foreach (var field in new[] { "modelProviders", "sourceKinds", "originators" })
        {
            if (!p.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100 || value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                throw Invalid("Bounded string filter required: " + field);
            var values = value.EnumerateArray().Select(x => x.GetString()).ToArray();
            if (values.Length > 0) selected = selected.Where(d => values.Contains(field switch
            { "modelProviders" => Text(d.Settings, "modelProvider"), "sourceKinds" => "appServer", _ => "tryagi-codex" }, StringComparer.Ordinal));
        }
        var sort = Text(p, "sortKey") ?? "created_at";
        if (sort is not ("created_at" or "updated_at")) throw Invalid("Unknown thread sort key.");
        var descending = Direction(p) == "desc";
        var rows = onlyLoaded ? selected.OrderBy(d => d.Id).Select(d => (d.Id.ToString(), (JsonNode?)JsonValue.Create(d.Id.ToString()))).ToArray()
            : (descending ? selected.OrderByDescending(d => sort == "created_at" ? d.CreatedAt : d.UpdatedAt).ThenByDescending(d => d.Id)
                : selected.OrderBy(d => sort == "created_at" ? d.CreatedAt : d.UpdatedAt).ThenBy(d => d.Id))
                .Select(d => (d.Id.ToString(), (JsonNode?)Thread(d, false))).ToArray();
        var sortPositions = onlyLoaded ? null : documents.ToDictionary(d => d.Id.ToString(),
            d => (sort == "created_at" ? d.CreatedAt : d.UpdatedAt).UtcTicks);
        var page = Page(onlyLoaded ? "loaded" : "threads", p, rows, sortPositions: sortPositions);
        if (!onlyLoaded) return page;
        var result = Object(page); result.Remove("backwardsCursor"); return Element(result);
    }

    private async Task<JsonElement> HistoryAsync(JsonElement p, bool items, CancellationToken ct)
    {
        ValidateFields(p, items ? ["threadId", "turnId", "cursor", "limit", "sortDirection"] : ["threadId", "cursor", "limit", "sortDirection", "itemsView"]);
        var thread = await store.ReadAsync(Id(p, "threadId"), ct).ConfigureAwait(false);
        if (Flag(thread.Settings, "ephemeral")) throw Invalid("Ephemeral threads do not support persisted history lists.");
        if (items)
        {
            var turnId = OptionalId(p, "turnId");
            if (turnId is { } requested && !thread.Turns.Any(t => t.Id == requested)) throw new KeyNotFoundException();
            var entries = thread.Turns.Where(t => turnId is null || t.Id == turnId).SelectMany(turn => Items(turn).Select(item =>
                (turn.Id + ":" + (item["id"] is JsonValue identity && identity.TryGetValue<string>(out var text)
                    ? text : throw Invalid("Missing item identity.")),
                (JsonNode?)new JsonObject { ["turnId"] = turn.Id.ToString(), ["item"] = item, ["startedAtMs"] = null, ["completedAtMs"] = null }))).ToArray();
            if (Direction(p, "asc") == "desc") Array.Reverse(entries);
            string? anchor = null;
            if (p.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Object)
            {
                ValidateFields(cursor, ["type", "itemId"]);
                if (turnId is null || Text(cursor, "type") != "item" || Text(cursor, "itemId") is not { Length: > 0 } itemId)
                    throw Invalid("An item anchor requires an item identity and a non-empty turnId.");
                anchor = turnId + ":" + itemId;
            }
            return Page("items:" + thread.Id + ":" + turnId, p, entries, "asc", anchor);
        }
        var view = Text(p, "itemsView") ?? "summary";
        if (view is not ("full" or "summary" or "notLoaded")) throw Invalid("Invalid itemsView.");
        var turns = thread.Turns.Select(t => (t.Id.ToString(), (JsonNode?)Turn(t, view))).ToArray();
        if (Direction(p) == "desc") Array.Reverse(turns);
        return Page("turns:" + thread.Id, p, turns);
    }

    private static string Direction(JsonElement p, string defaultDirection = "desc")
    {
        var value = Text(p, "sortDirection") ?? defaultDirection;
        return value is "asc" or "desc" ? value : throw Invalid("Invalid sort direction.");
    }

    private static JsonElement Page(string method, JsonElement p, (string Id, JsonNode? Body)[] rows,
        string defaultDirection = "desc", string? itemAnchor = null, IReadOnlyDictionary<string, long>? sortPositions = null)
    {
        var limit = 50;
        if (p.TryGetProperty("limit", out var n) && n.ValueKind != JsonValueKind.Null)
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out limit) || limit < 0) throw Invalid("Invalid page limit.");
            limit = Math.Clamp(limit, 1, 100);
        }
        var filter = new JsonObject { ["method"] = method };
        foreach (var prop in p.EnumerateObject().Where(x => x.Name is not ("cursor" or "limit" or "sortDirection")).OrderBy(x => x.Name, StringComparer.Ordinal))
            filter[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(filter.ToJsonString())));
        var direction = Direction(p, defaultDirection); var offset = 0;
        if (itemAnchor is { } anchor)
        {
            var index = Array.FindIndex(rows, row => row.Id == anchor);
            if (index < 0) throw Invalid("Item anchor is not visible in the requested turn.");
            offset = index + 1;
        }
        else if (Text(p, "cursor") is { } cursor)
        {
            try
            {
                if (cursor.Length > 2048) throw Invalid("Oversized cursor.");
                using var c = JsonDocument.Parse(Convert.FromBase64String(cursor));
                var a = c.RootElement;
                if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != (sortPositions is null ? 4 : 5)
                    || a[0].GetString() != scope || a[1].GetString() != direction)
                    throw Invalid("Cursor belongs to another query/direction.");
                if (sortPositions is not null)
                {
                    var boundary = a[4].GetInt64(); var id = a[2].GetString(); var inclusive = a[3].GetBoolean();
                    offset = Array.FindIndex(rows, row =>
                    {
                        var comparison = sortPositions[row.Id].CompareTo(boundary);
                        if (comparison == 0) comparison = string.CompareOrdinal(row.Id, id);
                        return direction == "asc" ? comparison > 0 || inclusive && comparison == 0 : comparison < 0 || inclusive && comparison == 0;
                    });
                    if (offset < 0) offset = rows.Length;
                }
                else
                {
                    var index = Array.FindIndex(rows, row => row.Id == a[2].GetString());
                    if (index < 0) throw Invalid("Cursor anchor is no longer available.");
                    offset = index + (a[3].GetBoolean() ? 0 : 1);
                }
            }
            catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
            { throw Invalid("Invalid native cursor."); }
        }
        string Cursor(string id, string dir, bool include)
        {
            var parts = new JsonArray(scope, dir, id, include);
            if (sortPositions is not null) parts.Add((JsonNode?)JsonValue.Create(sortPositions[id]));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(parts.ToJsonString()));
        }
        var page = rows.Skip(offset).Take(limit).ToArray();
        return Element(new JsonObject { ["data"] = new JsonArray(page.Select(x => x.Body).ToArray()),
            ["nextCursor"] = offset + page.Length < rows.Length && page.Length != 0 ? Cursor(page[^1].Id, direction, false) : null,
            ["backwardsCursor"] = page.Length == 0 ? null : Cursor(page[0].Id, direction == "asc" ? "desc" : "asc", true) });
    }

    private JsonObject Thread(CodexThreadDocument thread, bool includeTurns) => new()
    {
        ["id"] = thread.Id.ToString(), ["sessionId"] = Text(thread.Settings, "nativeSessionId") ?? thread.Id.ToString(), ["forkedFromId"] = thread.ForkedFromId?.ToString(),
        ["preview"] = Preview(thread), ["ephemeral"] = Flag(thread.Settings, "ephemeral"), ["projectId"] = null,
        ["modelProvider"] = Text(thread.Settings, "modelProvider"), ["model"] = Text(thread.Settings, "model"),
        ["reasoningEffort"] = Text(thread.Settings, "effort"), ["createdAt"] = thread.CreatedAt.ToUnixTimeSeconds(),
        ["updatedAt"] = thread.UpdatedAt.ToUnixTimeSeconds(), ["cwd"] = Text(thread.Settings, "cwd"),
        ["cliVersion"] = "tryagi-codex", ["source"] = "appServer", ["originator"] = "tryagi-codex", ["threadSource"] = Text(thread.Settings, "threadSource"),
        ["path"] = null, ["historyMode"] = "paginated", ["status"] = loaded.ContainsKey(thread.Id) && active.ContainsKey(thread.Id)
            ? new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray() }
            : new JsonObject { ["type"] = loaded.ContainsKey(thread.Id) ? "idle" : "notLoaded" },
        ["turns"] = new JsonArray(includeTurns ? thread.Turns.Select(t => (JsonNode)Turn(t, "full")).ToArray() : [])
    };

    private static string Preview(CodexThreadDocument thread)
    {
        var text = string.Concat(thread.Turns.Take(1).SelectMany(t => t.Input)
            .Select(x => x.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                ? string.Concat(c.EnumerateArray().Select(y => Text(y, "text") ?? "")) : "")).Trim();
        return text[..Math.Min(200, text.Length)];
    }

    private static JsonObject Turn(CodexStoredTurn turn, string view)
    {
        var items = view == "notLoaded" ? [] : Items(turn);
        if (view == "summary") items = items.Where(i => i["type"]?.GetValue<string>() == "agentMessage").TakeLast(1).ToArray();
        return new JsonObject { ["id"] = turn.Id.ToString(), ["rootTurnId"] = turn.RootTurnId.ToString(), ["status"] = turn.Status,
            ["items"] = new JsonArray(items.Cast<JsonNode>().ToArray()), ["itemsView"] = view, ["error"] = null,
            ["startedAt"] = turn.StartedAt.ToUnixTimeSeconds(), ["completedAt"] = turn.CompletedAt?.ToUnixTimeSeconds(),
            ["durationMs"] = turn.CompletedAt is { } end ? (long?)(end - turn.StartedAt).TotalMilliseconds : null };
    }

    private static JsonObject[] Items(CodexStoredTurn turn)
    {
        var result = new List<JsonObject>(); var index = 0; var userIndex = 0;
        foreach (var item in turn.Input.Concat(turn.Output))
        {
            var id = Text(item, "id") ?? turn.Id + ":" + index; index++;
            var type = Text(item, "type"); var role = Text(item, "role");
            if (role == "user")
            {
                var content = new JsonArray();
                if (item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    foreach (var value in c.EnumerateArray())
                    {
                        var kind = Text(value, "type");
                        if (kind == "input_text") content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = Text(value, "text"), ["text_elements"] = new JsonArray() });
                        else if (kind == "input_image") content.Add((JsonNode)new JsonObject { ["type"] = "image", ["url"] = Text(value, "image_url") });
                        else throw new CodexThreadRpcException(-32601, "Native history contains an unprojected user input variant.");
                    }
                else if (item.TryGetProperty("content", out c) && c.ValueKind == JsonValueKind.String)
                    content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = c.GetString(), ["text_elements"] = new JsonArray() });
                var clientId = turn.ClientUserMessageIds is { } clients && userIndex < clients.Length ? clients[userIndex] : null;
                userIndex++;
                result.Add(new JsonObject { ["id"] = id, ["type"] = "userMessage", ["content"] = content, ["clientId"] = clientId });
            }
            else if (type is "message" or null && role == "assistant")
            {
                var text = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                    ? string.Concat(c.EnumerateArray().Where(v => Text(v, "type") == "output_text").Select(v => Text(v, "text") ?? ""))
                    : c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                var message = new JsonObject { ["id"] = id, ["type"] = "agentMessage", ["text"] = text };
                if (Text(item, "phase") is { } phase) message["phase"] = phase;
                result.Add(message);
            }
            else if (type == "reasoning")
                result.Add(new JsonObject { ["id"] = id, ["type"] = "reasoning", ["summary"] = item.TryGetProperty("summary", out var summary)
                    && summary.ValueKind == JsonValueKind.Array ? new JsonArray(summary.EnumerateArray().Select(s => (JsonNode?)JsonValue.Create(Text(s, "text") ?? "")).ToArray()) : new JsonArray() });
            else if (type is "function_call_output" or "custom_tool_call_output")
            {
                if (Text(item, "call_id") is null && Text(item, "name") is { } name)
                    result.Add(new JsonObject { ["id"] = id, ["type"] = "functionCallOutput", ["name"] = name,
                        ["namespace"] = Text(item, "namespace"), ["output"] = item.TryGetProperty("output", out var output) ? JsonNode.Parse(output.GetRawText()) : null });
            }
            else if (type is "function_call" or "custom_tool_call")
            {
                var receipt = turn.Dispatches.SingleOrDefault(d => d.Kind == "tool" && d.CallId == Text(item, "call_id"));
                result.Add(new JsonObject { ["id"] = id, ["type"] = "dynamicToolCall", ["tool"] = Text(item, "name"),
                    ["arguments"] = type == "function_call" ? JsonNode.Parse(Text(item, "arguments") ?? "{}") : JsonValue.Create(Text(item, "input")),
                    ["status"] = receipt?.State == "succeeded" ? "completed" : receipt?.State == "failed" ? "failed" : "inProgress",
                    ["success"] = receipt?.State == "succeeded" ? true : receipt?.State == "failed" ? false : null,
                    ["namespace"] = Text(item, "namespace"), ["contentItems"] = ToolContents(receipt) });
            }
            else throw new CodexThreadRpcException(-32601, "Native history variant is not projected; refusing incomplete success.");
        }
        return result.ToArray();
    }

    private static JsonArray? ToolContents(CodexStoredDispatch? receipt)
    {
        if (receipt?.Result is not { } result || !result.TryGetProperty("output", out var output)) return null;
        if (output.ValueKind == JsonValueKind.String)
            return new JsonArray((JsonNode)new JsonObject { ["type"] = "inputText", ["text"] = output.GetString() });
        if (output.ValueKind != JsonValueKind.Array) throw Invalid("Unprojected tool result variant.");
        var contents = new JsonArray();
        foreach (var part in output.EnumerateArray())
        {
            var type = Text(part, "type");
            if (type == "input_text") contents.Add((JsonNode)new JsonObject { ["type"] = "inputText", ["text"] = Text(part, "text") });
            else if (type == "input_image") contents.Add((JsonNode)new JsonObject { ["type"] = "inputImage", ["imageUrl"] = Text(part, "image_url") });
            else throw Invalid("Unprojected tool content variant.");
        }
        return contents;
    }
}
