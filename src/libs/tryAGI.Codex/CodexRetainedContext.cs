using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>One immutable raw history item and its optional host-side Rust-compatible metadata.</summary>
/// <param name="Item">Original Responses item, including opaque fields.</param>
/// <param name="Metadata">Harness metadata sidecar, never sent as a new tool or model item.</param>
public sealed record CodexRetainedContextEntry(JsonElement Item, JsonElement? Metadata = null);

/// <summary>Explicit feature and budget settings for remote compaction retained history.</summary>
public sealed record CodexRetainedContextOptions
{
    /// <summary>Rust RETAINED_MESSAGE_TOKEN_BUDGET, separate from provider billing.</summary>
    public int MaximumTokens { get; init; } = 64_000;
    /// <summary>Charge retained input images rather than preserving them at zero text-budget cost.</summary>
    public bool ChargeImages { get; init; }
    /// <summary>Retain developer messages explicitly annotated as client authored.</summary>
    public bool RetainClientDeveloperMessages { get; init; }
    /// <summary>Retain client-authored standalone tool outputs.</summary>
    public bool RetainClientToolOutputs { get; init; }
    /// <summary>Last known sampled history sequence, if the host tracks pending agent mail.</summary>
    public long? SampledThroughSequence { get; init; }
    /// <summary>Optional known image token estimate, for original-detail image formats not decoded by the SDK.</summary>
    public Func<JsonElement, int?>? EstimateImageTokens { get; init; }
    /// <summary>Known file audio token estimates by file ID.</summary>
    public IReadOnlyDictionary<string, int>? FileAudioTokens { get; init; }
}

/// <summary>Read-only retained-context projection; does not alter history or authorize tool execution.</summary>
/// <param name="Entries">Retained items in original order, with remapped sidecars.</param>
/// <param name="BudgetedTokens">Approximate tokens charged to the selection budget, not billed usage.</param>
/// <param name="RetainedImageCount">Number of retained message or tool-output input images.</param>
/// <param name="PendingSequence">First known unsampled agent-message sequence.</param>
public sealed record CodexRetainedContextSelection(IReadOnlyList<CodexRetainedContextEntry> Entries,
    int BudgetedTokens, int RetainedImageCount, long? PendingSequence)
{
    /// <summary>Insert an opaque new compaction checkpoint before pending mail, otherwise append it.</summary>
    public IReadOnlyList<CodexRetainedContextEntry> WithCheckpoint(CodexRetainedContextEntry checkpoint)
    {
        if (CodexRealtimeProtocol.Text(checkpoint.Item, "type") is not ("compaction" or "context_compaction"))
            throw new ArgumentException("Expected a compaction checkpoint.", nameof(checkpoint));
        var result = Entries.Select(CodexRetainedContext.Clone).ToList();
        var index = PendingSequence is { } sequence
            ? result.FindIndex(entry => CodexRetainedContext.Sequence(entry) is { } n && n >= sequence) : -1;
        result.Insert(index < 0 ? result.Count : index, CodexRetainedContext.Clone(checkpoint));
        return result;
    }
}

