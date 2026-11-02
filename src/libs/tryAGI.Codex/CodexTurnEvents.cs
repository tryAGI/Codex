using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Host-bound real producers; subscriber failure never repeats a committed action.</summary>
/// <param name="threadId">Thread the notifications belong to.</param>
/// <param name="turnId">Turn the notifications belong to.</param>
/// <param name="notify">Sends an App Server notification.</param>
public sealed class CodexTurnEvents(string threadId, string turnId, Func<string, JsonElement, Task> notify)
{
    private readonly CodexTurnDiff diff = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private long failures;
    private bool diffIncomplete;
    /// <summary>Notifications that could not be delivered.</summary>
    public long DeliveryFailures => Interlocked.Read(ref failures);
    /// <summary>Executes the built-in <c>update_plan</c> tool and sends <c>turn/plan/updated</c>.</summary>
    public Task<CodexToolOutput> PlanAsync(string arguments, CancellationToken ct = default) =>
        CodexTurnPlan.ExecuteAsync(arguments, value => SendAsync("turn/plan/updated", value), ct);
    /// <summary>Forwards provider moderation metadata as <c>turn/moderationMetadata</c>.</summary>
    public Task ProviderAsync(CodexResponseEvent item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return item.Type == "response.metadata" && item.Payload.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("openai_chatgpt_moderation_metadata", out var value)
                ? SendAsync("turn/moderationMetadata", Element(new JsonObject { ["metadata"] = JsonNode.Parse(value.GetRawText()) })) : Task.CompletedTask;
    }
    /// <summary>Sends the updated turn diff (<c>turn/diff/updated</c>) after a patch was applied.</summary>
    public async Task AppliedPatchAsync(CodexWorkspacePatchResult result)
    {
        if (result.DryRun) throw new ArgumentException("A preview is not an applied change.");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (diffIncomplete) throw new InvalidOperationException("Earlier applied changes lack a confirmed diff; no partial aggregate published.");
            await SendAsync("turn/diff/updated", Element(new JsonObject { ["diff"] = diff.Apply(result) })).ConfigureAwait(false);
        }
        catch { diffIncomplete = true; throw; }
        finally { gate.Release(); }
    }
    private async Task SendAsync(string method, JsonElement value)
    {
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > 1_000_000) throw new ArgumentException("Event exceeds its bound.");
            var body = JsonNode.Parse(value.GetRawText()) as JsonObject ?? throw new ArgumentException("Event object required.");
            body["threadId"] = threadId; body["turnId"] = turnId;
            await notify(method, Element(body)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Interlocked.Increment(ref failures); }
    }
    private static JsonElement Element(JsonObject value)
    { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
}
