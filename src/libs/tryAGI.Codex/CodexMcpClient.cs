using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>One already authorized MCP connection. Exactly one reader is used;
/// the client serializes writes. A null read means the connection is closed.
/// Implementations must honor cancellation and either send a complete message or fail.
/// Creating this interface does not authorize launching a process or connecting to a URL.</summary>
public interface ICodexMcpTransport : IAsyncDisposable
{
    /// <summary>Sends one complete JSON-RPC message.</summary>
    ValueTask SendAsync(JsonElement message, CancellationToken cancellationToken);
    /// <summary>Receives the next complete JSON-RPC message in transport order.</summary>
    ValueTask<JsonElement?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Lifecycle of a single MCP connection; failed sessions are never silently replayed.</summary>
public enum CodexMcpState
{
    /// <summary>Transport supplied; no handshake attempted.</summary>
    Created,
    /// <summary>Handshake in progress.</summary>
    Initializing,
    /// <summary>Negotiated and initialized.</summary>
    Ready,
    /// <summary>Unrecoverable connection failure; create a new client.</summary>
    Faulted,
    /// <summary>Explicitly closed by the owner.</summary>
    Closed,
}

/// <summary>Exact request presented to the host's authorization policy.</summary>
public sealed record CodexMcpRequest(string Method, JsonElement Parameters);

/// <summary>Negotiated MCP server information, retaining extensions.</summary>
public sealed record CodexMcpServerInfo(string ProtocolVersion, JsonElement Capabilities, JsonElement Raw);

/// <summary>Atomic tool inventory at a particular notification revision.</summary>
public sealed record CodexMcpToolCatalog(long Revision, IReadOnlyList<JsonElement> Tools);

/// <summary>Remote JSON-RPC error, distinct from a tool result whose isError flag is true.</summary>
public sealed class CodexMcpRpcException(int code, string message, JsonElement? data = null) : IOException(message)
{
    /// <summary>Exact JSON-RPC error code.</summary>
    public int Code { get; } = code;
    /// <summary>Optional raw JSON-RPC error data.</summary>
    public JsonElement? DataValue { get; } = data;
}

/// <summary>Native legacy MCP JSON-RPC session over an explicitly supplied transport.
/// The required host policy authorizes every outgoing operation independently of discovery.
/// No credentials, endpoint discovery, process launch, retry, or approval are implicit.</summary>
public sealed class CodexMcpClient : IAsyncDisposable
{
    /// <summary>Supported legacy MCP wire version.</summary>
    public const string LegacyProtocolVersion = "2025-06-18";
    private readonly ICodexMcpTransport transport;
    private readonly Func<CodexMcpRequest, CancellationToken, ValueTask<bool>> authorize;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken stop;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly object closeGate = new();
    private readonly object catalogGate = new();
    private int state;
    private long nextId;
    private long toolRevision;
    private Task? receiveTask;
    private Task? closeTask;
    private CodexMcpServerInfo? serverInfo;
    private CodexMcpToolCatalog? catalog;

    /// <summary>Accepts an established host-authorized connection and explicit request policy.</summary>
    public CodexMcpClient(ICodexMcpTransport transport,
        Func<CodexMcpRequest, CancellationToken, ValueTask<bool>> authorize, TimeSpan? requestTimeout = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        stop = lifetime.Token;
    }

    /// <summary>Current connection state.</summary>
    public CodexMcpState State => (CodexMcpState)Volatile.Read(ref state);
    /// <summary>Negotiated information, if the handshake returned it.</summary>
    public CodexMcpServerInfo? ServerInfo => Volatile.Read(ref serverInfo);
    /// <summary>Number of catalog invalidations received on this connection.</summary>
    public long ToolListRevision => Interlocked.Read(ref toolRevision);
    /// <summary>Last complete catalog, possibly invalidated by the server.</summary>
    public CodexMcpToolCatalog? ToolCatalog => Volatile.Read(ref catalog);
    /// <summary>Whether the catalog matches the current ready connection and revision.</summary>
    public bool IsToolCatalogCurrent => State == CodexMcpState.Ready &&
        ToolCatalog is { } current && current.Revision == ToolListRevision;

