using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

// Raw retained-history shape assertions from the pinned Rust compaction suite.
// Verify both the retained projection and host-derived checkpoint provenance.
public sealed class RustParityCompactionShapeTests
{
    private static JsonElement Json(string wire)
    {
        using var document = JsonDocument.Parse(wire);
        return document.RootElement.Clone();
    }

    private static CodexRetainedContextEntry Message(string role, string text, string? phase = null,
        string? metadata = null, string? id = null)
    {
        var node = new JsonObject
        {
            ["type"] = "message", ["role"] = role,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
        };
        if (phase is not null) node["phase"] = phase;
        if (id is not null) node["id"] = id;
        return new(Json(node.ToJsonString()), metadata is null ? null : Json(metadata));
    }

    private static readonly CodexRetainedContextEntry NewCheckpoint =
        new(Json("""{"type":"compaction","encrypted_content":"new"}"""));

    private static void AssertRaw(IReadOnlyList<CodexRetainedContextEntry> expected,
        IReadOnlyList<CodexRetainedContextEntry> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(JsonElement.DeepEquals(expected[i].Item, actual[i].Item), "Retained raw item differs at index " + i);
    }

    [Fact]
    public void FiltersDiscardedHistoryBeforeApplyingTokenBudget()
    {
        // compact_remote_v2.rs:1180. Discarded inputs must not consume the retained budget.
        var old = Message("user", "old");
        var newest = Message("user", "new");
        var history = new[]
        {
            old,
            Message("developer", new string('d', (64_000 + 1) * 4)),
            Message("user", "<environment_context>\n" + new string('c', (64_000 + 1) * 4) + "\n</environment_context>"),
            newest,
        };
        var before = history.Select(e => e.Item.GetRawText()).ToArray();
        var selection = CodexRetainedContext.Select(history);
        AssertRaw([old, newest, NewCheckpoint], selection.WithCheckpoint(NewCheckpoint));
        Assert.Equal(2, selection.BudgetedTokens);
        Assert.Equal(before, history.Select(e => e.Item.GetRawText()).ToArray());
    }

    [Fact]
    public void InstalledShapeKeepsUserAndHookButDiscardsPreviousCheckpointAndOutputs()
    {
        // compact_remote_v2.rs:1040. The XML hook prompt is a synthetic equivalent
        // of build_hook_prompt_message; its generated identifier is intentionally fixed.
        var user = Message("user", "user");
        var hook = Message("user", """<hook_prompt hook_run_id="hook-run">hook</hook_prompt>""", id: "msg_fixture");
        var history = new[]
        {
            Message("developer", "dev"),
            Message("system", "sys"),
            user,
            hook,
            Message("assistant", "commentary", "commentary"),
            Message("assistant", "final", "final_answer"),
            new CodexRetainedContextEntry(Json("""{"type":"function_call","name":"shell_command","arguments":"{}","call_id":"call_1"}""")),
            new CodexRetainedContextEntry(Json("""{"type":"compaction","encrypted_content":"old"}""")),
        };
        var before = history.Select(e => e.Item.GetRawText()).ToArray();
        var retained = CodexRetainedContext.Select(history).WithCheckpoint(NewCheckpoint);
        AssertRaw([user, hook, NewCheckpoint], retained);
        Assert.Equal(before, history.Select(e => e.Item.GetRawText()).ToArray());
    }

    [Fact]
    public void CountsAllRetainedInputImagesInTheInstalledShape()
    {
        // compact_remote_v2.rs:1206. Image counting is distinct from token charging.
        var user = new CodexRetainedContextEntry(Json("""
            {"type":"message","role":"user","content":[
              {"type":"input_text","text":"user"},
              {"type":"input_image","image_url":"data:image/png;base64,abc"},
              {"type":"input_image","image_url":"data:image/png;base64,def"}]}
            """));
        var selection = CodexRetainedContext.Select([user], new() { ChargeImages = false });
        Assert.Equal(2, selection.RetainedImageCount);
        AssertRaw([user, NewCheckpoint], selection.WithCheckpoint(NewCheckpoint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingRetainedEnvelopeMetadataAndResizeNoticeAreNotReclassified(bool enabled)
    {
        // Narrow subset of compact_remote_v2.rs:1087. This test does not certify
        // the compaction_without_file_error_sources marker on the new checkpoint.
        var harness = Message("developer", "drop");
        var client = Message("developer", "<image_resize_notice>client</image_resize_notice>",
            metadata: """{"client_authored":true}""");
        var user = Message("user", "keep", metadata: "{}");
        var generated = Message("developer", "<image_resize_notice>generated</image_resize_notice>");
        var input = new[] { harness, client, user, generated };
        var expected = enabled ? new[] { client, user, generated } : [user, generated];
        var selected = CodexRetainedContext.Select(input, new() { RetainClientDeveloperMessages = enabled }).Entries;
        AssertRaw(expected, selected);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].Metadata is { } metadata)
                Assert.True(JsonElement.DeepEquals(metadata, selected[i].Metadata!.Value));
            else
                Assert.Null(selected[i].Metadata);
        }
    }

