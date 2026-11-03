using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace tryAGI.Codex;

// A hand-maintained subset of the App Server protocol: the core thread/turn methods and notifications.
// The schema has hundreds of types and changes between releases, so everything else stays raw JSON, and
// every model here keeps members it does not declare in AdditionalProperties. AppServerModelSchemaTests
// checks each declared member against the newest schema snapshot, so the drift pull request fails when
// one is renamed or removed.

/// <summary>Members of an App Server payload that this model does not declare, kept verbatim.</summary>
public abstract record CodexAppServerPayload
{
    /// <summary>Members this model does not declare, kept verbatim so newer fields stay readable.</summary>
    // Settable rather than init-only: System.Text.Json binds init-only members like constructor
    // parameters, and extension data cannot be one.
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Parameters of <c>thread/start</c>. Null members are omitted; set others through
/// <see cref="CodexAppServerPayload.AdditionalProperties"/>.</summary>
/// <param name="Model">Model to use, or the server default.</param>
/// <param name="Cwd">Working directory of the thread.</param>
/// <param name="BaseInstructions">Replaces the base instructions.</param>
/// <param name="DeveloperInstructions">Additional developer instructions.</param>
/// <param name="Ephemeral">Do not persist the thread.</param>
/// <param name="ServiceTier">Service tier, if the plan offers one.</param>
public sealed record CodexAppServerThreadStartParams(string? Model = null, string? Cwd = null, string? BaseInstructions = null,
    string? DeveloperInstructions = null, bool? Ephemeral = null, string? ServiceTier = null) : CodexAppServerPayload;

/// <summary>Result of <c>thread/start</c>.</summary>
/// <param name="Thread">The new thread.</param>
/// <param name="Model">Model the thread uses.</param>
/// <param name="ModelProvider">Provider of that model.</param>
/// <param name="Cwd">Working directory of the thread.</param>
public sealed record CodexAppServerThreadStartResponse(CodexAppServerThread Thread, string Model, string ModelProvider, string Cwd) : CodexAppServerPayload;

/// <summary>An App Server thread.</summary>
/// <param name="Id">Thread ID.</param>
/// <param name="SessionId">Session the thread belongs to.</param>
/// <param name="Preview">Short preview of the conversation.</param>
/// <param name="ModelProvider">Provider of the thread's model.</param>
/// <param name="CreatedAt">Creation time in Unix seconds.</param>
/// <param name="UpdatedAt">Last update in Unix seconds.</param>
/// <param name="Ephemeral">The thread is not persisted.</param>
/// <param name="Turns">Turns included in this response; may be empty when not loaded.</param>
/// <param name="Model">Model of the thread, if known.</param>
/// <param name="Name">User-assigned name, if any.</param>
public sealed record CodexAppServerThread(string Id, string SessionId, string Preview, string ModelProvider, long CreatedAt, long UpdatedAt,
    bool Ephemeral, IReadOnlyList<CodexAppServerTurn> Turns, string? Model = null, string? Name = null) : CodexAppServerPayload;

/// <summary>Parameters of <c>turn/start</c>.</summary>
/// <param name="ThreadId">Thread to run the turn in.</param>
/// <param name="Input">User input items; build them with <see cref="CodexUserInput.ToJsonArray"/>.</param>
/// <param name="Model">Model for this and later turns.</param>
/// <param name="Cwd">Working directory for this and later turns.</param>
/// <param name="Effort">Reasoning effort, for example <c>low</c> or <c>high</c>.</param>
/// <param name="ClientUserMessageId">Caller ID for the user message, for deduplicating retries.</param>
public sealed record CodexAppServerTurnStartParams([property: JsonRequired] string ThreadId, [property: JsonRequired] JsonArray Input,
    string? Model = null, string? Cwd = null, string? Effort = null, string? ClientUserMessageId = null) : CodexAppServerPayload
{
    /// <summary>Creates turn parameters from typed input items.</summary>
    /// <param name="threadId">Thread to run the turn in.</param>
    /// <param name="input">User input items.</param>
    public static CodexAppServerTurnStartParams Create(string threadId, IEnumerable<CodexUserInput> input) => new(threadId, CodexUserInput.ToJsonArray(input));
}

/// <summary>Result of <c>turn/start</c>.</summary>
/// <param name="Turn">The started turn.</param>
public sealed record CodexAppServerTurnStartResponse(CodexAppServerTurn Turn) : CodexAppServerPayload;

/// <summary>Parameters of <c>turn/interrupt</c>.</summary>
/// <param name="ThreadId">Thread of the turn.</param>
/// <param name="TurnId">Turn to interrupt.</param>
public sealed record CodexAppServerTurnInterruptParams([property: JsonRequired] string ThreadId, [property: JsonRequired] string TurnId) : CodexAppServerPayload;

/// <summary>A result without declared members, such as that of <c>turn/interrupt</c>.</summary>
public sealed record CodexAppServerEmpty : CodexAppServerPayload;

/// <summary>An App Server turn.</summary>
/// <param name="Id">Turn ID.</param>
/// <param name="Status">One of <see cref="CodexAppServerTurnStatus"/>, or a newer value.</param>
/// <param name="Items">Thread items of the turn, as raw JSON.</param>
/// <param name="Error">Why the turn failed, if it did.</param>
/// <param name="StartedAt">Start time in Unix seconds.</param>
/// <param name="CompletedAt">Completion time in Unix seconds.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
public sealed record CodexAppServerTurn(string Id, string Status, JsonArray Items, CodexAppServerTurnError? Error = null,
    long? StartedAt = null, long? CompletedAt = null, long? DurationMs = null) : CodexAppServerPayload;

/// <summary>Known <see cref="CodexAppServerTurn.Status"/> values.</summary>
public static class CodexAppServerTurnStatus
{
    /// <summary>The turn finished.</summary>
    public const string Completed = "completed";
    /// <summary>The turn was interrupted.</summary>
    public const string Interrupted = "interrupted";
    /// <summary>The turn failed; see <see cref="CodexAppServerTurn.Error"/>.</summary>
    public const string Failed = "failed";
    /// <summary>The turn is running.</summary>
    public const string InProgress = "inProgress";
}

/// <summary>Why an App Server turn failed.</summary>
/// <param name="Message">Error message.</param>
/// <param name="CodexErrorInfo">Classification: a string such as <c>usageLimitExceeded</c>, or an object, as raw JSON.</param>
/// <param name="AdditionalDetails">Extra details, if any.</param>
public sealed record CodexAppServerTurnError(string Message, JsonElement? CodexErrorInfo = null, string? AdditionalDetails = null) : CodexAppServerPayload
{
    /// <summary>The plan's allowance is used up; see <see cref="CodexUsageLimitException"/>.</summary>
    [JsonIgnore]
    public bool IsUsageLimit => CodexErrorInfo is { ValueKind: JsonValueKind.String } info && info.ValueEquals(CodexUsageLimitException.AppServerErrorInfo);
}

/// <summary>Payload of <c>turn/started</c> and <c>turn/completed</c>.</summary>
/// <param name="ThreadId">Thread of the turn.</param>
/// <param name="Turn">The turn.</param>
public sealed record CodexAppServerTurnNotification(string ThreadId, CodexAppServerTurn Turn) : CodexAppServerPayload;

/// <summary>Payload of <c>item/agentMessage/delta</c>.</summary>
/// <param name="ThreadId">Thread of the message.</param>
/// <param name="TurnId">Turn of the message.</param>
/// <param name="ItemId">Message item.</param>
/// <param name="Delta">Text appended to the message.</param>
public sealed record CodexAppServerAgentMessageDelta(string ThreadId, string TurnId, string ItemId, string Delta) : CodexAppServerPayload;

/// <summary>Payload of the <c>error</c> notification.</summary>
/// <param name="ThreadId">Thread of the turn.</param>
/// <param name="TurnId">Turn that hit the error.</param>
/// <param name="Error">The error.</param>
/// <param name="WillRetry">The server retries by itself.</param>
public sealed record CodexAppServerErrorNotification(string ThreadId, string TurnId, CodexAppServerTurnError Error, bool WillRetry) : CodexAppServerPayload;

/// <summary>A typed App Server request method.</summary>
/// <typeparam name="TParams">Parameter type.</typeparam>
/// <typeparam name="TResult">Result type.</typeparam>
/// <param name="Name">Method name.</param>
/// <param name="ParamsType">Serialization metadata of the parameters.</param>
/// <param name="ResultType">Serialization metadata of the result.</param>
public sealed record CodexAppServerMethod<TParams, TResult>(string Name, JsonTypeInfo<TParams> ParamsType, JsonTypeInfo<TResult> ResultType);

/// <summary>A typed App Server notification.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="Method">Notification method.</param>
/// <param name="PayloadType">Serialization metadata of the payload.</param>
public sealed record CodexAppServerNotificationType<T>(string Method, JsonTypeInfo<T> PayloadType)
{
    /// <summary>Reads <paramref name="notification"/> when it is this notification; false for any other method.</summary>
    /// <param name="notification">A notification from <see cref="CodexAppServerClient.Notifications"/>.</param>
    /// <param name="payload">The typed payload.</param>
    public bool TryRead(CodexAppServerNotification notification, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? payload)
    {
        ArgumentNullException.ThrowIfNull(notification);
        payload = default;
        if (!string.Equals(notification.Method, Method, StringComparison.Ordinal) || notification.Params is not { } value) return false;
        payload = value.Deserialize(PayloadType);
        return payload is not null;
    }
}

/// <summary>Typed core App Server methods. Others are called by name with raw JSON.</summary>
public static class CodexAppServerMethods
{
    /// <summary><c>thread/start</c>.</summary>
    public static CodexAppServerMethod<CodexAppServerThreadStartParams, CodexAppServerThreadStartResponse> ThreadStart { get; } =
        new("thread/start", CodexAppServerJson.Default.CodexAppServerThreadStartParams, CodexAppServerJson.Default.CodexAppServerThreadStartResponse);

