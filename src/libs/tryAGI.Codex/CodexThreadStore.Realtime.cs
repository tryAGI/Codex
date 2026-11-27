using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Logical realtime transport ownership, persisted independently of a socket attachment.</summary>
public enum CodexRealtimeTransportKind
{
    /// <summary>The media call already exists and has independent ownership.</summary>
    ExistingCall,
    /// <summary>The direct WebSocket owns the realtime provider session.</summary>
    DirectWebSocket,
}

/// <summary>Versioned lifecycle of a logical realtime session. Attachment identity fences one
/// transport; the logical owner and call survive transport loss. No state authorizes remote replay.</summary>
/// <param name="Version">Journal schema version.</param>
/// <param name="SessionId">Stable provider or host logical session identity.</param>
/// <param name="CallId">Existing media call identity; null for a direct WebSocket session.</param>
/// <param name="Protocol">Realtime wire version.</param>
/// <param name="OwnerId">Logical owner, independent of the attachment.</param>
/// <param name="State">Detached, attached, recoveryRequired, ending or ended (camel case).</param>
/// <param name="AttachmentId">Unique transport ownership token, present only while attached.</param>
/// <param name="EndOperationId">Durable end intent, retained through recovery and completion.</param>
public sealed record CodexStoredRealtimeSession(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CallId,
    [property: JsonRequired] string Protocol,
    [property: JsonRequired] string OwnerId,
    [property: JsonRequired] string State,
    Guid? AttachmentId = null,
    Guid? EndOperationId = null)
{
    /// <summary>All previously admitted attachment IDs; prevents reusing a retired transport token.</summary>
    public Guid[]? UsedAttachmentIds { get; init; }

    /// <summary>Existing-call sideband or transport-owned direct realtime session.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CodexRealtimeTransportKind TransportKind { get; init; }

    /// <summary>Durable speech frames, observation identities and backing reasoning carryover.</summary>
    public CodexStoredRealtimeHistory? History { get; init; }

    /// <summary>Durable start-or-steer receipts. A receipt never grants permission to replay provider dispatch.</summary>
    public CodexStoredRealtimeHandoffAdmission[]? HandoffAdmissions { get; init; }

    /// <summary>Immutable backing model mode instructions for this logical call.</summary>
    public CodexRealtimeModeInstructions? ModeInstructions { get; init; }

    /// <summary>Commit revision of the logical timeline's opening boundary.</summary>
    public long? StartedRevision { get; init; }

    /// <summary>Commit revision of the confirmed logical timeline's closing boundary.</summary>
    public long? ClosedRevision { get; init; }

    /// <summary>Backing reasoning turns associated with this voice session at a durable boundary.</summary>
    public Guid[]? PresentationTurnIds { get; init; }
}

public sealed partial class CodexThreadStore
{
    /// <summary>Registers an existing logical call without claiming its transport. A thread can
    /// have at most one open logical realtime session. No network operation is performed.</summary>
    public Task<CodexThreadDocument> CreateRealtimeSessionAsync(Guid threadId, long expectedRevision,
        string sessionId, string callId, string protocol, string ownerId, CancellationToken cancellationToken = default)
        => CreateRealtimeSessionCoreAsync(threadId, expectedRevision, sessionId, callId,
            protocol, ownerId, CodexRealtimeTransportKind.ExistingCall, cancellationToken);

    /// <summary>Register a new logical direct session without creating a provider call or socket.
    /// A subsequent attachment admission permits at most one connection creation attempt.</summary>
    public Task<CodexThreadDocument> CreateDirectRealtimeSessionAsync(Guid threadId, long expectedRevision,
        string sessionId, string protocol, string ownerId, CancellationToken cancellationToken = default)
        => CreateRealtimeSessionCoreAsync(threadId, expectedRevision, sessionId, null,
            protocol, ownerId, CodexRealtimeTransportKind.DirectWebSocket, cancellationToken);

