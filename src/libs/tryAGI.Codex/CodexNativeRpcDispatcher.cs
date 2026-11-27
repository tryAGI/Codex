using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>An explicitly registered native route. The host supplies an authenticated adapter;
/// registering a route does not grant permission to execute its effects.</summary>
public sealed record CodexNativeRpcRoute(string Method,
    Func<JsonElement?, CancellationToken, Task<JsonElement>> InvokeAsync);

/// <summary>Connection-scoped native App Server request dispatcher. Composes the existing thread, model,
/// account, login and configuration facades without creating a second reasoning engine or falling back
/// to a subprocess or remote API. The transport remains responsible for connection authentication,
/// initialization, request admission and notification delivery.</summary>
public sealed class CodexNativeRpcDispatcher
{
    private readonly IReadOnlyDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>> routes;
    private readonly CodexDeviceLoginRpc? login;

    /// <summary>Creates a dispatcher from explicitly configured native capabilities. Additional routes
    /// must be exact client request names in the embedded protocol catalog and must not overlap.</summary>
    public CodexNativeRpcDispatcher(CodexThreadRpc? threads = null, CodexModelCatalogRpc? models = null,
        CodexAccountRpc? accounts = null, CodexDeviceLoginRpc? login = null, CodexAccountSessionsRpc? sessions = null,
        CodexConfigurationReadRpc? configRead = null, CodexConfigurationWriteRpc? configWrite = null,
        CodexRuntimeFeatureRpc? features = null, IEnumerable<CodexNativeRpcRoute>? additionalRoutes = null)
    {
        this.login = login;
        var registered = new Dictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>>(StringComparer.Ordinal);
        void Register(string method, Func<JsonElement?, CancellationToken, Task<JsonElement>> invoke)
        {
            ArgumentNullException.ThrowIfNull(invoke);
            if (!CodexAppServerProtocol.IsClientRequest(method) || !registered.TryAdd(method, invoke))
                throw new ArgumentException("Unknown or duplicate native RPC route.", nameof(additionalRoutes));
        }
        void RegisterFacade(Func<string, bool> handles, Func<string, JsonElement?, CancellationToken, Task<JsonElement>> invoke)
        {
            foreach (var method in CodexAppServerProtocol.ClientRequests.Where(handles))
                Register(method, (parameters, ct) => invoke(method, parameters, ct));
        }
        if (threads is not null) RegisterFacade(CodexThreadRpc.Handles, threads.InvokeAsync);
        if (models is not null) RegisterFacade(CodexModelCatalogRpc.Handles, models.InvokeAsync);
        if (accounts is not null) RegisterFacade(CodexAccountRpc.Handles, accounts.InvokeAsync);
        if (login is not null) RegisterFacade(CodexDeviceLoginRpc.Handles, login.InvokeAsync);
        if (sessions is not null) RegisterFacade(CodexAccountSessionsRpc.Handles, sessions.InvokeAsync);
        if (configRead is not null) RegisterFacade(configRead.Handles, configRead.InvokeAsync);
        if (configWrite is not null) RegisterFacade(CodexConfigurationWriteRpc.Handles, configWrite.InvokeAsync);
        if (features is not null) RegisterFacade(CodexRuntimeFeatureRpc.Handles, features.InvokeAsync);
        if (additionalRoutes is not null)
            foreach (var route in additionalRoutes) Register(route.Method, route.InvokeAsync);
        routes = new ReadOnlyDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>>(registered);
        Methods = Array.AsReadOnly(registered.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Registered request methods. The listed methods remain subject to the underlying host's
    /// capability, policy and per-request authorization checks.</summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>Returns whether the method is routed by this configured native dispatcher.</summary>
    public bool Handles(string method) => routes.ContainsKey(method);

    /// <summary>Invokes a native request with its original JSON parameters and typed exceptions.
    /// Unknown or unconfigured methods are rejected without any fallback.</summary>
    public Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(method) || !routes.TryGetValue(method, out var invoke))
            throw new CodexThreadRpcException(-32601, "Native RPC method is not registered.");
        return invoke(parameters, cancellationToken);
    }

