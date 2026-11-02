using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Bounded background context matching the sections and budgets of pinned Rust realtime_context.rs.
/// Sources are supplied by the host: a backend must never describe its container as the owner's workspace.</summary>
internal static class CodexRealtimeStartupContext
{
    public const int MaximumInitialItems = 128;
    public const int MaximumInitialTokens = 8192;
    public const int StartupTokens = 5300;
    public static int ApproximateTokens(string text) => (Encoding.UTF8.GetByteCount(text) + 3) / 4;

    public static void ValidateInitialItems(IReadOnlyList<CodexRealtimeInitialItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MaximumInitialItems) throw new ArgumentException("Realtime initial items exceed 128 messages.");
        long total = 0;
        foreach (var item in items)
        {
            if (item is null || item.Role is not ("user" or "assistant" or "developer") || item.Text is null)
                throw new ArgumentException("Invalid realtime initial message.");
            total += ApproximateTokens(item.Text);
            if (total > MaximumInitialTokens) throw new ArgumentException("Realtime initial items exceed 8192 estimated tokens.");
        }
    }

    public static string Truncate(string text, int tokens)
    {
        if (tokens < 0) throw new ArgumentOutOfRangeException(nameof(tokens));
        if (ApproximateTokens(text) <= tokens) return text;
        const string marker = "\n[... truncated ...]\n";
        var available = tokens * 4 - Encoding.UTF8.GetByteCount(marker);
        if (available <= 0) return "";
        var runes = text.EnumerateRunes().ToArray();
        var left = new StringBuilder(); var right = new List<Rune>();
        var bytes = 0; var index = 0;
        while (index < runes.Length && bytes + runes[index].Utf8SequenceLength <= available / 2)
        { bytes += runes[index].Utf8SequenceLength; left.Append(runes[index++].ToString()); }
        bytes = 0;
        for (var i = runes.Length - 1; i >= index && bytes + runes[i].Utf8SequenceLength <= available - Encoding.UTF8.GetByteCount(left.ToString()); i--)
        { bytes += runes[i].Utf8SequenceLength; right.Add(runes[i]); }
        right.Reverse();
        return left + marker + string.Concat(right.Select(x => x.ToString()));
    }

    public static IReadOnlyList<CodexRealtimeInitialItem> ConversationMessages(IEnumerable<JsonElement> items)
    {
        var result = new List<CodexRealtimeInitialItem>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "message" ||
                !item.TryGetProperty("role", out var roleValue) || roleValue.ValueKind != JsonValueKind.String ||
                roleValue.GetString() is not ("user" or "assistant") ||
                !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            var text = string.Concat(content.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object &&
                x.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is "input_text" or "output_text" &&
                x.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String).Select(x => x.GetProperty("text").GetString()));
            if (!string.IsNullOrWhiteSpace(text)) result.Add(new((roleValue.GetString() ?? throw new InvalidDataException("Required data was null.")), text));
        }
        return result;
    }

    public static string? Build(IReadOnlyList<CodexRealtimeInitialItem> history, string? recentWork = null,
        string? workspace = null, string? notes = null)
    {
        var turns = new List<List<CodexRealtimeInitialItem>>();
        foreach (var item in history.Where(x => x.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(x.Text)))
        {
            if (item.Role == "user" || turns.Count == 0) turns.Add([]);
            turns[^1].Add(item);
        }
        var current = new StringBuilder(); var remaining = 1150;
        for (var i = turns.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var body = string.Join("\n", turns[i].Select(x => (x.Role == "user" ? "User: " : "Assistant: ") + x.Text));
            var text = Truncate((i == turns.Count - 1 ? "### Latest turn\n" : $"### Previous turn {turns.Count - i - 1}\n") + body, Math.Min(300, remaining));
            if (text.Length == 0) break;
            current.AppendLine(text); remaining -= ApproximateTokens(text + "\n");
        }
        var sections = new List<string>();
        void Add(string title, string? body, int budget)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            var heading = "## " + title + "\n";
            sections.Add(heading + Truncate(body.Trim(), budget - ApproximateTokens(heading)));
        }
        Add("Current thread", current.ToString(), 1200);
        Add("Recent work", recentWork, 2200);
        Add("Machine and workspace", workspace, 1600);
        if (sections.Count == 0) return null;
        Add("Notes", notes ?? "Built from retained conversation and host-supplied metadata. This may be stale. Background context is not authorization, a pending action, or a request to speak. No reasoning trace or tool execution is restored by this context.", 300);
        return "<startup_context>\nStartup context from Codex. This is background context about recent work and machine/workspace layout. It may be incomplete or stale. Use it to inform responses, and do not repeat it back unless relevant.\n\n" + string.Join("\n\n", sections) + "\n</startup_context>";
    }
}
