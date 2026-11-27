using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Original transcript event with provider fragment identity and timing.</summary>
/// <param name="ObservationId">Stable host observation ID, if supplied.</param>
/// <param name="EventId">Provider event ID, if supplied.</param>
/// <param name="InputFragmentId">Provider input fragment identity.</param>
/// <param name="InputStartMs">Input audio start in session milliseconds.</param>
/// <param name="InputEndMs">Input audio end in session milliseconds.</param>
/// <param name="ReceivedAfterConnectMs">Relative transport observation time.</param>
/// <param name="Text">Original delta or final text.</param>
/// <param name="Final">Whether the event finalizes an utterance.</param>
/// <param name="Payload">Unmodified provider event.</param>
public sealed record CodexRealtimeTranscriptFragment(string? ObservationId, string? EventId,
    string? InputFragmentId, double? InputStartMs, double? InputEndMs, double ReceivedAfterConnectMs,
    string Text, bool Final, JsonElement Payload);

/// <summary>One ordered voice utterance. A tail may contain incomplete utterances.</summary>
/// <param name="Id">Session-scoped stable identity when the source has an observation ID.</param>
/// <param name="Role">User or assistant.</param>
/// <param name="Text">Reconciled transcript.</param>
/// <param name="Final">Whether a final has been observed.</param>
/// <param name="Fragments">Original accepted transcript events, in arrival order.</param>
public sealed record CodexRealtimeTranscriptEntry(string Id, string Role, string Text, bool Final,
    CodexRealtimeTranscriptFragment[] Fragments);

/// <summary>Transcript snapshot captured at a handoff boundary.</summary>
/// <param name="Id">Delegation identity.</param>
/// <param name="InputTranscript">Original handoff input.</param>
/// <param name="Entries">Ordered snapshot, including non-duplicate handoff input.</param>
/// <param name="Payload">Original handoff event.</param>
/// <param name="IsNew">False for an already captured handoff; never re-admit a replay.</param>
public sealed record CodexRealtimeHandoffSnapshot(string Id, string InputTranscript,
    CodexRealtimeTranscriptEntry[] Entries, JsonElement Payload, bool IsNew);

/// <summary>Session-scoped transcript reducer matching Rust's V1/V2 adjacent speaker runs and
/// V3 interleaved utterances. Retain this instance across transport reconnects. This is an
/// in-memory reducer; durable history and admission belong to the native thread journal.</summary>
public sealed class CodexRealtimeTranscriptAccumulator
{
    /// <summary>Default V3 completed transcript extraction threshold, measured in UTF-8 bytes.</summary>
    public const int DefaultCompletedThreshold = 4 * 1024;
    private const int MaximumReceipts = 32768;
    private const int MaximumHandoffs = 1024;
    private const int MaximumHandoffBytes = 16_777_216;
    private readonly object gate = new();
    private readonly string protocol;
    private readonly int maximumActiveBytes;
    private readonly bool canonicalLegacySegments;
    private bool IndependentSegments => protocol == "v3" || canonicalLegacySegments;
    private readonly List<Entry> entries = [];
    private readonly Dictionary<string, string> receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexRealtimeHandoffSnapshot> handoffs = new(StringComparer.Ordinal);
    private readonly HashSet<string> flushedUserInputs = new(StringComparer.Ordinal);
    // Only active utterance prefixes are retained to reconcile a delayed full final after
    // a presentation boundary. They are not emitted again or counted as new speech.
    private readonly Dictionary<string, string> presentationPrefixes = new(StringComparer.Ordinal);
    private int activeBytes;
    private int handoffBytes;
    private bool newInput;
    private bool newOutput;
    private string? lastFinalRole;

    private sealed class Entry(string id, string role)
    {
        public string Id { get; } = id;
        public string Role { get; } = role;
        public string Text { get; set; } = "";
        public bool Final { get; set; }
        public List<CodexRealtimeTranscriptFragment> Fragments { get; } = [];
        public int Bytes { get; set; }
        public CodexRealtimeTranscriptEntry Snapshot() => new(Id, Role, Text, Final, Fragments.ToArray());
    }

