using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Host-scoped atomic storage. Return true only after durable commit. A thrown write may
/// have committed; callers must inspect the same thread/call identity and must not dispatch on uncertainty.</summary>
public interface ICodexThreadStorage
{
    /// <summary>Returns the stored document, or <see langword="null"/> when the thread does not exist.</summary>
    Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="document"/> only if the stored revision equals <paramref name="expectedRevision"/>
    /// (<see langword="null"/> means the thread must not exist yet). Returns <see langword="false"/> on a revision conflict.
    /// </summary>
    Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document, CancellationToken cancellationToken);
}

/// <summary>Native continuation journal, not the App Server Thread/Turn wire projection.
/// Settings are already resolved by the trusted host. Reads never execute or resume work.</summary>
/// <param name="Format">Document format version.</param>
/// <param name="Id">Thread ID.</param>
/// <param name="AccountId">Account that owns the thread.</param>
/// <param name="Revision">Optimistic concurrency revision, incremented on every write.</param>
/// <param name="Settings">Resolved thread settings (model, instructions, tools, …).</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last write time.</param>
/// <param name="Turns">Turns in order.</param>
/// <param name="ForkedFromId">Source thread of a fork.</param>
/// <param name="ForkedFromRevision">Source revision of a fork.</param>
public sealed record CodexThreadDocument(
    [property: JsonRequired] int Format,
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string AccountId,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] JsonElement Settings,
    [property: JsonRequired] DateTimeOffset CreatedAt,
    [property: JsonRequired] DateTimeOffset UpdatedAt,
    [property: JsonRequired] CodexStoredTurn[] Turns,
    Guid? ForkedFromId = null, long? ForkedFromRevision = null);

/// <summary>One stored turn.</summary>
/// <param name="Id">Turn ID.</param>
/// <param name="RootTurnId">First turn of a steered sequence; equals <paramref name="Id"/> otherwise.</param>
/// <param name="OwnerId">Host process or worker that owns the running turn.</param>
/// <param name="Status">Turn status, for example <c>running</c> or <c>completed</c>.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="Input">Input items of the turn.</param>
/// <param name="Output">Output items, including encrypted reasoning.</param>
/// <param name="Dispatches">Tool calls dispatched during the turn.</param>
/// <param name="CompletedAt">Completion time.</param>
/// <param name="ClientUserMessageIds">Client message IDs, for deduplicating retried submissions.</param>
/// <param name="TurnTrigger">Optional token describing what started the turn.</param>
public sealed record CodexStoredTurn(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] Guid RootTurnId,
    [property: JsonRequired] string OwnerId,
    [property: JsonRequired] string Status,
    [property: JsonRequired] DateTimeOffset StartedAt,
    [property: JsonRequired] JsonElement[] Input,
    [property: JsonRequired] JsonElement[] Output,
    [property: JsonRequired] CodexStoredDispatch[] Dispatches,
    DateTimeOffset? CompletedAt = null, string?[]? ClientUserMessageIds = null, string? TurnTrigger = null);

/// <summary>Request digest binds a call ID to its exact input bytes. Unknown outcomes remain
/// non-dispatchable, including after fork; this is not a provider billing or rollback receipt.</summary>
/// <param name="CallId">Provider call ID.</param>
/// <param name="Kind">Dispatch kind, for example <c>tool</c>.</param>
/// <param name="RequestSha256">SHA-256 of the exact call input.</param>
/// <param name="State"><c>dispatching</c>, <c>succeeded</c>, <c>failed</c> or <c>unknown</c>.</param>
/// <param name="Result">Recorded result, when known.</param>
public sealed record CodexStoredDispatch(
    [property: JsonRequired] string CallId,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] string State,
    JsonElement? Result = null);

/// <summary>Result of <see cref="CodexThreadStore.AdmitAsync"/>.</summary>
/// <param name="Admitted">The turn was newly admitted; <see langword="false"/> when it already existed.</param>
/// <param name="Thread">The thread after admission.</param>
public sealed record CodexThreadAdmission(bool Admitted, CodexThreadDocument Thread);

/// <summary>The stored revision changed since it was read. Re-read before deciding the next action.</summary>
public sealed class CodexThreadConflictException() : InvalidOperationException("Native thread revision conflict; re-read before deciding the next action.");

/// <summary>Source-generated JSON metadata for <see cref="CodexThreadDocument"/> (camelCase, unknown members rejected).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexThreadDocument))]
public partial class CodexThreadJson : JsonSerializerContext;
