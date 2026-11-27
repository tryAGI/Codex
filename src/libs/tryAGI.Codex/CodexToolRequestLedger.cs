using System.Globalization;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Exact JSON-RPC request ID. Numeric and string IDs never alias.</summary>
public sealed record CodexToolRequestId(string Value, bool IsNumeric = false)
{
    /// <summary>Parses the string or signed-integer ID without coercion.</summary>
    public static CodexToolRequestId Parse(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => new(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out var number) => new(number.ToString(CultureInfo.InvariantCulture), true),
        _ => throw new CodexProtocolException("Invalid server request ID."),
    };
}

/// <summary>Durable request key within an authenticated connection, independent of turn/item IDs.</summary>
public sealed record CodexToolRequestKey(string AccountId, string ConnectionId, CodexToolRequestId RequestId);

/// <summary>Server request protocol families. None is an execution approval.</summary>
public enum CodexToolRequestKind
{
    /// <summary>item/tool/call.</summary>
    DynamicTool,
    /// <summary>item/tool/requestUserInput.</summary>
    UserInput,
    /// <summary>mcpServer/elicitation/request.</summary>
    McpElicitation,
}

/// <summary>Host-resolved correlation for a server request and its eventual response.
/// The MCP turn may be absent for an independently initiated elicitation.</summary>
public sealed record CodexToolRequestCorrelation(CodexToolRequestKey Key, CodexToolRequestKind Kind,
    string ThreadId, string? TurnId = null, string? ItemId = null, string? ServerName = null,
    string? EnvironmentId = null);

/// <summary>Durable request lifecycle. A completed response is input, not permission to execute.</summary>
public enum CodexToolRequestState
{
    /// <summary>Awaiting a correlated response.</summary>
    Pending,
    /// <summary>One validated response has been committed.</summary>
    Completed,
    /// <summary>Cancelled before a response was committed.</summary>
    Cancelled,
    /// <summary>Response deadline elapsed.</summary>
    Expired,
}

/// <summary>Immutable request record. Parameters and response may contain secret user input;
/// host storage must enforce encryption, retention and access policy. Never log this payload.</summary>
public sealed record CodexToolRequestRecord(CodexToolRequestCorrelation Correlation, long Revision,
    CodexToolRequestState State, JsonElement Parameters, bool IsBlocking, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt, JsonElement? Response = null, string? ActorId = null)
{
    /// <summary>Redacted diagnostic representation.</summary>
    public override string ToString() => $"Tool request {Correlation.Kind} ({State}, revision {Revision})";
}

/// <summary>Host-owned account-partitioned durable storage. CompareExchange succeeds only
/// after durable commit; an exception may be an ambiguous commit and must not trigger replay.</summary>
public interface ICodexToolRequestStorage
{
    /// <summary>Reads the exact key or returns null.</summary>
    Task<CodexToolRequestRecord?> ReadAsync(CodexToolRequestKey key, CancellationToken cancellationToken);

    /// <summary>Commits a new record when absent or replaces the exact expected revision.</summary>
    Task<bool> CompareExchangeAsync(CodexToolRequestKey key, long? expectedRevision,
        CodexToolRequestRecord record, CancellationToken cancellationToken);
}

/// <summary>Outcome of admission; only Created permits initial publication of a new request.</summary>
public sealed record CodexToolRequestAdmission(CodexToolRequestRecord Record, bool Created);

/// <summary>Outcome of a correlated terminal transition.</summary>
public enum CodexToolRequestDisposition
{
    /// <summary>The transition durably committed.</summary>
    Applied,
    /// <summary>The request already reached a terminal state.</summary>
    AlreadyTerminal,
    /// <summary>No request exists with that key.</summary>
    Missing,
    /// <summary>The key exists but its correlation differs.</summary>
    CorrelationMismatch,
    /// <summary>The host declined authorization.</summary>
    Denied,
    /// <summary>Another writer won the CAS; no delivery is authorized.</summary>
    Conflict,
}