/// <summary>Pure retained history selection derived from Rust compact_remote_v2 and its media helper.</summary>
public static class CodexRetainedContext
{
    /// <summary>Select newest eligible groups first, then restore original order. Never mutates the audit journal.</summary>
    public static CodexRetainedContextSelection Select(IReadOnlyList<CodexRetainedContextEntry> history,
        CodexRetainedContextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumTokens);
        if (history.Any(e => e.Item.ValueKind != JsonValueKind.Object ||
            e.Metadata is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) }))
            throw new ArgumentException("History and metadata entries must be objects.", nameof(history));

        var remaining = options.MaximumTokens;
        var selected = new SortedDictionary<int, CodexRetainedContextEntry>();
        var pending = options.SampledThroughSequence is { } sampled
            ? history.Where(e => Type(e.Item) == "agent_message" && Sequence(e) is { } n && n > sampled)
                .Select(Sequence).FirstOrDefault() : null;

        for (var end = history.Count - 1; end >= 0 && remaining > 0;)
        {
            var sourceIndex = end;
            CodexRetainedContextEntry? notice = null;
            if (end > 0 && IsAttachedNotice(history[end - 1], history[end]))
            {
                notice = history[end];
                sourceIndex--;
            }
            var source = history[sourceIndex];
            var callIndex = sourceIndex - 1;
            var call = callIndex >= 0 && Always(source) && Always(history[callIndex]) &&
                MatchingTool(history[callIndex].Item, source.Item) ? history[callIndex] : null;
            end = call is null ? sourceIndex - 1 : callIndex - 1;
            if (!Eligible(source, options, pending)) continue;

            var full = Always(source) || ClientDeveloper(source);
            var chargeImages = options.ChargeImages && !full;
            var parts = Parts(source.Item);
            var boundary = parts.Any(p => chargeImages && Type(p) == "input_image" ||
                Type(p) == "input_audio" && FileId(p) is not null);
            var noticeTokens = notice is null ? 0 : Math.Max(1, ContentTokens(notice.Item, false, options));
            var contentTokens = ContentTokens(source.Item, chargeImages, options);
            var sourceTokens = full ? FullTokens(source.Item, options) : Math.Max(1, contentTokens);
            var total = call is null ? Add(sourceTokens, noticeTokens) : Add(FullTokens(call.Item, options),
                Add(FullTokens(source.Item, options), notice is null ? 0 : FullTokens(notice.Item, options)));
            CodexRetainedContextEntry? retained;
            if (total <= remaining)
            {
                remaining -= total;
                retained = Clone(source);
            }
            else if (call is not null || RetainedToolOutput(source) || full && !IsMessage(source.Item))
                break; // Structured outputs are atomic; never backfill older history across them.
            else if (remaining > noticeTokens)
            {
                var available = remaining - noticeTokens;
                var contentBudget = full ? Math.Max(0, available - Math.Max(0, sourceTokens - contentTokens)) : available;
                if (boundary) remaining = 0;
                retained = Truncate(source, contentBudget, boundary, chargeImages, options);
                if (retained is null) continue;
                if (full && FullTokens(retained.Item, options) > available)
                {
                    var adjustedBudget = Math.Max(0, contentBudget - (FullTokens(retained.Item, options) - available) - 1);
                    retained = Truncate(retained, adjustedBudget, boundary, chargeImages, options);
                    if (retained is null || FullTokens(retained.Item, options) > available) continue;
                }
                remaining = 0;
            }
            else if (boundary) break;
            else continue;

            if (call is not null) selected[callIndex] = Clone(call);
            selected[sourceIndex] = retained;
            if (notice is not null) selected[sourceIndex + 1] = Clone(notice);
        }

        var entries = selected.Values.ToArray();
        return new(entries, options.MaximumTokens - remaining,
            entries.Sum(e => Parts(e.Item).Count(p => Type(p) == "input_image")), pending);
    }

    internal static CodexRetainedContextEntry Clone(CodexRetainedContextEntry e) =>
        new(e.Item.Clone(), e.Metadata?.Clone());
    internal static long? Sequence(CodexRetainedContextEntry e) =>
        Property(e.Metadata, "history_sequence") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var value) ? value : null;

    private static JsonElement? Property(JsonElement? e, string key) =>
        e is { ValueKind: JsonValueKind.Object } item && item.TryGetProperty(key, out var value) ? value : null;
    private static string? Text(JsonElement e, string key) => CodexRealtimeProtocol.Text(e, key);
    private static string? Type(JsonElement e) => Text(e, "type");
    private static bool Flag(JsonElement? e, string key) => Property(e, key)?.ValueKind == JsonValueKind.True;
    private static bool? OptionalFlag(JsonElement? e, string key) => Property(e, key)?.ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static bool Always(CodexRetainedContextEntry e) => Flag(e.Metadata, "retain_across_windows");
    private static bool ClientDeveloper(CodexRetainedContextEntry e) =>
        IsMessage(e.Item) && Text(e.Item, "role") == "developer" && Flag(e.Metadata, "client_authored");
    private static bool RetainedToolOutput(CodexRetainedContextEntry e) =>
        (OptionalFlag(e.Metadata, "retain_across_windows") ?? Flag(e.Metadata, "client_authored")) &&
        Type(e.Item) == "function_call_output" && Text(e.Item, "call_id") is null && Text(e.Item, "name") is not null;
    private static bool IsMessage(JsonElement item) => Type(item) is "message" ||
        Type(item) is null && Text(item, "role") is not null;

    private static bool Eligible(CodexRetainedContextEntry e, CodexRetainedContextOptions options, long? pending)
    {
        if (OptionalFlag(e.Metadata, "retain_across_windows") ??
            (options.RetainClientDeveloperMessages && ClientDeveloper(e) ||
             options.RetainClientToolOutputs && RetainedToolOutput(e))) return true;
        var item = e.Item;
        if (Type(item) == "agent_message")
        {
            if (FullTokens(item, options) > 10_000) return false;
            if (pending is { } first && Sequence(e) is { } sequence && sequence >= first) return true;
            var text = Parts(item).FirstOrDefault();
            var message = text.ValueKind == JsonValueKind.Object ? Text(text, "text") : null;
            if (message?.StartsWith("Message Type: FINAL_ANSWER\n", StringComparison.Ordinal) == true) return false;
            var author = Text(item, "author");
            var recipient = Text(item, "recipient");
            return !(author is not null && recipient is not null &&
                author.StartsWith(recipient + "/", StringComparison.Ordinal) &&
                (message?.StartsWith("Message Type: MESSAGE\n", StringComparison.Ordinal) == true ||
                 message?.StartsWith("Message Type: CHANNEL_POST\n", StringComparison.Ordinal) == true));
        }
        if (!IsMessage(item) || Text(item, "role") != "user") return false;
        var parts = Parts(item);
        // Explicit visible hook prompts remain user history; runtime context wrappers are not user messages.
        if (parts.Any(p => PartText(p)?.StartsWith("<hook_prompt", StringComparison.Ordinal) == true)) return true;
        return !parts.Any(p => IsContextual(PartText(p)));
    }

    private static bool IsContextual(string? text)
    {
        if (text is null) return false;
        foreach (var tag in new[] { "environment_context", "user_instructions", "additional_context", "agent_message_board_notification",
            "skill", "user_shell_command", "turn_aborted", "subagent_notification", "internal_model_context",
            "recommended_plugins", "collaboration_mode" })
            if (text.StartsWith("<" + tag + ">", StringComparison.Ordinal) &&
                text.EndsWith("</" + tag + ">", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsAttachedNotice(CodexRetainedContextEntry source, CodexRetainedContextEntry notice)
    {
        if (!(IsMessage(source.Item) && Text(source.Item, "role") == "user" ||
            Type(source.Item) is "function_call_output" or "custom_tool_call_output") ||
            Flag(notice.Metadata, "client_authored") || OptionalFlag(notice.Metadata, "retain_across_windows") is not null ||
            !IsMessage(notice.Item) || Text(notice.Item, "role") != "developer") return false;
        var parts = Parts(notice.Item);
        return parts.Count == 1 && PartText(parts[0]) is { } text &&
            text.StartsWith("<image_resize_notice>", StringComparison.Ordinal) &&
            text.EndsWith("</image_resize_notice>", StringComparison.Ordinal);
    }

    private static bool MatchingTool(JsonElement call, JsonElement output) =>
        Text(call, "call_id") is { } id && id == Text(output, "call_id") &&
        (Type(call), Type(output)) is ("function_call", "function_call_output") or ("custom_tool_call", "custom_tool_call_output");

    private static IReadOnlyList<JsonElement> Parts(JsonElement item)
    {
        var content = Property(item, "content") ?? Property(item, "output");
        if (content is { ValueKind: JsonValueKind.Array } array) return array.EnumerateArray().ToArray();
        if (content is { ValueKind: JsonValueKind.String } str) return [str];
        return [];
    }

    private static string? PartText(JsonElement part) => part.ValueKind == JsonValueKind.String ? part.GetString() :
        Type(part) is "input_text" or "output_text" ? Text(part, "text") : null;
    private static string? FileId(JsonElement part) =>
        Text(part, "file_id") ?? (Property(part, "audio") is { ValueKind: JsonValueKind.Object } audio ? Text(audio, "file_id") : null);
    private static bool IsMedia(JsonElement part) => Type(part) is "input_image" or "input_audio";
    private static int Tokens(string? value) => value is null ? 0 : (int)((Encoding.UTF8.GetByteCount(value) + 3L) / 4);
    private static int Add(int a, int b) => (int)Math.Min(int.MaxValue, (long)a + b);

    private static int PartTokens(JsonElement part, bool chargeImages, CodexRetainedContextOptions options)
    {
        if (PartText(part) is { } text) return Tokens(text);
        if (Type(part) == "input_image") return chargeImages ? ImageTokens(part, options) : 0;
        if (Type(part) == "input_audio")
            return FileId(part) is { } id && options.FileAudioTokens?.TryGetValue(id, out var tokens) == true
                ? Math.Max(0, tokens) : 0;
        return Tokens(part.GetRawText());
    }

    private static int ImageTokens(JsonElement image, CodexRetainedContextOptions options)
    {
        if (options.EstimateImageTokens?.Invoke(image) is { } known) return Math.Max(0, known);
        if (Text(image, "detail") != "original") return 1844; // Rust 7,373 model-visible bytes.
        if (FileId(image) is not null || Property(image, "image") is { ValueKind: JsonValueKind.Object } reference &&
            FileId(reference) is not null) return 10_000;
        var url = Text(image, "image_url") ?? (Property(image, "image") is { ValueKind: JsonValueKind.Object } nested ? Text(nested, "image_url") : null);
        const string png = "data:image/png;base64,";
        if (url?.StartsWith(png, StringComparison.Ordinal) == true && url.Length >= png.Length + 32)
        {
            Span<byte> header = stackalloc byte[24];
            if (Convert.TryFromBase64Chars(url.AsSpan(png.Length, 32), header, out var count) && count == 24 &&
                header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
                var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
                if (width != 0 && height != 0)
                    return (int)Math.Min(10_000L, ((width + 31L) / 32) * ((height + 31L) / 32));
            }
        }
        return 1844; // Same fallback as upstream when dimensions cannot be decoded.
    }

    private static int ContentTokens(JsonElement item, bool chargeImages, CodexRetainedContextOptions options) =>
        IsMessage(item) ? Parts(item).Aggregate(0, (sum, p) => Add(sum, PartTokens(p, chargeImages, options))) :
        FullTokens(item, options);
    private static int FullTokens(JsonElement item, CodexRetainedContextOptions options)
    {
        if (IsMessage(item))
        {
            long bytes = 0;
            foreach (var part in Parts(item))
                bytes += PartText(part) is { } text ? Encoding.UTF8.GetByteCount(text) :
                    Type(part) == "input_image" && Text(part, "detail") != "original" && options.EstimateImageTokens is null
                        ? 7373 : (long)PartTokens(part, true, options) * 4;
            return (int)Math.Min(int.MaxValue, (bytes + 3) / 4);
        }
        if (Type(item) == "agent_message")
        {
            var bytes = Encoding.UTF8.GetByteCount(Text(item, "author") ?? "") + (long)Encoding.UTF8.GetByteCount(Text(item, "recipient") ?? "");
            foreach (var part in Parts(item))
                bytes += Text(part, "text") is { } text ? Encoding.UTF8.GetByteCount(text) : Encoding.UTF8.GetByteCount(part.GetRawText());
            return (int)Math.Min(int.MaxValue, (bytes + 3) / 4);
        }
        if (Type(item) is "function_call" or "custom_tool_call")
            return Tokens((Text(item, "name") ?? "") + (Text(item, "namespace") ?? "functions") +
                (Text(item, "arguments") ?? Text(item, "input") ?? ""));
        if (Type(item) is "function_call_output" or "custom_tool_call_output")
            return Parts(item).Aggregate(Tokens((Text(item, "call_id") ?? "") + (Text(item, "name") ?? "") +
                (Text(item, "namespace") ?? "")), (sum, p) => Add(sum, PartTokens(p, true, options)));
        return Tokens(item.GetRawText());
    }

    private static CodexRetainedContextEntry? Truncate(CodexRetainedContextEntry source, int budget, bool boundary,
        bool chargeImages, CodexRetainedContextOptions options)
    {
        if (!IsMessage(source.Item)) return null;
        var parts = Parts(source.Item);
        var retained = new SortedDictionary<int, JsonNode?>();
        var remaining = budget;
        if (!boundary)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (PartText(part) is { } text)
                {
                    if (remaining == 0) continue;
                    var cost = Tokens(text);
                    var snippet = TruncateText(text, remaining);
                    remaining = Math.Max(0, remaining - cost);
                    if (snippet.Length != 0) retained[i] = RewriteText(part, snippet);
                }
                else if (IsMedia(part)) retained[i] = JsonNode.Parse(part.GetRawText());
                else return null; // Unknown structured content must not be partially rewritten.
            }
        }
        else
        {
            for (var i = parts.Count - 1; i >= 0 && remaining > 0;)
            {
                var part = parts[i];
                var media = IsMedia(part) ? i : i > 0 && IsMedia(parts[i - 1]) &&
                    ClosingLabel(PartText(part), Type(parts[i - 1])) ? i - 1 : -1;
                if (media >= 0)
                {
                    var start = media > 0 && OpeningLabel(PartText(parts[media - 1]), Type(parts[media])) ? media - 1 : media;
                    var cost = Enumerable.Range(start, i - start + 1).Aggregate(0, (sum, n) => Add(sum, PartTokens(parts[n], chargeImages, options)));
                    if (cost > remaining) break;
                    remaining -= cost;
                    for (var n = start; n <= i; n++) retained[n] = JsonNode.Parse(parts[n].GetRawText());
                    i = start - 1;
                }
                else if (PartText(part) is { } text)
                {
                    var snippet = TruncateText(text, remaining);
                    remaining = Math.Max(0, remaining - Tokens(text));
                    if (snippet.Length != 0) retained[i] = RewriteText(part, snippet);
                    i--;
                }
                else break;
            }
        }
        if (retained.Count == 0) return null;
        var node = JsonNode.Parse(source.Item.GetRawText())!.AsObject();
        if (node["content"] is JsonValue && retained.Count == 1) node["content"] = retained.Values.Single();
        else
        {
            node["content"] = new JsonArray(retained.Values.ToArray());
            if (node["internal_chat_message_metadata_passthrough"] is not JsonObject metadata)
                node["internal_chat_message_metadata_passthrough"] = metadata = new JsonObject();
            var kinds = metadata["content_item_kinds"] as JsonArray;
            metadata["content_item_kinds"] = new JsonArray(retained.Keys.Select(i => kinds is not null && i < kinds.Count ?
                kinds[i]?.DeepClone() : JsonValue.Create("unknown")).ToArray());
        }
        JsonElement? sidecar = source.Metadata?.Clone();
        if (source.Metadata is { ValueKind: JsonValueKind.Object } original)
        {
            var copy = JsonNode.Parse(original.GetRawText())!.AsObject();
            if (copy["retained_source"] is JsonObject retainedSource) retainedSource["complete"] = false;
            if (copy["guardian_sources"] is JsonArray guardianSources)
                foreach (var guardian in guardianSources.OfType<JsonObject>()) guardian["complete"] = false;
            sidecar = Element(copy);
        }
        return new(Element(node), sidecar);
    }

    private static bool OpeningLabel(string? text, string? media) => text is not null && (media == "input_image" ?
        text == "<image>" || text.StartsWith("<image ", StringComparison.Ordinal) && text.EndsWith('>') :
        text == "<audio>" || text.StartsWith("<audio ", StringComparison.Ordinal) && text.EndsWith('>'));
    private static bool ClosingLabel(string? text, string? media) => text == (media == "input_image" ? "</image>" : "</audio>");
    private static JsonNode? RewriteText(JsonElement part, string text)
    {
        if (part.ValueKind == JsonValueKind.String) return JsonValue.Create(text);
        var node = JsonNode.Parse(part.GetRawText())!.AsObject();
        node["text"] = text;
        return node;
    }

    // Rust preserves UTF-8-aligned prefix and suffix; the omission marker is outside the text budget.
    private static string TruncateText(string text, int tokens)
    {
        var length = Encoding.UTF8.GetByteCount(text);
        var maxBytes = Math.Min(int.MaxValue, tokens * 4L);
        if (length <= maxBytes) return text;
        var left = maxBytes / 2;
        var right = maxBytes - left;
        var bytes = Encoding.UTF8.GetBytes(text);
        var prefix = (int)left;
        while (prefix > 0 && (bytes[prefix] & 0xc0) == 0x80) prefix--;
        var suffix = length - (int)right;
        while (suffix < length && (bytes[suffix] & 0xc0) == 0x80) suffix++;
        var removed = (length - maxBytes + 3) / 4;
        return Encoding.UTF8.GetString(bytes, 0, prefix) + $"…{removed} tokens truncated…" +
            Encoding.UTF8.GetString(bytes, suffix, length - suffix);
    }

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
