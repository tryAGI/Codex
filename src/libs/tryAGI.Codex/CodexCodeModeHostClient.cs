using System.Runtime.CompilerServices;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Negotiated Code Mode host lease capabilities. Truncation: 0 unspecified, 1 client, 2 server.</summary>
public sealed record CodexCodeModeHostOpened(string SessionId, bool SupportsCatalogRevision, int? OutputTruncation);

/// <summary>One lease-stream event. Exactly one concrete subtype is present.</summary>
public abstract record CodexCodeModeHostEvent
{
    /// <summary>Session lease opened.</summary>
    public sealed record Opened(CodexCodeModeHostOpened Host) : CodexCodeModeHostEvent;
    /// <summary>Nested callback cancellation, possibly before the callback arrives.</summary>
    public sealed record ToolCancelled(Guid InvocationId) : CodexCodeModeHostEvent;
    /// <summary>Correlated runtime notification.</summary>
    public sealed record Notification(Guid NotificationId, string ExecutionId, string CellId,
        string CallId, string Text) : CodexCodeModeHostEvent;
    /// <summary>Notification cancellation event.</summary>
    public sealed record NotificationCancelled(Guid NotificationId) : CodexCodeModeHostEvent;
    /// <summary>Terminal cell closure on the ordered lease stream.</summary>
    public sealed record CellClosed(string ExecutionId, string CellId, ulong FinalToolCallSequence) : CodexCodeModeHostEvent;
    /// <summary>Lease heartbeat, not an execution event.</summary>
    public sealed record Heartbeat : CodexCodeModeHostEvent;
}

/// <summary>Actual Code Mode wire tool definition. CallableName is the host-resolved JavaScript
/// name; Identity is the original registry identity. Neither grants permission to dispatch.</summary>
public sealed record CodexCodeModeWireTool(string CallableName, CodexToolIdentity Identity,
    CodexToolCallKind Kind, string Description, JsonElement? InputSchema = null, JsonElement? OutputSchema = null);

/// <summary>One nested tool-subscription message; null callback data indicates a heartbeat.</summary>
public sealed record CodexCodeModeHostToolCall(string SessionId, string? ExecutionId, string? CellId,
    Guid? InvocationId, string? RuntimeToolCallId, CodexToolIdentity? Identity, CodexToolCallKind? Kind,
    JsonElement? Input, ulong Sequence, string? TraceParent, bool Heartbeat);

/// <summary>Code Mode wire outcome; a yielded cell remains alive.</summary>
public enum CodexCodeModeHostOutcomeKind
{
    /// <summary>Cell yielded an observation.</summary>
    Yielded,
    /// <summary>Cell terminated.</summary>
    Terminated,
    /// <summary>Cell completed; ErrorText may contain a runtime error.</summary>
    Completed,
}

/// <summary>Text or media reference returned by the host. Media references are never fetched here.</summary>
public sealed record CodexCodeModeHostContent(string Kind, string Value, int? ImageDetail = null);

/// <summary>Observed execution outcome from the bound remote host.</summary>
public sealed record CodexCodeModeHostOutcome(string CellId, CodexCodeModeHostOutcomeKind Kind,
    IReadOnlyList<CodexCodeModeHostContent> Content, string? ErrorText, ulong HostDurationNanoseconds);

/// <summary>Execute stream events: one started receipt followed by one outcome.</summary>
public abstract record CodexCodeModeHostExecuteEvent
{
    /// <summary>Host accepted this execution and assigned its cell.</summary>
    public sealed record Started(string ExecutionId, string CellId) : CodexCodeModeHostExecuteEvent;
    /// <summary>Execution observation ended.</summary>
    public sealed record Outcome(CodexCodeModeHostOutcome Result) : CodexCodeModeHostExecuteEvent;
}

/// <summary>Wait or Terminate response. A missing cell is distinct from an unknown session.</summary>
public sealed record CodexCodeModeHostWaitResult(bool MissingCell, CodexCodeModeHostOutcome Outcome);

