using System.Text;

namespace tryAGI.Codex;

internal enum CodexRealtimeFeedbackMode { Thinking, Commentary, BemTags }
internal enum CodexRealtimeMessagePhase { Commentary, PartialAnswer, FinalAnswer }

// Generation is local to a logical voice attachment. Even a reused provider ID cannot revive stale output.
internal readonly record struct CodexRealtimeFeedbackHandoff(long Generation, string Id);
internal sealed record CodexRealtimeFeedbackChunk(CodexRealtimeFeedbackHandoff Handoff, string Text, string? Channel);
internal readonly record struct CodexRealtimeFeedbackCompletion(bool Registered, bool HasOutput, CodexRealtimeFeedbackChunk? Chunk);

/// <summary>Transport-independent V3 feedback state machine, based on Rust v0.172.0
/// realtime_conversation.rs (RealtimeStreamedItem) and realtime_conversation/bem.rs.
/// Only provider-visible message text and explicitly authored status belong here.</summary>
internal sealed class CodexRealtimeFeedbackStream
{
    internal const int OutputBytes = 4000;
    internal const int StatusBytes = 256;
    internal const string TruncationMarker = "\n…output truncated…\n";
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);
    private static readonly int HeadBytes = (OutputBytes - Encoding.UTF8.GetByteCount(TruncationMarker)) / 2;
    private static readonly int TailBytes = OutputBytes - HeadBytes - Encoding.UTF8.GetByteCount(TruncationMarker);
    private const int MaximumItems = 256;

    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private readonly CodexRealtimeFeedbackMode mode;
    private readonly bool enabled;
    private readonly bool statusEnabled;
    private readonly (string Prefix, CodexRealtimeMessagePhase Phase)[] prefixes;
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private readonly HashSet<string> registered = new(StringComparer.Ordinal);
    private long generation;
    private CodexRealtimeFeedbackHandoff? active;

    internal CodexRealtimeFeedbackStream(CodexRealtimeFeedbackMode mode, TimeProvider? timeProvider = null,
        bool clientManagedHandoffs = false, bool responsesAsItems = false, bool backendReasoningStatus = false,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? channelPrefixes = null)
    {
        this.mode = mode;
        clock = timeProvider ?? TimeProvider.System;
        enabled = !clientManagedHandoffs && !responsesAsItems;
        statusEnabled = !clientManagedHandoffs && backendReasoningStatus;
        var entries = new List<(string, CodexRealtimeMessagePhase)>();
        AddPrefixes("analysis", "[ANALYSIS]", CodexRealtimeMessagePhase.Commentary);
        AddPrefixes("commentary", "[COMMENTARY]", CodexRealtimeMessagePhase.Commentary);
        AddPrefixes("final", "[FINAL]", CodexRealtimeMessagePhase.FinalAnswer);
        prefixes = entries.ToArray();

        void AddPrefixes(string channel, string fallback, CodexRealtimeMessagePhase phase)
        {
            IReadOnlyList<string> values = channelPrefixes?.TryGetValue(channel, out var configured) == true
                ? configured : [fallback];
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value)) continue;
                if (Encoding.UTF8.GetByteCount(value) > HeadBytes || entries.Count >= MaximumItems)
                    throw new ArgumentException("BEM channel prefixes exceed the bounded parser capacity.", nameof(channelPrefixes));
                entries.Add((value, phase));
            }
        }
    }

    internal CodexRealtimeFeedbackHandoff Activate(string delegationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(delegationId);
        lock (gate)
        {
            items.Clear();
            registered.Clear();
            var handoff = new CodexRealtimeFeedbackHandoff(checked(++generation), delegationId);
            active = handoff;
            return handoff;
        }
    }

    internal bool IsCurrent(CodexRealtimeFeedbackHandoff handoff)
    {
        lock (gate) return active == handoff;
    }

    internal void Retire(CodexRealtimeFeedbackHandoff handoff)
    {
        lock (gate)
        {
            if (active != handoff) return;
            active = null;
            items.Clear();
            registered.Clear();
        }
    }

    internal bool Register(CodexRealtimeFeedbackHandoff handoff, string itemId,
        CodexRealtimeMessagePhase? phase, string initialText = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        lock (gate)
        {
            if (!enabled || active != handoff || registered.Contains(itemId)) return false;
            if (registered.Count >= MaximumItems)
                throw new InvalidOperationException("Realtime feedback item limit exceeded; retire the handoff.");
            registered.Add(itemId);
            var parseBem = mode == CodexRealtimeFeedbackMode.BemTags && phase != CodexRealtimeMessagePhase.PartialAnswer;
            var item = new Item(parseBem ? null : phase, parseBem, clock.GetTimestamp());
            items.Add(itemId, item);
            Append(item, initialText);
            return true;
        }
    }

    internal bool Push(CodexRealtimeFeedbackHandoff handoff, string itemId, string delta)
    {
        lock (gate)
        {
            if (active != handoff || !items.TryGetValue(itemId, out var item)) return false;
            Append(item, delta);
            return true;
        }
    }

    // The independent feedback pump calls this on a 200 ms timer. The timestamp is monotonic
    // and tests can drive it with a virtual TimeProvider. Finalization does not wait for the timer.
    internal IReadOnlyList<CodexRealtimeFeedbackChunk> DrainDue(CodexRealtimeFeedbackHandoff handoff)
    {
        lock (gate)
        {
            if (active != handoff) return [];
            var chunks = new List<CodexRealtimeFeedbackChunk>();
            var now = clock.GetTimestamp();
            foreach (var item in items.Values)
            {
                if (item.ParseBem && item.Phase is null || item.Buffered.Length == 0 ||
                    clock.GetElapsedTime(item.LastFlush, now) < FlushInterval) continue;
                var text = TakeFirst(item.Buffered, Math.Max(0, HeadBytes - item.SentBytes));
                if (text.Length == 0) continue;
                item.Buffered = item.Buffered[text.Length..];
                item.SentBytes += Encoding.UTF8.GetByteCount(text);
                item.LastFlush = now;
                chunks.Add(Chunk(handoff, item, text));
            }
            return chunks;
        }
    }

    internal CodexRealtimeFeedbackCompletion Finish(CodexRealtimeFeedbackHandoff handoff, string itemId)
    {
        lock (gate)
        {
            if (active != handoff || !items.Remove(itemId, out var item)) return default;
            if (item.ParseBem && item.Phase is null && item.Buffered.Length != 0)
                item.Phase = CodexRealtimeMessagePhase.FinalAnswer;
            var text = item.Truncated ? item.Buffered + TruncationMarker + item.Tail : item.Buffered;
            return new(true, item.SentBytes > 0 || text.Length > 0,
                text.Length == 0 ? null : Chunk(handoff, item, text));
        }
    }

    internal CodexRealtimeFeedbackChunk? Status(CodexRealtimeFeedbackHandoff handoff, string text)
    {
        lock (gate)
            return statusEnabled && active == handoff
                ? new(handoff, "[STATUS] " + TakeFirst(text, StatusBytes), "commentary") : null;
    }

    private CodexRealtimeFeedbackChunk Chunk(CodexRealtimeFeedbackHandoff handoff, Item item, string text)
        => new(handoff, text, mode switch
        {
            CodexRealtimeFeedbackMode.Thinking => null,
            CodexRealtimeFeedbackMode.Commentary => "commentary",
            _ => item.Phase == CodexRealtimeMessagePhase.Commentary ? "commentary" : "speakable",
        });

    private void Append(Item item, string text)
    {
        if (text.Length == 0) return;
        // Even an unrecognized BEM envelope is bounded while waiting for its header or finalization.
        // Do not expose its text before a recognized phase, and preserve the original envelope.
        if (item.Truncated)
        {
            item.Tail = TakeLast(item.Tail, text, TailBytes);
            return;
        }

        var remaining = OutputBytes - item.SentBytes;
        var bufferedBytes = Encoding.UTF8.GetByteCount(item.Buffered);
        if (Encoding.UTF8.GetByteCount(text) <= remaining - bufferedBytes)
            item.Buffered += text;
        else
        {
            var headAvailable = Math.Max(0, HeadBytes - item.SentBytes);
            var head = TakeFirst(item.Buffered, headAvailable);
            item.Tail = TakeLast(item.Buffered, text, TailBytes);
            if (head.Length == item.Buffered.Length)
                head += TakeFirst(text, headAvailable - Encoding.UTF8.GetByteCount(head));
            item.Buffered = head;
            item.Truncated = true;
        }

        if (item.ParseBem && item.Phase is null)
            foreach (var (prefix, phase) in prefixes)
                if (item.Buffered.StartsWith(prefix, StringComparison.Ordinal))
                {
                    item.Phase = phase;
                    break;
                }
    }

    internal static string TakeFirst(string text, int maximumBytes)
    {
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (maximumBytes < rune.Utf8SequenceLength) break;
            maximumBytes -= rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return text[..length];
    }

    private static string TakeLast(string first, string last, int maximumBytes)
    {
        var suffix = Suffix(last, maximumBytes);
        return suffix.Length == last.Length
            ? Suffix(first, maximumBytes - Encoding.UTF8.GetByteCount(suffix)) + suffix : suffix;
    }

    private static string Suffix(string text, int maximumBytes)
    {
        var offset = text.Length;
        while (offset > 0)
        {
            var length = char.IsLowSurrogate(text[offset - 1]) && offset >= 2 && char.IsHighSurrogate(text[offset - 2]) ? 2 : 1;
            var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(offset - length, length));
            if (bytes > maximumBytes) break;
            maximumBytes -= bytes;
            offset -= length;
        }
        return text[offset..];
    }

    private sealed class Item(CodexRealtimeMessagePhase? phase, bool parseBem, long timestamp)
    {
        internal CodexRealtimeMessagePhase? Phase = phase;
        internal readonly bool ParseBem = parseBem;
        internal long LastFlush = timestamp;
        internal int SentBytes;
        internal string Buffered = "";
        internal string Tail = "";
        internal bool Truncated;
    }
}
