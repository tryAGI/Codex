using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Source of a canonical realtime contextual user message.</summary>
public enum CodexRealtimeDelegationSource
{
    /// <summary>A provider-requested delegation.</summary>
    Handoff,
    /// <summary>Remaining transcript at a session boundary.</summary>
    TranscriptTailFlush,
}

/// <summary>One transcript row and its optional durable source identity.</summary>
public sealed record CodexRealtimeDelegationEntry(string? Id, string Role, string Text);

/// <summary>Contextual input and the exact retained source identities, in transcript order.</summary>
public sealed record CodexRealtimeDelegationInput(string Text, IReadOnlyList<string> SourceIds)
{
    /// <summary>Rust limits the escaped input and legacy transcript fields to 4 KiB each.</summary>
    public const int FieldBytes = 4096;
    private const string Marker = "…";

    /// <summary>Produces a native Responses user message. Context remains data, not a tool or authorization.</summary>
    public JsonElement ToResponseItem()
    {
        using var document = JsonDocument.Parse(new JsonObject { ["role"] = "user", ["content"] = Text }.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>Render the pinned Rust contextual user fragment, with input head and transcript tail retention.
    /// Canonical history preserves the transcript; legacy history retains a bounded suffix.</summary>
    public static string Render(string input, string? transcriptDelta = null,
        CodexRealtimeDelegationSource source = CodexRealtimeDelegationSource.Handoff, bool preserveTranscript = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        var builder = new StringBuilder("<realtime_delegation>\n");
        if (source == CodexRealtimeDelegationSource.TranscriptTailFlush)
            builder.Append("  <source>transcript_tail_flush</source>\n");
        else if (source != CodexRealtimeDelegationSource.Handoff)
            throw new ArgumentOutOfRangeException(nameof(source));
        builder.Append("  <input>").Append(Bound(Escape(input), false)).Append("</input>\n");
        if (!string.IsNullOrEmpty(transcriptDelta))
        {
            var escaped = Escape(transcriptDelta);
            builder.Append("  <transcript_delta>").Append(preserveTranscript ? escaped : Bound(escaped, true))
                .Append("</transcript_delta>\n");
        }
        return builder.Append("</realtime_delegation>").ToString();
    }

    /// <summary>Derive source attribution from exactly the retained, escaped transcript. A transcript-only
    /// handoff uses the same transcript as the input prefix and contextual suffix.</summary>
    public static CodexRealtimeDelegationInput? FromEntries(string input,
        IReadOnlyList<CodexRealtimeDelegationEntry> entries,
        CodexRealtimeDelegationSource source = CodexRealtimeDelegationSource.Handoff, bool preserveTranscript = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entries);
        if (input.Length == 0 && entries.Count == 0) return null;
        if (entries.Count > 32768) throw new ArgumentException("Realtime transcript exceeds its entry bound.", nameof(entries));
        var transcript = new StringBuilder();
        var ranges = new List<(string? Id, int Start, int End)>();
        var escapedLength = 0;
        string? inputSource = null;
        foreach (var entry in entries)
        {
            if (entry.Role is not ("user" or "assistant")) throw new ArgumentException("Unsupported realtime transcript role.", nameof(entries));
            if (transcript.Length > 0) { transcript.Append('\n'); escapedLength++; }
            var line = entry.Role + ": " + entry.Text;
            var start = escapedLength;
            escapedLength = checked(escapedLength + Encoding.UTF8.GetByteCount(Escape(line)));
            if (escapedLength > CodexThreadStore.MaximumDocumentBytes)
                throw new ArgumentException("Realtime transcript exceeds its byte bound.", nameof(entries));
            transcript.Append(line);
            ranges.Add((entry.Id, start, escapedLength));
            if (entry.Role == "user" && entry.Text.Trim() == input.Trim()) inputSource = entry.Id;
        }
        var raw = transcript.ToString();
        var escapedTranscript = Escape(raw);
        var prefixEnd = Encoding.UTF8.GetByteCount(Retain(escapedTranscript, false));
        var suffixStart = preserveTranscript ? 0 : escapedLength - Encoding.UTF8.GetByteCount(Retain(escapedTranscript, true));
        var sources = ranges.Where(x => x.Id is not null && (x.End > suffixStart ||
            input.Length == 0 && x.Start < prefixEnd || x.Id == inputSource)).Select(x => x.Id!).ToArray();
        return new(Render(input.Length == 0 ? raw : input, raw, source, preserveTranscript), sources);
    }

    private static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Bound(string value, bool tail)
    {
        if (Encoding.UTF8.GetByteCount(value) <= FieldBytes) return value;
        var retained = Retain(value, tail);
        return tail ? Marker + retained : retained + Marker;
    }

    private static string Retain(string value, bool tail)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= FieldBytes) return value;
        const int retainedBytes = FieldBytes - 3;
        if (tail)
        {
            var start = bytes.Length - retainedBytes;
            while ((bytes[start] & 0xC0) == 0x80) start++;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }
        var end = retainedBytes;
        while ((bytes[end] & 0xC0) == 0x80) end--;
        return Encoding.UTF8.GetString(bytes, 0, end);
    }
}