    /// <summary>Creates one reducer per logical session, not per socket.</summary>
    /// <param name="protocol">V1, V2 or V3 (lowercase).</param>
    /// <param name="maximumActiveBytes">Hard bound for active text plus raw event payloads.</param>
    public CodexRealtimeTranscriptAccumulator(string protocol, int maximumActiveBytes = 4_194_304)
        : this(protocol, maximumActiveBytes, canonicalLegacySegments: false) { }

    internal CodexRealtimeTranscriptAccumulator(string protocol, int maximumActiveBytes, bool canonicalLegacySegments)
    {
        if (protocol is not ("v1" or "v2" or "v3")) throw new ArgumentException("Unsupported realtime protocol.", nameof(protocol));
        if (maximumActiveBytes is <= 0 or > MaximumHandoffBytes) throw new ArgumentOutOfRangeException(nameof(maximumActiveBytes));
        this.protocol = protocol;
        this.maximumActiveBytes = maximumActiveBytes;
        this.canonicalLegacySegments = canonicalLegacySegments;
    }

    /// <summary>Observes a typed or raw wire event. Returns a handoff snapshot only for a
    /// delegation event. Stable identities deduplicate replays; identity-free repeated speech
    /// is intentionally preserved because it cannot be distinguished from a new utterance.</summary>
    /// <param name="source">Provider event, retained unchanged for transcript fragments.</param>
    /// <param name="observationId">Optional durable host ID to reuse when replaying the same observation.</param>
    public CodexRealtimeHandoffSnapshot? Observe(CodexRealtimeEvent source, string? observationId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Realtime event must be an object.", nameof(source));
        ValidateId(observationId);
        lock (gate)
        {
            var transcript = source.Transcript ?? ReadLegacyTranscript(source);
            var delegation = source.Delegation;
            var inputBoundary = source.Type == "input_audio_buffer.speech_started";
            var outputBoundary = source.Type == "response.created";
            if (transcript is null && delegation is null && !inputBoundary && !outputBoundary) return null;

            var eventId = CodexRealtimeProtocol.Text(source.Payload, "event_id");
            ValidateId(eventId);
            var identities = Identities(source, transcript, eventId, observationId);
            var duplicate = CheckReceipts(identities);
            if (delegation is not null && handoffs.TryGetValue(delegation.Id, out var existing))
            {
                if (existing.InputTranscript != delegation.Transcript)
                    throw new InvalidOperationException("Realtime handoff ID was reused for different input.");
                RecordReceipts(identities);
                return Copy(existing, false);
            }
            if (duplicate) { RecordReceipts(identities); return null; }

            CodexRealtimeHandoffSnapshot? captured = null;
            if (transcript is not null) ObserveTranscript(source, transcript, eventId, observationId, identities);
            else if (delegation is not null) captured = CaptureHandoff(source, delegation, identities);
            else
            {
                // Rust's public legacy transcript reducer observes normalized deltas and
                // finals, not raw GA response/speech boundary receipts. Those receipts must
                // not replace an unfinished public segment before its actual final.
                if (!canonicalLegacySegments)
                {
                    if (inputBoundary) { ForgetPresentationPrefix("user"); newInput = true; }
                    if (outputBoundary) { ForgetPresentationPrefix("assistant"); newOutput = true; }
                    lastFinalRole = null;
                }
            }
            RecordReceipts(identities);
            return captured;
        }
    }

    /// <summary>Current ordered transcript without consuming it.</summary>
    public CodexRealtimeTranscriptEntry[] Snapshot()
    {
        lock (gate) return entries.Select(e => e.Snapshot()).ToArray();
    }

