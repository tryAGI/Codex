using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Caller-owned native Responses WebSocket session. Dispose at the end of the reasoning turn.
/// A single connection is reused only within the same authenticated routing scope.</summary>
public sealed class CodexResponsesWebSocketSession : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<HttpRequestMessage, CancellationToken, Task<Connection?>> connect;
    private WebSocket? socket;
    private object? scope;
    private bool fallback;
    private bool disposed;
    private JsonObject? previous;
    private JsonArray? previousOutput;
    private string? responseId;
    private const int MaximumCompletedResponses = 1024;
    private readonly HashSet<string> completedResponses = new(StringComparer.Ordinal);

    /// <summary>Create an optional WebSocket transport, with SSE fallback before dispatch only.</summary>
    public CodexResponsesWebSocketSession() : this(ConnectNativeAsync) { }

    internal CodexResponsesWebSocketSession(Func<HttpRequestMessage, CancellationToken, Task<Connection?>> connect)
        => this.connect = connect;

    internal sealed record Connection(WebSocket Socket, IEnumerable<string>? TurnState = null,
        IReadOnlyList<CodexObservedQuota>? Quotas = null);

    internal async Task<Lease?> OpenAsync(HttpRequestMessage request, CodexResponsesRouting routing, object generation,
        int maximumBytes, CancellationToken ct, bool prewarm = false)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var handedOff = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ReferenceEquals(scope, generation))
            {
                Reset();
                scope = generation;
                fallback = false;
            }
            if (fallback) return null;
            IReadOnlyList<CodexObservedQuota>? quotas = null;
            if (socket?.State != WebSocketState.Open || completedResponses.Count >= MaximumCompletedResponses)
            {
                Reset();
                // A prewarm handshake carries the prewarm kind, but an HTTP fallback still carries the turn kind.
                const string metadataHeader = "x-codex-turn-metadata";
                var metadata = prewarm && request.Headers.TryGetValues(metadataHeader, out var values)
                    ? values.SingleOrDefault() : null;
                if (metadata is not null)
                {
                    request.Headers.Remove(metadataHeader);
                    request.Headers.Add(metadataHeader, CodexResponsesIdentity.WithRequestKind(metadata, "prewarm"));
                }
                Connection? connection;
                try { connection = await connect(request, ct).ConfigureAwait(false); }
                finally
                {
                    if (metadata is not null)
                    {
                        request.Headers.Remove(metadataHeader);
                        request.Headers.Add(metadataHeader, metadata);
                    }
                }
                if (connection is null)
                {
                    fallback = true;
                    return null;
                }
                socket = connection.Socket;
                quotas = connection.Quotas;
                if (connection.TurnState is { } state) routing.Capture(state, generation);
            }
            var lease = new Lease(this, routing, generation, maximumBytes) { Quotas = quotas };
            handedOff = true;
            return lease;
        }
        finally
        {
            if (!handedOff) gate.Release();
        }
    }

    private static async Task<Connection?> ConnectNativeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            var uri = new UriBuilder(request.RequestUri ?? throw new InvalidOperationException("Missing Responses endpoint."));
            if (uri.Scheme != "https") throw new CodexProtocolException("Responses WebSocket requires an HTTPS endpoint.");
            uri.Scheme = "wss";
            foreach (var header in request.Headers)
                if (!header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                    socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
            socket.Options.SetRequestHeader("OpenAI-Beta", "responses_websockets=2026-02-06");
            await socket.ConnectAsync(uri.Uri, ct).ConfigureAwait(false);
            var turnState = socket.HttpResponseHeaders?.FirstOrDefault(header =>
                header.Key.Equals("x-codex-turn-state", StringComparison.OrdinalIgnoreCase)).Value;
            return new(socket, turnState, socket.HttpResponseHeaders is { } headers ? CodexObservedUsage.ReadHeaders(headers) : null);
        }
        catch (WebSocketException)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            ct.ThrowIfCancellationRequested();
            // No response.create was sent. Never route around authentication or quota errors.
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new CodexProtocolException("Responses WebSocket handshake rejected.", (int)status);
            if ((int)status is not (0 or 404 or 405 or 426 or 500 or 501 or 502 or 503 or 504))
                throw new CodexProtocolException("Responses WebSocket handshake failed.", (int)status);
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal JsonObject CreateFrame(JsonObject body, string? turnState, bool prewarm)
    {
        var frame = (JsonObject)body.DeepClone();
        frame["type"] = "response.create";
        if (!prewarm && previous is not null && previousOutput is not null && responseId is not null &&
            IncrementalSuffix(previous, previousOutput, body) is { } suffix)
        {
            frame["input"] = suffix;
            frame["previous_response_id"] = responseId;
        }
        if (prewarm)
        {
            frame["generate"] = false;
            if (frame["client_metadata"] is JsonObject prewarmMetadata &&
                prewarmMetadata["x-codex-turn-metadata"]?.GetValue<string>() is { } turnMetadata)
                prewarmMetadata["x-codex-turn-metadata"] = CodexResponsesIdentity.WithRequestKind(turnMetadata, "prewarm");
        }
        if (turnState is not null)
        {
            if (frame["client_metadata"] is not JsonObject metadata)
            {
                metadata = new JsonObject();
                frame["client_metadata"] = metadata;
            }
            metadata["x-codex-turn-state"] = turnState;
        }
        return frame;
    }

    internal static JsonArray? IncrementalSuffix(JsonObject previous, JsonArray output, JsonObject current)
    {
        if (previous["input"] is not JsonArray oldInput || current["input"] is not JsonArray input ||
            input.Count < oldInput.Count + output.Count) return null;
        // Exact equality is intentionally conservative for opaque and unknown provider fields.
        var oldProperties = (JsonObject)previous.DeepClone();
        var newProperties = (JsonObject)current.DeepClone();
        // Rust excludes per-response delivery options and client metadata from context equality.
        // The connection's authenticated turn scope is fenced separately.
        foreach (var key in new[] { "input", "stream_options", "client_metadata" })
        {
            oldProperties.Remove(key);
            newProperties.Remove(key);
        }
        if (!JsonNode.DeepEquals(oldProperties, newProperties)) return null;
        for (var i = 0; i < oldInput.Count; i++)
            if (!JsonNode.DeepEquals(oldInput[i], input[i])) return null;
        for (var i = 0; i < output.Count; i++)
            if (!JsonNode.DeepEquals(output[i], input[oldInput.Count + i])) return null;
        return new JsonArray(input.Skip(oldInput.Count + output.Count).Select(item => item?.DeepClone()).ToArray());
    }

    private void Remember(JsonObject body, string id, IReadOnlyList<JsonElement> output, int maximumBytes)
    {
        previous = null;
        previousOutput = null;
        responseId = null;
        if (string.IsNullOrWhiteSpace(id) ||
            (long)Encoding.UTF8.GetByteCount(body.ToJsonString()) +
            output.Sum(item => (long)Encoding.UTF8.GetByteCount(item.GetRawText())) > (long)maximumBytes * 4)
            return;
        previous = (JsonObject)body.DeepClone();
        previousOutput = new JsonArray(output.Select(item => JsonNode.Parse(item.GetRawText())).ToArray());
        responseId = id;
    }

    private void Reset()
    {
        socket?.Dispose();
        socket = null;
        previous = null;
        previousOutput = null;
        responseId = null;
        completedResponses.Clear();
    }

    /// <summary>Release the native connection and all cached request/output continuation state.</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; Reset(); }
        finally { gate.Release(); }
    }

    internal sealed class Lease(CodexResponsesWebSocketSession owner, CodexResponsesRouting routing,
        object generation, int maximumBytes) : IDisposable
    {
        private bool completed;
        private bool released;

        internal IReadOnlyList<CodexObservedQuota>? Quotas { get; init; }
        internal bool NeedsPrewarm => owner.previous is null;
        internal CodexResponsesCorrelation NewCorrelation() => new(owner.completedResponses.Contains, requireTerminalId: true);

        internal async Task SendAsync(JsonObject body, bool prewarm, CancellationToken ct)
        {
            var frame = owner.CreateFrame(body, routing.GetState(generation), prewarm);
            var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
            await owner.socket!.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        internal async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await owner.socket!.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new CodexProtocolException("Responses WebSocket received a non-text message.");
                if (message.Length + result.Count > maximumBytes)
                    throw new CodexProtocolException("Responses WebSocket message exceeded the configured bound.");
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var data = new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, checked((int)message.Length));
                message.SetLength(0);
                yield return data;
            }
        }

        internal async Task PrewarmAsync(JsonObject body, CancellationToken ct)
        {
            await SendAsync(body, true, ct).ConfigureAwait(false);
            var correlation = NewCorrelation();
            await foreach (var data in ReadAsync(ct).ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(data);
                var payload = document.RootElement;
                var type = CodexRealtimeProtocol.Text(payload, "type") ?? throw new CodexProtocolException("Responses prewarm event omitted its type.");
                if (correlation.Observe(type, payload) is { } failure)
                    throw new CodexProtocolException("Responses prewarm rejected (" + failure + ").");
                routing.CaptureEvent(payload, type, generation);
                if (type is "error" or "response.failed" or "response.incomplete" or "response.output_item.added" or "response.output_item.done" ||
                    type.StartsWith("response.", StringComparison.Ordinal) && type.EndsWith(".delta", StringComparison.Ordinal))
                    throw new CodexProtocolException("Responses prewarm did not prove a non-generating completion.");
                if (type != "response.completed") continue;
                if (!payload.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object ||
                    response.TryGetProperty("output", out var output) && (output.ValueKind != JsonValueKind.Array ||
                    output.GetArrayLength() != 0) ||
                    response.TryGetProperty("status", out var status) && status.GetString() != "completed" ||
                    response.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null &&
                    (usage.ValueKind != JsonValueKind.Object || !HasOnlyZeroUsage(usage)))
                    throw new CodexProtocolException("Responses prewarm unexpectedly produced output or usage.");
                owner.completedResponses.Add(correlation.ResponseId!);
                owner.Remember(body, correlation.ResponseId!, [], maximumBytes);
                return;
            }
            throw new CodexProtocolException("Responses prewarm ended without completion.");
        }

        private static bool HasOnlyZeroUsage(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().All(property => HasOnlyZeroUsage(property.Value)),
            JsonValueKind.Number => value.TryGetInt64(out var count) && count == 0,
            JsonValueKind.Null => true,
            _ => false,
        };

        internal void Complete(JsonObject body, string id, IReadOnlyList<JsonElement> output)
        {
            owner.completedResponses.Add(id);
            owner.Remember(body, id, output, maximumBytes);
            completed = true;
        }

        public void Dispose()
        {
            if (released) return;
            released = true;
            if (!completed) owner.Reset();
            owner.gate.Release();
        }
    }
}
