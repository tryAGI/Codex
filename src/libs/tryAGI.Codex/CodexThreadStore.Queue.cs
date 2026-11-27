using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    internal const int MaximumQueuedSubmissions = 100;
    internal const string QueuedTurnTrigger = "native:queued";

    internal static void RequireQueueThread(CodexThreadDocument thread)
    {
        RequireDurableThread(thread);
        RequireUnarchived(thread);
    }

    internal static void RequireQueueInputThread(CodexThreadDocument thread)
    {
        RequireQueueThread(thread);
        // This native host has no subagent input-authority resolver yet.
        if (!ReadStateEligible(thread)) throw new ArgumentException("Direct queued input is unavailable for this thread source.");
    }

    internal static void ValidateQueueInput(JsonElement input, JsonElement? metadata)
    {
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() is < 1 or > 128
            || Encoding.UTF8.GetByteCount(input.GetRawText()) > 1_048_576
            || input.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
            throw new ArgumentException("Queued input must be a bounded nonempty user input array.");
        if (metadata is not { } value) return;
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > 65_536)
            throw new ArgumentException("Queued client metadata must be a bounded string map.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!ValidToken(property.Name) || !names.Add(property.Name) || names.Count > 64
                || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: <= 4096 })
                throw new ArgumentException("Queued client metadata must be a bounded string map.");
    }

    private static void ValidateQueue(CodexThreadDocument thread)
    {
        var queue = thread.QueuedSubmissions;
        if (queue is null) return;
        if (queue.Length > MaximumQueuedSubmissions) throw new InvalidDataException("Stored queue exceeds capacity.");
        var ids = new HashSet<Guid>();
        var clients = new HashSet<string>(StringComparer.Ordinal);
        foreach (var submission in queue)
        {
            if (submission is null || submission.Id == Guid.Empty || !ids.Add(submission.Id)
                || !ValidToken(submission.ClientUserMessageId) || !clients.Add(submission.ClientUserMessageId)
                || thread.Turns.Any(turn => turn.Id == submission.Id))
                throw new InvalidDataException("Invalid or duplicate queued submission.");
            ValidateQueueInput(submission.Input, submission.ResponsesApiClientMetadata);
        }
    }

    internal sealed record QueueChange(CodexThreadDocument Thread, CodexStoredQueuedSubmission? Submission, bool Changed);

    internal async Task<QueueChange> AddQueuedSubmissionAsync(Guid id, long revision, string clientId, JsonElement input,
        JsonElement? metadata, CancellationToken ct)
    {
        Token(clientId);
        ValidateQueueInput(input, metadata);
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireQueueInputThread(before);
        var queue = before.QueuedSubmissions ?? [];
        var existing = queue.SingleOrDefault(item => item.ClientUserMessageId == clientId);
        if (existing is not null)
        {
            if (!JsonElement.DeepEquals(existing.Input, input) || !SameQueueMetadata(existing.ResponsesApiClientMetadata, metadata))
                throw new ArgumentException("Queued client identity is already bound to different input.");
            return new(before, existing, false);
        }
        if (before.Turns.Any(turn => turn.ClientUserMessageIds?.Contains(clientId, StringComparer.Ordinal) == true))
            throw new ArgumentException("Queued client message identity was already admitted.");
        if (queue.Length >= MaximumQueuedSubmissions) throw new ArgumentException("queue cannot contain more than 100 submissions");
        var submission = new CodexStoredQueuedSubmission(Guid.CreateVersion7(), clientId, input.Clone(), metadata?.Clone());
        var saved = await WriteQueueAsync(before, [.. queue, submission], ct).ConfigureAwait(false);
        return new(saved, submission, true);
    }

    internal async Task<QueueChange> UpdateQueuedSubmissionAsync(Guid id, long revision, Guid submissionId, JsonElement input,
        JsonElement? metadata, CancellationToken ct)
    {
        ValidateQueueInput(input, metadata);
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireQueueInputThread(before);
        var queue = before.QueuedSubmissions ?? [];
        var existing = queue.SingleOrDefault(item => item.Id == submissionId)
            ?? throw new ArgumentException("Queued submission not found.");
        var updated = existing with { Input = input.Clone(),
            ResponsesApiClientMetadata = metadata is { } map && map.EnumerateObject().Any() ? map.Clone() : existing.ResponsesApiClientMetadata };
        if (JsonElement.DeepEquals(existing.Input, updated.Input) && SameQueueMetadata(existing.ResponsesApiClientMetadata, updated.ResponsesApiClientMetadata))
            return new(before, existing, false);
        var saved = await WriteQueueAsync(before, queue.Select(item => item.Id == submissionId ? updated : item).ToArray(), ct).ConfigureAwait(false);
        return new(saved, updated, true);
    }

    internal async Task<QueueChange> DeleteQueuedSubmissionAsync(Guid id, long revision, Guid submissionId, CancellationToken ct)
    {
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireQueueThread(before);
        var queue = before.QueuedSubmissions ?? [];
        var existing = queue.SingleOrDefault(item => item.Id == submissionId);
        if (existing is null) return new(before, null, false);
        var saved = await WriteQueueAsync(before, queue.Where(item => item.Id != submissionId).ToArray(), ct).ConfigureAwait(false);
        return new(saved, existing, true);
    }

    internal async Task<QueueChange> ReorderQueuedSubmissionsAsync(Guid id, long revision, Guid[] order, CancellationToken ct)
    {
        var before = await CurrentAsync(id, revision, ct).ConfigureAwait(false);
        RequireQueueThread(before);
        var queue = before.QueuedSubmissions ?? [];
        if (order.Length != queue.Length || order.Distinct().Count() != order.Length
            || !order.ToHashSet().SetEquals(queue.Select(item => item.Id)))
            throw new ArgumentException("queue reorder must include every queued submission exactly once");
        if (order.SequenceEqual(queue.Select(item => item.Id))) return new(before, null, false);
        var items = queue.ToDictionary(item => item.Id);
        return new(await WriteQueueAsync(before, order.Select(key => items[key]).ToArray(), ct).ConfigureAwait(false), null, true);
    }

    private Task<CodexThreadDocument> WriteQueueAsync(CodexThreadDocument before, CodexStoredQueuedSubmission[] queue, CancellationToken ct)
        => PublishAsync(before.Revision, before with { Revision = checked(before.Revision + 1), QueuedSubmissions = queue.Length == 0 ? null : queue }, ct);

    private static bool SameQueueMetadata(JsonElement? a, JsonElement? b)
        => a is { } left && b is { } right ? JsonElement.DeepEquals(left, right) : a is null && b is null;

    internal static bool SameQueuedSubmissions(CodexThreadDocument before, CodexThreadDocument after)
    {
        var left = before.QueuedSubmissions ?? [];
        var right = after.QueuedSubmissions ?? [];
        return left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.ClientUserMessageId == pair.Second.ClientUserMessageId
            && JsonElement.DeepEquals(pair.First.Input, pair.Second.Input)
            && SameQueueMetadata(pair.First.ResponsesApiClientMetadata, pair.Second.ResponsesApiClientMetadata));
    }

    private static CodexThreadDocument ConsumeQueuedSubmission(CodexThreadDocument thread, Guid turnId, string?[]? clients, string? trigger)
    {
        var queue = thread.QueuedSubmissions ?? [];
        var submission = queue.SingleOrDefault(item => item.Id == turnId);
        if (trigger != QueuedTurnTrigger)
        {
            if (submission is not null) throw new InvalidOperationException("Turn identity is reserved by a queued submission.");
            return thread;
        }
        RequireQueueInputThread(thread);
        if (submission is null || clients is not [var client] || client != submission.ClientUserMessageId)
            throw new CodexThreadConflictException();
        var remaining = queue.Where(item => item.Id != turnId).ToArray();
        return thread with { QueuedSubmissions = remaining.Length == 0 ? null : remaining };
    }
}