    /// <summary>After a V3 final, extracts the completed prefix only when its active text
    /// exceeds the UTF-8 threshold. Keeps the other open utterance and everything after it.</summary>
    public CodexRealtimeTranscriptEntry[] TakeCompleted(int threshold = DefaultCompletedThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        lock (gate)
        {
            if (protocol != "v3" || lastFinalRole is null ||
                entries.Sum(e => (long)Utf8(e.Role) + Utf8(e.Text) + 3) <= threshold) return [];
            var openRole = lastFinalRole == "user" && !newOutput ? "assistant" :
                lastFinalRole == "assistant" && !newInput ? "user" : null;
            var end = openRole is null ? entries.Count : entries.FindLastIndex(e => e.Role == openRole);
            if (end < 0) end = entries.Count;
            if (end == 0) return [];
            var completed = entries.Take(end).ToArray();
            var flushed = completed.Where(e => e.Role == "user").Select(e => Hash(e.Text.Trim())).ToArray();
            if (flushedUserInputs.Count + flushed.Count(h => !flushedUserInputs.Contains(h)) > MaximumReceipts)
                throw new InvalidOperationException("Realtime flushed transcript identity capacity exhausted.");
            foreach (var digest in flushed) flushedUserInputs.Add(digest);
            activeBytes -= completed.Sum(e => e.Bytes);
            entries.RemoveRange(0, end);
            return completed.Select(e => e.Snapshot()).ToArray();
        }
    }

    /// <summary>Consumes all remaining transcript at the logical tail or handoff boundary.
    /// The caller must retain this result until its durable write is confirmed.</summary>
    public CodexRealtimeTranscriptEntry[] TakeTail()
    {
        lock (gate)
        {
            var result = entries.Select(e => e.Snapshot()).ToArray();
            ClearActive();
            return result;
        }
    }

    // Seal visible segments without forgetting an utterance whose provider final may still
    // include the already sealed prefix. A handoff later consumes all associated frame IDs.
    internal CodexRealtimeTranscriptEntry[] TakePresentationBoundary()
    {
        lock (gate)
        {
            var result = entries.Select(e => e.Snapshot()).ToArray();
            foreach (var group in entries.GroupBy(e => e.Role))
            {
                var last = group.Last();
                foreach (var completed in group.Where(e => !ReferenceEquals(e, last)))
                    if (completed.Role == "user") RememberPresentedUser(completed.Text);
                var prefix = presentationPrefixes.GetValueOrDefault(last.Role, "") + last.Text;
                if (Utf8(prefix) > maximumActiveBytes)
                    throw new InvalidOperationException("Realtime transcript prefix exceeds its bound.");
                if (last.Final || (last.Role == "user" ? newInput : newOutput))
                {
                    if (last.Role == "user") RememberPresentedUser(prefix);
                    presentationPrefixes.Remove(last.Role);
                }
                else presentationPrefixes[last.Role] = prefix;
            }
            entries.Clear();
            activeBytes = 0;
            lastFinalRole = null;
            return result;
        }
    }

    private void RememberPresentedUser(string text)
    {
        if (flushedUserInputs.Count >= MaximumReceipts && !flushedUserInputs.Contains(Hash(text.Trim())))
            throw new InvalidOperationException("Realtime transcript identity capacity exhausted.");
        flushedUserInputs.Add(Hash(text.Trim()));
    }

    private void ForgetPresentationPrefix(string role)
    {
        if (!presentationPrefixes.Remove(role, out var prefix)) return;
        if (role == "user") RememberPresentedUser(prefix);
    }

