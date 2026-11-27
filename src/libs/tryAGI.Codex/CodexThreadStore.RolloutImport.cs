using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Exact host-authorized import of one archived immutable logical Rust rollout.</summary>
/// <param name="OperationId">Stable create-only import identity.</param>
/// <param name="ThreadId">Original logical thread ID and native destination ID.</param>
/// <param name="LogicalSha256">SHA-256 of the complete uncompressed source bytes.</param>
/// <param name="LogicalBytes">Exact source byte length.</param>
/// <param name="ArchiveReference">Opaque host-owned immutable source reference, not a filesystem path or credential.</param>
/// <param name="EffectiveSettings">Resolved native settings, never taken from untrusted rollout metadata.</param>
/// <param name="RecoverInterruptedTail">Explicit host confirmation that an unfinished source tail cannot still execute.</param>
public sealed record CodexRustRolloutImportRequest(Guid OperationId, Guid ThreadId, string LogicalSha256,
    long LogicalBytes, string ArchiveReference, JsonElement EffectiveSettings, bool RecoverInterruptedTail = false);

/// <summary>Import/reconciliation authorization; the host verifies source ownership and immutable archival.</summary>
/// <param name="AccountId">Authenticated native account.</param>
/// <param name="Request">Exact source, destination and settings.</param>
/// <param name="ReconciliationOnly">Whether the operation reads only the existing native receipt.</param>
public sealed record CodexRustRolloutImportAccess(string AccountId, CodexRustRolloutImportRequest Request,
    bool ReconciliationOnly);

/// <summary>Provenance committed in the same native journal CAS as the imported turns.</summary>
/// <param name="OperationId">Original import operation.</param>
/// <param name="ThreadId">Original logical imported thread, even when later inherited by a fork.</param>
/// <param name="LogicalSha256">Verified source byte digest.</param>
/// <param name="LogicalBytes">Verified source byte length.</param>
/// <param name="ArchiveReference">Host-owned immutable original source.</param>
/// <param name="SettingsSha256">Digest of the exact host-supplied effective settings.</param>
/// <param name="RecoverInterruptedTail">Whether recovery of a proven dead tail was authorized.</param>
/// <param name="ImportedTurns">Number of turns in the imported original prefix.</param>
/// <param name="Checkpoint">Verified final source cursor and identity.</param>
public sealed record CodexRustRolloutImportProvenance(Guid OperationId, Guid ThreadId, string LogicalSha256,
    long LogicalBytes, string ArchiveReference, string SettingsSha256, bool RecoverInterruptedTail,
    int ImportedTurns, CodexRolloutReplayCheckpoint Checkpoint);

public sealed partial record CodexThreadDocument
{
    /// <summary>Original imported-prefix provenance, not an execution or provider billing receipt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexRustRolloutImportProvenance? RustRolloutImport { get; init; }
}

/// <summary>An import contains history semantics not handled by the strict plain-turn importer.</summary>
public sealed class CodexRustRolloutCompatibilityException(long lineNumber, string reason)
    : IOException($"Rust rollout line {lineNumber} requires compatibility review: {reason}.")
{
    /// <summary>One-based source line requiring review.</summary>
    public long LineNumber { get; } = lineNumber;
}

/// <summary>The native root publication outcome is unknown; reconcile the same import identity before retrying.</summary>
public sealed class CodexRustRolloutImportSettlementException(CodexRustRolloutImportRequest request, Exception innerException)
    : IOException("Rust rollout import requires exact-operation reconciliation.", innerException)
{
    /// <summary>Original request, including the authorized settings; keep private.</summary>
    public CodexRustRolloutImportRequest Request { get; } = request;
}

