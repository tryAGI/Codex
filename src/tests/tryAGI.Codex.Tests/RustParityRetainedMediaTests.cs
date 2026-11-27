using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

// Exact budget boundaries from pinned Rust compact_remote_v2_media_budget_tests.rs.
public sealed class RustParityRetainedMediaTests
{
    private static JsonElement Json(string wire)
    {
        using var document = JsonDocument.Parse(wire);
        return document.RootElement.Clone();
    }

    private static JsonObject Text(string value) => new() { ["type"] = "input_text", ["text"] = value };
    private static JsonObject InlineImage() => new() { ["type"] = "input_image", ["image_url"] = "data:image/png;base64,abc" };
    private static JsonObject FileImage() => new() { ["type"] = "input_image", ["file_id"] = "file_123", ["detail"] = "original" };
    private static JsonObject FileAudio() => new()
    {
        ["type"] = "input_audio", ["audio"] = new JsonObject { ["file_id"] = "file_audio" },
    };
    private static JsonObject InlineAudio() => new()
    {
        ["type"] = "input_audio", ["audio"] = new JsonObject { ["audio_url"] = "data:audio/wav;base64,older" },
    };

    private static CodexRetainedContextEntry Message(JsonObject[] parts, string[]? kinds = null)
    {
        var item = new JsonObject
        {
            ["type"] = "message", ["role"] = "user",
            ["content"] = new JsonArray(parts.Select(p => p.DeepClone()).ToArray()),
        };
        if (kinds is not null)
            item["internal_chat_message_metadata_passthrough"] = new JsonObject
            {
                ["content_item_kinds"] = new JsonArray(kinds.Select(k => JsonValue.Create(k)).ToArray()),
            };
        return new(Json(item.ToJsonString()));
    }

    private static JsonElement[] Parts(CodexRetainedContextEntry entry) =>
        entry.Item.GetProperty("content").EnumerateArray().ToArray();

    private static void AssertParts(CodexRetainedContextEntry expected, CodexRetainedContextEntry actual)
    {
        Assert.Equal(expected.Item.GetProperty("role").GetString(), actual.Item.GetProperty("role").GetString());
        Assert.True(JsonElement.DeepEquals(expected.Item.GetProperty("content"), actual.Item.GetProperty("content")));
        if (expected.Item.TryGetProperty("internal_chat_message_metadata_passthrough", out var metadata))
            Assert.True(JsonElement.DeepEquals(metadata, actual.Item.GetProperty("internal_chat_message_metadata_passthrough")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileAudioBoundaryPreservesExactAnnotatedSuffixInBothBudgetModes(bool chargeImages)
    {
        // Rust lines 98–179. Default unknown file audio reserves 12000 tokens;
        // the three text parts cost 2, 2, and 1 tokens respectively.
        var parts = new[] { Text("before"), FileAudio(), Text("after") };
        var kinds = new[] { "before", "audio", "after" };
        var boundary = Message(parts, kinds);
        var newest = Message([Text("new")]);
        var history = new[] { Message([Text("older")]), boundary, newest };
        var original = history.Select(e => e.Item.GetRawText()).ToArray();

        foreach (var (budget, start) in new[] { (12005, 0), (12003, 1), (12002, 2) })
        {
            var selected = CodexRetainedContext.Select(history, new()
            {
                ChargeImages = chargeImages, MaximumTokens = budget,
            });
            Assert.Equal(2, selected.Entries.Count);
            AssertParts(Message(parts[start..], kinds[start..]), selected.Entries[0]);
            AssertParts(newest, selected.Entries[1]);
            Assert.Equal(original, history.Select(e => e.Item.GetRawText()).ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlineAndOriginalFileImagesAreAtomicWithoutOlderBackfill(bool file)
    {
        // Rust lines 71–95. Inline resized and original file image estimates
        // are distinct; at one token below the boundary neither is split.
        var image = Message([file ? FileImage() : InlineImage()]);
        var newest = Message([Text("new")]);
        var history = new[] { Message([Text("old")]), image, newest };
        var imageTokens = file ? 10000 : 1844;
        foreach (var budget in new[] { imageTokens + 1, imageTokens, 1 })
        {
            var selected = CodexRetainedContext.Select(history, new()
            {
                ChargeImages = true, MaximumTokens = budget,
            });
            Assert.Equal(budget == imageTokens + 1 ? 2 : 1, selected.Entries.Count);
            AssertParts(newest, selected.Entries[^1]);
            Assert.Equal(budget == imageTokens + 1 ? 1 : 0, selected.RetainedImageCount);
            if (selected.Entries.Count == 2) AssertParts(image, selected.Entries[0]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileAudioCutoffDropsAllEarlierUnchargedMediaInBothModes(bool chargeImages)
    {
        // Rust lines 182–226: after the unaffordable file-audio boundary,
        // no earlier inline audio/image or associated harness labels are backfilled.
        var boundary = Message([
            Text("<image>"), InlineImage(), Text("</image>"), Text("older recording"),
            InlineAudio(), Text("newer recording"), FileAudio(), Text("after"),
        ]);
        var before = boundary.Item.GetRawText();
        var selection = CodexRetainedContext.Select([boundary], new()
        {
            ChargeImages = chargeImages, MaximumTokens = 12001,
        });
        var retained = Assert.Single(selection.Entries);
        AssertParts(Message([Text("after")]), retained);
        Assert.Single(Parts(retained));
        Assert.Equal(0, selection.RetainedImageCount);
        Assert.Equal(before, boundary.Item.GetRawText());
    }
}
