using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private int queueEnabled;
    private Func<CodexQueuedMetadataContext, CancellationToken, Task<JsonElement?>>? queuedMetadataResolver;

    /// <summary>Installs the host's queued provider-metadata resolver. Return an approved string map
    /// (possibly empty), or null to deny dispatch. Reserved Core-owned fields are always filtered.</summary>
    public void EnableQueuedProviderMetadata(Func<CodexQueuedMetadataContext, CancellationToken, Task<JsonElement?>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (Interlocked.CompareExchange(ref queuedMetadataResolver, resolver, null) is not null)
            throw new InvalidOperationException("Queued provider metadata resolver is already installed.");
    }

    /// <summary>Enables the experimental durable user queue for a negotiated client surface.
    /// Explicit starts share the existing native turn admission. Automatic idle wake-up is not enabled by this contract.</summary>
    public void EnableThreadQueue() => Interlocked.Exchange(ref queueEnabled, 1);

    private bool QueueEnabled => Volatile.Read(ref queueEnabled) != 0;

    private static JsonObject QueuedSubmission(CodexStoredQueuedSubmission submission) => new()
    {
        ["id"] = submission.Id.ToString(),
        ["input"] = JsonNode.Parse(submission.Input.GetRawText()),
        ["clientUserMessageId"] = submission.ClientUserMessageId,
        ["responsesapiClientMetadata"] = submission.ResponsesApiClientMetadata is { } metadata ? JsonNode.Parse(metadata.GetRawText()) : null
    };

    private static JsonElement? QueueMetadata(JsonElement p)
        => p.TryGetProperty("responsesapiClientMetadata", out var value) && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;

    private async Task<JsonElement> QueueAsync(string method, JsonElement p, CancellationToken ct)
    {
        if (!QueueEnabled) throw new CodexThreadRpcException(-32601, "Experimental user message queue is not enabled.");
        var id = Id(p, "threadId");
        switch (method)
        {
            case "thread/queue/list":
            {
                ValidateFields(p, ["threadId", "cursor", "limit"]);
                var thread = await store.ReadAsync(id, ct).ConfigureAwait(false);
                CodexThreadStore.RequireQueueThread(thread);
                var cursor = Text(p, "cursor");
                var offset = 0;
                if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
                    throw Invalid("Invalid queue pagination cursor.");
                var limit = 50;
                if (p.TryGetProperty("limit", out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    if (!value.TryGetUInt32(out var requested)) throw Invalid("Invalid queue page limit.");
                    limit = (int)Math.Clamp(requested, 1u, 100u);
                }
                var queue = thread.QueuedSubmissions ?? [];
                var page = queue.Skip(offset).Take(limit).ToArray();
                return Element(new JsonObject { ["data"] = new JsonArray(page.Select(item => (JsonNode)QueuedSubmission(item)).ToArray()),
                    ["nextCursor"] = offset < queue.Length && queue.Length - offset > page.Length ? (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null });
            }
            case "thread/queue/start":
            {
                ValidateFields(p, ["threadId", "queuedSubmissionId"]);
                var thread = await store.ReadAsync(id, ct).ConfigureAwait(false);
                CodexThreadStore.RequireQueueInputThread(thread);
                var selectedId = OptionalId(p, "queuedSubmissionId");
                var selected = selectedId is { } requested ? thread.QueuedSubmissions?.SingleOrDefault(item => item.Id == requested)
                    : thread.QueuedSubmissions?.FirstOrDefault();
                if (selected is null) throw Invalid("Queued submission not found.");
                return await StartQueuedAsync(thread, selected, ct).ConfigureAwait(false);
            }
        }

        CodexThreadStore.QueueChange change;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckAttachmentAdmission(id);
            var before = await store.ReadAsync(id, ct).ConfigureAwait(false);
            switch (method)
            {
                case "thread/queue/add":
                case "thread/queue/update":
                {
                    var adding = method == "thread/queue/add";
                    ValidateFields(p, adding ? ["threadId", "input", "clientUserMessageId", "responsesapiClientMetadata"]
                        : ["threadId", "input", "queuedSubmissionId", "responsesapiClientMetadata"]);
                    var normalized = Input(p);
                    validateInput?.Invoke(normalized);
                    var raw = p.GetProperty("input");
                    var metadata = QueueMetadata(p);
                    change = adding
                        ? await store.AddQueuedSubmissionAsync(id, before.Revision,
                            InputIdentity(p, "clientUserMessageId") ?? throw Invalid("Client message identity is required."), raw, metadata, ct).ConfigureAwait(false)
                        : await store.UpdateQueuedSubmissionAsync(id, before.Revision, Id(p, "queuedSubmissionId"), raw, metadata, ct).ConfigureAwait(false);
                    break;
                }
                case "thread/queue/delete":
                    ValidateFields(p, ["threadId", "queuedSubmissionId"]);
                    change = await store.DeleteQueuedSubmissionAsync(id, before.Revision, Id(p, "queuedSubmissionId"), ct).ConfigureAwait(false);
                    break;
                case "thread/queue/reorder":
                    ValidateFields(p, ["threadId", "queuedSubmissionIds"]);
                    if (!p.TryGetProperty("queuedSubmissionIds", out var order) || order.ValueKind != JsonValueKind.Array
                        || order.GetArrayLength() > CodexThreadStore.MaximumQueuedSubmissions)
                        throw Invalid("Bounded queue order is required.");
                    var ids = order.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                        && Guid.TryParse(item.GetString(), out var parsed) && parsed != Guid.Empty ? parsed : throw Invalid("Invalid queue identity.")).ToArray();
                    change = await store.ReorderQueuedSubmissionsAsync(id, before.Revision, ids, ct).ConfigureAwait(false);
                    break;
                default: throw new CodexThreadRpcException(-32601, "Native queue method not found.");
            }
        }
        finally { admission.Release(); }
        if (change.Changed) await NotifyQueueChangedAsync(id).ConfigureAwait(false);
        if (change.Changed && method == "thread/queue/add")
            await TryWakeQueueAsync(id).ConfigureAwait(false);
        return method switch
        {
            "thread/queue/add" or "thread/queue/update" => Element(new JsonObject { ["queuedSubmission"] = QueuedSubmission(change.Submission!) }),
            "thread/queue/delete" => Element(new JsonObject { ["deleted"] = change.Changed }),
            _ => Json("{}")
        };
    }

    private async Task<JsonElement> StartQueuedAsync(CodexThreadDocument thread, CodexStoredQueuedSubmission selected, CancellationToken ct)
    {
        JsonElement? approvedMetadata = null;
        if (selected.ResponsesApiClientMetadata is { } metadata && metadata.EnumerateObject().Any())
        {
            var resolver = Volatile.Read(ref queuedMetadataResolver)
                ?? throw new CodexThreadRpcException(-32601, "Queued provider metadata requires a host resolver.");
            // Resolve outside admission and bind the result to the exact observed queue revision.
            var approved = await resolver(new(thread.AccountId, thread.Id, selected.Id, thread.Revision, metadata.Clone()), ct)
                .ConfigureAwait(false);
            if (approved is null) throw new CodexThreadRpcException(-32000, "Queued provider metadata was not authorized.");
            approvedMetadata = CodexQueuedClientMetadata.Filter(approved.Value);
        }
        return await StartAsync(Element(new JsonObject
        {
            ["threadId"] = thread.Id.ToString(), ["input"] = JsonNode.Parse(selected.Input.GetRawText()),
            ["clientUserMessageId"] = selected.ClientUserMessageId
        }), ct, selected.Id, thread.Revision, approvedMetadata).ConfigureAwait(false);
    }

    private Task NotifyQueueChangedAsync(Guid id)
        => EmitAsync("thread/queue/changed", new JsonObject { ["threadId"] = id.ToString() });
}
