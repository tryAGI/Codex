using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RetainedJournalMetadataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static readonly JsonElement User = Json("""{"role":"user","content":"same user text"}""");
    private static readonly JsonElement Developer = Json("""{"role":"developer","content":"client context"}""");
    private static readonly JsonElement Call = Json("""{"type":"function_call","call_id":"executed","name":"read","arguments":"{}"}""");
    private static readonly JsonElement Output = Json("""{"type":"function_call_output","call_id":"executed","output":"receipt"}""");
    private static readonly JsonElement Always = Json("""{"retain_across_windows":true,"future":{"preserve":42}}""");
    private static readonly JsonElement Checkpoint = Json("""{"type":"compaction","encrypted_content":"opaque"}""");

    private static CodexResponseOptions Settings(Guid thread) => new("model", "instructions")
    { Identity = new("session", thread.ToString(), Guid.NewGuid().ToString()) };

    private static CodexCompactionResult Result(CodexThreadCompactionCandidate candidate, string id = "response")
        => new(candidate.Model, id, Checkpoint, null, candidate.History, candidate.ExecutedCallIds);

    private static async Task<(RealtimeHistoryTests.Storage Storage, CodexThreadStore Store, CodexThreadDocument Thread)> Setup(bool tool = false)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "owner",
            [User, User, Developer], cancellationToken: Ct);
        if (tool)
        {
            thread = (await store.AdmitAsync(thread.Id, thread.Revision, turn, "owner", "executed", "tool", Call, Ct)).Thread;
            thread = await store.SettleAsync(thread.Id, thread.Revision, turn, "owner", "executed", "succeeded", Output, Ct);
        }
        thread = await store.CompleteAsync(thread.Id, thread.Revision, turn, "owner", "completed",
            tool ? [Call, Output] : [], Ct);
        return (storage, store, thread);
    }

    [Fact]
    public async Task ExactOccurrenceMetadataSurvivesRestartAndRepeatedCompaction()
    {
        var (storage, store, thread) = await Setup();
        var turn = thread.Turns[0].Id;
        var first = Json("""{"history_sequence":1,"future":"first"}""");
        var second = Json("""{"history_sequence":2,"future":"second"}""");
        var metadata = new[]
        {
            CodexStoredRetainedContextMetadata.Create(false, 0, User, first),
            CodexStoredRetainedContextMetadata.Create(false, 1, User, second),
            CodexStoredRetainedContextMetadata.Create(false, 2, Developer, Always),
        };
        thread = await store.SetRetainedContextMetadataAsync(thread.Id, thread.Revision, turn, metadata, Ct);
        var restored = new CodexThreadStore(storage, "account");
        thread = await restored.ReadAsync(thread.Id, Ct);
        var settings = Settings(thread.Id);
        var candidate = await restored.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        Assert.Equal(3, candidate.HistoryMetadata.Count);
        Assert.True(JsonElement.DeepEquals(first, candidate.HistoryMetadata[0]!.Value));
        Assert.True(JsonElement.DeepEquals(second, candidate.HistoryMetadata[1]!.Value));
        Assert.True(JsonElement.DeepEquals(Always, candidate.HistoryMetadata[2]!.Value));
        thread = await restored.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct);
        var checkpoint = thread.CompactionCheckpoint!;
        Assert.Equal(4, checkpoint.Projection.Length);
        Assert.Equal(4, checkpoint.ProjectionMetadata!.Length);
        Assert.True(checkpoint.ProjectionMetadata[3]!.Value.GetProperty("compaction_without_file_error_sources").GetBoolean());
        Assert.False(checkpoint.Projection[3].TryGetProperty("compaction_without_file_error_sources", out _));
        var malformed = checkpoint with
        {
            Projection = [.. checkpoint.Projection, Json("""{"type":"compaction","encrypted_content":""}""")],
            ProjectionMetadata = [.. checkpoint.ProjectionMetadata, null],
        };
        Assert.Throws<InvalidDataException>(() => restored.ValidateSnapshot(CodexThreadStore.Encode(
            thread with { CompactionCheckpoint = malformed })));
        Assert.True(JsonElement.DeepEquals(User, checkpoint.Projection[0]));
        Assert.True(JsonElement.DeepEquals(User, checkpoint.Projection[1]));
        Assert.True(JsonElement.DeepEquals(Developer, checkpoint.Projection[2]));
        Assert.False(checkpoint.Projection[2].TryGetProperty("retain_across_windows", out _));
        var nextCandidate = await restored.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        Assert.True(CodexRetainedJournalMetadata.Same(checkpoint.ProjectionMetadata, nextCandidate.HistoryMetadata));
        var next = await restored.InstallCompactionAsync(nextCandidate, Result(nextCandidate, "response-2"), cancellationToken: Ct);
        Assert.True(CodexRetainedJournalMetadata.Same(checkpoint.ProjectionMetadata, next.CompactionCheckpoint!.ProjectionMetadata!));
        Assert.True(JsonElement.DeepEquals(first, next.Turns[0].RetainedContextMetadata![0].Metadata));
        var fork = await restored.ForkAsync(next.Id, next.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.CompactionCheckpoint);
        Assert.Equal(3, fork.Turns[0].RetainedContextMetadata!.Length);
    }

    [Fact]
    public async Task SettledToolExchangeIsRetainedAtomicallyWithoutDispatchAuthority()
    {
        var (_, store, thread) = await Setup(tool: true);
        var turn = thread.Turns[0].Id;
        var annotations = new[]
        {
            CodexStoredRetainedContextMetadata.Create(true, 0, Call, Always),
            CodexStoredRetainedContextMetadata.Create(true, 1, Output, Always),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetRetainedContextMetadataAsync(
            thread.Id, thread.Revision, turn, [annotations[0]], Ct));
        thread = await store.SetRetainedContextMetadataAsync(thread.Id, thread.Revision, turn, annotations, Ct);
        var settings = Settings(thread.Id);
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        var installed = await store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct);
        var checkpoint = installed.CompactionCheckpoint!;
        var index = Array.FindIndex(checkpoint.Projection, item => JsonElement.DeepEquals(Call, item));
        Assert.True(index >= 0);
        Assert.True(JsonElement.DeepEquals(Output, checkpoint.Projection[index + 1]));
        Assert.True(JsonElement.DeepEquals(Always, checkpoint.ProjectionMetadata![index]!.Value));
        Assert.True(JsonElement.DeepEquals(Always, checkpoint.ProjectionMetadata[index + 1]!.Value));
        Assert.Contains("executed", CodexAgent.ExecutedIds(checkpoint.Projection, CodexThreadStore.DispatchedToolCallIds(installed)));
        var forged = checkpoint with
        {
            Projection = checkpoint.Projection.Where((_, position) => position != index + 1).ToArray(),
            ProjectionMetadata = checkpoint.ProjectionMetadata.Where((_, position) => position != index + 1).ToArray(),
        };
        Assert.Throws<InvalidDataException>(() => store.ValidateSnapshot(CodexThreadStore.Encode(installed with { CompactionCheckpoint = forged })));
        var tiny = await store.CaptureCompactionAsync(installed.Id, installed.Revision, settings, Ct);
        var trimmed = await store.InstallCompactionAsync(tiny, Result(tiny, "response-small"),
            new() { MaximumTokens = 1 }, Ct);
        Assert.Single(trimmed.CompactionCheckpoint!.Projection);
        Assert.Contains("executed", CodexThreadStore.DispatchedToolCallIds(trimmed));
    }

    [Fact]
    public async Task MetadataChangesInvalidateSpeculationAndCoveredProjection()
    {
        var (_, store, thread) = await Setup();
        var settings = Settings(thread.Id);
        var stale = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        var annotation = CodexStoredRetainedContextMetadata.Create(false, 2, Developer, Always);
        var turn = thread.Turns[0].Id;
        thread = await store.SetRetainedContextMetadataAsync(thread.Id, thread.Revision, turn, [annotation], Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.InstallBackgroundCompactionAsync(
            stale, Result(stale), settings, thread.Revision, cancellationToken: Ct));
        var candidate = await store.CaptureCompactionAsync(thread.Id, thread.Revision, settings, Ct);
        thread = await store.InstallCompactionAsync(candidate, Result(candidate), cancellationToken: Ct);
        Assert.NotNull(thread.CompactionCheckpoint);
        thread = await store.SetRetainedContextMetadataAsync(thread.Id, thread.Revision, turn, [], Ct);
        Assert.Null(thread.CompactionCheckpoint);
        Assert.Equal(3, thread.Turns[0].Input.Length);
    }

    [Fact]
    public async Task TamperedPositionsAndDigestAreRejectedAndNoWriteOccurs()
    {
        var (_, store, thread) = await Setup();
        var annotation = CodexStoredRetainedContextMetadata.Create(false, 2, Developer, Always);
        var turn = thread.Turns[0].Id;
        foreach (var invalid in new[] { annotation with { Index = 1 }, annotation with { Index = 99 },
            annotation with { ItemSha256 = new string('0', 64) } })
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SetRetainedContextMetadataAsync(
                thread.Id, thread.Revision, turn, [invalid], Ct));
        Assert.Equal(thread.Revision, (await store.ReadAsync(thread.Id, Ct)).Revision);
        var malformed = thread with
        {
            Turns = [thread.Turns[0] with { RetainedContextMetadata = [annotation with { Index = 1 }] }],
        };
        Assert.Throws<InvalidDataException>(() => store.ValidateSnapshot(CodexThreadStore.Encode(malformed)));
        var active = await store.StartTurnAsync(thread.Id, thread.Revision, Guid.NewGuid(), "owner", [User], cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetRetainedContextMetadataAsync(
            active.Id, active.Revision, turn, [annotation], Ct));
    }
}
