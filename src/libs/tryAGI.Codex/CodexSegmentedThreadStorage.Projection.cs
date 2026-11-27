using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexSegmentedThreadStorage
{
    /// <summary>Read and atomically cache a bounded realtime projection batch in an explicitly
    /// separate trusted cache namespace. Corrupt derived payloads may be rebuilt from the
    /// source, but a malformed cache root, rollback or uncertain cache commit is not replayed.
    /// This never modifies the authoritative thread or dispatches effects.</summary>
    public Task<CodexCachedRealtimeProjection> ReadAndPersistRealtimeProjectionAsync(
        CodexPersistentRealtimeProjectionCache cache, Guid threadId, string sessionId,
        int pageSize = 64, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        cache.RequireSeparateNamespace(roots, pages);
        return cache.ReadAsync(this, accountId, threadId, sessionId, pageSize, cancellationToken);
    }

    /// <summary>Restore a trusted derived reducer checkpoint and apply only the verified
    /// observation suffix from one captured version-3 root. Save the returned checkpoint
    /// as a single atomic value in account-scoped host storage. A changed scope/prefix,
    /// unavailable index, corrupt checkpoint or unsupported reducer version requires a
    /// full verified replay. This read-only API never emits notifications or authorizes
    /// reasoning, tools, handoff admission, or provider dispatch.</summary>
    public Task<CodexRealtimeProjectionRead> ReadRealtimeProjectionAsync(Guid threadId, string sessionId,
        CodexRealtimeProjectionCheckpoint? checkpoint = null, int pageSize = 64,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prior = checkpoint is null ? null : CodexRealtimeProjectionCodec.Decode(checkpoint);
        return ReadRealtimeProjectionCoreAsync(threadId, sessionId, prior, pageSize, cancellationToken);
    }

    internal async Task<CodexRealtimeProjectionRead> ReadRealtimeProjectionCoreAsync(Guid threadId, string sessionId,
        CodexRealtimeProjectionEnvelope? prior, int pageSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateObservationDeltaRequest(threadId, sessionId, prior?.Prefix, pageSize);
        if (prior is not null && (prior.AccountId != accountId || prior.Scope.SessionId != sessionId))
            throw new InvalidOperationException("Realtime projection checkpoint scope does not match.");
        var root = await roots.ReadAsync(threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Native thread not found.");
        var (revision, manifest) = ReadRoot(threadId, root);
        if (manifest is not { Version: 3, ReductionScopes: { } scopes })
            throw new InvalidOperationException("Realtime reducer scope index is unavailable; verified full replay is required.");
        var scope = scopes.SingleOrDefault(s => s.SessionId == sessionId)
            ?? throw new KeyNotFoundException("Native realtime session not found.");
        if (prior is not null && !scope.CanResume(prior.Scope))
            throw new CodexThreadConflictException();
        var delta = await ReadCapturedObservationDeltaAsync(threadId, sessionId, prior?.Prefix, pageSize,
            revision, manifest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return CodexThreadStore.ReduceRealtimeProjection(accountId, scope, delta, prior?.State);
    }

    private static CodexRealtimeReductionScope[] BuildReductionScopes(JsonElement document)
    {
        if (!document.TryGetProperty("realtimeSessions", out var sessions) || sessions.ValueKind == JsonValueKind.Null)
            return [];
        try
        {
            var result = new List<CodexRealtimeReductionScope>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var session in sessions.EnumerateArray())
            {
                var historyMode = session.TryGetProperty("history", out var history) && history.ValueKind != JsonValueKind.Null
                    ? (CodexRealtimeHistoryMode)history.GetProperty("mode").GetInt32() : CodexRealtimeHistoryMode.Canonical;
                var scope = new CodexRealtimeReductionScope(session.GetProperty("version").GetInt32(),
                    session.GetProperty("sessionId").GetString()!, session.GetProperty("ownerId").GetString()!,
                    session.GetProperty("callId").GetString(), session.GetProperty("protocol").GetString()!,
                    session.TryGetProperty("transportKind", out var kind) ? (CodexRealtimeTransportKind)kind.GetInt32() : CodexRealtimeTransportKind.ExistingCall,
                    session.TryGetProperty("canonicalLegacySegments", out var legacy) && legacy.GetBoolean(),
                    historyMode,
                    session.TryGetProperty("usedAttachmentIds", out var attachments) && attachments.ValueKind != JsonValueKind.Null
                        ? attachments.EnumerateArray().Select(v => v.GetGuid()).ToArray() : []);
                scope.Validate();
                if (!ids.Add(scope.SessionId) || result.Count >= 4096)
                    throw new InvalidDataException("Realtime reducer scope identities exceed their bound.");
                result.Add(scope);
            }
            return result.ToArray();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Invalid native realtime reducer scope metadata.", error);
        }
    }

    private static string ScopeDigest(CodexRealtimeReductionScope[] scopes)
        => Digest(JsonSerializer.SerializeToUtf8Bytes(scopes, CodexRealtimeProjectionJson.Default.CodexRealtimeReductionScopeArray));

    private static void ValidateReductionScopes(JsonElement raw, CodexRealtimeReductionScope[] scopes, CodexThreadSegmentManifest manifest)
    {
        if (scopes.Length > 4096) throw new InvalidDataException("Realtime reducer scope index exceeds its bound.");
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (scope is null) throw new InvalidDataException("Missing realtime reducer scope.");
            scope.Validate();
            if (!sessions.Add(scope.SessionId)) throw new InvalidDataException("Duplicate realtime reducer scope.");
        }
        foreach (var entry in raw.EnumerateArray()) RequireUniqueProperties(entry);
        if (manifest.HistoryIndex!.Any(e => e.SessionId is { } id && !sessions.Contains(id)) ||
            !ValidDigest(manifest.ReductionScopesSha256) || ScopeDigest(scopes) != manifest.ReductionScopesSha256)
            throw new InvalidDataException("Realtime reducer scope index digest or collection identity mismatch.");
    }
}
