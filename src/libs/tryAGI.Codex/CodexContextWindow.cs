using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Provider context counters observed at one validated terminal response. The prefix digest
/// binds the observation to the model-visible history, not the complete journal or billed usage.</summary>
public sealed record CodexStoredContextWindowObservation
{
    /// <summary>Local inference reservation ID.</summary>
    [JsonRequired] public required string RequestId { get; init; }
    /// <summary>Provider response ID.</summary>
    [JsonRequired] public required string ResponseId { get; init; }
    /// <summary>Effective model, prompt configuration, window identity and checkpoint generation digest.</summary>
    [JsonRequired] public required string ScopeSha256 { get; init; }
    /// <summary>Digest of the funding account/user binding; no access token is retained.</summary>
    [JsonRequired] public required string AuthOwnerSha256 { get; init; }
    /// <summary>Exact model-visible prefix digest.</summary>
    [JsonRequired] public required string HistorySha256 { get; init; }
    /// <summary>Number of history items in the observed prefix.</summary>
    [JsonRequired] public required int HistoryItemCount { get; init; }
    /// <summary>Explicit provider active-context tokens, or unknown.</summary>
    public long? UsedTokens { get; init; }
    /// <summary>Explicit provider model context window, or unknown.</summary>
    public long? ModelContextWindow { get; init; }
    /// <summary>Explicit provider remaining-context tokens, or unknown.</summary>
    public long? RemainingTokens { get; init; }
    /// <summary>Local observation time.</summary>
    [JsonRequired] public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>Current context decision. Observed counts, approximate new tokens and financial usage
/// are separate. An estimate is never represented as an observed provider count.</summary>
public sealed record CodexContextWindowStatus
{
    /// <summary>Last matching provider observation, if any.</summary>
    public CodexStoredContextWindowObservation? Observation { get; init; }
    /// <summary>Approximate tokens after the observed prefix, or the entire prompt if no count matched.</summary>
    public required long EstimatedAdditionalTokens { get; init; }
    /// <summary>Observed used tokens plus estimated additions, or the approximate full prompt.</summary>
    public required long ProjectedActiveTokens { get; init; }
    /// <summary>Effective hard context limit after applying the host policy, if known.</summary>
    public long? EffectiveContextLimit { get; init; }
    /// <summary>The projected context reaches the hard limit or configured compaction threshold.</summary>
    public bool TokenLimitReached { get; init; }
    /// <summary>The lower speculative threshold is reached, but the hard threshold is not.</summary>
    public bool BackgroundThresholdReached { get; init; }
}

internal static class CodexContextWindow
{
    private const int MaximumPrefixItems = 131072;

    internal static string AuthHash(CodexThreadCompactionScheduler.AuthOwner owner) =>
        Digest(new JsonObject
        {
            ["account"] = owner.AccountId, ["user"] = owner.UserId, ["restricted"] = owner.Restricted,
        }.ToJsonString());

    internal static string ScopeHash(CodexResponseOptions settings, CodexStoredCompactionCheckpoint? checkpoint) =>
        Digest(new JsonObject
        {
            ["prompt_scope"] = CodexThreadCompaction.ScopeHash(settings),
            ["session"] = settings.Identity?.SessionId,
            ["thread"] = settings.Identity?.ThreadId,
            ["window"] = settings.Identity?.WindowId,
            ["context_window"] = settings.Identity?.ContextWindowId,
            ["installation"] = settings.Identity?.InstallationId,
            ["routing"] = settings.Identity?.RoutingHint,
            ["beta"] = settings.Identity?.BetaFeatures,
            ["prompt_cache_key"] = settings.PromptCacheKey,
            ["concurrent_reasoning_summaries"] = settings.ConcurrentReasoningSummaries,
            ["parallel_tools"] = settings.ParallelToolCalls,
            ["required_function"] = settings.RequiredFirstFunction,
            ["verbosity"] = settings.Verbosity,
            ["output_schema"] = settings.OutputSchema is { } schema ? JsonNode.Parse(schema.GetRawText()) : null,
            ["output_schema_strict"] = settings.OutputSchemaStrict,
            ["checkpoint_revision"] = checkpoint?.SourceRevision,
            ["checkpoint_response"] = checkpoint?.ResponseId,
        }.ToJsonString());

    internal static CodexStoredContextWindowObservation? Capture(CodexResponseOptions settings,
        CodexStoredCompactionCheckpoint? checkpoint, CodexThreadCompactionScheduler.AuthOwner owner,
        IReadOnlyList<JsonElement> input, CodexResponseEvent terminal)
    {
        // Billing totals, cached tokens, reasoning tokens and session snapshots are not
        // active-context usage. Only explicit counters of a validated reasoning terminal qualify.
        if (terminal.Type != "response.completed" || terminal.CompletedOutput is not { } output ||
            terminal.Usage is not { } usage || terminal.Identity != settings.Identity ||
            terminal.RequestId is not { Length: > 0 and <= 512 } requestId ||
            terminal.ResponseId is not { Length: > 0 and <= 512 } responseId ||
            !terminal.Payload.TryGetProperty("response", out var response) ||
            CodexRealtimeProtocol.Text(response, "id") != responseId ||
            CodexRealtimeProtocol.Text(response, "model") is { } model && model != settings.Model ||
            input.Count + (long)output.Count > MaximumPrefixItems)
            return null;
        if (usage.ContextUsedTokens is null && usage.ModelContextWindow is not > 0 &&
            usage.ContextRemainingTokens is null)
            return null;
        var prefix = input.Concat(output).ToArray();
        return new()
        {
            RequestId = requestId, ResponseId = responseId,
            ScopeSha256 = ScopeHash(settings, checkpoint), AuthOwnerSha256 = AuthHash(owner),
            HistorySha256 = HistoryHash(prefix, prefix.Length), HistoryItemCount = prefix.Length,
            UsedTokens = usage.ContextUsedTokens,
            ModelContextWindow = usage.ModelContextWindow is > 0 ? usage.ModelContextWindow : null,
            RemainingTokens = usage.ContextRemainingTokens, ObservedAt = terminal.ObservedAt,
        };
    }

