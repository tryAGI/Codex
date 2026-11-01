using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Host-scoped atomic storage. Return true only after durable commit. A thrown write may
/// have committed; callers must inspect the same thread/call identity and must not dispatch on uncertainty.</summary>
public interface ICodexThreadStorage
{
    Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken);
    Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document, CancellationToken cancellationToken);
}

/// <summary>Native continuation journal, not the App Server Thread/Turn wire projection.
/// Settings are already resolved by the trusted host. Reads never execute or resume work.</summary>
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
public sealed record CodexStoredDispatch(
    [property: JsonRequired] string CallId,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] string State,
    JsonElement? Result = null);

public sealed record CodexThreadAdmission(bool Admitted, CodexThreadDocument Thread);

public sealed class CodexThreadConflictException() : InvalidOperationException("Native thread revision conflict; re-read before deciding the next action.");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CodexThreadDocument))]
public partial class CodexThreadJson : JsonSerializerContext;