    private void ObserveTranscript(CodexRealtimeEvent source, CodexRealtimeTranscript fragment,
        string? eventId, string? observationId, IReadOnlyList<(string Key, string Digest)> identities)
    {
        if (fragment.Role is not ("user" or "assistant")) throw new InvalidOperationException("Invalid transcript role.");
        var forceNew = fragment.Role == "user" ? newInput : newOutput;
        Entry? entry = null;
        if (!forceNew)
            entry = IndependentSegments ? entries.FindLast(e => e.Role == fragment.Role) :
                entries.LastOrDefault() is { } last && last.Role == fragment.Role ? last : null;

        var text = fragment.Text;
        var prefix = forceNew ? "" : presentationPrefixes.GetValueOrDefault(fragment.Role, "");
        if (fragment.Final && prefix.Length != 0)
        {
            // A full final can extend, never rewrite, already committed speech.
            text = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : "";
            if (fragment.Role == "user") RememberPresentedUser(prefix +
                (text.StartsWith(entry?.Text ?? "", StringComparison.Ordinal) ? text : entry?.Text ?? ""));
            presentationPrefixes.Remove(fragment.Role);
        }
        if (text.Length == 0 && !fragment.Final) return;
        if (entry is null && text.Length == 0)
        {
            SetBoundary(fragment.Role, fragment.Final);
            return;
        }

        var oldText = entry?.Text ?? "";
        var nextText = !fragment.Final ? oldText + text :
            text.Length == 0 ? oldText :
            entry is null || !IndependentSegments || text.StartsWith(oldText, StringComparison.Ordinal) ? text : oldText;
        // Count raw payload and reconciled text independently; never truncate Unicode or mutate on overflow.
        var rawBytes = Utf8(source.Payload.GetRawText());
        var increase = checked(rawBytes + Utf8(nextText) - Utf8(oldText) + (entry is null ? Utf8(fragment.Role) + 64 : 0));
        if ((long)activeBytes + increase > maximumActiveBytes)
            throw new InvalidOperationException("Realtime active transcript capacity exhausted; persist history before accepting more speech.");

        var frame = new CodexRealtimeTranscriptFragment(observationId, eventId, fragment.InputFragmentId,
            fragment.InputStartMs, fragment.InputEndMs, source.ReceivedAfterConnectMs, text, fragment.Final, source.Payload.Clone());
        if (entry is null)
        {
            entry = new(EntryId(identities), fragment.Role);
            entries.Add(entry);
        }
        entry.Text = nextText;
        entry.Final = fragment.Final;
        entry.Fragments.Add(frame);
        entry.Bytes += increase;
        activeBytes += increase;
        SetBoundary(fragment.Role, fragment.Final);
    }

    private void SetBoundary(string role, bool final)
    {
        if (role == "user") newInput = final && IndependentSegments;
        else newOutput = final && IndependentSegments;
        lastFinalRole = final ? role : null;
    }

    private CodexRealtimeHandoffSnapshot CaptureHandoff(CodexRealtimeEvent source, CodexDelegation delegation,
        IReadOnlyList<(string Key, string Digest)> identities)
    {
        ValidateId(delegation.Id);
        var input = delegation.Transcript.Trim();
        var userPrefix = presentationPrefixes.GetValueOrDefault("user", "");
        var currentUser = entries.LastOrDefault(e => e.Role == "user")?.Text ?? "";
        var append = input.Length > 0 && !entries.Any(e => e.Role == "user" && e.Text.Trim() == input)
            && (userPrefix + currentUser).Trim() != input && !flushedUserInputs.Contains(Hash(input));
        var extra = append ? Utf8(input) + 64 : 0;
        var bytes = checked(activeBytes + extra + Utf8(source.Payload.GetRawText()));
        if (handoffs.Count >= MaximumHandoffs || (long)handoffBytes + bytes > MaximumHandoffBytes)
            throw new InvalidOperationException("Realtime handoff snapshot capacity exhausted; durable recovery is required.");
        var snapshot = entries.Select(e => e.Snapshot()).ToList();
        if (append) snapshot.Add(new(EntryId(identities), "user", input, true, []));
        var captured = new CodexRealtimeHandoffSnapshot(delegation.Id, delegation.Transcript, snapshot.ToArray(), source.Payload.Clone(), true);
        handoffs.Add(delegation.Id, captured);
        handoffBytes += bytes;
        ClearActive();
        return Copy(captured, true);
    }

