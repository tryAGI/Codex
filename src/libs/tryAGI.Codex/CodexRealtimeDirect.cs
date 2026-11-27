using System.Net.WebSockets;

namespace tryAGI.Codex;

public sealed partial class CodexClient
{
    /// <summary>Bind the SDK's direct provider WebSocket to a registered direct logical session.
    /// The OAuth account is checked before connecting; API keys are explicitly owned by the host.
    /// Use the coordinator's normal attach, pump and end operations.</summary>
    public CodexRealtimeSessionController CreateDirectRealtimeSessionController(CodexThreadStore store,
        Guid threadId, string sessionId, string ownerId, CodexRealtimeSessionOptions session,
        CodexRealtimeReconnectOptions? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _ = DirectRealtimeDestination(session, realtimeApiKeyProvider is not null);
        if (session.SessionId is not null && session.SessionId != sessionId)
            throw new ArgumentException("Direct session identity does not match its logical enrollment.", nameof(session));
        return CodexRealtimeSessionController.CreateDirect(store, threadId, sessionId, ownerId, (request, ct) =>
        {
            if (request.Protocol != session.Protocol) throw new InvalidOperationException("Direct protocol changed.");
            return ConnectDirectRealtimeCoreAsync(session with { SessionId = request.SessionId }, request.AccountId, ct);
        }, lifecycle);
    }

    /// <summary>Initialize a direct session on an explicitly supplied, already connected WebSocket.
    /// The host is responsible for authenticating and selecting the destination and account.
    /// Ownership transfers to the SDK; initialization failure disposes the socket. No call ID is invented.</summary>
    public static async Task<CodexRealtimeConnection> InitializeDirectRealtimeWebSocketAsync(WebSocket socket,
        CodexRealtimeSessionOptions session, int maximumMessageBytes = 1_048_576,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(session);
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (maximumMessageBytes is < 1024 or > 16_777_216 || limit <= TimeSpan.Zero ||
            socket.State != WebSocketState.Open)
            throw new ArgumentException("A connected WebSocket and valid message/timeout bounds are required.");
        CodexRealtimeConnection? connection = null;
        try
        {
            CodexRealtimeProtocol.ValidateSessionOptions(session);
            connection = new(socket, maximumMessageBytes, session.Protocol, chatGptCall: false);
            await connection.InitializeDirectSessionAsync(session, limit, legacyOAuthGa: false, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            if (connection is null) socket.Dispose();
            else await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