    /// <summary>Negotiates the pinned legacy MCP protocol and completes initialized notification.
    /// A failed or cancelled handshake requires a new client/transport.</summary>
    public async Task<CodexMcpServerInfo> InitializeAsync(string clientName, string clientVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        lock (closeGate)
        {
            if (Interlocked.CompareExchange(ref state, (int)CodexMcpState.Initializing,
                    (int)CodexMcpState.Created) != (int)CodexMcpState.Created)
                throw new InvalidOperationException("MCP client has already started or closed.");
            receiveTask = ReceiveLoopAsync();
        }
        try
        {
            var result = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = LegacyProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = clientVersion },
            }, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object ||
                CodexRealtimeProtocol.Text(result, "protocolVersion") != LegacyProtocolVersion ||
                !result.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("serverInfo", out var info) || info.ValueKind != JsonValueKind.Object)
                throw new CodexProtocolException("Unsupported or invalid MCP initialize result.");
            using var initialized = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop);
            initialized.CancelAfter(timeout);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized",
            }, initialized.Token).ConfigureAwait(false);
            var negotiated = new CodexMcpServerInfo(LegacyProtocolVersion, capabilities.Clone(), result.Clone());
            Volatile.Write(ref serverInfo, negotiated);
            if (Interlocked.CompareExchange(ref state, (int)CodexMcpState.Ready,
                    (int)CodexMcpState.Initializing) != (int)CodexMcpState.Initializing)
                throw new IOException("MCP connection closed during initialization.");
            return negotiated;
        }
        catch (Exception error)
        {
            Fault(error);
            throw;
        }
    }

    /// <summary>Lists one page of tools; this does not authorize their execution.</summary>
    public Task<JsonElement> ListToolsAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
        InvokeAsync("tools/list", "tools", Page(cursor), cancellationToken);

    /// <summary>Lists one page of authorized resources.</summary>
    public Task<JsonElement> ListResourcesAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
        InvokeAsync("resources/list", "resources", Page(cursor), cancellationToken);

    /// <summary>Lists one page of authorized resource templates.</summary>
    public Task<JsonElement> ListResourceTemplatesAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
        InvokeAsync("resources/templates/list", "resources", Page(cursor), cancellationToken);

    /// <summary>Lists one page of authorized prompts.</summary>
    public Task<JsonElement> ListPromptsAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
        InvokeAsync("prompts/list", "prompts", Page(cursor), cancellationToken);

    /// <summary>Reads a resource URI after host authorization of the exact request.</summary>
    public Task<JsonElement> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return InvokeAsync("resources/read", "resources", new JsonObject { ["uri"] = uri }, cancellationToken);
    }

    /// <summary>Obtains a named prompt with host-authorized arguments.</summary>
    public Task<JsonElement> GetPromptAsync(string name, JsonElement? arguments = null, CancellationToken cancellationToken = default) =>
        InvokeAsync("prompts/get", "prompts", Named(name, arguments), cancellationToken);

    /// <summary>Calls an exact tool only after host authorization of its name and arguments.
    /// Server annotations and previous discovery do not grant permission.</summary>
    public Task<JsonElement> CallToolAsync(string name, JsonElement? arguments = null, CancellationToken cancellationToken = default) =>
        InvokeAsync("tools/call", "tools", Named(name, arguments), cancellationToken);

    /// <summary>Refreshes the complete catalog, refusing duplicate names/cursors, invalid schemas,
    /// or an invalidation during pagination. No partial catalog becomes current.</summary>
    public async Task<CodexMcpToolCatalog> RefreshToolsAsync(int maximumPages = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPages);
        var revision = ToolListRevision;
        var tools = new List<JsonElement>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < maximumPages; page++)
        {
            var result = await ListToolsAsync(cursor, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("tools", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new CodexProtocolException("Invalid MCP tool inventory.");
            foreach (var entry in entries.EnumerateArray())
            {
                var name = entry.ValueKind == JsonValueKind.Object ? CodexRealtimeProtocol.Text(entry, "name") : null;
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name) ||
                    !entry.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                    throw new CodexProtocolException("Invalid or duplicate MCP tool definition.");
                tools.Add(entry.Clone());
            }
            if (ToolListRevision != revision)
                throw new CodexProtocolException("MCP tool inventory changed during refresh.");
            if (!result.TryGetProperty("nextCursor", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                lock (catalogGate)
                {
                    if (ToolListRevision != revision || State != CodexMcpState.Ready)
                        throw new CodexProtocolException("MCP tool inventory changed during refresh.");
                    var snapshot = new CodexMcpToolCatalog(revision, Array.AsReadOnly(tools.ToArray()));
                    Volatile.Write(ref catalog, snapshot);
                    return snapshot;
                }
            }
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cursor = next.GetString()) || !cursors.Add(cursor))
                throw new CodexProtocolException("Invalid or repeated MCP inventory cursor.");
        }
        throw new CodexProtocolException("MCP inventory pagination limit exceeded.");
    }

    private static JsonObject Page(string? cursor) =>
        cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };

    private static JsonObject Named(string name, JsonElement? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (arguments is { } input && input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("MCP arguments must be an object.", nameof(arguments));
        var result = new JsonObject { ["name"] = name };
        if (arguments is { } args) result["arguments"] = JsonNode.Parse(args.GetRawText());
        return result;
    }

    private Task<JsonElement> InvokeAsync(string method, string capability, JsonObject parameters, CancellationToken ct)
    {
        if (State != CodexMcpState.Ready || ServerInfo is not { } info)
            throw new InvalidOperationException("MCP client is not ready.");
        if (!info.Capabilities.TryGetProperty(capability, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"MCP server did not advertise {capability} capability.");
        return RequestAsync(method, parameters, ct);
    }

    private async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        if (!await authorize(new CodexMcpRequest(method, ToElement(parameters)), token).ConfigureAwait(false))
            throw new UnauthorizedAccessException("MCP request denied by host policy.");
        token.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate MCP request ID.");
        var attempted = false;
        try
        {
            await writes.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                attempted = true;
                try
                {
                    await transport.SendAsync(ToElement(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
                    }), token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    pending.TryRemove(id, out _);
                    Fault(error);
                    throw;
                }
            }
            finally { writes.Release(); }
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Prefer a correlated result that arrived before cancellation. Never replay a sent call.
            if (completion.Task.IsCompleted) return await completion.Task.ConfigureAwait(false);
            if (attempted && !stop.IsCancellationRequested)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
                cancel.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
                        ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "MCP request cancelled by caller" },
                    }, cancel.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // Cancellation is best effort; an ambiguous remote result is never retried.
                }
            }
            if (!ct.IsCancellationRequested && !stop.IsCancellationRequested)
                throw new TimeoutException($"MCP request {method} timed out.");
            throw;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async ValueTask SendAsync(JsonObject message, CancellationToken ct)
    {
        await writes.WaitAsync(ct).ConfigureAwait(false);
        try { await transport.SendAsync(ToElement(message), ct).ConfigureAwait(false); }
        finally { writes.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var message = await transport.ReceiveAsync(stop).ConfigureAwait(false);
                if (message is null) throw new IOException("MCP connection closed.");
                var value = message.Value;
                if (value.ValueKind != JsonValueKind.Object || CodexRealtimeProtocol.Text(value, "jsonrpc") != "2.0")
                    throw new CodexProtocolException("Invalid MCP JSON-RPC envelope.");
                if (value.TryGetProperty("method", out var method))
                {
                    if (method.ValueKind != JsonValueKind.String)
                        throw new CodexProtocolException("Invalid MCP JSON-RPC method.");
                    if (value.TryGetProperty("id", out var serverId))
                    {
                        // No elicitation capability is advertised in the legacy client.
                        // Ping is protocol housekeeping, never a tool or permission grant.
                        if (serverId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                            throw new CodexProtocolException("Invalid MCP server request ID.");
                        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(serverId.GetRawText()) };
                        if (method.GetString() == "ping") reply["result"] = new JsonObject();
                        else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported" };
                        await SendAsync(reply, stop).ConfigureAwait(false);
                    }
                    else if (method.GetString() == "notifications/tools/list_changed")
                        lock (catalogGate) Interlocked.Increment(ref toolRevision);
                    continue;
                }
                if (!value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number))
                    throw new CodexProtocolException("Invalid MCP response ID.");
                if (!pending.TryRemove(number, out var target)) continue;
                var hasResult = value.TryGetProperty("result", out var result);
                var hasError = value.TryGetProperty("error", out var errorValue);
                if (hasResult == hasError)
                    target.TrySetException(new CodexProtocolException("MCP response must contain exactly one result or error."));
                else if (hasResult) target.TrySetResult(result.Clone());
                else if (errorValue.ValueKind == JsonValueKind.Object &&
                    errorValue.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var errorCode) &&
                    CodexRealtimeProtocol.Text(errorValue, "message") is { } errorMessage)
                    target.TrySetException(new CodexMcpRpcException(errorCode, errorMessage,
                        errorValue.TryGetProperty("data", out var data) ? data.Clone() : null));
                else target.TrySetException(new CodexProtocolException("Invalid MCP response error."));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Fault(error); }
    }

    private void Fault(Exception error)
    {
        while (true)
        {
            var previous = State;
            if (previous is CodexMcpState.Closed or CodexMcpState.Faulted) return;
            if (Interlocked.CompareExchange(ref state, (int)CodexMcpState.Faulted, (int)previous) == (int)previous) break;
        }
        foreach (var item in pending) item.Value.TrySetException(error);
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) when (State == CodexMcpState.Closed) { }
    }

    private static JsonElement ToElement(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>Closes the connection and settles pending local requests without replay.</summary>
    public ValueTask DisposeAsync()
    {
        lock (closeGate) return new ValueTask(closeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        Interlocked.Exchange(ref state, (int)CodexMcpState.Closed);
        foreach (var item in pending) item.Value.TrySetException(new ObjectDisposedException(nameof(CodexMcpClient)));
        lifetime.Cancel();
        try { await transport.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (receiveTask is not null) await receiveTask.ConfigureAwait(false);
            lifetime.Dispose();
        }
    }
}