    /// <summary><c>turn/start</c>.</summary>
    public static CodexAppServerMethod<CodexAppServerTurnStartParams, CodexAppServerTurnStartResponse> TurnStart { get; } =
        new("turn/start", CodexAppServerJson.Default.CodexAppServerTurnStartParams, CodexAppServerJson.Default.CodexAppServerTurnStartResponse);

    /// <summary><c>turn/interrupt</c>.</summary>
    public static CodexAppServerMethod<CodexAppServerTurnInterruptParams, CodexAppServerEmpty> TurnInterrupt { get; } =
        new("turn/interrupt", CodexAppServerJson.Default.CodexAppServerTurnInterruptParams, CodexAppServerJson.Default.CodexAppServerEmpty);
}

/// <summary>Typed core App Server notifications.</summary>
public static class CodexAppServerNotifications
{
    /// <summary><c>turn/started</c>.</summary>
    public static CodexAppServerNotificationType<CodexAppServerTurnNotification> TurnStarted { get; } =
        new("turn/started", CodexAppServerJson.Default.CodexAppServerTurnNotification);

    /// <summary><c>turn/completed</c>.</summary>
    public static CodexAppServerNotificationType<CodexAppServerTurnNotification> TurnCompleted { get; } =
        new("turn/completed", CodexAppServerJson.Default.CodexAppServerTurnNotification);

