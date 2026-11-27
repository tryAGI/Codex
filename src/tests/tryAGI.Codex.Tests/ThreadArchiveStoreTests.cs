using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class ThreadArchiveStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];

        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(rows.TryGetValue(id, out var row) ? (JsonElement?)row.Clone() : null);
        }

        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? before = rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (before != revision) return Task.FromResult(false);
                rows[id] = body.Clone();
                return Task.FromResult(true);
            }
        }
    }

    [Fact]
    public async Task ArchiveSurvivesReloadAndFencesExecutionWithoutDeletingHistory()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        var id = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var before = await store.CreateAsync(id, Json("{}"), Ct);
        var running = await store.StartTurnAsync(id, before.Revision, turnId, "owner", [], cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ArchiveAsync(id, running.Revision, Ct));
        var completed = await store.CompleteAsync(id, running.Revision, turnId, "owner", "completed", [], Ct);
        var archived = await store.ArchiveAsync(id, completed.Revision, Ct);
        Assert.NotNull(archived.ArchivedAt);
        Assert.Equal(completed.Revision + 1, archived.Revision);

        var cold = new CodexThreadStore(storage, "account");
        var saved = await cold.ReadAsync(id, Ct);
        Assert.Equal(archived.ArchivedAt, saved.ArchivedAt);
        Assert.Equal(turnId, Assert.Single(saved.Turns).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => cold.StartTurnAsync(id, saved.Revision, Guid.NewGuid(), "owner", [], cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cold.CreateDirectRealtimeSessionAsync(id, saved.Revision, "voice", "v3", "owner", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => cold.SetNameAsync(id, saved.Revision, "no", Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => cold.UnarchiveAsync(id, completed.Revision, Ct));
        Assert.Equal(saved.Revision, (await cold.ReadAsync(id, Ct)).Revision);
        var fork = await cold.ForkAsync(id, saved.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.ArchivedAt);
        Assert.Single(fork.Turns);

        var restored = await cold.UnarchiveAsync(id, saved.Revision, Ct);
        Assert.Null(restored.ArchivedAt);
        Assert.Single(restored.Turns);
        Assert.Equal(saved.Revision + 1, restored.Revision);
        await Assert.ThrowsAsync<ArgumentException>(() => cold.UnarchiveAsync(id, restored.Revision, Ct));
        var next = await cold.StartTurnAsync(id, restored.Revision, Guid.NewGuid(), "owner", [], cancellationToken: Ct);
        Assert.Equal(2, next.Turns.Length);
    }

    [Fact]
    public async Task ArchiveRejectsOpenRealtimeEphemeralAndForeignAccount()
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        var id = Guid.NewGuid();
        var created = await store.CreateAsync(id, Json("{}"), Ct);
        var voice = await store.CreateDirectRealtimeSessionAsync(id, created.Revision, "voice", "v3", "owner", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ArchiveAsync(id, voice.Revision, Ct));
        Assert.Null((await store.ReadAsync(id, Ct)).ArchivedAt);

        var ephemeral = await store.CreateAsync(Guid.NewGuid(), Json("""{"ephemeral":true}"""), Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ArchiveAsync(ephemeral.Id, ephemeral.Revision, Ct));
        var foreign = new CodexThreadStore(storage, "another-account");
        await Assert.ThrowsAsync<InvalidDataException>(() => foreign.ArchiveAsync(id, voice.Revision, Ct));
        Assert.Equal(voice.Revision, (await store.ReadAsync(id, Ct)).Revision);
    }
}