/// <summary>Trusted host authorization input for a response or cancellation.</summary>
public sealed record CodexToolRequestAuthorization(CodexToolRequestRecord Request, string ActorId,
    CodexToolRequestState RequestedState, JsonElement? Response)
{
    /// <summary>Redacted diagnostic representation.</summary>
    public override string ToString() => $"Tool request authorization ({RequestedState})";
}

/// <summary>Durable, correlated server requests, independent of the reasoning loop and approval store.
/// Only the authenticated host can admit requests and authenticate responders. Repeated or late
/// responses never re-complete a request. A terminal record can be recovered after restart;
/// projection into turn history must be idempotent by the original request key.</summary>
public sealed class CodexToolRequestLedger
{
    private readonly ICodexToolRequestStorage storage;
    private readonly Func<CodexToolRequestAuthorization, CancellationToken, ValueTask<bool>> authorize;
    private readonly Func<CodexToolRequestAuthorization, CancellationToken, ValueTask<bool>>? verifyNative;
    private readonly TimeProvider clock;
    private readonly TimeSpan pollingInterval;

    /// <summary>Creates the ledger with explicit host authorization for terminal mutations.
    /// Device verification acceptance additionally requires a native proof verifier;
    /// ordinary elicitation replies never grant tool or permission authority.</summary>
    public CodexToolRequestLedger(ICodexToolRequestStorage storage,
        Func<CodexToolRequestAuthorization, CancellationToken, ValueTask<bool>> authorize,
        Func<CodexToolRequestAuthorization, CancellationToken, ValueTask<bool>>? verifyNative = null,
        TimeProvider? clock = null, TimeSpan? pollingInterval = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        this.verifyNative = verifyNative;
        this.clock = clock ?? TimeProvider.System;
        this.pollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(250);
        if (this.pollingInterval <= TimeSpan.Zero || this.pollingInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(pollingInterval));
    }