    private Task<CodexThreadDocument> CreateRealtimeSessionCoreAsync(Guid threadId, long expectedRevision,
        string sessionId, string? callId, string protocol, string ownerId, CodexRealtimeTransportKind kind, CancellationToken ct)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            Token(sessionId); Token(ownerId);
            if (kind == CodexRealtimeTransportKind.ExistingCall) Token(callId ?? throw new ArgumentNullException(nameof(callId)));
            if (protocol is not ("v1" or "v2" or "v3")) throw new ArgumentException("Unsupported realtime protocol.", nameof(protocol));
            if (document.Settings.TryGetProperty("ephemeral", out var ephemeral) &&
                ephemeral.ValueKind == System.Text.Json.JsonValueKind.True)
                throw new InvalidOperationException("Realtime lifecycle requires durable thread storage.");
            var sessions = document.RealtimeSessions ?? [];
            if (sessions.Any(s => s.SessionId == sessionId || callId is not null && s.CallId == callId || s.State != "ended"))
                throw new InvalidOperationException("Realtime session identity is already registered or another session is open.");
            return document with { RealtimeSessions = [.. sessions,
                new CodexStoredRealtimeSession(1, sessionId, callId, protocol, ownerId, "detached")
                { TransportKind = kind, StartedRevision = checked(document.Revision + 1) }] };
        }, ct);

    /// <summary>Atomically claims a detached call for one transport. A new attachment ID must
    /// be allocated before attempting the write; an ambiguous write never authorizes replay.</summary>
    public Task<CodexThreadDocument> AttachRealtimeSessionAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            var session = FindRealtimeSession(document, sessionId, ownerId);
            Id(attachmentId);
            var used = session.UsedAttachmentIds ?? [];
            if (session.State != "detached" || used.Contains(attachmentId) || used.Length >= MaximumItems ||
                session.TransportKind == CodexRealtimeTransportKind.DirectWebSocket && used.Length != 0)
                throw new InvalidOperationException("Realtime session is not available for this attachment.");
            // Older detached snapshots may contain unadmitted captures from the previous transport.
            document = RetireLostRealtimeHandoffs(document, session);
            session = FindRealtimeSession(document, sessionId, ownerId);
            document = ReplaceRealtimeSession(document, session with
            {
                State = "attached", AttachmentId = attachmentId, UsedAttachmentIds = [.. used, attachmentId],
            });
            return document.Turns.LastOrDefault() is { Status: "inProgress" } turn
                ? BindRealtimePresentation(document, turn.Id, sessionId) : document;
        }, cancellationToken);

    /// <summary>Detaches the exact transport after all accepted events have been drained and
    /// persisted. Unadmitted captures from that transport are retired without discarding their
    /// transcript; admitted handoffs remain bound to their reasoning turns. Does not end the media call.</summary>
    public Task<CodexThreadDocument> DetachRealtimeSessionAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            var session = FindRealtimeSession(document, sessionId, ownerId);
            RequireRealtimeAttachment(session, attachmentId);
            if (session.TransportKind == CodexRealtimeTransportKind.DirectWebSocket)
                throw new InvalidOperationException("A lost direct socket requires authoritative end reconciliation.");
            document = RetireLostRealtimeHandoffs(document, session);
            session = FindRealtimeSession(document, sessionId, ownerId);
            return ReplaceRealtimeSession(document, session with { State = "detached", AttachmentId = null });
        }, cancellationToken);

    /// <summary>Fences an attachment after the host proves transport ownership was lost, or
    /// marks an uncertain end/history write. This never reconnects or dispatches a remote command.</summary>
    public Task<CodexThreadDocument> RequireRealtimeHistoryRecoveryAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid? expectedAttachmentId, CancellationToken cancellationToken = default)
        => ChangeRealtimeAsync(threadId, expectedRevision, sessionId, ownerId, session =>
        {
            if (session.State is not ("attached" or "detached" or "ending") ||
                session.AttachmentId != expectedAttachmentId)
                throw new InvalidOperationException("Realtime recovery identity or state does not match.");
            return session with { State = "recoveryRequired", AttachmentId = null };
        }, cancellationToken);

    /// <summary>After the host has verified persisted history and the same logical call's status,
    /// clears the recovery fence. A prior end intent requires authoritative proof of closure;
    /// uncertain end intents cannot be reopened or replayed.</summary>
    public Task<CodexThreadDocument> ConfirmRealtimeHistoryRecoveryAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, bool verifiedEnded, CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            var session = FindRealtimeSession(document, sessionId, ownerId);
            if (session.State != "recoveryRequired" || (!verifiedEnded &&
                (session.EndOperationId is not null || session.TransportKind == CodexRealtimeTransportKind.DirectWebSocket)))
                throw new InvalidOperationException("Realtime history remains uncertain.");
            _ = RecoverRealtimeHistory(session);
            document = RetireLostRealtimeHandoffs(document, session);
            session = FindRealtimeSession(document, sessionId, ownerId);
            if (verifiedEnded)
                document = SealRealtimeHistory(document, session, "recovery-end:" + expectedRevision, "sessionEnd");
            var recovered = FindRealtimeSession(document, sessionId, ownerId);
            return ReplaceRealtimeSession(document, recovered with
            {
                State = verifiedEnded ? "ended" : "detached",
                ClosedRevision = verifiedEnded ? checked(document.Revision + 1) : null,
            });
        }, cancellationToken);

    /// <summary>Records an end intent before any external effect, including after a clean detach.
    /// An existing or uncertain end intent never admits a second end attempt.</summary>
    public Task<CodexThreadAdmission> BeginRealtimeEndAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid? expectedAttachmentId, Guid endOperationId, CancellationToken cancellationToken = default)
        => BeginRealtimeEndCoreAsync(threadId, expectedRevision, sessionId, ownerId, expectedAttachmentId,
            endOperationId, false, cancellationToken);

    // Direct sockets must send close while the sole reader is still alive. Persist the intent
    // without sealing the tail yet, so final transcript events can still be durably accepted.
    internal async Task<CodexThreadAdmission> BeginDirectRealtimeCloseAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid attachmentId, Guid operationId, CancellationToken ct)
    {
        Id(operationId);
        var before = await CurrentAsync(threadId, expectedRevision, ct).ConfigureAwait(false);
        var session = FindRealtimeSession(before, sessionId, ownerId);
        RequireRealtimeAttachment(session, attachmentId);
        if (session.TransportKind != CodexRealtimeTransportKind.DirectWebSocket)
            throw new InvalidOperationException("Only a direct session owns its close command.");
        if (session.EndOperationId is { } prior)
        {
            if (prior != operationId) throw new InvalidOperationException("Direct close is owned by another operation.");
            return new(false, before);
        }
        var next = ReplaceRealtimeSession(before, session with { EndOperationId = operationId });
        return new(true, await PublishAsync(before.Revision, next with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, ct).ConfigureAwait(false));
    }

    // Only the lifecycle controller may retire an attached generation after its sole reader
    // has drained. The general storage API continues to require handoff settlement.
    internal Task<CodexThreadAdmission> BeginDrainedRealtimeEndAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid? expectedAttachmentId, Guid endOperationId, CancellationToken ct)
        => BeginRealtimeEndCoreAsync(threadId, expectedRevision, sessionId, ownerId, expectedAttachmentId,
            endOperationId, true, ct);

    private async Task<CodexThreadAdmission> BeginRealtimeEndCoreAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid? expectedAttachmentId, Guid endOperationId,
        bool attachmentDrained, CancellationToken cancellationToken)
    {
        Id(endOperationId);
        var before = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var session = FindRealtimeSession(before, sessionId, ownerId);
        if (session.State == "ended") return new(false, before);
        if (session.State == "ending" && session.EndOperationId == endOperationId) return new(false, before);
        if (session.State is not ("attached" or "detached") || session.AttachmentId != expectedAttachmentId ||
            session.EndOperationId is { } prior && prior != endOperationId ||
            session.TransportKind == CodexRealtimeTransportKind.DirectWebSocket && session.State == "attached" &&
                (!attachmentDrained || session.EndOperationId != endOperationId))
            throw new InvalidOperationException("Realtime end is fenced by another attachment, end intent or history recovery.");
        if (session.State == "detached" || attachmentDrained)
        {
            before = RetireLostRealtimeHandoffs(before, session);
            session = FindRealtimeSession(before, sessionId, ownerId);
        }
        var sealedThread = SealRealtimeHistory(before, session, "end:" + endOperationId.ToString("N"), "sessionEnd");
        var sealedSession = FindRealtimeSession(sealedThread, sessionId, ownerId);
        var next = ReplaceRealtimeSession(sealedThread, sealedSession with { State = "ending", AttachmentId = null, EndOperationId = endOperationId });
        return new(true, await PublishAsync(before.Revision, next with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Completes the exact admitted end intent after the host has durably sealed history.
    /// Does not send a provider hangup. Repeated completion of the same end is harmless.</summary>
    public async Task<CodexThreadDocument> CompleteRealtimeEndAsync(Guid threadId, long expectedRevision,
        string sessionId, string ownerId, Guid endOperationId, CancellationToken cancellationToken = default)
    {
        Id(endOperationId);
        var before = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var session = FindRealtimeSession(before, sessionId, ownerId);
        if (session.EndOperationId != endOperationId || session.State is not ("ending" or "ended"))
            throw new InvalidOperationException("Realtime end intent does not match.");
        if (session.State == "ended") return before;
        return await PublishAsync(before.Revision, ReplaceRealtimeSession(before, session with
        { State = "ended", ClosedRevision = checked(before.Revision + 1) }) with
        {
            Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<CodexThreadDocument> ChangeRealtimeAsync(Guid threadId, long revision, string sessionId,
        string ownerId, Func<CodexStoredRealtimeSession, CodexStoredRealtimeSession> update, CancellationToken ct)
        => ChangeAsync(threadId, revision, document =>
            ReplaceRealtimeSession(document, update(FindRealtimeSession(document, sessionId, ownerId))), ct);

    private static CodexStoredRealtimeSession FindRealtimeSession(CodexThreadDocument document, string sessionId, string ownerId)
    {
        Token(sessionId); Token(ownerId);
        return document.RealtimeSessions?.FirstOrDefault(s => s.SessionId == sessionId && s.OwnerId == ownerId)
            ?? throw new InvalidOperationException("Realtime logical session or owner does not match.");
    }

    private static void RequireRealtimeAttachment(CodexStoredRealtimeSession session, Guid attachmentId)
    {
        Id(attachmentId);
        if (session.State != "attached" || session.AttachmentId != attachmentId)
            throw new InvalidOperationException("Realtime attachment ownership does not match.");
    }

    private static CodexThreadDocument ReplaceRealtimeSession(CodexThreadDocument document, CodexStoredRealtimeSession session)
        => document with { RealtimeSessions = (document.RealtimeSessions ?? []).Select(s => s.SessionId == session.SessionId ? session : s).ToArray() };

    private static void ValidateRealtimeSessions(CodexStoredRealtimeSession[]? sessions)
    {
        if (sessions is null) return;
        if (sessions.Length > MaximumTurns) throw new InvalidDataException("Realtime session journal exceeds its bound.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var open = false;
        foreach (var session in sessions)
        {
            if (session is null || open || session.Version != 1 ||
                !ValidToken(session.SessionId) || !ValidToken(session.OwnerId) || !ids.Add(session.SessionId) ||
                !Enum.IsDefined(session.TransportKind) ||
                session.StartedRevision is < 1 || session.ClosedRevision is < 1 ||
                session.ClosedRevision is not null && (session.State != "ended" || session.ClosedRevision < session.StartedRevision) ||
                (session.TransportKind == CodexRealtimeTransportKind.ExistingCall
                    ? session.CallId is not { } callId || !ValidToken(callId) || !calls.Add(callId)
                    : session.CallId is not null || session.UsedAttachmentIds is { Length: > 1 } ||
                        session.State == "detached" && session.UsedAttachmentIds is { Length: > 0 }) ||
                session.Protocol is not ("v1" or "v2" or "v3") ||
                session.State is not ("attached" or "detached" or "recoveryRequired" or "ending" or "ended") ||
                (session.State == "attached") != (session.AttachmentId is not null) || session.AttachmentId == Guid.Empty ||
                (session.UsedAttachmentIds is { } used && (used.Length > MaximumItems || used.Contains(Guid.Empty) || used.Distinct().Count() != used.Length)) ||
                (session.AttachmentId is { } attachment && !(session.UsedAttachmentIds?.Contains(attachment) ?? false)) ||
                session.EndOperationId == Guid.Empty || (session.State == "ending" && session.EndOperationId is null) ||
                (session.State == "detached" || session.State == "attached" &&
                    session.TransportKind == CodexRealtimeTransportKind.ExistingCall) && session.EndOperationId is not null)
                throw new InvalidDataException("Invalid realtime session journal.");
            CodexRealtimeModeContext.Validate(session.ModeInstructions);
            ValidateRealtimeHandoffAdmissions(session);
            open = session.State != "ended";
        }
    }
}