public sealed partial class CodexThreadStore
{
    /// <summary>Maximum complete logical Rust source accepted by this bounded materialized importer.</summary>
    public const long MaximumRustImportBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Creates one native thread from a fully verified, archived plain-turn Rust rollout.
    /// Original bytes are retained by the host; the SDK does not repair them. It normalizes
    /// supported response items in source order, maps terminal tool evidence to non-replay
    /// fences, and publishes the entire imported prefix and provenance through the existing
    /// native CAS (including segmented roots). Existing threads are never overwritten.
    /// Fork references, compaction, rollback, retained-context, agent/realtime and unrecognized
    /// wire semantics require a dedicated reducer and are refused before publication.
    /// The host must explicitly authorize the source's historical model-visible instructions.
    /// </summary>
    public async Task<CodexThreadDocument> ImportRustRolloutAsync(CodexRustRolloutImportRequest request,
        Stream logicalSource, Func<CodexRustRolloutImportAccess, CancellationToken, ValueTask> authorize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalSource);
        ArgumentNullException.ThrowIfNull(authorize);
        var frozen = ValidateRustImport(request);
        await authorize(new(accountId, frozen, false), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await ReconcileRustImportCoreAsync(frozen, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;
        if (!logicalSource.CanRead || logicalSource.CanSeek && logicalSource.Length != frozen.LogicalBytes)
            throw new InvalidDataException("Rust rollout source length does not match the authorized revision.");

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var reducer = new PlainRustTurnReducer(frozen.RecoverInterruptedTail);
        CodexRolloutReplayFence? fence = null;
        long lastOffset = 0;
        DateTimeOffset created = default, updated = default;
        await foreach (var entry in CodexRolloutReader.ReadAsync(logicalSource, frozen.LogicalSha256,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (entry.NextCursor.ByteOffset > frozen.LogicalBytes || entry.LineNumber > 1_000_000 ||
                entry.RawLine is not { } raw)
                throw new InvalidDataException("Rust rollout exceeds the authorized source or record bound.");
            digest.AppendData(raw.Span);
            if (entry.HasNewline) digest.AppendData("\n"u8);
            lastOffset = entry.NextCursor.ByteOffset;
            if (fence is null)
            {
                if (entry.Status == CodexRolloutLineStatus.Blank) continue;
                fence = CodexRolloutReplayFence.Begin(entry, frozen.ThreadId);
                if (fence.HistoryBase is not null || fence.SubagentHistoryStartOrdinal is not null)
                    throw new CodexRustRolloutCompatibilityException(entry.LineNumber, "external or subagent history prefix");
                var header = entry.Record!;
                ValidateUniqueObject(header.Envelope, entry.LineNumber);
                ValidateUniqueObject(header.Payload, entry.LineNumber);
                created = RustTimestamp(header, entry.LineNumber);
                updated = created;
                continue;
            }

            fence.Observe(entry);
            if (entry.Status == CodexRolloutLineStatus.Blank) continue;
            var record = entry.Record!;
            ValidateUniqueObject(record.Envelope, entry.LineNumber);
            ValidateUniqueObject(record.Payload, entry.LineNumber);
            var at = RustTimestamp(record, entry.LineNumber);
            if (at > updated) updated = at;
            reducer.Observe(record, entry.LineNumber, at, raw.Length);
        }
        if (fence is null || lastOffset != frozen.LogicalBytes ||
            !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(frozen.LogicalSha256)))
            throw new InvalidDataException("Rust rollout bytes do not match the authorized immutable source.");
        _ = fence.NextOrdinalAtEnd();

        var turns = reducer.Finish(updated);
        var provenance = new CodexRustRolloutImportProvenance(frozen.OperationId, frozen.ThreadId,
            frozen.LogicalSha256, frozen.LogicalBytes, frozen.ArchiveReference, Digest(frozen.EffectiveSettings),
            frozen.RecoverInterruptedTail, turns.Length, fence.CaptureCheckpoint());
        var value = new CodexThreadDocument(1, frozen.ThreadId, accountId, 1, frozen.EffectiveSettings,
            created, updated, turns) { RustRolloutImport = provenance };
        // Validate the complete native document before entering the publication boundary.
        _ = Decode(Encode(value, reserveRealtimeClosure: true));
        cancellationToken.ThrowIfCancellationRequested();
        try { return await PublishAsync(null, value, cancellationToken).ConfigureAwait(false); }
        catch (CodexThreadConflictException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new CodexRustRolloutImportSettlementException(frozen, error);
        }
    }

    /// <summary>
    /// Reconciles an ambiguous native import without opening or rescanning the original rollout.
    /// An existing different thread or import identity is a conflict. A missing receipt does
    /// not prove that an in-flight publication is absent; the host must settle it before retry.
    /// </summary>
    public async Task<CodexThreadDocument?> ReconcileRustRolloutImportAsync(CodexRustRolloutImportRequest request,
        Func<CodexRustRolloutImportAccess, CancellationToken, ValueTask> authorize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        var frozen = ValidateRustImport(request);
        await authorize(new(accountId, frozen, true), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await ReconcileRustImportCoreAsync(frozen, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodexThreadDocument?> ReconcileRustImportCoreAsync(CodexRustRolloutImportRequest request, CancellationToken ct)
    {
        lock (ephemeralGate)
            if (ephemeral.ContainsKey(request.ThreadId)) throw new CodexThreadConflictException();
        var body = await storage.ReadAsync(request.ThreadId, ct).ConfigureAwait(false);
        if (body is null) return null;
        var existing = Decode(body.Value);
        if (existing.Id != request.ThreadId || existing.RustRolloutImport is not { } provenance ||
            provenance.ThreadId != request.ThreadId || provenance.OperationId != request.OperationId ||
            provenance.LogicalSha256 != request.LogicalSha256 || provenance.LogicalBytes != request.LogicalBytes ||
            provenance.ArchiveReference != request.ArchiveReference ||
            provenance.SettingsSha256 != Digest(request.EffectiveSettings) ||
            provenance.RecoverInterruptedTail != request.RecoverInterruptedTail ||
            provenance.ImportedTurns < 0 || provenance.ImportedTurns > existing.Turns.Length ||
            provenance.Checkpoint?.Cursor?.ByteOffset != request.LogicalBytes)
            throw new CodexThreadConflictException();
        _ = CodexRolloutReplayFence.RestoreCheckpoint(provenance.Checkpoint, request.ThreadId, request.LogicalSha256);
        return existing;
    }

    private static CodexRustRolloutImportRequest ValidateRustImport(CodexRustRolloutImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Id(request.ThreadId);
        Id(request.OperationId);
        Token(request.ArchiveReference);
        if (request.LogicalSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
            request.LogicalBytes is < 1 or > MaximumRustImportBytes || request.EffectiveSettings.ValueKind != JsonValueKind.Object ||
            request.EffectiveSettings.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw new ArgumentException("Invalid durable Rust import source or effective settings.", nameof(request));
        ValidateUniqueObject(request.EffectiveSettings, 0);
        return request with { LogicalSha256 = hash.ToLowerInvariant(), EffectiveSettings = request.EffectiveSettings.Clone() };
    }

    private static void ValidateUniqueObject(JsonElement value, long line)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CodexRustRolloutCompatibilityException(line, "non-object payload");
        CheckFields(value, line);
    }

    private static void CheckFields(JsonElement value, long line)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CodexRustRolloutCompatibilityException(line, "duplicate object field");
                CheckFields(property.Value, line);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckFields(item, line);
    }

    private static DateTimeOffset RustTimestamp(CodexRolloutRecord record, long line)
    {
        if (!DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ||
            at == default)
            throw new CodexRustRolloutCompatibilityException(line, "invalid source timestamp");
        return at;
    }

    private sealed class PlainRustTurnReducer(bool recoverInterruptedTail)
    {
        private sealed class Turn(Guid id, Guid root, DateTimeOffset started)
        {
            internal Guid Id { get; } = id;
            internal Guid Root { get; } = root;
            internal DateTimeOffset Started { get; } = started;
            internal List<JsonElement> Input { get; } = [];
            internal List<JsonElement> Output { get; } = [];
            internal List<CodexStoredDispatch> Dispatches { get; } = [];
            internal Dictionary<string, (int Index, string Type)> Calls { get; } = new(StringComparer.Ordinal);
            internal Dictionary<(string Role, string Text), int> Messages { get; } = [];
            internal List<(string Role, string Text, long Line)> Mirrors { get; } = [];
        }

        private readonly List<CodexStoredTurn> turns = [];
        private readonly HashSet<Guid> turnIds = [];
        private readonly HashSet<string> callIds = new(StringComparer.Ordinal);
        private Turn? active;
        private long projectedBytes;
        private long lastLine;

        internal void Observe(CodexRolloutRecord record, long line, DateTimeOffset at, int bytes)
        {
            lastLine = line;
            switch (record.Type)
            {
                case "event_msg":
                    Event(record.Payload, line, at);
                    break;
                case "turn_context":
                    // The embedding host supplies effective settings. Preserve source context
                    // in the original archive, and never hydrate runtime/tool authority from it.
                    if (active is null || OptionalTurnId(record.Payload, "turn_id", line) is { } id && id != active.Id ||
                        OptionalTurnId(record.Payload, "root_turn_id", line) is { } root && root != active.Root ||
                        record.Payload.TryGetProperty("realtime_active", out var realtime) &&
                            realtime.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                        throw Unsupported(line, "unmatched or realtime turn context");
                    break;
                case "token_usage_record":
                    // Historical usage records are not native financial admissions.
                    break;
                case "response_item":
                    if (record.Envelope.TryGetProperty("metadata", out var metadata) && metadata.ValueKind != JsonValueKind.Null)
                        throw Unsupported(line, "response harness metadata");
                    projectedBytes = checked(projectedBytes + bytes);
                    if (projectedBytes > MaximumDocumentBytes)
                        throw new InvalidDataException("Rust projected history exceeds the native journal bound.");
                    Item(record.Payload, line);
                    break;
                default:
                    throw Unsupported(line, record.Type);
            }
        }

        private void Event(JsonElement payload, long line, DateTimeOffset at)
        {
            var type = Text(payload, "type", line);
            switch (type)
            {
                case "task_started":
                case "turn_started":
                    var id = RequiredTurnId(payload, "turn_id", line);
                    var root = OptionalTurnId(payload, "root_turn_id", line) ?? id;
                    if (active is not null || !turnIds.Add(id) || turns.Count >= MaximumTurns)
                        throw Unsupported(line, "overlapping or repeated turn");
                    active = new(id, root, EventTime(payload, "started_at", at, line));
                    break;
                case "task_complete":
                case "turn_complete":
                case "turn_aborted":
                    if (active is null || RequiredTurnId(payload, "turn_id", line) != active.Id ||
                        OptionalTurnId(payload, "root_turn_id", line) is { } terminalRoot && terminalRoot != active.Root)
                        throw Unsupported(line, "unmatched terminal turn");
                    var status = type == "turn_aborted" ? "interrupted" :
                        payload.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ? "failed" : "completed";
                    if (OptionalText(payload, "last_agent_message", line, allowEmpty: true) is { Length: > 0 } last &&
                        !active.Messages.ContainsKey(("assistant", last)))
                        throw Unsupported(line, "terminal assistant text without a corresponding response item");
                    Close(status, EventTime(payload, "completed_at", at, line), line);
                    break;
                case "user_message":
                case "agent_message":
                    var turn = active ?? throw Unsupported(line, "message event outside a turn");
                    if (turn.Mirrors.Count >= MaximumItems)
                        throw new InvalidDataException("Rust turn has too many message mirrors.");
                    if (type == "user_message" && new[] { "images", "file_ids", "local_images", "audio",
                        "audio_file_ids", "local_audio", "text_elements" }.Any(name => HasValue(payload, name)) ||
                        type == "agent_message" && new[] { "moderation", "delivery", "questions", "memory_citation" }
                            .Any(name => HasValue(payload, name)))
                        throw Unsupported(line, "message event requires a dedicated acceptance or media reducer");
                    turn.Mirrors.Add((type == "user_message" ? "user" : "assistant",
                        Text(payload, "message", line, allowEmpty: true), line));
                    break;
                case "token_count":
                case "agent_reasoning":
                case "agent_reasoning_raw_content":
                case "patch_apply_end":
                case "mcp_tool_call_end":
                case "web_search_end":
                case "image_generation_end":
                    // These are presentation mirrors, not additional model input or authority.
                    break;
                default:
                    throw Unsupported(line, "event " + type);
            }
        }

        private void Item(JsonElement payload, long line)
        {
            var turn = active ?? throw Unsupported(line, "response item outside a turn");
            if (turn.Input.Count + turn.Output.Count >= MaximumItems)
                throw new InvalidDataException("Rust turn exceeds the native item bound.");
            var type = Text(payload, "type", line);
            if (payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var passthrough) &&
                passthrough.ValueKind != JsonValueKind.Null)
                throw Unsupported(line, "message acceptance metadata");
            var wire = StripRolloutAllocation(payload);
            switch (type)
            {
                case "message":
                    var role = Text(payload, "role", line);
                    if (role is not ("user" or "assistant" or "developer" or "system") ||
                        !payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array ||
                        content.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String ||
                            itemType.GetString() is not ("input_text" or "output_text") ||
                            !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String))
                        throw Unsupported(line, "non-text or malformed message");
                    var message = string.Concat(content.EnumerateArray().Select(item => item.GetProperty("text").GetString()));
                    turn.Messages[(role, message)] = turn.Messages.GetValueOrDefault((role, message)) + 1;
                    if (role != "assistant" && turn.Output.Count == 0) turn.Input.Add(wire);
                    else turn.Output.Add(wire);
                    break;
                case "reasoning":
                    turn.Output.Add(wire);
                    break;
                case "function_call":
                case "custom_tool_call":
                    var callId = Text(payload, "call_id", line);
                    var name = Text(payload, "name", line);
                    var input = Text(payload, type == "function_call" ? "arguments" : "input", line, allowEmpty: true);
                    var ns = OptionalText(payload, "namespace", line);
                    if (!ValidToken(callId) || !ValidToken(name) || ns is not null && !ValidToken(ns) ||
                        !callIds.Add(callId))
                        throw Unsupported(line, "invalid or duplicate historical tool identity");
                    if (type == "function_call")
                    {
                        using var arguments = JsonDocument.Parse(input);
                        ValidateUniqueObject(arguments.RootElement, line);
                    }
                    var call = new CodexToolCall(callId, name, input,
                        type == "function_call" ? CodexToolCallKind.Function : CodexToolCallKind.Custom) { Namespace = ns };
                    turn.Calls.Add(callId, (turn.Dispatches.Count, type));
                    // This is a historical replay fence; no inference admission or native tool
                    // execution is fabricated. An absent result remains explicitly unknown.
                    turn.Dispatches.Add(new(callId, "tool", Digest(CodexNativeGoalTools.Request(call)), "unknown"));
                    turn.Output.Add(wire);
                    break;
                case "function_call_output":
                case "custom_tool_call_output":
                    var resultId = Text(payload, "call_id", line);
                    if (!turn.Calls.TryGetValue(resultId, out var found) || found.Type + "_output" != type ||
                        turn.Dispatches[found.Index].State != "unknown" ||
                        !payload.TryGetProperty("output", out var result) ||
                        result.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                        throw Unsupported(line, "unmatched or duplicate historical tool output");
                    turn.Dispatches[found.Index] = turn.Dispatches[found.Index] with { State = "succeeded", Result = wire };
                    turn.Output.Add(wire);
                    break;
                default:
                    throw Unsupported(line, "response item " + type);
            }
        }

        private void Close(string status, DateTimeOffset completed, long line)
        {
            var turn = active!;
            foreach (var mirror in turn.Mirrors)
            {
                var key = (mirror.Role, mirror.Text);
                var count = turn.Messages.GetValueOrDefault(key);
                if (count <= 0) throw Unsupported(mirror.Line, "message event has no corresponding response item");
                turn.Messages[key] = count - 1;
            }
            if (completed < turn.Started || status == "completed" && turn.Dispatches.Any(x => x.State == "unknown"))
                throw Unsupported(line, "invalid terminal time or unresolved completed-turn tool");
            turns.Add(new(turn.Id, turn.Root, "rust-rollout-import", status, turn.Started,
                turn.Input.ToArray(), turn.Output.ToArray(), turn.Dispatches.ToArray(), completed));
            active = null;
        }

        internal CodexStoredTurn[] Finish(DateTimeOffset at)
        {
            if (active is not null)
            {
                if (!recoverInterruptedTail) throw Unsupported(lastLine, "unconfirmed active tail");
                Close("interrupted", at < active.Started ? active.Started : at, lastLine);
            }
            return turns.ToArray();
        }

        private static JsonElement StripRolloutAllocation(JsonElement payload)
        {
            if (!payload.TryGetProperty("id_allocation", out _)) return payload.Clone();
            // Rust rollout-only ID allocation is not a Responses wire field.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in payload.EnumerateObject())
                    if (property.Name != "id_allocation") property.WriteTo(writer);
                writer.WriteEndObject();
            }
            using var parsed = JsonDocument.Parse(stream.ToArray());
            return parsed.RootElement.Clone();
        }

        private static string Text(JsonElement payload, string name, long line, bool allowEmpty = false) =>
            payload.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
            (allowEmpty || !string.IsNullOrWhiteSpace(field.GetString()))
                ? field.GetString()! : throw Unsupported(line, "missing or invalid " + name);

        private static string? OptionalText(JsonElement payload, string name, long line, bool allowEmpty = false) =>
            !payload.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null ? null : Text(payload, name, line, allowEmpty);

        private static bool HasValue(JsonElement payload, string name) =>
            payload.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null &&
            (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() != 0);

        private static Guid RequiredTurnId(JsonElement payload, string name, long line) =>
            OptionalTurnId(payload, name, line) ?? throw Unsupported(line, "missing " + name);

        private static Guid? OptionalTurnId(JsonElement payload, string name, long line)
        {
            var value = OptionalText(payload, name, line);
            return value is null ? null : Guid.TryParse(value, out var id) && id != Guid.Empty
                ? id : throw Unsupported(line, "non-native turn identity");
        }

        private static DateTimeOffset EventTime(JsonElement payload, string name, DateTimeOffset fallback, long line)
        {
            if (!payload.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return fallback;
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var seconds))
                throw Unsupported(line, "invalid event timestamp");
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { throw Unsupported(line, "event timestamp outside range"); }
        }

        private static CodexRustRolloutCompatibilityException Unsupported(long line, string reason) => new(line, reason);
    }
}
