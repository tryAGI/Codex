using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace tryAGI.Codex;

/// <summary>
/// JSON-RPC client for an installed Codex App Server (<c>codex app-server</c>), over stdio, the Unix-socket proxy or WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// This is the optional bridge for hosts that already run Codex. <see cref="CodexThreadRpc"/> serves the same
/// thread/turn method surface natively, and <see cref="CodexAgent"/> runs turns without any Codex process.
/// </para>
/// <para>
/// Envelopes are passed through as raw JSON so newer App Server fields and notifications stay forward-compatible.
/// Use the protocol schema in <c>specs/codex-app-server</c> of this repository, or
/// <c>codex app-server generate-json-schema</c>, for the exact shapes of your installed version.
/// </para>
/// </remarks>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly CodexAppServerOptions options;
    private readonly ICodexAppServerTransport transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private readonly Channel<CodexAppServerNotification> notifications =
        Channel.CreateUnbounded<CodexAppServerNotification>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource lifetime = new();
    private Task readLoop = Task.CompletedTask;
    private long sequence;
    private int disposed;

    private CodexAppServerClient(CodexAppServerOptions options, ICodexAppServerTransport transport)
    {
        this.options = options;
        this.transport = transport;
    }

    /// <summary>The <c>result</c> of the <c>initialize</c> handshake (user agent, platform and server capabilities).</summary>
    public JsonElement InitializeResult { get; private set; }

    /// <summary>Completes when the transport ends; faulted when it ended unexpectedly.</summary>
    public Task Completion => readLoop;

    /// <summary>
    /// Server notifications in arrival order. The channel completes when the transport ends.
    /// Read it promptly: it is unbounded, so an unread stream holds every notification in memory.
    /// </summary>
    public ChannelReader<CodexAppServerNotification> Notifications => notifications.Reader;

    /// <summary>Starts or connects the configured transport and performs the <c>initialize</c>/<c>initialized</c> handshake.</summary>
    public static async Task<CodexAppServerClient> ConnectAsync(CodexAppServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ICodexAppServerTransport transport = options.ResolveKind() switch
        {
            CodexAppServerTransportKind.WebSocket => await WebSocketAppServerTransport.ConnectAsync(options, cancellationToken).ConfigureAwait(false),
            _ => ProcessAppServerTransport.Start(options),
        };
        return await ConnectAsync(options, transport, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CodexAppServerClient> ConnectAsync(
        CodexAppServerOptions options, ICodexAppServerTransport transport, CancellationToken cancellationToken)
    {
        var client = new CodexAppServerClient(options, transport);
        try
        {
            client.readLoop = client.ReadLoopAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.InitializeTimeout);
            try
            {
                client.InitializeResult = await client.SendRequestAsync("initialize", client.BuildInitializeParams(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Codex App Server initialization timed out after {options.InitializeTimeout.TotalSeconds:0.###} seconds.", ex);
            }

            await transport.SendAsync("""{"method":"initialized"}""", cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a request and returns its <c>result</c>. Overloaded (<c>-32001</c>) rejections are retried with backoff.</summary>
    /// <exception cref="CodexAppServerException">The server returned an error, or the transport ended first.</exception>
    // The priority keeps a literal null or default resolving here rather than being ambiguous with the JsonNode overload.
    [OverloadResolutionPriority(1)]
    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (CodexAppServerException ex) when (ex.Code == CodexAppServerException.OverloadedCode && attempt < options.MaxOverloadRetries)
            {
                // The server rejected the request before running it, so resending cannot duplicate an effect.
                var delay = TimeSpan.FromMilliseconds((250 << attempt) + RandomNumberGenerator.GetInt32(0, 250));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends a request built as a <see cref="JsonNode"/>, for example a <see cref="JsonObject"/> holding
    /// <see cref="CodexUserInput.ToJsonArray"/>, and returns its <c>result</c>.
    /// Overloaded (<c>-32001</c>) rejections are retried with backoff.
    /// </summary>
    /// <param name="method">JSON-RPC method, such as <c>turn/start</c>.</param>
    /// <param name="parameters">The <c>params</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="cancellationToken">Cancels waiting for the response.</param>
    /// <returns>The <c>result</c> member of the response.</returns>
    /// <exception cref="CodexAppServerException">The server returned an error, or the transport ended first.</exception>
    public Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default) =>
        RequestAsync(method, ToElement(parameters), cancellationToken);

    /// <summary>
    /// Sends a typed request from <see cref="CodexAppServerMethods"/> and reads its typed <c>result</c>.
    /// Members the models do not declare are kept in <see cref="CodexAppServerPayload.AdditionalProperties"/>.
    /// </summary>
    /// <typeparam name="TParams">Parameter type.</typeparam>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="method">The typed method, for example <see cref="CodexAppServerMethods.TurnStart"/>.</param>
    /// <param name="parameters">The parameters.</param>
    /// <param name="cancellationToken">Cancels waiting for the response.</param>
    /// <returns>The typed <c>result</c>.</returns>
    /// <exception cref="CodexAppServerException">The server returned an error, a result that does not match
    /// the model, or the transport ended first.</exception>
    public async Task<TResult> RequestAsync<TParams, TResult>(CodexAppServerMethod<TParams, TResult> method, TParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        var result = await RequestAsync(method.Name, JsonSerializer.SerializeToElement(parameters, method.ParamsType), cancellationToken).ConfigureAwait(false);
        try
        {
            return result.Deserialize(method.ResultType)
                ?? throw new CodexAppServerException(CodexAppServerException.ServerErrorCode, $"{method.Name} returned a null result.");
        }
        catch (JsonException ex)
        {
            throw new CodexAppServerException($"{method.Name} returned a result that does not match its model.", ex);
        }
    }

    /// <summary>Sends a notification (a message without an id).</summary>
    [OverloadResolutionPriority(1)]
    public Task NotifyAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return transport.SendAsync(Serialize(null, method, parameters), cancellationToken);
    }

    /// <summary>Sends a notification (a message without an id) whose parameters are a <see cref="JsonNode"/>.</summary>
    /// <param name="method">JSON-RPC method.</param>
    /// <param name="parameters">The <c>params</c> value, or <see langword="null"/> to omit it.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the message is written.</returns>
    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default) =>
        NotifyAsync(method, ToElement(parameters), cancellationToken);

    private static JsonElement? ToElement(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>Reads notifications until the transport ends or <paramref name="cancellationToken"/> fires.</summary>
    public IAsyncEnumerable<CodexAppServerNotification> ReadNotificationsAsync(CancellationToken cancellationToken = default) =>
        notifications.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
        try { await readLoop.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or CodexAppServerException) { }
        FailPending(new CodexAppServerException("Codex App Server client was disposed."));
        notifications.Writer.TryComplete();
        lifetime.Dispose();
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var id = "c" + Interlocked.Increment(ref sequence).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            if (readLoop.IsCompleted) throw new CodexAppServerException("Codex App Server transport has ended.");
            await transport.SendAsync(Serialize(id, method, parameters), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var line in transport.ReadMessagesAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    root = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // One unreadable line must not end the session; later notifications are still valid.
                    continue;
                }

                if (root.ValueKind == JsonValueKind.Object) Dispatch(root);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or WebSocketException or InvalidOperationException)
        {
            failure = ex;
        }

        var ended = new CodexAppServerException(failure is null
            ? "Codex App Server transport ended."
            : "Codex App Server transport failed: " + failure.Message);
        FailPending(ended);
        notifications.Writer.TryComplete(failure is null ? null : ended);
    }

    private void Dispatch(JsonElement root)
    {
        var hasId = root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        JsonElement? parameters = root.TryGetProperty("params", out var p) ? p : null;

        if (hasId && method is null)
        {
            var key = id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : id.GetRawText();
            if (!pending.TryGetValue(key, out var completion)) return;
            if (root.TryGetProperty("error", out var error)) completion.TrySetException(ToException(error));
            else completion.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
            return;
        }

        if (method is null) return;
        if (hasId) _ = AnswerServerRequestAsync(new CodexAppServerRequest(id, method, parameters));
        else notifications.Writer.TryWrite(new CodexAppServerNotification(method, parameters));
    }

    private async Task AnswerServerRequestAsync(CodexAppServerRequest request)
    {
        string response;
        try
        {
            var handler = options.ServerRequestHandler
                ?? throw new CodexAppServerException(CodexAppServerException.MethodNotFoundCode, "No handler for server request " + request.Method + ".");
            var result = await handler(request, lifetime.Token).ConfigureAwait(false);
            response = SerializeResponse(request.Id, result, null);
        }
        catch (CodexAppServerException ex)
        {
            response = SerializeResponse(request.Id, null, ex);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) // A failing host handler must still answer, or the server waits forever.
        {
            response = SerializeResponse(request.Id, null, new CodexAppServerException(CodexAppServerException.ServerErrorCode, ex.Message));
        }

        try { await transport.SendAsync(response, lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or InvalidOperationException) { }
    }

    private void FailPending(Exception exception)
    {
        foreach (var entry in pending)
            entry.Value.TrySetException(exception);
    }

    private JsonElement BuildInitializeParams()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("clientInfo");
            writer.WriteString("name", options.ClientName);
            writer.WriteString("title", options.ClientTitle);
            writer.WriteString("version", options.ClientVersion ?? DefaultClientVersion());
            writer.WriteEndObject();
            writer.WriteStartObject("capabilities");
            writer.WriteBoolean("experimentalApi", options.ExperimentalApi);
            foreach (var capability in options.Capabilities)
                if (capability.Key != "experimentalApi") writer.WriteBoolean(capability.Key, capability.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string DefaultClientVersion() =>
        typeof(CodexAppServerClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static CodexAppServerException ToException(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return new CodexAppServerException(error.GetRawText());
        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : CodexAppServerException.ServerErrorCode;
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : error.GetRawText();
        JsonElement? data = error.TryGetProperty("data", out var d) ? d.Clone() : null;
        return new CodexAppServerException(code, message, data);
    }

    internal static string Serialize(string? id, string method, JsonElement? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (id is not null) writer.WriteString("id", id);
            writer.WriteString("method", method);
            if (parameters is { } value)
            {
                writer.WritePropertyName("params");
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string SerializeResponse(JsonElement id, JsonElement? result, CodexAppServerException? error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            if (error is not null)
            {
                writer.WriteStartObject("error");
                writer.WriteNumber("code", error.Code);
                writer.WriteString("message", error.Message);
                if (error.ErrorData is { } data)
                {
                    writer.WritePropertyName("data");
                    data.WriteTo(writer);
                }

                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("result");
                if (result is { ValueKind: not JsonValueKind.Undefined } value) value.WriteTo(writer);
                else writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal interface ICodexAppServerTransport : IAsyncDisposable
{
    Task SendAsync(string message, CancellationToken cancellationToken);

    IAsyncEnumerable<string> ReadMessagesAsync(CancellationToken cancellationToken);
}

internal sealed class ProcessAppServerTransport : ICodexAppServerTransport
{
    private readonly Process process;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Task stderrLoop;

    private ProcessAppServerTransport(Process process, Action<string>? stderr)
    {
        this.process = process;
        stderrLoop = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    if (!string.IsNullOrWhiteSpace(line)) stderr?.Invoke(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }

    public static ProcessAppServerTransport Start(CodexAppServerOptions options)
    {
        var startInfo = BuildStartInfo(options, OperatingSystem.IsWindows());
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start codex app-server.");
        return new ProcessAppServerTransport(process, options.StandardErrorLine);
    }

    internal static IReadOnlyList<string> BuildArguments(CodexAppServerOptions options)
    {
        var arguments = new List<string> { "app-server" };
        if (options.ResolveKind() == CodexAppServerTransportKind.UnixProxy) arguments.Add("proxy");
        foreach (var feature in options.DisabledFeatures)
        {
            arguments.Add("--disable");
            arguments.Add(feature);
        }

        if (options.ResolveKind() == CodexAppServerTransportKind.UnixProxy)
        {
            if (options.UnixSocketPath is { } socket)
            {
                arguments.Add("--sock");
                arguments.Add(socket);
            }
        }
        else
        {
            arguments.Add("--listen");
            arguments.Add("stdio://");
        }

        return arguments;
    }

    internal static ProcessStartInfo BuildStartInfo(CodexAppServerOptions options, bool isWindows)
    {
        var executable = ResolveExecutable(options.ExecutablePath, isWindows);
        var arguments = BuildArguments(options);
        var extension = Path.GetExtension(executable);
        // npm installs codex as a .cmd shim on Windows, which CreateProcess cannot start directly.
        var viaCmd = isWindows && (extension.Length == 0 ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));
        var startInfo = new ProcessStartInfo
        {
            FileName = viaCmd ? "cmd.exe" : executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        if (viaCmd)
        {
            startInfo.Arguments = "/d /c " + string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteWindows));
        }
        else
        {
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(options.CodexHome))
            startInfo.Environment["CODEX_HOME"] = Environment.ExpandEnvironmentVariables(options.CodexHome.Trim());
        return startInfo;
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process.HasExited) throw new IOException("codex app-server has exited.");
            await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            yield return line;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }

        try { await stderrLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        process.Dispose();
        writeLock.Dispose();
    }

    private static string ResolveExecutable(string path, bool isWindows)
    {
        if (Path.IsPathRooted(path) || path.Contains('/', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
            return path;
        var extensions = isWindows ? new[] { string.Empty, ".exe", ".cmd", ".bat" } : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(directory, path + extension);
            if (File.Exists(candidate)) return candidate;
        }

        return path;
    }

    private static string QuoteWindows(string value) =>
        value.Length == 0 ? "\"\""
        : value.Any(static ch => char.IsWhiteSpace(ch) || ch == '"') ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
        : value;
}

internal sealed class WebSocketAppServerTransport(ClientWebSocket socket) : ICodexAppServerTransport
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public static async Task<WebSocketAppServerTransport> ConnectAsync(CodexAppServerOptions options, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            if (!string.IsNullOrWhiteSpace(options.BearerToken))
                socket.Options.SetRequestHeader("Authorization", "Bearer " + options.BearerToken);
            await socket.ConnectAsync(new Uri(options.Endpoint.Trim()), cancellationToken).ConfigureAwait(false);
            return new WebSocketAppServerTransport(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }

        socket.Dispose();
        writeLock.Dispose();
    }
}
