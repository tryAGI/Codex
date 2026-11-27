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

    /// <summary>Create an optional WebSocket transport, with SSE fallback before dispatch only.</summary>
    public CodexResponsesWebSocketSession() : this(ConnectNativeAsync) { }

    internal CodexResponsesWebSocketSession(Func<HttpRequestMessage, CancellationToken, Task<Connection?>> connect)
        => this.connect = connect;

    internal sealed record Connection(WebSocket Socket, IEnumerable<string>? TurnState = null);

    internal async Task<Lease?> OpenAsync(HttpRequestMessage request, CodexResponsesRouting routing, object generation,
        int maximumBytes, CancellationToken ct)
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
            if (socket?.State != WebSocketState.Open)
            {
                Reset();
                var connection = await connect(request, ct).ConfigureAwait(false);
                if (connection is null)
                {
                    fallback = true;
                    return null;
                }
                socket = connection.Socket;
                if (connection.TurnState is { } state) routing.Capture(state, generation);
            }
            var lease = new Lease(this, routing, generation, maximumBytes);
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
            return new(socket, turnState);
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
        if (prewarm) frame["generate"] = false;
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
        oldProperties.Remove("input");
        newProperties.Remove("input");
        if (!JsonNode.DeepEquals(oldProperties, newProperties)) return null;
        for (var i = 0; i < oldInput.Count; i++)
            if (!JsonNode.DeepEquals(oldInput[i], input[i])) return null;
        for (var i = 0; i < output.Count; i++)
            if (!JsonNode.DeepEquals(output[i], input[oldInput.Count + i])) return null;
        return new JsonArray(input.Skip(oldInput.Count + output.Count).Select(item => item?.DeepClone()).ToArray());
    }

    private void Remember(JsonObject body, JsonElement response, int maximumBytes)
    {
        previous = null;
        previousOutput = null;
        responseId = null;
        var id = CodexRealtimeProtocol.Text(response, "id");
        if (string.IsNullOrWhiteSpace(id) || !response.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array ||
            (long)Encoding.UTF8.GetByteCount(body.ToJsonString()) + Encoding.UTF8.GetByteCount(output.GetRawText()) > (long)maximumBytes * 4)
            return;
        previous = (JsonObject)body.DeepClone();
        previousOutput = JsonNode.Parse(output.GetRawText()) as JsonArray;
        responseId = id;
    }

    private void Reset()
    {
        socket?.Dispose();
        socket = null;
        previous = null;
        previousOutput = null;
        responseId = null;
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
        private readonly List<JsonElement> streamed = [];
        private bool streamedOverflow;
        private long streamedBytes;

        internal bool NeedsPrewarm => owner.previous is null;

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
            await foreach (var data in ReadAsync(ct).ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(data);
                var payload = document.RootElement;
                var type = CodexRealtimeProtocol.Text(payload, "type");
                if (type is "error" or "response.failed" or "response.incomplete" or "response.output_item.added" or "response.output_item.done")
                    throw new CodexProtocolException("Responses prewarm did not prove a non-generating completion.");
                if (type != "response.completed") continue;
                if (!payload.TryGetProperty("response", out var response) ||
                    !response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array ||
                    output.GetArrayLength() != 0 ||
                    response.TryGetProperty("status", out var status) && status.GetString() != "completed" ||
                    response.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null &&
                    (usage.ValueKind != JsonValueKind.Object || !HasOnlyZeroUsage(usage)))
                    throw new CodexProtocolException("Responses prewarm unexpectedly produced output or usage.");
                owner.Remember(body, response, maximumBytes);
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

        internal void Observe(string type, JsonElement payload, JsonObject body)
        {
            if (type == "response.output_item.done" && payload.TryGetProperty("item", out var item))
            {
                streamedBytes += Encoding.UTF8.GetByteCount(item.GetRawText());
                if (!streamedOverflow && streamed.Count < 1024 && streamedBytes <= (long)maximumBytes * 4)
                    streamed.Add(item.Clone());
                else { streamedOverflow = true; streamed.Clear(); }
            }
            if (type != "response.completed") return;
            var response = payload.GetProperty("response");
            var output = response.GetProperty("output");
            // If streaming and terminal output disagree, never infer a continuation prefix.
            if (!streamedOverflow && (streamed.Count == 0 ||
                streamed.Count == output.GetArrayLength() && streamed.Zip(output.EnumerateArray()).All(pair =>
                    JsonElement.DeepEquals(pair.First, pair.Second))))
                owner.Remember(body, response, maximumBytes);
            else
            {
                owner.previous = null;
                owner.previousOutput = null;
                owner.responseId = null;
            }
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
