using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Opaque, persistable derived realtime projection state, including its exact
/// consumed-prefix proof. Store both fields atomically in trusted account-scoped storage.
/// SHA-256 detects corruption, not malicious replacement. This is never an admission receipt.</summary>
public sealed record CodexRealtimeProjectionCheckpoint(string StateBase64, string StateSha256);

/// <summary>Read-only projection of a verified observation prefix. When HasMore is true,
/// the result is a partial projection, not the current complete session history.</summary>
public sealed record CodexRealtimeProjectionRead(long Revision, int ProcessedRecords, int TotalRecords,
    int AppliedRecords, CodexRealtimeHistoryMode Mode, CodexStoredRealtimeFrame[] Frames,
    CodexStoredRealtimeHandoffCapture[] Handoffs, CodexStoredRealtimeCarryover[] Carryover,
    CodexRealtimeProjectionCheckpoint Checkpoint)
{
    /// <summary>Whether more observations exist in the captured revision.</summary>
    public bool HasMore => ProcessedRecords < TotalRecords;
}

internal sealed record CodexRealtimeReductionScope(int Version, string SessionId, string OwnerId,
    string? CallId, string Protocol, CodexRealtimeTransportKind TransportKind,
    bool CanonicalLegacySegments, CodexRealtimeHistoryMode Mode, Guid[] Attachments)
{
    internal bool CanResume(CodexRealtimeReductionScope prior) =>
        Version == prior.Version && SessionId == prior.SessionId && OwnerId == prior.OwnerId &&
        CallId == prior.CallId && Protocol == prior.Protocol && TransportKind == prior.TransportKind &&
        CanonicalLegacySegments == prior.CanonicalLegacySegments && Mode == prior.Mode &&
        Attachments.AsSpan().StartsWith(prior.Attachments);

    internal void Validate()
    {
        static bool Identity(string? value) => value is { Length: > 0 and <= 512 } &&
            !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
        if (Version != 1 || !Identity(SessionId) || !Identity(OwnerId) ||
            Protocol is not ("v1" or "v2" or "v3") || !Enum.IsDefined(Mode) || !Enum.IsDefined(TransportKind) ||
            CanonicalLegacySegments && Protocol == "v3" ||
            (TransportKind == CodexRealtimeTransportKind.ExistingCall ? !Identity(CallId) : CallId is not null) ||
            Attachments is null || Attachments.Length > 32768 ||
            TransportKind == CodexRealtimeTransportKind.DirectWebSocket && Attachments.Length > 1 ||
            Attachments.Contains(Guid.Empty) || Attachments.Distinct().Count() != Attachments.Length)
            throw new InvalidDataException("Invalid realtime reducer scope.");
    }
}

internal sealed record CodexRealtimeReducerPosition(string Id, long Sequence, long Timestamp);

internal sealed record CodexRealtimeReducerState(long Sequence, long PreviousTimestamp, long PreviousRevision,
    int PreviousTurnCount, string? LastId, CodexRealtimeAccumulatorState Accumulator,
    CodexStoredRealtimeFrame[] Frames, CodexStoredRealtimeHandoffCapture[] Handoffs,
    CodexStoredRealtimeCarryover[] Carryover, CodexRealtimeReducerPosition[] Observed,
    string[] EventIds, string[] SplitFrames, string[] PublishedFrames);

internal sealed record CodexRealtimeProjectionEnvelope(int Version, string AccountId,
    CodexRealtimeReductionScope Scope, CodexRealtimeObservationPrefix Prefix, CodexRealtimeReducerState State);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CodexRealtimeProjectionEnvelope))]
[JsonSerializable(typeof(CodexRealtimeReductionScope[]))]
internal partial class CodexRealtimeProjectionJson : JsonSerializerContext;

internal static class CodexRealtimeProjectionCodec
{
    internal const int Version = 1;
    private const int MaximumBytes = CodexThreadStore.MaximumDocumentBytes;
    private const int MaximumBase64 = ((MaximumBytes + 2) / 3) * 4;

