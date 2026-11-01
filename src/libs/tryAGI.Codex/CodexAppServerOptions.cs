using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>How <see cref="CodexAppServerClient"/> reaches a Codex App Server.</summary>
public enum CodexAppServerTransportKind
{
    /// <summary>Launch <c>codex app-server</c> and speak JSON-RPC over its stdin/stdout.</summary>
    Stdio,

    /// <summary>Launch <c>codex app-server proxy</c>, which bridges stdio to a running server's Unix socket.</summary>
    UnixProxy,

    /// <summary>Connect to an already running App Server over <c>ws://</c> (loopback only) or <c>wss://</c>.</summary>
    WebSocket,
}

/// <summary>Connection settings for <see cref="CodexAppServerClient"/>.</summary>
/// <remarks>
/// This client is for hosts that want to drive an installed Codex App Server. The rest of this package
/// implements the Codex protocols natively and does not need one.
/// </remarks>
public sealed record CodexAppServerOptions
{
    /// <summary>
    /// <c>stdio://</c> (default), <c>unix://</c> or <c>unix:///absolute/path.sock</c>, <c>ws://127.0.0.1:PORT</c>, or <c>wss://host/path</c>.
    /// Plain <c>ws://</c> is accepted only for loopback hosts.
    /// </summary>
    public string Endpoint { get; init; } = "stdio://";

    /// <summary>The <c>codex</c> executable for process transports. A bare name is resolved through <c>PATH</c>.</summary>
    public string ExecutablePath { get; init; } = "codex";

    /// <summary>Working directory of the launched process. Defaults to the current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Optional <c>CODEX_HOME</c> for the launched process, isolating its credential store.</summary>
    public string? CodexHome { get; init; }

    /// <summary>Codex features passed as <c>--disable</c> to the launched process.</summary>
    public IReadOnlyList<string> DisabledFeatures { get; init; } = [];

    /// <summary>Bearer token sent on the WebSocket handshake. Never logged by this client.</summary>
    public string? BearerToken { get; init; }

    /// <summary><c>clientInfo.name</c> sent with <c>initialize</c>.</summary>
    public string ClientName { get; init; } = "tryagi_codex";

    /// <summary><c>clientInfo.title</c> sent with <c>initialize</c>.</summary>
    public string ClientTitle { get; init; } = "tryAGI.Codex";

    /// <summary><c>clientInfo.version</c> sent with <c>initialize</c>. Defaults to this assembly's version.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>Opts in to experimental App Server methods and fields.</summary>
    public bool ExperimentalApi { get; init; } = true;

    /// <summary>Additional <c>initialize</c> capability flags, merged after <see cref="ExperimentalApi"/>.</summary>
    public IReadOnlyDictionary<string, bool> Capabilities { get; init; } = new Dictionary<string, bool>();

    /// <summary>Deadline for the <c>initialize</c> response.</summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries for a request rejected with the server-overloaded error (<c>-32001</c>).</summary>
    public int MaxOverloadRetries { get; init; } = 3;

    /// <summary>
    /// Answers server-initiated requests (approvals, user input, elicitation). Return the JSON-RPC <c>result</c>,
    /// or throw <see cref="CodexAppServerException"/> to return an error. Without a handler every server request
    /// is answered with method-not-found.
    /// </summary>
    public Func<CodexAppServerRequest, CancellationToken, ValueTask<JsonElement>>? ServerRequestHandler { get; init; }

    /// <summary>Receives each non-empty line the launched process writes to stderr.</summary>
    public Action<string>? StandardErrorLine { get; init; }

    internal CodexAppServerTransportKind ResolveKind()
    {
        var endpoint = Endpoint.Trim();
        if (endpoint == "stdio://") return CodexAppServerTransportKind.Stdio;
        if (endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)) return CodexAppServerTransportKind.UnixProxy;
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)) return CodexAppServerTransportKind.WebSocket;
            if (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
                return uri.IsLoopback
                    ? CodexAppServerTransportKind.WebSocket
                    : throw new ArgumentException("Plain ws:// App Server endpoints must be loopback or SSH-forwarded. Use wss:// for remote hosts.", nameof(Endpoint));
        }

        throw new ArgumentException("App Server endpoint must be stdio://, unix://[path], ws://loopback:port, or wss://host.", nameof(Endpoint));
    }

    internal string? UnixSocketPath
    {
        get
        {
            var endpoint = Endpoint.Trim();
            return endpoint.Length > "unix://".Length ? Uri.UnescapeDataString(endpoint["unix://".Length..]) : null;
        }
    }
}

/// <summary>A notification sent by the App Server (for example <c>turn/completed</c> or <c>item/agentMessage/delta</c>).</summary>
/// <param name="Method">The notification method.</param>
/// <param name="Params">The <c>params</c> payload, if any.</param>
public sealed record CodexAppServerNotification(string Method, JsonElement? Params);

/// <summary>A request initiated by the App Server that expects a response from this client.</summary>
/// <param name="Id">The JSON-RPC id, echoed in the response.</param>
/// <param name="Method">The request method, for example <c>item/commandExecution/requestApproval</c>.</param>
/// <param name="Params">The <c>params</c> payload, if any.</param>
public sealed record CodexAppServerRequest(JsonElement Id, string Method, JsonElement? Params);

/// <summary>A JSON-RPC error returned by the App Server, or the transport ending before a response.</summary>
public sealed class CodexAppServerException : Exception
{
    /// <summary>JSON-RPC code used when the server replied with an unstructured error.</summary>
    public const int ServerErrorCode = -32000;

    /// <summary>JSON-RPC code the App Server uses when it is overloaded.</summary>
    public const int OverloadedCode = -32001;

    /// <summary>JSON-RPC method-not-found code.</summary>
    public const int MethodNotFoundCode = -32601;

    /// <summary>Creates an error with the server-error code.</summary>
    public CodexAppServerException() : this(ServerErrorCode, "Codex App Server error") { }

    /// <summary>Creates an error with the server-error code.</summary>
    public CodexAppServerException(string message) : this(ServerErrorCode, message) { }

    /// <summary>Creates an error with the server-error code.</summary>
    public CodexAppServerException(string message, Exception innerException) : base(message, innerException) => Code = ServerErrorCode;

    /// <summary>Creates an error with an explicit JSON-RPC code.</summary>
    public CodexAppServerException(int code, string message, JsonElement? data = null) : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    /// <summary>The JSON-RPC error code.</summary>
    public int Code { get; }

    /// <summary>The JSON-RPC error <c>data</c>, if any.</summary>
    public JsonElement? ErrorData { get; }
}
