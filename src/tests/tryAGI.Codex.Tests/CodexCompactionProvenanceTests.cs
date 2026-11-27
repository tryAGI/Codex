using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexCompactionProvenanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static CodexRetainedContextEntry Entry(string item, string? metadata = null)
        => new(Json(item), metadata is null ? null : Json(metadata));

    private static readonly CodexRetainedContextEntry User = Entry("""{"role":"user","content":"hello"}""");
    private static readonly CodexRetainedContextEntry Summary = Entry("""{"type":"compaction","encrypted_content":"opaque"}""");
    private static readonly CodexRetainedContextEntry ResultMail = Entry("""
        {"type":"agent_message","author":"parent/child","recipient":"parent","content":[{"type":"input_text","text":"Message Type: FINAL_ANSWER\nresult"}]}
        """);
    private static bool Clear(CodexRetainedContextEntry entry) =>
        CodexRetainedJournalMetadata.Flag(entry.Metadata, "compaction_without_file_error_sources");

    [Theory]
    [InlineData("""{"role":"user","content":[{"type":"input_image","image":{"image_url":"data:image/png;base64,abc"}}]}""", true)]
    [InlineData("""{"role":"user","content":[{"type":"input_image","image_url":"https://example.test/image.png"}]}""", true)]
    [InlineData("""{"role":"user","content":[{"type":"input_image","image":{"file_id":"file"}}]}""", false)]
    [InlineData("""{"role":"user","content":[{"type":"input_image","file_id":"file","image_url":"inline"}]}""", false)]
    [InlineData("""{"role":"user","content":[{"type":"input_audio","audio":{"file_id":"file"}}]}""", false)]
    [InlineData("""{"role":"user","content":[{"type":"input_audio","audio":{"audio_url":"data:audio/wav;base64,abc"}}]}""", false)]
    [InlineData("""{"type":"function_call_output","call_id":"call","output":[{"type":"input_audio","audio":{"file_id":"file"}}]}""", false)]
    [InlineData("""{"type":"custom_tool_call_output","call_id":"call","output":[{"type":"input_image","image":{"file_id":"file"}}]}""", false)]
    [InlineData("""{"type":"function_call_output","call_id":"call","output":"text only"}""", true)]
    [InlineData("""{"role":"user","content":[{"type":"future_file","id":"unknown"}]}""", false)]
    public void FileSourceProvenanceIsDerivedBeforeRetention(string source, bool expected)
    {
        // Even a dropped file source participated in the opaque summary.
        var selected = CodexRetainedContext.Select([Entry(source), User], new() { MaximumTokens = 1 });
        Assert.Equal(expected, selected.CheckpointInputWithoutFileErrorSources);
        Assert.Equal(expected, Clear(selected.WithCheckpoint(Summary)[^1]));
        Assert.False(Summary.Item.TryGetProperty("compaction_without_file_error_sources", out _));
    }

    [Fact]
    public void OpaqueCheckpointNeedsTrustedProvenanceAndCannotSelfAssertIt()
    {
        var spoofed = Entry("""{"type":"compaction","encrypted_content":"old","compaction_without_file_error_sources":true}""");
        var inherited = Summary with { Metadata = Json("""{"compaction_without_file_error_sources":true}""") };
        Assert.False(Clear(CodexRetainedContext.Select([spoofed, User]).WithCheckpoint(inherited)[^1]));
        Assert.True(Clear(CodexRetainedContext.Select([inherited, User]).WithCheckpoint(Summary)[^1]));
        Assert.False(Clear(CodexRetainedContext.Select([inherited,
            Entry("""{"type":"context_compaction","encrypted_content":"unknown"}""")]).WithCheckpoint(Summary)[^1]));
    }

    [Fact]
    public void SamplingWatermarkSurvivesDroppedSourcesAndNeverAdvancesDuringCompaction()
    {
        var sampledMail = ResultMail with { Metadata = Json("""{"history_sequence":8}""") };
        var pendingMail = ResultMail with { Metadata = Json("""{"history_sequence":10}""") };
        var dropped = Entry("""{"role":"assistant","content":"discarded answer"}""", """{"history_sequence":9,"sampled_through_sequence":8}""");
        var later = User with { Metadata = Json("""{"history_sequence":11}""") };
        var selection = CodexRetainedContext.Select([sampledMail, dropped, pendingMail, later],
            new() { SampledThroughSequence = 1 });
        Assert.Equal(8, selection.SampledThroughSequence);
        Assert.Equal(10, selection.PendingSequence);
        var projection = selection.WithCheckpoint(Summary);
        Assert.Equal(3, projection.Count);
        Assert.True(JsonElement.DeepEquals(Summary.Item, projection[0].Item));
        Assert.Equal(8, projection[0].Metadata!.Value.GetProperty("sampled_through_sequence").GetInt64());
        Assert.Equal(10, CodexRetainedContext.Sequence(projection[1]));
        var again = CodexRetainedContext.Select(projection).WithCheckpoint(Summary);
        Assert.Equal(3, again.Count);
        Assert.Equal(8, again[0].Metadata!.Value.GetProperty("sampled_through_sequence").GetInt64());
        Assert.Equal(10, CodexRetainedContext.Sequence(again[1]));
        Assert.False(again[0].Item.TryGetProperty("sampled_through_sequence", out _));
    }

    [Fact]
    public void LegacyMailRemainsPendingWithoutInventingDeliveryEvidence()
    {
        var selection = CodexRetainedContext.Select([User, ResultMail, User]);
        Assert.Null(selection.PendingSequence);
        Assert.True(selection.HasUnsequencedPendingMail);
        Assert.Equal(0, selection.SampledThroughSequence);
        var projection = selection.WithCheckpoint(Summary);
        Assert.Equal(4, projection.Count);
        Assert.True(JsonElement.DeepEquals(Summary.Item, projection[1].Item));
        Assert.True(JsonElement.DeepEquals(ResultMail.Item, projection[2].Item));
        Assert.False(projection[1].Metadata!.Value.TryGetProperty("sampled_through_sequence", out _));
    }

    [Fact]
    public void OversizedPendingMailStillPlacesSummaryBeforeLaterInput()
    {
        var node = JsonNode.Parse(ResultMail.Item.GetRawText())!.AsObject();
        node["content"]![0]!["text"] = new string('x', 41000);
        var oversized = new CodexRetainedContextEntry(Json(node.ToJsonString()), Json("""{"history_sequence":9}"""));
        var later = User with { Metadata = Json("""{"history_sequence":10}""") };
        var selection = CodexRetainedContext.Select([oversized, later], new() { SampledThroughSequence = 8 });
        Assert.Equal(9, selection.PendingSequence);
        Assert.Single(selection.Entries);
        Assert.True(JsonElement.DeepEquals(Summary.Item, selection.WithCheckpoint(Summary)[0].Item));
    }

    [Fact]
    public async Task NativeCheckpointPreservesPendingMailAndSamplingEvidenceAfterReload()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner",
            [ResultMail.Item, ResultMail.Item, User.Item], cancellationToken: Ct);
        var answer = Json("""{"role":"assistant","content":"answer"}""");
        thread = await store.CompleteAsync(thread.Id, thread.Revision, turn, "owner", "completed", [answer], Ct);
        var annotations = new[]
        {
            CodexStoredRetainedContextMetadata.Create(false, 0, ResultMail.Item, Json("""{"history_sequence":7}""")),
            CodexStoredRetainedContextMetadata.Create(false, 1, ResultMail.Item, Json("""{"history_sequence":9}""")),
            CodexStoredRetainedContextMetadata.Create(false, 2, User.Item, Json("""{"history_sequence":10}""")),
            CodexStoredRetainedContextMetadata.Create(true, 0, answer, Json("""{"history_sequence":11,"sampled_through_sequence":8}""")),
        };
        thread = await store.SetRetainedContextMetadataAsync(thread.Id, thread.Revision, turn, annotations, Ct);
        var settings = new CodexResponseOptions("model", "instructions");
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        thread = await store.InstallCompactionAsync(candidate, new(candidate.Model, "response", Summary.Item,
            null, candidate.History, candidate.ExecutedCallIds), cancellationToken: Ct);
        var restored = new CodexThreadStore(storage, "account");
        var reloaded = await restored.ReadAsync(thread.Id, Ct);
        var checkpoint = reloaded.CompactionCheckpoint!;
        Assert.Equal(3, checkpoint.Projection.Length);
        Assert.Equal(8, checkpoint.ProjectionMetadata![0]!.Value.GetProperty("sampled_through_sequence").GetInt64());
        Assert.True(checkpoint.ProjectionMetadata[0]!.Value.GetProperty("compaction_without_file_error_sources").GetBoolean());
        Assert.Equal(9, checkpoint.ProjectionMetadata[1]!.Value.GetProperty("history_sequence").GetInt64());
        candidate = await restored.CaptureCompactionAsync(reloaded.Id, reloaded.Revision, settings, Ct);
        var repeated = await restored.InstallCompactionAsync(candidate, new(candidate.Model, "second", Summary.Item,
            null, candidate.History, candidate.ExecutedCallIds), cancellationToken: Ct);
        Assert.Equal(3, repeated.CompactionCheckpoint!.Projection.Length);
        Assert.True(CodexRetainedJournalMetadata.Same(checkpoint.ProjectionMetadata, repeated.CompactionCheckpoint.ProjectionMetadata!));
    }

    [Fact]
    public async Task SourceAnnotationCannotForgeCheckpointProvenance()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner", [User.Item], cancellationToken: Ct);
        thread = await store.CompleteAsync(thread.Id, thread.Revision, turn, "owner", "completed", [], Ct);
        var invalid = CodexStoredRetainedContextMetadata.Create(false, 0, User.Item,
            Json("""{"compaction_without_file_error_sources":true}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetRetainedContextMetadataAsync(
            thread.Id, thread.Revision, turn, [invalid], Ct));
        Assert.Equal(thread.Revision, (await store.ReadAsync(thread.Id, Ct)).Revision);
    }
}