    /// <summary><c>item/agentMessage/delta</c>.</summary>
    public static CodexAppServerNotificationType<CodexAppServerAgentMessageDelta> AgentMessageDelta { get; } =
        new("item/agentMessage/delta", CodexAppServerJson.Default.CodexAppServerAgentMessageDelta);

    /// <summary><c>error</c>.</summary>
    public static CodexAppServerNotificationType<CodexAppServerErrorNotification> Error { get; } =
        new("error", CodexAppServerJson.Default.CodexAppServerErrorNotification);
}

/// <summary>Source-generated JSON metadata for the typed App Server subset (camelCase, nulls omitted, strict numbers).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
    NumberHandling = JsonNumberHandling.Strict, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodexAppServerThreadStartParams))]
[JsonSerializable(typeof(CodexAppServerThreadStartResponse))]
[JsonSerializable(typeof(CodexAppServerTurnStartParams))]
[JsonSerializable(typeof(CodexAppServerTurnStartResponse))]
[JsonSerializable(typeof(CodexAppServerTurnInterruptParams))]
[JsonSerializable(typeof(CodexAppServerEmpty))]
[JsonSerializable(typeof(CodexAppServerTurnNotification))]
[JsonSerializable(typeof(CodexAppServerAgentMessageDelta))]
[JsonSerializable(typeof(CodexAppServerErrorNotification))]
public partial class CodexAppServerJson : JsonSerializerContext;
