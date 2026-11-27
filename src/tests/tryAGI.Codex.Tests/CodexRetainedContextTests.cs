using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexRetainedContextTests
{
    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static CodexRetainedContextEntry Message(string role, string text, string? metadata = null) =>
        new(Json(new JsonObject
        {
            ["type"] = "message", ["role"] = role,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
        }.ToJsonString()), metadata is null ? null : Json(metadata));

    private static string Text(CodexRetainedContextEntry entry) =>
        entry.Item.GetProperty("content")[0].GetProperty("text").GetString()!;

    private static readonly CodexRetainedContextEntry Checkpoint =
        new(Json("""{"type":"compaction","encrypted_content":"opaque","future":{"x":1}}"""));
    private static readonly CodexRetainedContextEntry Image =
        new(Json("""{"type":"message","role":"user","content":[{"type":"input_image","image_url":"data:image/png;base64,abc"}]}"""));

    // Derived from Rust build_v2_compacted_history_preserves_retained_metadata_sidecar.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetentionUsesMetadataAndKeepsAttachedResizeNotice(bool retainClient)
    {
        var old = Message("developer", "drop");
        var client = Message("developer", "client", """{"client_authored":true,"unknown":"preserved"}""");
        var user = Message("user", "keep", """{"history_sequence":10}""");
        var notice = Message("developer", "<image_resize_notice>generated</image_resize_notice>");
        var runtime = Message("user", "<environment_context>hidden</environment_context>");
        var assistant = Message("assistant", "answer");
        var history = new[] { old, client, user, notice, runtime, assistant, Checkpoint };
        var original = history.Select(e => e.Item.GetRawText()).ToArray();
        var selection = CodexRetainedContext.Select(history, new() { RetainClientDeveloperMessages = retainClient });
        Assert.Equal(retainClient ? new[] { "client", "keep", "<image_resize_notice>generated</image_resize_notice>" } :
            new[] { "keep", "<image_resize_notice>generated</image_resize_notice>" }, selection.Entries.Select(Text));
        if (retainClient) Assert.Equal("preserved", selection.Entries[0].Metadata!.Value.GetProperty("unknown").GetString());
        Assert.True(JsonElement.DeepEquals(Checkpoint.Item, selection.WithCheckpoint(Checkpoint)[^1].Item));
        Assert.Equal(original, history.Select(e => e.Item.GetRawText()));
    }

    // Exact fixture from retained_history_truncation_keeps_newest_messages_first.
    [Fact]
    public void NewestFirstWithRustUtf8MiddleTruncation()
    {
        var history = new[] { Message("user", "old-old"), Message("user", "middle1234"), Message("user", "new") };
        var selection = CodexRetainedContext.Select(history, new() { MaximumTokens = 3 });
        Assert.Equal(new[] { "midd…1 tokens truncated…1234", "new" }, selection.Entries.Select(Text));
        Assert.Equal(3, selection.BudgetedTokens);
        Assert.Equal("unknown", selection.Entries[0].Item.GetProperty("internal_chat_message_metadata_passthrough")
            .GetProperty("content_item_kinds")[0].GetString());
        Assert.Equal("middle1234", Text(history[1]));
    }

    // Derived from retained_history_truncation_preserves_images_and_truncates_later_text_parts.
    [Fact]
    public void MixedContentPreservesImagesKindsAndMarksRetainedSourcesIncomplete()
    {
        var entry = new CodexRetainedContextEntry(Json("""
            {"type":"message","role":"user","content":[
              {"type":"input_text","text":"abcdef"},
              {"type":"input_image","image_url":"data:image/png;base64,abc"},
              {"type":"output_text","text":"uvwxyz","annotations":[{"type":"note","unknown":42}]},
              {"type":"input_text","text":"discarded after the text budget"},
              {"type":"input_image","image_url":"data:image/png;base64,def"}],
             "internal_chat_message_metadata_passthrough":{"turn_id":"turn-1","content_item_kinds":["user.text","user.image","user.text","user.text","user.image"]}}
            """), Json("""{"retained_source":{"id":"original","complete":true},"guardian_sources":[{"id":"source","complete":true}],"future":42}"""));
        var result = CodexRetainedContext.Select([entry], new() { MaximumTokens = 3 });
        var retained = Assert.Single(result.Entries);
        var parts = retained.Item.GetProperty("content");
        Assert.Equal(4, parts.GetArrayLength());
        Assert.Equal("uv…1 tokens truncated…yz", parts[2].GetProperty("text").GetString());
        Assert.Equal(42, parts[2].GetProperty("annotations")[0].GetProperty("unknown").GetInt32());
        Assert.Equal(new[] { "user.text", "user.image", "user.text", "user.image" },
            retained.Item.GetProperty("internal_chat_message_metadata_passthrough").GetProperty("content_item_kinds")
                .EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(2, result.RetainedImageCount);
        Assert.False(retained.Metadata!.Value.GetProperty("retained_source").GetProperty("complete").GetBoolean());
        Assert.False(retained.Metadata.Value.GetProperty("guardian_sources")[0].GetProperty("complete").GetBoolean());
        Assert.Equal(42, retained.Metadata.Value.GetProperty("future").GetInt32());
        Assert.True(entry.Metadata!.Value.GetProperty("retained_source").GetProperty("complete").GetBoolean());
        Assert.Equal(5, entry.Item.GetProperty("content").GetArrayLength());
    }

    [Theory]
    [InlineData(false, 2, 2, 1)]
    [InlineData(false, 1, 1, 0)]
    [InlineData(true, 1845, 2, 1)]
    [InlineData(true, 1844, 1, 0)]
    [InlineData(true, 0, 0, 0)]
    public void ImageOnlyBoundaryHasRustBudgetAndDoesNotBackfill(bool charge, int budget, int count, int images)
    {
        var history = new[] { Message("user", "old"), Image, Message("user", "new") };
        var selection = CodexRetainedContext.Select(history, new() { ChargeImages = charge, MaximumTokens = budget });
        Assert.Equal(count, selection.Entries.Count);
        Assert.Equal(images, selection.RetainedImageCount);
        if (count > 0) Assert.Equal("new", Text(selection.Entries[^1]));
        if (images > 0) Assert.True(JsonElement.DeepEquals(Image.Item, selection.Entries[0].Item));
    }

    [Fact]
    public void BudgetedMediaLabelsAreKeptOrDroppedAsAGroup()
    {
        var entry = new CodexRetainedContextEntry(Json("""
            {"type":"message","role":"user","content":[
            {"type":"input_text","text":"before"},
            {"type":"input_text","text":"<image>"},
            {"type":"input_image","image_url":"data:image/png;base64,abc"},
            {"type":"input_text","text":"</image>"},
            {"type":"input_text","text":"last"}]}
            """));
        var result = CodexRetainedContext.Select([Message("user", "old"), entry],
            new() { MaximumTokens = 10, ChargeImages = true });
        var retained = Assert.Single(result.Entries);
        Assert.Equal("last", Text(retained));
        Assert.Equal(0, result.RetainedImageCount);
    }

    [Fact]
    public void ExplicitToolExchangeIsAtomicAndRawReceiptsSurvive()
    {
        var metadata = Json("""{"retain_across_windows":true,"receipt":"durable"}""");
        var call = new CodexRetainedContextEntry(Json("""{"type":"function_call","call_id":"call","name":"tool","arguments":"{}"}"""), metadata);
        var output = new CodexRetainedContextEntry(Json("""{"type":"function_call_output","call_id":"call","output":"result","provider_extension":"opaque"}"""), metadata);
        var history = new[] { Message("user", "old"), call, output };
        var enough = CodexRetainedContext.Select(history, new() { MaximumTokens = 100 });
        Assert.Equal(3, enough.Entries.Count);
        Assert.True(JsonElement.DeepEquals(call.Item, enough.Entries[1].Item));
        Assert.True(JsonElement.DeepEquals(output.Item, enough.Entries[2].Item));
        Assert.Equal("durable", enough.Entries[2].Metadata!.Value.GetProperty("receipt").GetString());
        Assert.Empty(CodexRetainedContext.Select(history, new() { MaximumTokens = 1 }).Entries);
    }

    [Fact]
    public void PendingMailRemainsAfterCheckpoint()
    {
        var mail = new CodexRetainedContextEntry(Json("""
            {"type":"agent_message","author":"parent/child","recipient":"parent","content":[{"type":"input_text","text":"Message Type: FINAL_ANSWER\nresult"}]}
            """), Json("""{"history_sequence":9}"""));
        var history = new[] { Message("user", "old", """{"history_sequence":8}"""), mail,
            Message("user", "new", """{"history_sequence":10}""") };
        var projection = CodexRetainedContext.Select(history, new() { SampledThroughSequence = 8 }).WithCheckpoint(Checkpoint);
        Assert.Equal(4, projection.Count);
        Assert.True(JsonElement.DeepEquals(Checkpoint.Item, projection[1].Item));
        Assert.True(JsonElement.DeepEquals(mail.Item, projection[2].Item));
    }

    private static CodexRetainedContextEntry OriginalImage(string mime, byte[] header) =>
        new(Json(new JsonObject
        {
            ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "input_image", ["detail"] = "original",
                ["image"] = new JsonObject { ["image_url"] = "DATA:" + mime + ";charset=binary;BASE64," + Convert.ToBase64String(header) },
            }),
        }.ToJsonString()));

    [Theory]
    [InlineData("image/jpeg", "FFD8FFC0000B080021004101010000", 6)]
    [InlineData("image/gif", "47494638396141002100", 6)]
    [InlineData("image/webp", "524946461600000057454250565038580A00000000000000400000200000", 6)]
    [InlineData("image/webp", "5249464612000000574542505650384C050000002F40000800", 6)]
    [InlineData("image/webp", "524946461600000057454250565038200A0000000000009D012A41002100", 6)]
    public void OriginalDetailCommonImageHeadersUsePatchDimensions(string mime, string hex, int expected)
    {
        var header = Convert.FromHexString(hex);
        var image = OriginalImage(mime, header);
        var selection = CodexRetainedContext.Select([image], new() { ChargeImages = true, MaximumTokens = expected });
        Assert.Single(selection.Entries);
        Assert.Equal(expected, selection.BudgetedTokens);
        Assert.Empty(CodexRetainedContext.Select([image], new() { ChargeImages = true, MaximumTokens = expected - 1 }).Entries);
    }

    [Fact]
    public void JpegMetadataBeyondInitialHeaderIsScannedWithinBound()
    {
        var metadata = new byte[2048];
        byte[] frame = Convert.FromHexString("FFC2000B080021004101010000");
        var jpeg = new byte[6 + metadata.Length + frame.Length];
        jpeg[0] = 0xff;
        jpeg[1] = 0xd8;
        jpeg[2] = 0xff;
        jpeg[3] = 0xe1;
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4, 2), (ushort)(metadata.Length + 2));
        frame.CopyTo(jpeg, 6 + metadata.Length);
        var image = OriginalImage("image/jpeg", jpeg);
        Assert.Equal(6, CodexRetainedContext.Select([image], new() { ChargeImages = true }).BudgetedTokens);
        var malformed = OriginalImage("image/jpeg", jpeg[..100]);
        Assert.Equal(1844, CodexRetainedContext.Select([malformed], new() { ChargeImages = true }).BudgetedTokens);
    }

    [Fact]
    public void BmpDimensionsAreSignedAndPatchCountsAreCapped()
    {
        var header = new byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18, 4), 65);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22, 4), -33);
        var image = OriginalImage("image/bmp", header);
        Assert.Equal(6, CodexRetainedContext.Select([image], new() { ChargeImages = true }).BudgetedTokens);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18, 4), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22, 4), int.MaxValue);
        Assert.Equal(10_000, CodexRetainedContext.Select([OriginalImage("image/bmp", header)], new() { ChargeImages = true }).BudgetedTokens);
    }

    [Theory]
    [InlineData(null, 12000)]
    [InlineData(0, 12000)]
    [InlineData(-1, 12000)]
    [InlineData(15000, 12000)]
    [InlineData(25, 25)]
    public void FileAudioReservesTrustedDurationOrFullUpstreamBudget(int? reported, int expected)
    {
        var entry = new CodexRetainedContextEntry(Json("""
            {"type":"message","role":"user","content":[{"type":"input_audio","audio":{"file_id":"audio"}}]}
            """));
        var options = new CodexRetainedContextOptions
        {
            FileAudioTokens = reported is { } value ? new Dictionary<string, int> { ["audio"] = value } : null,
            MaximumTokens = expected + 1,
        };
        var history = new[] { Message("user", "old"), entry, Message("user", "new") };
        foreach (var charge in new[] { false, true })
        {
            var selection = CodexRetainedContext.Select(history, options with { ChargeImages = charge });
            Assert.Equal(expected + 1, selection.BudgetedTokens);
            Assert.Equal(2, selection.Entries.Count);
            Assert.True(JsonElement.DeepEquals(entry.Item, selection.Entries[0].Item));
            var tooSmall = CodexRetainedContext.Select(history, options with { ChargeImages = charge, MaximumTokens = expected });
            Assert.Equal("new", Text(Assert.Single(tooSmall.Entries)));
        }
    }

    [Fact]
    public void FileAudioCutoffDoesNotBackfillOlderUnchargedMedia()
    {
        var entry = new CodexRetainedContextEntry(Json("""
            {"type":"message","role":"user","content":[
              {"type":"input_image","image_url":"data:image/png;base64,older"},
              {"type":"input_audio","audio":{"audio_url":"data:audio/wav;base64,older"}},
              {"type":"input_text","text":"<audio>"},
              {"type":"input_audio","audio":{"file_id":"audio"}},
              {"type":"input_text","text":"</audio>"},
              {"type":"input_text","text":"after"}],
             "internal_chat_message_metadata_passthrough":{"content_item_kinds":["image","audio","label","file","label","text"]}}
            """), Json("""{"retained_source":{"complete":true}}"""));
        var selection = CodexRetainedContext.Select([Message("user", "old"), entry], new() { MaximumTokens = 12000 });
        var retained = Assert.Single(selection.Entries);
        Assert.Equal("after", Text(retained));
        Assert.Equal(0, selection.RetainedImageCount);
        Assert.Equal("text", Assert.Single(retained.Item.GetProperty("internal_chat_message_metadata_passthrough")
            .GetProperty("content_item_kinds").EnumerateArray()).GetString());
        Assert.False(retained.Metadata!.Value.GetProperty("retained_source").GetProperty("complete").GetBoolean());
        Assert.True(entry.Metadata!.Value.GetProperty("retained_source").GetProperty("complete").GetBoolean());
        Assert.Equal(6, entry.Item.GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void OriginalDetailPngUsesPatchEstimateAndFileReferenceUsesMaximum()
    {
        var header = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), 64);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 64);
        var png = new CodexRetainedContextEntry(Json(new JsonObject
        {
            ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject
            { ["type"] = "input_image", ["detail"] = "original", ["image_url"] = "data:image/png;base64," + Convert.ToBase64String(header) }),
        }.ToJsonString()));
        var selection = CodexRetainedContext.Select([png], new() { ChargeImages = true, MaximumTokens = 4 });
        Assert.Single(selection.Entries);
        Assert.Equal(4, selection.BudgetedTokens);
        var file = new CodexRetainedContextEntry(Json("""{"type":"message","role":"user","content":[{"type":"input_image","detail":"original","file_id":"file"}]}"""));
        Assert.Empty(CodexRetainedContext.Select([file], new() { ChargeImages = true, MaximumTokens = 9999 }).Entries);
        Assert.Equal(10_000, CodexRetainedContext.Select([file], new() { ChargeImages = true, MaximumTokens = 10_000 }).BudgetedTokens);
    }
}