    /// <summary>Admits the original wire request once. A repeated exact admission returns
    /// the prior record with Created=false; a different payload or scope using the same
    /// connection-local ID is a conflict, even after completion.</summary>
    public async Task<CodexToolRequestAdmission> AdmitAsync(CodexToolRequestCorrelation correlation,
        JsonElement parameters, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        ValidateCorrelation(correlation);
        var blocking = ValidateParameters(correlation, parameters);
        var snapshot = parameters.Clone();
        var existing = await storage.ReadAsync(correlation.Key, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return Existing(existing);
        var now = clock.GetUtcNow();
        if (expiresAt <= now) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        var record = new CodexToolRequestRecord(correlation, 1, CodexToolRequestState.Pending,
            snapshot, blocking, now, expiresAt, now);
        if (await storage.CompareExchangeAsync(correlation.Key, null, record, cancellationToken).ConfigureAwait(false))
            return new(record, true);
        existing = await storage.ReadAsync(correlation.Key, cancellationToken).ConfigureAwait(false);
        return existing is not null ? Existing(existing) : throw new IOException("Tool request admission conflict.");

        CodexToolRequestAdmission Existing(CodexToolRequestRecord value)
        {
            if (value.Correlation != correlation || value.ExpiresAt != expiresAt ||
                !string.Equals(value.Parameters.GetRawText(), snapshot.GetRawText(), StringComparison.Ordinal))
                throw new InvalidOperationException("Conflicting server request identity.");
            return new(value, false);
        }
    }

    /// <summary>Completes the exact request with a validated, authenticated response.
    /// A denied, expired, duplicate or mismatched reply is not delivered.</summary>
    public Task<CodexToolRequestDisposition> RespondAsync(CodexToolRequestCorrelation correlation,
        string actorId, JsonElement response, CancellationToken cancellationToken = default) =>
        TransitionAsync(correlation, actorId, CodexToolRequestState.Completed, response, cancellationToken);

    /// <summary>Cancels the exact request without creating a synthetic success response.</summary>
    public Task<CodexToolRequestDisposition> CancelAsync(CodexToolRequestCorrelation correlation,
        string actorId, CancellationToken cancellationToken = default) =>
        TransitionAsync(correlation, actorId, CodexToolRequestState.Cancelled, null, cancellationToken);

    /// <summary>Reads a request for the exact host correlation and settles elapsed deadlines.</summary>
    public async Task<CodexToolRequestRecord?> ReadAsync(CodexToolRequestCorrelation correlation,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelation(correlation);
        var record = await storage.ReadAsync(correlation.Key, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        if (record.Correlation != correlation) throw new InvalidOperationException("Mismatched tool request correlation.");
        return await ExpireIfNeededAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for a persisted terminal response, including one written by another host process.
    /// Cancelling the waiter does not cancel the request. Nonblocking requests can instead
    /// return immediately after AdmitAsync and be read/projected later.</summary>
    public async Task<CodexToolRequestRecord> WaitAsync(CodexToolRequestCorrelation correlation,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await ReadAsync(correlation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Unknown tool request.");
            if (record.State != CodexToolRequestState.Pending) return record;
            var remaining = record.ExpiresAt - clock.GetUtcNow();
            var delay = remaining > TimeSpan.Zero && remaining < pollingInterval ? remaining : pollingInterval;
            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CodexToolRequestDisposition> TransitionAsync(CodexToolRequestCorrelation correlation,
        string actorId, CodexToolRequestState state, JsonElement? response, CancellationToken ct)
    {
        ValidateCorrelation(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var record = await storage.ReadAsync(correlation.Key, ct).ConfigureAwait(false);
        if (record is null) return CodexToolRequestDisposition.Missing;
        if (record.Correlation != correlation) return CodexToolRequestDisposition.CorrelationMismatch;
        record = await ExpireIfNeededAsync(record, ct).ConfigureAwait(false);
        if (record.State != CodexToolRequestState.Pending) return CodexToolRequestDisposition.AlreadyTerminal;
        var snapshot = response?.Clone();
        if (state == CodexToolRequestState.Completed) ValidateResponse(record, snapshot!.Value);
        var request = new CodexToolRequestAuthorization(record, actorId, state, snapshot);
        if (!await authorize(request, ct).ConfigureAwait(false)) return CodexToolRequestDisposition.Denied;
        if (state == CodexToolRequestState.Completed && record.Correlation.Kind == CodexToolRequestKind.McpElicitation &&
            Text(record.Parameters, "mode") == "openai/userVerification" && Text(snapshot!.Value, "action") == "accept" &&
            (verifyNative is null || !await verifyNative(request, ct).ConfigureAwait(false)))
            return CodexToolRequestDisposition.Denied;

        ct.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        if (now >= record.ExpiresAt)
        {
            record = await ExpireIfNeededAsync(record, ct).ConfigureAwait(false);
            return record.State == CodexToolRequestState.Pending
                ? CodexToolRequestDisposition.Conflict : CodexToolRequestDisposition.AlreadyTerminal;
        }
        var updated = record with
        {
            State = state, Revision = checked(record.Revision + 1), UpdatedAt = now,
            Response = snapshot, ActorId = actorId,
        };
        return await storage.CompareExchangeAsync(correlation.Key, record.Revision, updated, ct).ConfigureAwait(false)
            ? CodexToolRequestDisposition.Applied : CodexToolRequestDisposition.Conflict;
    }

    private async Task<CodexToolRequestRecord> ExpireIfNeededAsync(CodexToolRequestRecord record, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (record.State != CodexToolRequestState.Pending || now < record.ExpiresAt) return record;
        var expired = record with
        {
            State = CodexToolRequestState.Expired, Revision = checked(record.Revision + 1), UpdatedAt = now,
        };
        if (await storage.CompareExchangeAsync(record.Correlation.Key, record.Revision, expired, ct).ConfigureAwait(false))
            return expired;
        var current = await storage.ReadAsync(record.Correlation.Key, ct).ConfigureAwait(false);
        if (current is null || current.Correlation != record.Correlation)
            throw new IOException("Tool request changed during expiry.");
        return current;
    }

    private static void ValidateCorrelation(CodexToolRequestCorrelation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Key);
        ArgumentNullException.ThrowIfNull(value.Key.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Key.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Key.ConnectionId);
        var id = value.Key.RequestId;
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        if (id.IsNumeric && (!long.TryParse(id.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ||
            number.ToString(CultureInfo.InvariantCulture) != id.Value))
            throw new ArgumentException("Numeric request ID is not canonical.", nameof(value));
        ArgumentException.ThrowIfNullOrWhiteSpace(value.ThreadId);
        if (value.TurnId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(value.TurnId);
        if (value.ItemId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(value.ItemId);
        if (value.EnvironmentId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(value.EnvironmentId);
        if (!Enum.IsDefined(value.Kind)) throw new ArgumentException("Unknown server request family.", nameof(value));
        if (value.Kind == CodexToolRequestKind.McpElicitation)
            ArgumentException.ThrowIfNullOrWhiteSpace(value.ServerName);
        else
        {
            if (value.ServerName is not null) throw new ArgumentException("Unexpected MCP server identity.", nameof(value));
            ArgumentException.ThrowIfNullOrWhiteSpace(value.TurnId);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.ItemId);
        }
    }

    private static bool ValidateParameters(CodexToolRequestCorrelation correlation, JsonElement parameters)
    {
        RequireObject(parameters);
        if (RequiredText(parameters, "threadId") != correlation.ThreadId ||
            OptionalText(parameters, "turnId") != correlation.TurnId)
            throw new CodexProtocolException("Tool request thread/turn correlation mismatch.");

        switch (correlation.Kind)
        {
            case CodexToolRequestKind.DynamicTool:
                if (RequiredText(parameters, "callId") != correlation.ItemId)
                    throw new CodexProtocolException("Dynamic tool call ID mismatch.");
                _ = RequiredText(parameters, "tool");
                _ = OptionalText(parameters, "namespace");
                if (!parameters.TryGetProperty("arguments", out _))
                    throw new CodexProtocolException("Dynamic tool call requires arguments.");
                return true;
            case CodexToolRequestKind.UserInput:
                if (RequiredText(parameters, "itemId") != correlation.ItemId)
                    throw new CodexProtocolException("User input item ID mismatch.");
                var questionIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var question in Array(parameters, "questions").EnumerateArray())
                {
                    RequireObject(question);
                    if (!questionIds.Add(RequiredText(question, "id")))
                        throw new CodexProtocolException("Duplicate user input question ID.");
                    _ = RequiredText(question, "header");
                    _ = RequiredText(question, "question");
                    _ = OptionalBoolean(question, "isOther");
                    _ = OptionalBoolean(question, "isSecret");
                    if (question.TryGetProperty("options", out var options) && options.ValueKind != JsonValueKind.Null)
                    {
                        if (options.ValueKind != JsonValueKind.Array)
                            throw new CodexProtocolException("Invalid user input options.");
                        foreach (var option in options.EnumerateArray())
                        {
                            RequireObject(option);
                            _ = RequiredText(option, "label");
                            _ = RequiredText(option, "description");
                        }
                    }
                }
                if (questionIds.Count == 0) throw new CodexProtocolException("User input requires questions.");
                if (parameters.TryGetProperty("autoResolutionMs", out var auto) && auto.ValueKind != JsonValueKind.Null &&
                    (auto.ValueKind != JsonValueKind.Number || !auto.TryGetUInt64(out _)))
                    throw new CodexProtocolException("Invalid legacy user input resolution duration.");
                return OptionalBoolean(parameters, "isBlocking") ?? true;
            case CodexToolRequestKind.McpElicitation:
                if (RequiredText(parameters, "serverName") != correlation.ServerName ||
                    OptionalText(parameters, "environmentId") != correlation.EnvironmentId)
                    throw new CodexProtocolException("MCP elicitation registration mismatch.");
                var mode = RequiredText(parameters, "mode");
                switch (mode)
                {
                    case "form":
                    case "openai/form":
                    case "openaiForm":
                        _ = RequiredText(parameters, "message");
                        RequireObject(Property(parameters, "requestedSchema"));
                        break;
                    case "url":
                        _ = RequiredText(parameters, "message");
                        _ = RequiredText(parameters, "url");
                        _ = RequiredText(parameters, "elicitationId");
                        break;
                    case "openai/userVerification":
                        _ = RequiredText(parameters, "title");
                        _ = RequiredText(parameters, "description");
                        _ = RequiredText(parameters, "challenge");
                        break;
                    default: throw new CodexProtocolException("Unsupported MCP elicitation mode.");
                }
                return true;
            default: throw new CodexProtocolException("Unsupported tool request kind.");
        }
    }

    private static void ValidateResponse(CodexToolRequestRecord record, JsonElement response)
    {
        RequireObject(response);
        switch (record.Correlation.Kind)
        {
            case CodexToolRequestKind.DynamicTool:
                if (Property(response, "success").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new CodexProtocolException("Dynamic response requires a boolean success.");
                foreach (var item in Array(response, "contentItems").EnumerateArray())
                {
                    RequireObject(item);
                    var field = RequiredText(item, "type") switch
                    {
                        "inputText" => "text",
                        "inputImage" => "imageUrl",
                        "inputAudio" => "audioUrl",
                        _ => throw new CodexProtocolException("Unsupported dynamic response content."),
                    };
                    // Content is retained as wire data; URLs are never opened or fetched here.
                    if (Property(item, field).ValueKind != JsonValueKind.String)
                        throw new CodexProtocolException("Invalid dynamic tool content.");
                }
                break;
            case CodexToolRequestKind.UserInput:
                var questions = Array(record.Parameters, "questions").EnumerateArray()
                    .Select(question => RequiredText(question, "id")).ToHashSet(StringComparer.Ordinal);
                var answers = Property(response, "answers");
                RequireObject(answers);
                foreach (var answer in answers.EnumerateObject())
                {
                    if (!questions.Contains(answer.Name))
                        throw new CodexProtocolException("Response references an unknown user input question.");
                    RequireObject(answer.Value);
                    foreach (var text in Array(answer.Value, "answers").EnumerateArray())
                        if (text.ValueKind != JsonValueKind.String)
                            throw new CodexProtocolException("User input answers must be strings.");
                }
                break;
            case CodexToolRequestKind.McpElicitation:
                var action = RequiredText(response, "action");
                if (action is not ("accept" or "decline" or "cancel"))
                    throw new CodexProtocolException("Invalid MCP elicitation action.");
                var content = response.TryGetProperty("content", out var supplied) ? supplied : default;
                if (action != "accept" && content.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    throw new CodexProtocolException("Declined or cancelled elicitation must not contain input.");
                if (action == "accept" && Text(record.Parameters, "mode") == "openai/userVerification")
                    RequireObject(content);
                break;
            default: throw new CodexProtocolException("Unsupported tool response kind.");
        }
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Expected a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
            if (!names.Add(member.Name)) throw new CodexProtocolException("Duplicate JSON member.");
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.TryGetProperty(name, out var member) ? member : throw new CodexProtocolException($"Missing {name}.");

    private static JsonElement Array(JsonElement value, string name)
    {
        var member = Property(value, name);
        return member.ValueKind == JsonValueKind.Array ? member : throw new CodexProtocolException($"Invalid {name} array.");
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    private static string RequiredText(JsonElement value, string name) =>
        Text(value, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text :
            throw new CodexProtocolException($"Missing or invalid {name}.");

    private static string? OptionalText(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.Null) return null;
        return member.ValueKind == JsonValueKind.String ? member.GetString() :
            throw new CodexProtocolException($"Invalid {name}.");
    }

    private static bool? OptionalBoolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var member)) return null;
        return member.ValueKind is JsonValueKind.True or JsonValueKind.False ? member.GetBoolean() :
            throw new CodexProtocolException($"Invalid {name}.");
    }
}