/// <summary>Native typed client for codex.code_mode.v1.CodeModeHost. It sends exact pinned
/// protobuf fields through the explicitly authorized transport. Higher-level execution
/// admission, lease ownership, callback dispatch, recovery and output delivery belong to
/// the session coordinator. No implicit JavaScript execution or retries occur here.</summary>
public sealed class CodexCodeModeHostClient(CodexCodeModeGrpcTransport transport)
{
    private readonly CodexCodeModeGrpcTransport transport =
        transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>Opens the session lease; keep this stream alive for the lease lifetime.</summary>
    public async IAsyncEnumerable<CodexCodeModeHostEvent> OpenSessionAsync(
        uint maxPendingDelegateCalls, uint heartbeatIntervalSeconds = 0, int? outputTruncation = null,
        ulong? maxYieldTimeMs = null, ulong? maxHeapSizeBytes = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(maxPendingDelegateCalls);
        if (outputTruncation is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(outputTruncation));
        var request = Build(writer =>
        {
            if (maxYieldTimeMs is not null || maxHeapSizeBytes is not null)
                writer.Message(1, limits =>
                {
                    if (maxYieldTimeMs is { } yield) limits.UInt(1, yield);
                    if (maxHeapSizeBytes is { } heap) limits.UInt(2, heap);
                });
            if (outputTruncation is { } truncation) writer.UInt(2, (ulong)truncation);
            writer.UInt(3, maxPendingDelegateCalls);
            if (heartbeatIntervalSeconds != 0) writer.UInt(5, heartbeatIntervalSeconds);
        });
        var opened = false;
        await foreach (var raw in transport.StreamAsync("OpenSession", request, cancellationToken).ConfigureAwait(false))
        {
            var result = ParseHostEvent(new(raw));
            if (!opened && result is not CodexCodeModeHostEvent.Opened ||
                opened && result is CodexCodeModeHostEvent.Opened)
                throw new CodexProtocolException("Invalid Code Mode lease opening sequence.");
            opened = true;
            yield return result;
        }
        if (!opened) throw new CodexProtocolException("Code Mode lease did not open.");
    }

    /// <summary>Subscribes to nested tool calls for a single opened session. An empty filter
    /// selects all calls. Independent HTTP/2 streams keep completions off the lease stream.</summary>
    public async IAsyncEnumerable<CodexCodeModeHostToolCall> SubscribeToToolCallsAsync(string sessionId,
        IReadOnlyList<CodexToolIdentity>? filter = null, uint heartbeatIntervalSeconds = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        var request = Build(writer =>
        {
            writer.Text(1, sessionId);
            if (filter is not null)
                foreach (var identity in filter) writer.Message(2, nested => WriteIdentity(nested, identity));
            if (heartbeatIntervalSeconds != 0) writer.UInt(4, heartbeatIntervalSeconds);
        });
        await foreach (var raw in transport.StreamAsync("SubscribeToToolCalls", request, cancellationToken).ConfigureAwait(false))
        {
            var call = ParseToolCall(new(raw));
            if (call.SessionId != sessionId)
                throw new CodexProtocolException("Code Mode tool callback belongs to another session.");
            yield return call;
        }
    }