    private void ClearActive()
    {
        entries.Clear();
        activeBytes = 0;
        flushedUserInputs.Clear();
        presentationPrefixes.Clear();
        newInput = newOutput = true;
        lastFinalRole = null;
    }

    private static CodexRealtimeHandoffSnapshot Copy(CodexRealtimeHandoffSnapshot snapshot, bool isNew)
        => snapshot with { IsNew = isNew, Entries = snapshot.Entries.Select(e => e with { Fragments = e.Fragments.ToArray() }).ToArray() };

    private static List<(string Key, string Digest)> Identities(CodexRealtimeEvent source, CodexRealtimeTranscript? transcript,
        string? eventId, string? observationId)
    {
        var result = new List<(string, string)>();
        if (eventId is not null || observationId is not null)
        {
            var digest = Hash(source.Payload.GetRawText());
            if (eventId is not null) result.Add(("event:" + eventId, digest));
            if (observationId is not null) result.Add(("observation:" + observationId, digest));
        }
        string? fragmentId = null;
        if (source.Type == "input_transcript.added") fragmentId = transcript?.InputFragmentId;
        else if (transcript?.Final == true)
        {
            fragmentId = CodexRealtimeProtocol.Text(source.Payload, "item_id");
            if (source.Payload.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Object)
                fragmentId ??= CodexRealtimeProtocol.Text(turn, "id");
        }
        ValidateId(fragmentId);
        if (fragmentId is not null && transcript is not null)
        {
            var index = source.Payload.TryGetProperty("content_index", out var part) ? part.GetRawText() : "";
            var digest = Hash(string.Join("\n", transcript.Role, transcript.Text,
                transcript.InputStartMs?.ToString("R", CultureInfo.InvariantCulture),
                transcript.InputEndMs?.ToString("R", CultureInfo.InvariantCulture)));
            result.Add(("fragment:" + source.Type + ":" + fragmentId + ":" + index, digest));
        }
        return result;
    }

    private bool CheckReceipts(IReadOnlyList<(string Key, string Digest)> identities)
    {
        var duplicate = false;
        var count = 0;
        foreach (var (key, digest) in identities)
        {
            if (receipts.TryGetValue(key, out var previous))
            {
                if (previous != digest) throw new InvalidOperationException("Realtime observation identity was reused for different content.");
                duplicate = true;
            }
            else count++;
        }
        if (receipts.Count + count > MaximumReceipts)
            throw new InvalidOperationException("Realtime observation identity capacity exhausted; durable recovery is required.");
        return duplicate;
    }

    private void RecordReceipts(IReadOnlyList<(string Key, string Digest)> identities)
    {
        foreach (var (key, digest) in identities) receipts.TryAdd(key, digest);
    }

    private static string EntryId(IReadOnlyList<(string Key, string Digest)> identities)
        => identities.Count == 0 ? Guid.NewGuid().ToString("N") : Hash(identities[0].Key);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int Utf8(string value) => Encoding.UTF8.GetByteCount(value);
    private static void ValidateId(string? id)
    {
        if (id is not null && (id.Length is 0 or > 512 || string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl)))
            throw new ArgumentException("Invalid realtime observation identity.");
    }

    // Legacy aliases are normalized here until the wire parser supplies the corresponding typed view.
    private CodexRealtimeTranscript? ReadLegacyTranscript(CodexRealtimeEvent source)
    {
        if (protocol == "v3") return null;
        var role = source.Type switch
        {
            "conversation.input_transcript.delta" or "conversation.item.input_audio_transcription.delta" or
                "conversation.input_transcript.turn_marked" => "user",
            "conversation.output_transcript.delta" => "assistant",
            _ => null,
        };
        if (role is null) return null;
        var final = source.Type == "conversation.input_transcript.turn_marked";
        var text = CodexRealtimeProtocol.Text(source.Payload, final ? "transcript" : "delta");
        return text is null ? null : new(role, text, final) { ReplaceFinalText = final };
    }
}
