using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Account-bound, optimistic native thread journal. No model, tool, filesystem effect,
/// cancellation callback or automatic replay is performed by this store. Execution leases and
/// authorization belong to the host; expected revisions fence all persisted transitions.</summary>
public sealed class CodexThreadStore
{
    /// <summary>Largest serialized thread document.</summary>
    public const int MaximumDocumentBytes = 16_777_216;
    private const int MaximumTurns = 4096;
    private const int MaximumItems = 32768;
    private readonly ICodexThreadStorage storage;
    private readonly string accountId;
    private readonly TimeProvider time;
    private readonly object ephemeralGate = new();
    private readonly Dictionary<Guid, JsonElement> ephemeral = [];

    /// <summary>Threads kept only in memory (started with <c>ephemeral</c>).</summary>
    public IReadOnlyList<CodexThreadDocument> EphemeralThreads
    {
        get { lock (ephemeralGate) return ephemeral.Values.Select(Decode).ToArray(); }
    }

    /// <summary>Creates a store for one account.</summary>
    /// <param name="storage">Durable storage.</param>
    /// <param name="accountId">Account that owns every thread in this store.</param>
    /// <param name="timeProvider">Clock for timestamps.</param>
    public CodexThreadStore(ICodexThreadStorage storage, string accountId, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Token(accountId);
        this.storage = storage; this.accountId = accountId; time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The caller retains the proposed ID before awaiting creation, including uncertain writes.</summary>
    public async Task<CodexThreadDocument> CreateAsync(Guid threadId, JsonElement effectiveSettings,
        CancellationToken cancellationToken = default)
    {
        Id(threadId);
        if (effectiveSettings.ValueKind != JsonValueKind.Object) throw new ArgumentException("Resolved thread settings must be an object.", nameof(effectiveSettings));
        var now = time.GetUtcNow();
        var next = new CodexThreadDocument(1, threadId, accountId, 1, effectiveSettings.Clone(), now, now, []);
        return await PublishAsync(null, next, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a thread.</summary>
    /// <exception cref="KeyNotFoundException">The thread does not exist.</exception>
    public async Task<CodexThreadDocument> ReadAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        Id(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (ephemeralGate)
            if (ephemeral.TryGetValue(threadId, out var memory)) return Decode(memory);
        var body = await storage.ReadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Native thread not found.");
        var value = Decode(body);
        if (value.Id != threadId) throw new InvalidDataException("Native thread identity mismatch.");
        return value;
    }

    /// <summary>Records a new turn. Fails when another turn is in progress.</summary>
    public Task<CodexThreadDocument> StartTurnAsync(Guid threadId, long expectedRevision, Guid turnId,
        string ownerId, IReadOnlyList<JsonElement> input, Guid? rootTurnId = null, CancellationToken cancellationToken = default,
        string?[]? clientUserMessageIds = null, string? turnTrigger = null)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            Id(turnId); Token(ownerId);
            if (document.Turns.Any(t => t.Id == turnId) || document.Turns.LastOrDefault()?.Status == "inProgress")
                throw new InvalidOperationException("Thread already has this turn or another active turn.");
            var turn = new CodexStoredTurn(turnId, rootTurnId ?? turnId, ownerId, "inProgress", time.GetUtcNow(),
                Items(input), [], [], ClientUserMessageIds: clientUserMessageIds?.ToArray(), TurnTrigger: turnTrigger);
            return document with { Turns = [.. document.Turns, turn] };
        }, cancellationToken);

    /// <summary>Records client message IDs for the turn's user inputs. Existing IDs cannot be rewritten.</summary>
    public Task<CodexThreadDocument> RecordInputClientsAsync(Guid threadId, long expectedRevision, Guid turnId,
        string ownerId, string?[] clients, CancellationToken cancellationToken = default)
        => ChangeTurnAsync(threadId, expectedRevision, turnId, ownerId, turn =>
        {
            var previous = turn.ClientUserMessageIds ?? [];
            if (clients.Length < previous.Length || !previous.SequenceEqual(clients.Take(previous.Length)))
                throw new InvalidOperationException("User message correlation cannot be rewritten.");
            return turn with { ClientUserMessageIds = clients.ToArray() };
        }, cancellationToken);

    /// <summary>Appends steering input to a running turn.</summary>
    public Task<CodexThreadDocument> AppendInputAsync(Guid threadId, long expectedRevision, Guid expectedTurnId,
        string ownerId, IReadOnlyList<JsonElement> input, CancellationToken cancellationToken = default)
        => ChangeTurnAsync(threadId, expectedRevision, expectedTurnId, ownerId,
            turn => turn with { Input = [.. turn.Input, .. Items(input)] }, cancellationToken);

    /// <summary>Replace host-resolved defaults only while idle. Running captures stay immutable.</summary>
    public Task<CodexThreadDocument> UpdateSettingsAsync(Guid threadId, long expectedRevision, JsonElement settings,
        CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            if (document.Turns.LastOrDefault()?.Status == "inProgress")
                throw new InvalidOperationException("Cannot replace defaults of an active native thread.");
            if (settings.ValueKind != JsonValueKind.Object) throw new ArgumentException("Resolved settings must be an object.");
            return document with { Settings = settings.Clone() };
        }, cancellationToken);

