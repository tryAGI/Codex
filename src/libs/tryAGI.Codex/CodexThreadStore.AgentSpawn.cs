using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    /// <summary>Whether this host adapter supports atomic named child creation and initial mail.</summary>
    public bool SupportsNamedAgentSpawn => storage is ICodexNamedAgentSpawnStorage;

    /// <summary>Create a fresh named child and accept its initial follow-up in one durable
    /// transaction. No runtime is loaded and no model is called. The caller must retain the
    /// original child and message identities; after an uncertain result inspect the same
    /// identity with <see cref="InspectNamedAgentSpawnAsync"/>.</summary>
    public async Task<CodexNamedAgentIdentity> SpawnNamedAgentAsync(CodexNamedAgentSpawnRequest request,
        CancellationToken cancellationToken = default)
    {
        var adapter = RequireNamedSpawnStorage();
        ValidateNamedSpawnRequest(request);
        var settings = PrepareNamedSpawnSettings(request.EffectiveSettings);
        var directory = await ReadAgentIdentitiesAsync(request.RootThreadId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(directory.Graph.Revision, request.ExpectedGraphRevision, StringComparison.Ordinal))
            throw new CodexThreadConflictException();
        var parent = directory.GetActive(request.ParentThreadId);
        var path = CodexAgentPath.Join(parent.Path, request.TaskName);
        if (path.Count(c => c == '/') - 1 > request.MaximumDepth)
            throw new InvalidOperationException("Authorized named agent spawn depth exceeded.");
        if (directory.Graph.Edges.Count >= MaximumSpawnEdges ||
            directory.Graph.Edges.Any(edge => edge.ChildThreadId == request.ChildThreadId) ||
            directory.RestorableDescendants.Any(identity => identity.Path == path))
            throw new InvalidOperationException("Named agent graph capacity, child identity or active path is already occupied.");

        var parentThread = await CurrentAsync(request.ParentThreadId, request.ExpectedParentRevision,
            cancellationToken).ConfigureAwait(false);
        RequireDurableThread(parentThread);
        RequireUnarchived(parentThread);
        var turn = Active(parentThread, request.ParentTurnId, request.ParentOwnerId);
        var dispatch = turn.Dispatches.SingleOrDefault(item => item.CallId == request.SourceDispatchId);
        if (dispatch is not { Kind: "tool", State: "dispatching" })
            throw new InvalidOperationException("Named agent spawn requires its currently admitted parent tool dispatch.");
        if (directory.ContainsSpawnDispatch(request.ParentThreadId, request.ParentTurnId, request.SourceDispatchId))
            throw new InvalidOperationException("Parent spawn dispatch already owns a child.");

        var origin = new CodexAgentSpawnOrigin(turn.Id, turn.RootTurnId, turn.OwnerId, dispatch.CallId,
            dispatch.RequestSha256, parentThread.Revision, directory.Graph.Revision, request.InitialMessage.Id,
            AgentMailFingerprint(request.InitialMessage), CodexRetainedJournalMetadata.ItemHash(settings))
        {
            InitialModel = CodexRealtimeProtocol.Text(settings, "model"),
            InitialReasoningEffort = CodexRealtimeProtocol.Text(settings, "reasoningEffort"),
        };
        if (!origin.IsValid) throw new InvalidDataException("Invalid native spawn origin.");
        var identity = new CodexNamedAgentIdentity(request.ChildThreadId, parent.ThreadId, request.RootThreadId,
            path, request.Role, request.Nickname) { SpawnOrigin = origin };
        var now = time.GetUtcNow();
        // Fresh child defaults are host-resolved. Never inherit live dispatches, history, goals,
        // realtime attachments or user queues merely because the new thread has a parent.
        var document = new CodexThreadDocument(1, identity.ThreadId, accountId, 1, settings, now, now, []);
        var body = Encode(document, reserveRealtimeClosure: true);
        _ = Decode(body);
        var mailbox = new CodexAgentMailbox(accountId, identity.RootThreadId, identity.ThreadId, 1,
            Array.AsReadOnly(new[] { request.InitialMessage }), Array.Empty<CodexAgentMailReceipt>());
        var plan = new CodexNamedAgentSpawnPlan(accountId, identity, body, mailbox, request.MaximumDepth);

        cancellationToken.ThrowIfCancellationRequested();
        bool committed;
        try { committed = await adapter.CompareCreateNamedAgentAsync(plan, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new IOException("Named agent creation is unconfirmed; inspect the same child identity.", error);
        }
        if (!committed) throw new CodexThreadConflictException();
        return identity;
    }

    /// <summary>Inspect whether the exact original named spawn request committed, without
    /// starting, resuming or replaying a child. A matching historical identity proves initial
    /// acceptance under the adapter contract, even if its mail was consumed or the child closed.
    /// Null means no matching recorded identity in this snapshot, not a guarantee that an
    /// earlier uncertain write cannot still commit.</summary>
    public async Task<CodexNamedAgentIdentity?> InspectNamedAgentSpawnAsync(CodexNamedAgentSpawnRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireNamedSpawnStorage();
        ValidateNamedSpawnRequest(request);
        var settings = PrepareNamedSpawnSettings(request.EffectiveSettings);
        var directory = await ReadAgentIdentitiesAsync(request.RootThreadId, cancellationToken).ConfigureAwait(false);
        CodexNamedAgentIdentity identity;
        try { identity = directory.GetRecorded(request.ChildThreadId); }
        catch (KeyNotFoundException) { return null; }
        var parent = directory.GetRecorded(request.ParentThreadId);
        if (identity.RootThreadId != request.RootThreadId || identity.ParentThreadId != request.ParentThreadId ||
            identity.Path != CodexAgentPath.Join(parent.Path, request.TaskName) ||
            identity.Role != request.Role || identity.Nickname != request.Nickname ||
            identity.SpawnOrigin is not { } origin || !SameSpawnDispatch(identity, request) ||
            origin.ParentOwnerId != request.ParentOwnerId || origin.ParentRevision != request.ExpectedParentRevision ||
            origin.GraphRevision != request.ExpectedGraphRevision || origin.InitialMessageId != request.InitialMessage.Id ||
            origin.InitialMessageFingerprint != AgentMailFingerprint(request.InitialMessage) ||
            origin.InitialSettingsSha256 != CodexRetainedJournalMetadata.ItemHash(settings))
            throw new InvalidOperationException("Recorded child identity belongs to a different spawn request.");
        return identity;
    }

    private ICodexNamedAgentSpawnStorage RequireNamedSpawnStorage()
        => storage as ICodexNamedAgentSpawnStorage
            ?? throw new InvalidOperationException("Atomic named agent creation is unavailable.");

    private static bool SameSpawnDispatch(CodexNamedAgentIdentity identity, CodexNamedAgentSpawnRequest request)
        => identity.ParentThreadId == request.ParentThreadId && identity.SpawnOrigin is { } origin &&
            origin.ParentTurnId == request.ParentTurnId && origin.SourceDispatchId == request.SourceDispatchId;

    private static void ValidateNamedSpawnRequest(CodexNamedAgentSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Id(request.RootThreadId); Id(request.ParentThreadId); Id(request.ParentTurnId); Id(request.ChildThreadId);
        Token(request.ParentOwnerId); Token(request.SourceDispatchId);
        if (request.ChildThreadId == request.ParentThreadId || request.ChildThreadId == request.RootThreadId ||
            request.ExpectedParentRevision < 1 || string.IsNullOrWhiteSpace(request.ExpectedGraphRevision) ||
            request.ExpectedGraphRevision.Length > 1024 || request.MaximumDepth is < 1 or > 128 ||
            request.Role is not null && !ValidToken(request.Role) || request.Nickname is not null && !ValidToken(request.Nickname))
            throw new ArgumentException("Invalid named agent spawn identity or host policy.", nameof(request));
        _ = CodexAgentPath.Join(CodexAgentPath.Root, request.TaskName);
        if (!ValidAgentMail(request.InitialMessage, request.RootThreadId, request.ChildThreadId) ||
            request.InitialMessage.SenderThreadId != request.ParentThreadId ||
            request.InitialMessage.Mode != CodexAgentMailMode.Followup ||
            string.IsNullOrWhiteSpace(request.InitialMessage.Content))
            throw new ArgumentException("Named agent spawn requires nonempty attributed initial follow-up mail.", nameof(request));
    }

    private static JsonElement PrepareNamedSpawnSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object ||
            settings.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw new ArgumentException("Durable named agent creation requires non-ephemeral resolved settings.", nameof(settings));
        // Normalize only lifecycle source, not authority or model configuration.
        var node = JsonNode.Parse(settings.GetRawText())!.AsObject();
        node["source"] = "subagent";
        node["threadSource"] = "subagent";
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