    /// <summary>Sends an explicitly admitted cell to the bound host. The caller must resolve
    /// the JavaScript callable names from its exact admitted tool registry. If a legacy host
    /// lacks catalog revision support, provide a self-contained tool catalog instead.</summary>
    public async IAsyncEnumerable<CodexCodeModeHostExecuteEvent> ExecuteAsync(string sessionId,
        string executionId, string toolCallId, string source, IReadOnlyList<CodexCodeModeWireTool> tools,
        ulong? yieldTimeMs = null, ulong? maxOutputTokens = null, ulong? catalogRevision = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        ValidateId(executionId);
        ValidateId(toolCallId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tools);
        if (catalogRevision == 0) throw new ArgumentOutOfRangeException(nameof(catalogRevision));
        if (tools.Select(tool => tool.CallableName).Distinct(StringComparer.Ordinal).Count() != tools.Count ||
            tools.Select(tool => tool.Identity).Distinct().Count() != tools.Count)
            throw new ArgumentException("Ambiguous Code Mode tool catalog.", nameof(tools));
        var request = Build(writer =>
        {
            writer.Text(1, sessionId);
            writer.Text(2, executionId);
            writer.Text(3, toolCallId);
            writer.Text(4, source);
            foreach (var tool in tools) writer.Message(5, nested => WriteTool(nested, tool));
            if (yieldTimeMs is { } yield) writer.UInt(6, yield);
            if (maxOutputTokens is { } tokens) writer.UInt(7, tokens);
            if (catalogRevision is { } revision) writer.UInt(8, revision);
        });
        string? cell = null;
        CodexCodeModeHostOutcome? terminal = null;
        await foreach (var raw in transport.StreamAsync("Execute", request, cancellationToken).ConfigureAwait(false))
        {
            var wire = new CodexCodeModeProto(raw);
            var kind = wire.OneOf(1, 2);
            if (kind == 1 && cell is null)
            {
                var started = wire.Message(1);
                if (started.RequiredText(1) != executionId)
                    throw new CodexProtocolException("Code Mode execution acknowledgment is uncorrelated.");
                cell = started.RequiredText(2);
                ValidateId(cell);
                yield return new CodexCodeModeHostExecuteEvent.Started(executionId, cell);
            }
            else if (kind == 2 && cell is not null && terminal is null)
            {
                var result = ParseOutcome(wire.Message(2));
                if (result.CellId != cell) throw new CodexProtocolException("Code Mode execution outcome belongs to another cell.");
                terminal = result;
            }
            else throw new CodexProtocolException("Unexpected Code Mode execute event ordering.");
        }
        // Do not publish an outcome before the transport validates the final gRPC status.
        yield return new CodexCodeModeHostExecuteEvent.Outcome(terminal ??
            throw new CodexProtocolException("Code Mode execute stream ended without an outcome."));
    }

    /// <summary>Waits for a live or missing cell observation on its independent RPC stream.</summary>
    public async Task<CodexCodeModeHostWaitResult> WaitAsync(string sessionId, string cellId,
        string waitId, ulong yieldTimeMs, ulong? maxOutputTokens = null, CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        ValidateId(cellId);
        ValidateId(waitId);
        var request = Build(writer =>
        {
            writer.Text(1, sessionId); writer.Text(2, cellId); writer.Text(3, waitId);
            writer.UInt(4, yieldTimeMs);
            if (maxOutputTokens is { } tokens) writer.UInt(5, tokens);
        });
        return ParseWait(await transport.UnaryAsync("Wait", request, cancellationToken).ConfigureAwait(false), cellId);
    }

    /// <summary>Terminates exactly one cell. This is not a local cancellation receipt.</summary>
    public async Task<CodexCodeModeHostWaitResult> TerminateAsync(string sessionId, string cellId,
        ulong? maxOutputTokens = null, CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        ValidateId(cellId);
        var request = Build(writer =>
        {
            writer.Text(1, sessionId); writer.Text(2, cellId);
            if (maxOutputTokens is { } tokens) writer.UInt(3, tokens);
        });
        return ParseWait(await transport.UnaryAsync("Terminate", request, cancellationToken).ConfigureAwait(false), cellId);
    }

    /// <summary>Requests early observation yield without terminating the cell.</summary>
    public Task YieldObservationAsync(string sessionId, string observationId, bool isWait,
        CancellationToken cancellationToken = default) =>
        EmptyAsync("YieldObservation", Identified(sessionId, isWait ? 3 : 2, observationId), cancellationToken);

    /// <summary>Retires a cancelled wait observation before another is admitted.</summary>
    public Task CancelWaitAsync(string sessionId, string waitId, CancellationToken cancellationToken = default) =>
        EmptyAsync("CancelWait", Identified(sessionId, 2, waitId), cancellationToken);

    /// <summary>Acknowledges a notification after host delivery has been accepted.</summary>
    public Task AcknowledgeNotificationAsync(string sessionId, Guid notificationId,
        CancellationToken cancellationToken = default) =>
        EmptyAsync("AcknowledgeNotification", Identified(sessionId, 2, GuidText(notificationId)), cancellationToken);