    internal static CodexRealtimeProjectionCheckpoint Encode(CodexRealtimeProjectionEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, CodexRealtimeProjectionJson.Default.CodexRealtimeProjectionEnvelope);
        if (bytes.Length > MaximumBytes)
            throw new InvalidOperationException("Realtime projection checkpoint exceeds its bound; verified full replay is required.");
        return new(Convert.ToBase64String(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal static CodexRealtimeProjectionEnvelope Decode(CodexRealtimeProjectionCheckpoint checkpoint)
    {
        if (checkpoint.StateBase64 is not { Length: > 0 and <= MaximumBase64 } || checkpoint.StateSha256 is not { Length: 64 })
            throw new InvalidDataException("Invalid realtime projection checkpoint bounds.");
        try
        {
            var bytes = Convert.FromBase64String(checkpoint.StateBase64);
            if (bytes.Length > MaximumBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != checkpoint.StateSha256)
                throw new InvalidDataException("Realtime projection checkpoint digest mismatch.");
            var envelope = JsonSerializer.Deserialize(bytes, CodexRealtimeProjectionJson.Default.CodexRealtimeProjectionEnvelope)
                ?? throw new InvalidDataException("Missing realtime projection checkpoint.");
            if (envelope.Version != Version || envelope.Scope is null || envelope.Prefix is null || envelope.State is null)
                throw new InvalidDataException("Unsupported realtime projection checkpoint.");
            envelope.Scope.Validate();
            return envelope;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new InvalidDataException("Invalid serialized realtime projection checkpoint.", error);
        }
    }
}

public sealed partial class CodexThreadStore
{
    // Reuse the same reducer as authoritative full replay, but never install the
    // restored state into the authoritative validation cache or emit old events.
    internal static CodexRealtimeProjectionRead ReduceRealtimeProjection(string accountId,
        CodexRealtimeReductionScope scope, CodexRealtimeObservationDelta delta, CodexRealtimeReducerState? prior)
    {
        scope.Validate();
        if (delta.StartOrdinal < 0 || delta.TotalCount > MaximumItems ||
            delta.Checkpoint.NextOrdinal != delta.StartOrdinal + delta.Items.Length ||
            delta.Checkpoint.NextOrdinal > delta.TotalCount || (prior?.Sequence ?? 0) != delta.StartOrdinal)
            throw new InvalidDataException("Invalid realtime projection prefix count.");
        if (prior is not null) ValidateProjectionState(prior, scope, delta.Revision);
        var session = new CodexStoredRealtimeSession(scope.Version, scope.SessionId, scope.CallId,
            scope.Protocol, scope.OwnerId, "detached")
        {
            CanonicalLegacySegments = scope.CanonicalLegacySegments, TransportKind = scope.TransportKind,
            UsedAttachmentIds = scope.Attachments,
        };
        try
        {
            var reduction = CreateRealtimeHistoryReduction(session, scope.Mode, prior);
            foreach (var raw in delta.Items)
            {
                var item = raw.Deserialize(CodexThreadJson.Default.CodexStoredRealtimeHistoryEvent)
                    ?? throw new InvalidDataException("Missing realtime journal observation.");
                if (item.CommitRevision > delta.Revision)
                    throw new InvalidDataException("Realtime observation exceeds the captured revision.");
                reduction.Append(item);
            }
            var snapshot = reduction.Snapshot();
            var state = reduction.CaptureCheckpoint();
            ValidateProjectionState(state, scope, delta.Revision);
            var saved = CodexRealtimeProjectionCodec.Encode(new(CodexRealtimeProjectionCodec.Version, accountId,
                scope, delta.Checkpoint, state));
            return new(delta.Revision, delta.Checkpoint.NextOrdinal, delta.TotalCount, delta.Items.Length, scope.Mode,
                snapshot.Frames, snapshot.Handoffs, snapshot.Carryover, saved);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid realtime projection state or observation.", error);
        }
    }

    private static void ValidateProjectionState(CodexRealtimeReducerState state, CodexRealtimeReductionScope scope, long revision)
    {
        if (state.Sequence is < 0 or > MaximumItems || state.PreviousTimestamp < 0 ||
            state.PreviousRevision < 0 || state.PreviousRevision > revision || state.PreviousTurnCount is < 0 or > MaximumTurns ||
            state.Accumulator is null || state.Accumulator.Entries is null || state.Accumulator.Entries.Length > MaximumItems ||
            state.Accumulator.Handoffs is null || state.Accumulator.Handoffs.Length > MaximumItems ||
            state.EventIds is null || state.EventIds.Length != state.Sequence ||
            state.Observed is null || state.Observed.Length > state.Sequence ||
            state.Frames is null || state.Frames.Length > MaximumItems ||
            state.Handoffs is null || state.Handoffs.Length > MaximumItems ||
            state.Carryover is null || state.Carryover.Length > MaximumItems ||
            state.SplitFrames is null || state.SplitFrames.Length > MaximumItems ||
            state.PublishedFrames is null || state.PublishedFrames.Length > MaximumItems)
            throw new InvalidDataException("Invalid realtime projection checkpoint bounds.");
        var ids = state.EventIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count != state.EventIds.Length || ids.Any(id => !ValidToken(id)) ||
            (state.Sequence == 0 ? state.LastId is not null : state.LastId is null || !ids.Contains(state.LastId)))
            throw new InvalidDataException("Invalid realtime projection event identity.");
        var observations = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        foreach (var position in state.Observed)
            if (position is null || !ids.Contains(position.Id) || !observations.Add(position.Id) ||
                position.Sequence <= 0 || position.Sequence > state.Sequence || !sequences.Add(position.Sequence) ||
                position.Timestamp < 0 || position.Timestamp > state.PreviousTimestamp)
                throw new InvalidDataException("Invalid realtime projection observation position.");
        var frames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in state.Frames)
            if (frame is null || !ValidToken(frame.Id) || !frames.Add(frame.Id) || !frame.Sealed ||
                frame.Role is not ("user" or "assistant") || frame.Text is null ||
                frame.Sequence <= 0 || frame.Sequence > state.Sequence ||
                frame.ObservedAtMs < 0 || frame.ObservedAtMs > state.PreviousTimestamp ||
                frame.SourceObservationIds is not { Length: > 0 } ||
                frame.SourceObservationIds.Any(id => !observations.Contains(id)) ||
                frame.Disposition is not ("pending" or "accepted" or "carried" or "legacyOnly"))
                throw new InvalidDataException("Invalid realtime projection frame.");
        var handoffs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handoff in state.Handoffs)
            if (handoff is null || !ValidToken(handoff.HandoffId) || !handoffs.Add(handoff.HandoffId) ||
                handoff.InputTranscript is null || handoff.FrameIds is null || handoff.FrameIds.Any(id => !frames.Contains(id)) ||
                handoff.State is not ("pending" or "accepted" or "rejected") ||
                (handoff.State == "accepted") != (handoff.TurnId is not null))
                throw new InvalidDataException("Invalid realtime projection handoff.");
        foreach (var saved in state.Carryover)
            if (saved is null || !ids.Contains(saved.Id) || saved.SessionId != scope.SessionId ||
                saved.BeforeTurnCount < 0 || saved.BeforeTurnCount > state.PreviousTurnCount ||
                saved.SourceFrameIds is not { Length: > 0 } || saved.SourceFrameIds.Any(id => !frames.Contains(id)) ||
                saved.Input.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid realtime projection carryover.");
        if (state.SplitFrames.Any(id => !frames.Contains(id)) ||
            state.SplitFrames.Distinct(StringComparer.Ordinal).Count() != state.SplitFrames.Length ||
            !handoffs.SetEquals(state.Accumulator.Handoffs.Where(h => h is not null).Select(h => h.Id)))
            throw new InvalidDataException("Invalid realtime projection split frame or handoff identity.");
        // Published active finals are intentionally not necessarily sealed yet.
        // The restored accumulator validates its own receipt, prefix and byte bounds.
        var activeIds = state.Accumulator.Entries.Where(e => e is not null).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (state.PublishedFrames.Any(id => !frames.Contains(id) && !activeIds.Contains(id)) ||
            state.PublishedFrames.Distinct(StringComparer.Ordinal).Count() != state.PublishedFrames.Length)
            throw new InvalidDataException("Invalid realtime projection publication identity.");
    }
}