    /// <summary>Dispatches one App Server request envelope, preserving a string or signed integer request ID.
    /// Accepts the App Server's versionless framing and standard JSON-RPC 2.0 framing. Notifications,
    /// responses and batches are intentionally not accepted as requests. Cancellation propagates to the host.</summary>
    public async Task<JsonElement> DispatchAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonElement? id = null;
        var versioned = false;
        var envelopeValid = false;
        try
        {
            if (request.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(request.GetRawText()) > 1_114_112)
                throw InvalidRequest();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? method = null;
            JsonElement? parameters = null;
            foreach (var field in request.EnumerateObject())
            {
                if (!seen.Add(field.Name)) throw InvalidRequest();
                switch (field.Name)
                {
                    case "jsonrpc":
                        if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() != "2.0")
                            throw InvalidRequest();
                        versioned = true;
                        break;
                    case "id":
                        if (!ValidId(field.Value)) throw InvalidRequest();
                        id = field.Value.Clone();
                        break;
                    case "method":
                        if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() is not { Length: > 0 and <= 256 } name ||
                            name.Any(char.IsControl)) throw InvalidRequest();
                        method = name;
                        break;
                    case "params":
                        parameters = field.Value.Clone();
                        break;
                    default:
                        throw InvalidRequest();
                }
            }
            if (id is null || method is null) throw InvalidRequest();
            envelopeValid = true;
            var result = await InvokeAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Undefined)
                return Response(id, versioned, null, -32603, "Native RPC returned no result.");
            return Response(id, versioned, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CodexConfigurationWriteException error)
        {
            var wire = error.Failure.ToString();
            var code = char.ToLowerInvariant(wire[0]) + wire[1..];
            return Response(id, versioned, null, -32600, "Configuration write rejected.",
                new JsonObject { ["config_write_error_code"] = code });
        }
        catch (CodexThreadRpcException error)
        {
            // Facades carry typed RPC errors. Do not copy arbitrary host exception messages
            // (which may contain request data or credentials) into the public error envelope.
            return Response(envelopeValid ? id : null, versioned, null, error.Code, SafeMessage(error.Code));
        }
        catch (CodexOAuthRefreshException error)
        {
            return Response(id, versioned, null, -32000, "OAuth session operation rejected.",
                new JsonObject { ["reason"] = error.Failure.ToString() });
        }
        catch (CodexProviderRouteException error)
        {
            return Response(id, versioned, null, -32000, "Provider route rejected.",
                new JsonObject { ["reason"] = error.Failure.ToString() });
        }
        catch (CodexAuthorizationUnavailableException)
        { return Response(id, versioned, null, -32000, "Account authorization is unavailable."); }
        catch (CodexProtocolException)
        { return Response(id, versioned, null, -32000, "Native provider operation failed."); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Response(id, versioned, null, -32603, "Native RPC failed."); }
    }

    /// <summary>Drives an admitted device-code login only after the transport sent its start response.
    /// The caller must retain this task and cancel the connection lifetime on disconnect.</summary>
    public Task<bool> RunPendingLoginAsync(Guid loginId, CancellationToken cancellationToken = default)
        => login is null ? throw new CodexThreadRpcException(-32601, "Device login is not registered.")
            : login.RunPendingAsync(loginId, cancellationToken);

    /// <summary>Cancels a pending connection-bound device login on transport shutdown, if registered.</summary>
    public Task CancelPendingLoginAsync(CancellationToken cancellationToken = default)
        => login?.CancelPendingAsync(cancellationToken) ?? Task.CompletedTask;

    private static bool ValidId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() is { Length: > 0 and <= 512 } value && !value.Any(char.IsControl),
        JsonValueKind.Number => id.TryGetInt64(out _),
        _ => false
    };

    private static CodexThreadRpcException InvalidRequest() => new(-32600, "Invalid native RPC request.");
    private static string SafeMessage(int code) => code switch
    {
        -32600 => "Invalid native RPC request.",
        -32601 => "Native RPC method is not registered.",
        -32602 => "Invalid native RPC parameters.",
        -32010 => "Native RPC host is draining or at capacity.",
        _ => "Native RPC request rejected."
    };

    private static JsonElement Response(JsonElement? id, bool versioned, JsonElement? result, int? code = null,
        string? message = null, JsonObject? data = null)
    {
        var envelope = new JsonObject();
        if (versioned) envelope["jsonrpc"] = "2.0";
        envelope["id"] = id is { } value ? JsonNode.Parse(value.GetRawText()) : null;
        if (code is { } errorCode)
        {
            var error = new JsonObject { ["code"] = errorCode, ["message"] = message };
            if (data is not null) error["data"] = data;
            envelope["error"] = error;
        }
        else envelope["result"] = result is { } payload ? JsonNode.Parse(payload.GetRawText()) : null;
        using var document = JsonDocument.Parse(envelope.ToJsonString());
        return document.RootElement.Clone();
    }
}