    [Fact]
    public void TruncatingAMessagePreservesItsExistingDefaultHarnessMetadata()
    {
        // compact_remote_v2.rs:1163. A default sidecar stays present, not null.
        var original = Message("user", string.Concat(Enumerable.Repeat("word ", 200)), metadata: "{}");
        var retained = Assert.Single(CodexRetainedContext.Select([original], new() { MaximumTokens = 4 }).Entries);
        Assert.True(JsonElement.DeepEquals(Json("{}"), retained.Metadata!.Value));
        Assert.False(JsonElement.DeepEquals(original.Item, retained.Item));
        Assert.Equal(1000, original.Item.GetProperty("content")[0].GetProperty("text").GetString()!.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledCheckpointHasExactDerivedHarnessMetadata(bool enabled)
    {
        // Pinned compact_remote_v2.rs:1087-1160: the whole envelope vector,
        // including the new opaque checkpoint's host-derived marker.
        var harness = Message("developer", "drop");
        var client = Message("developer", "<image_resize_notice>client</image_resize_notice>",
            metadata: """{"client_authored":true}""");
        var user = Message("user", "keep", metadata: "{}");
        var generated = Message("developer", "<image_resize_notice>generated</image_resize_notice>");
        var expectedCheckpoint = NewCheckpoint with
        {
            Metadata = Json("""{"compaction_without_file_error_sources":true}"""),
        };
        CodexRetainedContextEntry[] expected = enabled
            ? [client, user, generated, expectedCheckpoint] : [user, generated, expectedCheckpoint];

        var actual = CodexRetainedContext.Select([harness, client, user, generated],
            new() { RetainClientDeveloperMessages = enabled }).WithCheckpoint(NewCheckpoint);
        AssertRaw(expected, actual);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].Metadata is { } metadata)
                Assert.True(JsonElement.DeepEquals(metadata, actual[i].Metadata!.Value));
            else
                Assert.Null(actual[i].Metadata);
        }
        // Harness metadata must not become provider-visible response item fields.
        Assert.False(actual[^1].Item.TryGetProperty("compaction_without_file_error_sources", out _));
        Assert.Null(NewCheckpoint.Metadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DroppedFileSourceCannotBecomeClearAfterColdCompaction(bool fileAudio)
    {
        // Rust derives file-source provenance before retained-item selection,
        // and later compaction may trust only the marker on an existing ciphertext.
        var ct = TestContext.Current.CancellationToken;
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), ct);
        var turn = Guid.NewGuid();
        var older = fileAudio ? new CodexRetainedContextEntry(Json("""
            {"type":"message","role":"user","content":[{"type":"input_audio","audio":{"file_id":"file_audio"}}]}
            """)) : Message("user", "older");
        var newest = Message("user", "new");
        JsonElement[] original = [older.Item, newest.Item];
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", original, cancellationToken: ct);
        thread = await store.CompleteAsync(thread.Id, thread.Revision, turn, "owner", "completed", [], ct);
        var settings = new CodexResponseOptions("model", "instructions");
        var retention = new CodexRetainedContextOptions { MaximumTokens = 1 };

        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, ct);
        thread = await store.InstallCompactionAsync(candidate,
            new(candidate.Model, "response-first", NewCheckpoint.Item, null, candidate.History, candidate.ExecutedCallIds),
            retention, ct);

        var second = Json("""{"type":"compaction","encrypted_content":"second"}""");
        var restored = new CodexThreadStore(storage, "account");
        thread = await restored.ReadAsync(thread.Id, ct);
        var first = thread.CompactionCheckpoint!;
        Assert.Equal(2, first.Projection.Length);
        Assert.True(JsonElement.DeepEquals(newest.Item, first.Projection[0]));
        Assert.True(JsonElement.DeepEquals(NewCheckpoint.Item, first.Projection[1]));
        Assert.Equal(!fileAudio, CodexRetainedJournalMetadata.Flag(first.ProjectionMetadata?[1],
            "compaction_without_file_error_sources"));
        candidate = await restored.CaptureCompactionAsync(thread.Id, thread.Revision, settings, ct);
        Assert.Equal(2, candidate.History.Count);
        Assert.DoesNotContain(candidate.History, item => JsonElement.DeepEquals(older.Item, item));
        thread = await restored.InstallCompactionAsync(candidate,
            new(candidate.Model, "response-second", second, null, candidate.History, candidate.ExecutedCallIds),
            retention, ct);
        var final = await new CodexThreadStore(storage, "account").ReadAsync(thread.Id, ct);
        var installed = final.CompactionCheckpoint!;
        Assert.Equal(2, installed.Projection.Length);
        Assert.True(JsonElement.DeepEquals(newest.Item, installed.Projection[0]));
        Assert.True(JsonElement.DeepEquals(second, installed.Projection[1]));
        Assert.Equal(!fileAudio, CodexRetainedJournalMetadata.Flag(installed.ProjectionMetadata?[1],
            "compaction_without_file_error_sources"));
        Assert.Equal(original.Select(item => item.GetRawText()),
            Assert.Single(final.Turns).Input.Select(item => item.GetRawText()));
        Assert.Empty(final.Turns[0].Dispatches);
    }

}