    internal static CodexContextWindowStatus Evaluate(CodexResponseOptions settings,
        IReadOnlyList<JsonElement> history, CodexStoredContextWindowObservation? observation,
        CodexStoredCompactionCheckpoint? checkpoint, CodexThreadCompactionScheduler.AuthOwner owner)
    {
        if (observation is not null && (observation.ScopeSha256 != ScopeHash(settings, checkpoint) ||
            observation.AuthOwnerSha256 != AuthHash(owner) ||
            observation.HistoryItemCount < 0 || observation.HistoryItemCount > history.Count ||
            observation.HistorySha256 != HistoryHash(history, observation.HistoryItemCount)))
            observation = null;

        long estimated;
        if (observation?.UsedTokens is not null)
        {
            long bytes = 0;
            for (var index = observation.HistoryItemCount; index < history.Count; index++)
                bytes += Encoding.UTF8.GetByteCount(history[index].GetRawText());
            estimated = (bytes + 3) / 4;
        }
        else estimated = CodexThreadCompaction.EstimateTokens(settings, history);

        var observed = observation?.UsedTokens ?? 0;
        var projected = observed > long.MaxValue - estimated ? long.MaxValue : observed + estimated;
        var policy = settings.AutomaticCompaction;
        if (policy is not null) ValidatePolicy(policy);
        var capacity = observation?.ModelContextWindow;
        if (policy?.ContextWindowTokens is { } configured)
            capacity = capacity is { } measured ? Math.Min(measured, configured) : configured;
        long? limit = capacity is { } window
            ? Math.Max(1, (long)((decimal)window * (policy?.EffectiveContextWindowPercent ?? 100) / 100))
            : null;
        var hard = limit is { } hardLimit && projected >= hardLimit ||
            policy is not null && projected >= policy.TokenThreshold;
        return new()
        {
            Observation = observation, EstimatedAdditionalTokens = estimated,
            ProjectedActiveTokens = projected, EffectiveContextLimit = limit,
            TokenLimitReached = hard,
            BackgroundThresholdReached = !hard && policy?.BackgroundTokenThreshold is { } lower && projected >= lower,
        };
    }

    internal static void ValidatePolicy(CodexThreadCompactionOptions policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.TokenThreshold);
        if (policy.ContextWindowTokens is <= 0 || policy.EffectiveContextWindowPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(policy), "Invalid effective context-window policy.");
    }

    // The journal's raw inference receipt remains authoritative. Invalid derived metadata
    // is ignored when reading old or externally supplied snapshots, not used as a trigger.
    internal static CodexStoredContextWindowObservation? ReadObservation(CodexStoredTurn? turn)
    {
        if (turn?.ContextWindowObservation is not { } observation) return null;
        try { Validate(turn, observation); }
        catch (InvalidDataException) { return null; }
        return observation;
    }

    internal static void Validate(CodexStoredTurn turn, CodexStoredContextWindowObservation observation)
    {
        static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        var receipt = turn.Dispatches.FirstOrDefault(item => item.CallId == observation.RequestId &&
            item.Kind == "inference" && item.State == "succeeded");
        if (turn.Status != "completed" || observation.RequestId is not { Length: > 0 and <= 512 } ||
            observation.ResponseId is not { Length: > 0 and <= 512 } ||
            !Hash(observation.ScopeSha256) || !Hash(observation.AuthOwnerSha256) ||
            !Hash(observation.HistorySha256) || observation.HistoryItemCount is < 0 or > MaximumPrefixItems ||
            observation.ObservedAt == default || observation.UsedTokens is < 0 ||
            observation.ModelContextWindow is <= 0 || observation.RemainingTokens is < 0 ||
            observation.UsedTokens is null && observation.ModelContextWindow is null && observation.RemainingTokens is null ||
            receipt?.Result is not { ValueKind: JsonValueKind.Object } response ||
            CodexRealtimeProtocol.Text(response, "id") != observation.ResponseId)
            throw new InvalidDataException("Native context-window observation is invalid or has no matching inference receipt.");
        // Counters in the persisted nested response must agree with the derived observation.
        // Event-level counters may exist independently of that nested receipt.
        static long? Count(JsonElement value, string property) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var counter) &&
            counter.ValueKind == JsonValueKind.Number && counter.TryGetInt64(out var number) && number >= 0 ? number : null;
        var context = response.TryGetProperty("context_window", out var window) ? window : default;
        if (Count(context, "used_tokens") is { } used && used != observation.UsedTokens ||
            Count(context, "remaining_tokens") is { } remaining && remaining != observation.RemainingTokens ||
            (Count(response, "model_context_window") ?? Count(context, "total_tokens")) is > 0 and var capacity &&
                capacity != observation.ModelContextWindow)
            throw new InvalidDataException("Native context-window observation disagrees with its inference receipt.");
    }

    private static string HistoryHash(IReadOnlyList<JsonElement> history, int count)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            // Normalize serialization whitespace so journal round trips preserve the prefix digest.
            writer.WriteStartArray();
            for (var index = 0; index < count; index++) history[index].WriteTo(writer);
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
