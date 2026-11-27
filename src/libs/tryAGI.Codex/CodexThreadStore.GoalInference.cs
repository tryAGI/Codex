using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Original goal generation and stable receipt identity captured before provider dispatch.
/// A fork or replacement goal cannot charge an inherited observation.</summary>
/// <param name="OriginThreadId">Thread that admitted the request.</param>
/// <param name="GoalId">Goal active at admission.</param>
/// <param name="UsageId">Unique durable accounting identity for this admitted request.</param>
public sealed record CodexGoalInferenceBinding(Guid OriginThreadId, Guid GoalId, Guid UsageId);

public sealed partial class CodexThreadStore
{
    private static CodexGoalInferenceBinding? CaptureGoalInference(CodexThreadDocument thread, string kind)
    {
        if (kind != "inference" || thread.Goal is not { Status: "active" } goal) return null;
        if ((goal.UsageReceipts?.Length ?? 0) >= MaximumGoalUsageReceipts)
            throw new InvalidOperationException("Goal accounting receipt capacity exhausted before provider admission.");
        return new(thread.Id, goal.Id, Guid.CreateVersion7());
    }

    private static void ValidateGoalInference(CodexStoredDispatch receipt, HashSet<Guid> ids)
    {
        if (receipt.GoalInference is { } binding &&
            (receipt.Kind != "inference" || binding.OriginThreadId == Guid.Empty || binding.GoalId == Guid.Empty
                || binding.UsageId == Guid.Empty || !ids.Add(binding.UsageId)))
            throw new InvalidDataException("Invalid native goal inference binding.");
    }

    private CodexThreadDocument AccountGoalInference(CodexThreadDocument thread, CodexStoredDispatch receipt,
        JsonElement evidence, bool wrappedEvent = false)
    {
        // Only a request admitted against this goal generation can charge it, including an
        // in-flight response arriving after a pause, block, budget limit or completion.
        if (receipt.GoalInference is not { } binding || binding.OriginThreadId != thread.Id
            || thread.Goal is not { } goal || goal.Id != binding.GoalId) return thread;
        if (wrappedEvent)
        {
            if (evidence.ValueKind != JsonValueKind.Object ||
                !evidence.TryGetProperty("response", out var response)) return thread;
            evidence = response;
        }
        if (evidence.ValueKind != JsonValueKind.Object ||
            !CodexAbnormalInferenceAccounting.ValidId(CodexRealtimeProtocol.Text(evidence, "id")) ||
            !evidence.TryGetProperty("usage", out var usage)) return thread;
        var tokens = CodexObservedUsage.ReadTokens(usage);
        if (tokens is not { InputTokens: { } input, OutputTokens: { } output }) return thread;
        // Match the pinned Rust goal budget: uncached input + output, not billing total
        // or an extra addition of reasoning tokens already included in output.
        var uncached = Math.Max(0, input - (tokens.CachedInputTokens ?? 0));
        var delta = output > long.MaxValue - uncached ? long.MaxValue : uncached + output;
        var mode = goal.Status == "complete" ? CodexGoalAccountingMode.ActiveOrComplete : CodexGoalAccountingMode.ActiveOrStopped;
        var updated = ApplyGoalUsage(goal, binding.UsageId, delta, 0, mode);
        return ReferenceEquals(goal, updated) ? thread : thread with { Goal = updated };
    }

    internal static bool SameGoalSnapshot(CodexStoredThreadGoal? left, CodexStoredThreadGoal? right)
        => left is null || right is null ? left is null && right is null :
            (left with { UsageReceipts = null }) == (right with { UsageReceipts = null })
            && (left.UsageReceipts ?? []).SequenceEqual(right.UsageReceipts ?? []);
}