    /// <summary>Completes an exact tool invocation with an explicitly supplied JSON result.</summary>
    public Task CompleteToolCallAsync(string sessionId, Guid invocationId, JsonElement output,
        CancellationToken cancellationToken = default)
    {
        if (output.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Missing tool output.", nameof(output));
        return CompleteAsync(sessionId, invocationId, writer =>
            writer.Message(3, succeeded => succeeded.Bytes(1, System.Text.Encoding.UTF8.GetBytes(output.GetRawText()))),
            cancellationToken);
    }

    /// <summary>Reports a tool invocation failure without fabricating successful output.</summary>
    public Task FailToolCallAsync(string sessionId, Guid invocationId, string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return CompleteAsync(sessionId, invocationId, writer => writer.Message(4, failed => failed.Text(1, message)), cancellationToken);
    }

    /// <summary>Explicitly closes the bound host session.</summary>
    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateId(sessionId);
        return EmptyAsync("CloseSession", Build(writer => writer.Text(1, sessionId)), cancellationToken);
    }

    private Task CompleteAsync(string sessionId, Guid invocationId, Action<CodexCodeModeProtoWriter> outcome,
        CancellationToken ct)
    {
        ValidateId(sessionId);
        var id = GuidText(invocationId);
        return EmptyAsync("CompleteToolCall", Build(writer =>
        {
            writer.Text(1, sessionId); writer.Text(2, id); outcome(writer);
        }), ct);
    }

    private async Task EmptyAsync(string method, byte[] payload, CancellationToken ct)
    {
        // Empty proto3 responses may contain unknown future fields.
        _ = new CodexCodeModeProto(await transport.UnaryAsync(method, payload, ct).ConfigureAwait(false));
    }

    private static byte[] Identified(string sessionId, int field, string identity)
    {
        ValidateId(sessionId);
        ValidateId(identity);
        return Build(writer => { writer.Text(1, sessionId); writer.Text(field, identity); });
    }

    private static CodexCodeModeHostEvent ParseHostEvent(CodexCodeModeProto message)
    {
        var field = message.OneOf(1, 2, 3, 4, 5, 6);
        var value = message.Message(field);
        return field switch
        {
            1 => new CodexCodeModeHostEvent.Opened(new(value.RequiredText(1), value.Boolean(3),
                value.Has(2) ? ParseTruncation(value.UInt(2)) : null)),
            2 => new CodexCodeModeHostEvent.ToolCancelled(ParseGuid(value.RequiredText(1))),
            3 => new CodexCodeModeHostEvent.Notification(ParseGuid(value.RequiredText(1)), value.RequiredText(2),
                value.RequiredText(3), value.RequiredText(4), value.Text(5) ?? ""),
            4 => new CodexCodeModeHostEvent.NotificationCancelled(ParseGuid(value.RequiredText(1))),
            5 => new CodexCodeModeHostEvent.CellClosed(value.RequiredText(1), value.RequiredText(2), value.UInt(3)),
            6 => new CodexCodeModeHostEvent.Heartbeat(),
            _ => throw new CodexProtocolException("Unknown Code Mode lease event."),
        };
    }

    private static CodexCodeModeHostToolCall ParseToolCall(CodexCodeModeProto message)
    {
        var session = message.RequiredText(1);
        if (message.Has(11))
        {
            _ = message.Message(11);
            if (Enumerable.Range(2, 9).Any(message.Has))
                throw new CodexProtocolException("Code Mode heartbeat carried a tool invocation.");
            return new(session, null, null, null, null, null, null, null, 0, null, true);
        }
        var identity = message.Message(6);
        var sequence = message.UInt(9);
        if (sequence == 0) throw new CodexProtocolException("Invalid Code Mode tool sequence.");
        return new(session, message.RequiredText(2), message.RequiredText(3), ParseGuid(message.RequiredText(4)),
            message.RequiredText(5), new(identity.Text(2), identity.RequiredText(1)), message.UInt(7) switch
            {
                1 => CodexToolCallKind.Function,
                2 => CodexToolCallKind.Custom,
                _ => throw new CodexProtocolException("Unknown Code Mode tool kind."),
            }, ParseJson(message.Bytes(8)), sequence, message.Text(10), false);
    }