    /// <summary>Host-validated generated history may grow, never replace an observed prefix.
    /// This does not authorize tools or validate provider output on the caller's behalf.</summary>
    public Task<CodexThreadDocument> SynchronizeOutputAsync(Guid threadId, long expectedRevision, Guid expectedTurnId,
        string ownerId, IReadOnlyList<JsonElement> output, CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            var turn = Active(document, expectedTurnId, ownerId);
            var next = Items(output);
            if (next.Length < turn.Output.Length || turn.Output.Where((item, index) => !JsonElement.DeepEquals(item, next[index])).Any())
                throw new InvalidOperationException("Observed native thread output cannot be rewritten.");
            // Format 1 readers reject active output. Signal the changed invariant explicitly;
            // old snapshots remain readable, and older writers fail closed on this format.
            return Replace(document, turn with { Output = next }) with { Format = 2 };
        }, cancellationToken);

    /// <summary>Only a true durable admission permits dispatch. A repeated call ID never grants
    /// another dispatch, even if its prior outcome is unknown. Changed bytes/kind are a conflict.</summary>
    public async Task<CodexThreadAdmission> AdmitAsync(Guid threadId, long expectedRevision, Guid turnId,
        string ownerId, string callId, string kind, JsonElement request, CancellationToken cancellationToken = default)
    {
        Token(callId);
        if (kind is not ("inference" or "tool")) throw new ArgumentException("Invalid dispatch kind.", nameof(kind));
        var hash = Digest(request);
        var before = await CurrentAsync(threadId, expectedRevision, cancellationToken).ConfigureAwait(false);
        var active = Active(before, turnId, ownerId);
        var prior = before.Turns.SelectMany(t => t.Dispatches).FirstOrDefault(d => d.CallId == callId);
        if (prior is not null)
        {
            if (prior.Kind != kind || prior.RequestSha256 != hash) throw new InvalidOperationException("Dispatch identity is already bound to different request bytes.");
            return new(false, before);
        }
        var turn = active with { Dispatches = [.. active.Dispatches, new(callId, kind, hash, "dispatching")] };
        var next = Replace(before, turn) with { Revision = checked(before.Revision + 1), UpdatedAt = time.GetUtcNow() };
        return new(true, await PublishAsync(before.Revision, next, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Records the outcome of a dispatch: <c>succeeded</c>, <c>failed</c> or <c>unknown</c>.</summary>
    public Task<CodexThreadDocument> SettleAsync(Guid threadId, long expectedRevision, Guid turnId,
        string ownerId, string callId, string outcome, JsonElement? result = null, CancellationToken cancellationToken = default)
        => ChangeAsync(threadId, expectedRevision, document =>
        {
            var turn = document.Turns.FirstOrDefault(t => t.Id == turnId && t.OwnerId == ownerId)
                ?? throw new InvalidOperationException("Recorded turn owner does not match.");
            if (outcome is not ("succeeded" or "failed" or "unknown")) throw new ArgumentException("Invalid dispatch outcome.", nameof(outcome));
            var prior = turn.Dispatches.FirstOrDefault(d => d.CallId == callId)
                ?? throw new KeyNotFoundException("Native dispatch receipt not found.");
            if (prior.State is "succeeded" or "failed")
                throw new InvalidOperationException("An observed terminal dispatch result cannot be replaced.");
            return Replace(document, turn with { Dispatches = turn.Dispatches.Select(d => d.CallId == callId
                ? d with { State = outcome, Result = result?.Clone() } : d).ToArray() });
        }, cancellationToken);

    /// <summary>Completes a turn with its final status and output items.</summary>
    public Task<CodexThreadDocument> CompleteAsync(Guid threadId, long expectedRevision, Guid turnId,
        string ownerId, string status, IReadOnlyList<JsonElement> output, CancellationToken cancellationToken = default)
        => ChangeTurnAsync(threadId, expectedRevision, turnId, ownerId, turn =>
        {
            if (status is not ("completed" or "failed" or "interrupted")) throw new ArgumentException("Invalid terminal turn status.", nameof(status));
            if (turn.Dispatches.Any(d => d.State == "dispatching") ||
                status == "completed" && turn.Dispatches.Any(d => d.State == "unknown"))
                throw new InvalidOperationException("Unsettled dispatches prevent this terminal transition.");
            return turn with { Status = status, Output = Items(output), CompletedAt = time.GetUtcNow() };
        }, cancellationToken);

    /// <summary>Call only after the host proves the recorded execution owner is lost. A read alone
    /// is not that proof. Preserve known results and mark unobserved dispatches unknown; never replay.</summary>
    public Task<CodexThreadDocument> RecoverInterruptedAsync(Guid threadId, long expectedRevision, Guid turnId,
        string lostOwnerId, CancellationToken cancellationToken = default)
        => ChangeTurnAsync(threadId, expectedRevision, turnId, lostOwnerId, turn => turn with
        {
            Status = "interrupted", CompletedAt = time.GetUtcNow(),
            Dispatches = turn.Dispatches.Select(d => d.State == "dispatching" ? d with { State = "unknown" } : d).ToArray()
        }, cancellationToken);

    /// <summary>Fork one observed source revision. Inclusive last/exclusive before are mutually
    /// exclusive. A selected active turn is refused; copied call identities remain non-dispatchable.</summary>
    public async Task<CodexThreadDocument> ForkAsync(Guid sourceId, long expectedSourceRevision, Guid destinationId,
        Guid? lastTurnId = null, Guid? beforeTurnId = null, CancellationToken cancellationToken = default,
        JsonElement? effectiveSettings = null)
    {
        Id(destinationId);
        if (sourceId == destinationId || lastTurnId is not null && beforeTurnId is not null)
            throw new ArgumentException("Fork requires distinct identity and one cutoff at most.");
        var source = await CurrentAsync(sourceId, expectedSourceRevision, cancellationToken).ConfigureAwait(false);
        if (effectiveSettings is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Fork settings must be an object.");
        var count = source.Turns.Length;
        var cutoff = lastTurnId ?? beforeTurnId;
        if (cutoff is { } turnId)
        {
            var index = Array.FindIndex(source.Turns, t => t.Id == turnId);
            if (index < 0) throw new KeyNotFoundException("Fork cutoff turn not found.");
            count = index + (lastTurnId is not null ? 1 : 0);
        }
        var selected = source.Turns.Take(count).ToArray();
        if (selected.Any(t => t.Status == "inProgress")) throw new InvalidOperationException("Cannot fork an active turn.");
        var now = time.GetUtcNow();
        return await PublishAsync(null, source with { Id = destinationId, Revision = 1, CreatedAt = now, UpdatedAt = now,
            ForkedFromId = source.Id, ForkedFromRevision = source.Revision, Turns = selected,
            Settings = effectiveSettings?.Clone() ?? source.Settings }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodexThreadDocument> CurrentAsync(Guid threadId, long expected, CancellationToken ct)
    {
        var value = await ReadAsync(threadId, ct).ConfigureAwait(false);
        if (expected < 1 || value.Revision != expected) throw new CodexThreadConflictException();
        return value;
    }
    private async Task<CodexThreadDocument> ChangeAsync(Guid threadId, long revision,
        Func<CodexThreadDocument, CodexThreadDocument> update, CancellationToken ct)
    {
        var before = await CurrentAsync(threadId, revision, ct).ConfigureAwait(false);
        return await PublishAsync(before.Revision, update(before) with { Revision = checked(before.Revision + 1),
            UpdatedAt = time.GetUtcNow() }, ct).ConfigureAwait(false);
    }
    private Task<CodexThreadDocument> ChangeTurnAsync(Guid threadId, long revision, Guid turnId, string ownerId,
        Func<CodexStoredTurn, CodexStoredTurn> update, CancellationToken ct)
        => ChangeAsync(threadId, revision, value => Replace(value, update(Active(value, turnId, ownerId))), ct);
    private static CodexStoredTurn Active(CodexThreadDocument value, Guid turnId, string ownerId)
    {
        var turn = value.Turns.LastOrDefault();
        if (turn is null || turn.Id != turnId || turn.Status != "inProgress" || turn.OwnerId != ownerId)
            throw new InvalidOperationException("Expected live turn and execution owner do not match.");
        return turn;
    }
    private static CodexThreadDocument Replace(CodexThreadDocument value, CodexStoredTurn turn)
        => value with { Turns = value.Turns.Select(t => t.Id == turn.Id ? turn : t).ToArray() };
    private async Task<CodexThreadDocument> PublishAsync(long? expected, CodexThreadDocument value, CancellationToken ct)
    {
        if (value.Revision != checked((expected ?? 0) + 1)) throw new InvalidOperationException("Invalid next native thread revision.");
        // Round-trip validation produces an owned copy before handing bytes to storage.
        var body = Encode(value); var next = Decode(body);
        if (value.Settings.TryGetProperty("ephemeral", out var transient) && transient.ValueKind == JsonValueKind.True)
        {
            ct.ThrowIfCancellationRequested();
            lock (ephemeralGate)
            {
                long? revision = ephemeral.TryGetValue(value.Id, out var previous) ? previous.GetProperty("revision").GetInt64() : null;
                if (revision != expected) throw new CodexThreadConflictException();
                if (expected is null && ephemeral.Count >= 64 || ephemeral.Where(x => x.Key != value.Id)
                    .Sum(x => (long)Encoding.UTF8.GetByteCount(x.Value.GetRawText())) + Encoding.UTF8.GetByteCount(body.GetRawText()) > MaximumDocumentBytes)
                    throw new InvalidOperationException("Ephemeral thread memory capacity exhausted; no durable fallback.");
                ephemeral[value.Id] = body.Clone();
                return next;
            }
        }
        if (!await storage.CompareExchangeAsync(value.Id, expected, body, ct).ConfigureAwait(false))
            throw new CodexThreadConflictException();
        return next;
    }
    /// <summary>Serializes a thread document, enforcing <see cref="MaximumDocumentBytes"/>.</summary>
    public static JsonElement Encode(CodexThreadDocument value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CodexThreadJson.Default.CodexThreadDocument);
        if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("Native thread journal is full; source must be archived before continuing.");
        using var parsed = JsonDocument.Parse(bytes); return parsed.RootElement.Clone();
    }
    /// <summary>Parses and validates a stored document without writing it.</summary>
    public CodexThreadDocument ValidateSnapshot(JsonElement body) => Decode(body);

    /// <summary>Returns every tool call ID ever admitted on the thread.</summary>
    public static IReadOnlyCollection<string> DispatchedToolCallIds(CodexThreadDocument document)
        => document.Turns.SelectMany(t => t.Dispatches).Where(d => d.Kind == "tool").Select(d => d.CallId).ToArray();

    private CodexThreadDocument Decode(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(body.GetRawText()) > MaximumDocumentBytes)
            throw new InvalidDataException("Native thread journal is invalid or exceeds its bound.");
        var value = body.Deserialize(CodexThreadJson.Default.CodexThreadDocument)
            ?? throw new InvalidDataException("Native thread journal is missing.");
        if (value.Format is not (1 or 2) || value.Id == Guid.Empty || value.AccountId != accountId || value.Revision < 1 ||
            value.Settings.ValueKind != JsonValueKind.Object || value.Turns is null || value.Turns.Length > MaximumTurns ||
            value.CreatedAt == default || value.UpdatedAt == default ||
            (value.ForkedFromId is null) != (value.ForkedFromRevision is null) || value.ForkedFromRevision is < 1)
            throw new InvalidDataException("Native thread identity, revision or history is invalid.");
        var turnIds = new HashSet<Guid>(); var callIds = new HashSet<string>(StringComparer.Ordinal); var active = false;
        foreach (var turn in value.Turns)
        {
            if (turn is null || active || turn.Id == Guid.Empty || turn.RootTurnId == Guid.Empty || !turnIds.Add(turn.Id) ||
                !ValidToken(turn.OwnerId) || turn.Status is not ("inProgress" or "completed" or "failed" or "interrupted") ||
                turn.StartedAt == default || (turn.Status == "inProgress") != (turn.CompletedAt is null) ||
                turn.Input is null || turn.Output is null || turn.Dispatches is null || turn.Dispatches.Length > MaximumItems)
                throw new InvalidDataException("Native turn history is invalid.");
            active = turn.Status == "inProgress";
            if (active && value.Format == 1 && turn.Output.Length != 0 || turn.CompletedAt == default(DateTimeOffset))
                throw new InvalidDataException("Native terminal history is invalid.");
            _ = Items(turn.Input); _ = Items(turn.Output);
            if (turn.ClientUserMessageIds is { } clients && (clients.Length > MaximumItems || clients.Any(c => c is not null && !ValidToken(c)))
                || turn.TurnTrigger is { } trigger && !ValidToken(trigger))
                throw new InvalidDataException("Invalid native user message correlation.");
            foreach (var receipt in turn.Dispatches)
            {
                if (receipt is null || !ValidToken(receipt.CallId) || !callIds.Add(receipt.CallId) ||
                    receipt.Kind is not ("inference" or "tool") || receipt.State is not ("dispatching" or "succeeded" or "failed" or "unknown") ||
                    receipt.RequestSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
                    receipt.Result is { ValueKind: JsonValueKind.Undefined } ||
                    !active && receipt.State == "dispatching" || turn.Status == "completed" && receipt.State == "unknown")
                    throw new InvalidDataException("Native dispatch history is invalid.");
            }
        }
        return value;
    }
    private static JsonElement[] Items(IReadOnlyList<JsonElement> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MaximumItems || items.Any(x => x.ValueKind != JsonValueKind.Object))
            throw new ArgumentException("Native history requires bounded object items.", nameof(items));
        return items.Select(x => x.Clone()).ToArray();
    }
    private static string Digest(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new ArgumentException("Dispatch request must be an object.", nameof(request));
        var bytes = Encoding.UTF8.GetBytes(request.GetRawText());
        if (bytes.Length > MaximumDocumentBytes) throw new ArgumentException("Dispatch request exceeds its bound.", nameof(request));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    private static bool ValidToken(string? value) => value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static void Token(string value) { if (!ValidToken(value)) throw new ArgumentException("Identity must be bounded and contain no control characters."); }
    private static void Id(Guid value) { if (value == Guid.Empty) throw new ArgumentException("A nonempty thread/turn identity is required."); }
}