    private static CodexCodeModeHostWaitResult ParseWait(byte[] bytes, string expectedCellId)
    {
        var response = new CodexCodeModeProto(bytes);
        var state = response.OneOf(1, 2);
        var outcome = ParseOutcome(response.Message(state));
        if (outcome.CellId != expectedCellId) throw new CodexProtocolException("Code Mode wait outcome belongs to another cell.");
        return new(state == 2, outcome);
    }

    private static CodexCodeModeHostOutcome ParseOutcome(CodexCodeModeProto outcome)
    {
        var field = outcome.OneOf(3, 4, 5);
        var body = outcome.Message(field);
        var contents = outcome.Repeated(2).Select(content =>
        {
            var variant = content.OneOf(1, 2, 3);
            var item = content.Message(variant);
            return variant switch
            {
                1 => new CodexCodeModeHostContent("text", item.Text(1) ?? ""),
                2 => new CodexCodeModeHostContent("image", item.RequiredText(1),
                    item.Has(2) ? ParseImageDetail(item.UInt(2)) : null),
                3 => new CodexCodeModeHostContent("audio", item.RequiredText(1)),
                _ => throw new CodexProtocolException("Unknown Code Mode content type."),
            };
        }).ToArray();
        return new(outcome.RequiredText(1), field switch
        {
            3 => CodexCodeModeHostOutcomeKind.Yielded,
            4 => CodexCodeModeHostOutcomeKind.Terminated,
            _ => CodexCodeModeHostOutcomeKind.Completed,
        }, Array.AsReadOnly(contents), field == 5 ? body.Text(1) : null, outcome.UInt(6));
    }

    private static void WriteTool(CodexCodeModeProtoWriter writer, CodexCodeModeWireTool tool)
    {
        ValidateId(tool.CallableName);
        ArgumentNullException.ThrowIfNull(tool.Description);
        writer.Text(1, tool.CallableName);
        writer.Message(2, nested => WriteIdentity(nested, tool.Identity));
        writer.Text(3, tool.Description);
        writer.UInt(4, tool.Kind switch
        {
            CodexToolCallKind.Function => 1UL,
            CodexToolCallKind.Custom => 2UL,
            _ => throw new ArgumentException("Unknown Code Mode tool kind.", nameof(tool)),
        });
        if (tool.InputSchema is { } input) writer.Bytes(5, System.Text.Encoding.UTF8.GetBytes(input.GetRawText()));
        if (tool.OutputSchema is { } output) writer.Bytes(6, System.Text.Encoding.UTF8.GetBytes(output.GetRawText()));
    }

    private static void WriteIdentity(CodexCodeModeProtoWriter writer, CodexToolIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateId(identity.Name);
        ValidateId(identity.Namespace);
        writer.Text(1, identity.Name);
        writer.Text(2, identity.Namespace);
    }

    private static JsonElement? ParseJson(ReadOnlyMemory<byte>? input)
    {
        if (input is not { } bytes) return null;
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static int ParseTruncation(ulong value) => value <= 2 ? (int)value :
        throw new CodexProtocolException("Unknown Code Mode output truncation.");

    private static int ParseImageDetail(ulong value) => value <= 4 ? (int)value :
        throw new CodexProtocolException("Unknown Code Mode image detail.");

    // The host protocol uses canonical UUID strings. Refuse alternate spellings rather
    // than acknowledge a differently spelled wire identity through Guid normalization.
    private static Guid ParseGuid(string text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty &&
        id.ToString("D") == text ? id : throw new CodexProtocolException("Invalid Code Mode callback UUID.");

    private static string GuidText(Guid id) => id == Guid.Empty ?
        throw new ArgumentException("Missing Code Mode callback UUID.", nameof(id)) : id.ToString("D");

    private static byte[] Build(Action<CodexCodeModeProtoWriter> build)
    {
        var writer = new CodexCodeModeProtoWriter();
        build(writer);
        return writer.ToArray();
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 1024 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid Code Mode identity.");
    }
}
