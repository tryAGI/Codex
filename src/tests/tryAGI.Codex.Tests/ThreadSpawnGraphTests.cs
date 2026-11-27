using System.Globalization;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class ThreadSpawnGraphTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class Storage : ICodexThreadSpawnGraphStorage
    {
        private readonly Dictionary<Guid, CodexThreadSpawnEdge> edges = [];
        private int revision;
        internal bool LoseAcknowledgement { get; set; }
        internal bool ConflictOnWrite { get; set; }
        internal int WriteCalls { get; private set; }
        internal Func<CodexThreadSpawnGraph, CodexThreadSpawnGraph>? Transform { get; set; }
        private string Revision => revision.ToString(CultureInfo.InvariantCulture);

        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Graph queries must not load journals.");

        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Graph mutations must not write journals.");

        public Task<CodexThreadSpawnGraph> ReadSpawnGraphAsync(string accountId, Guid rootThreadId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new CodexThreadSpawnGraph(accountId, rootThreadId, Revision, edges.Values.Reverse().ToArray());
            return Task.FromResult(Transform?.Invoke(result) ?? result);
        }

        public Task<bool> CompareExchangeSpawnEdgeAsync(string accountId, Guid rootThreadId, string expectedGraphRevision,
            CodexThreadSpawnEdge edge, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls++;
            if (ConflictOnWrite || expectedGraphRevision != Revision) return Task.FromResult(false);
            edges[edge.ChildThreadId] = edge;
            revision++;
            if (LoseAcknowledgement) throw new IOException("Simulated lost acknowledgement.");
            return Task.FromResult(true);
        }
    }

    private static async Task AddAsync(CodexThreadStore store, int parent, int child,
        CodexThreadSpawnEdgeStatus status = CodexThreadSpawnEdgeStatus.Open)
    {
        var graph = await store.ReadSpawnGraphAsync(Id(1), Ct);
        await store.UpsertSpawnEdgeAsync(Id(1), graph.Revision, new(Id(parent), Id(child), status), Ct);
    }

    [Fact]
    public async Task GraphOnlyDescendantsSurviveStoreRecreationAndSortAcrossEachDepth()
    {
        var database = new Storage();
        var store = new CodexThreadStore(database, "account");
        await AddAsync(store, 1, 4);
        await AddAsync(store, 1, 2);
        await AddAsync(store, 4, 3);
        await AddAsync(store, 2, 5);
        var reopened = new CodexThreadStore(database, "account");
        Assert.True(reopened.SupportsSpawnGraph);
        Assert.Equal(new[] { Id(2), Id(4) }, await reopened.ListSpawnChildrenAsync(Id(1), cancellationToken: Ct));
        Assert.Equal(new[] { Id(2), Id(4), Id(3), Id(5) }, await reopened.ListSpawnDescendantsAsync(Id(1), cancellationToken: Ct));
    }

    [Fact]
    public async Task FilterPrunesEntireClosedBranchAndMissingStatusUpdateIsNoOp()
    {
        var database = new Storage();
        var store = new CodexThreadStore(database, "account");
        await AddAsync(store, 1, 2, CodexThreadSpawnEdgeStatus.Closed);
        await AddAsync(store, 2, 3);
        await AddAsync(store, 1, 4);
        Assert.Equal(new[] { Id(4) }, await store.ListSpawnDescendantsAsync(Id(1), CodexThreadSpawnEdgeStatus.Open, Ct));
        Assert.Equal(new[] { Id(2) }, await store.ListSpawnDescendantsAsync(Id(1), CodexThreadSpawnEdgeStatus.Closed, Ct));
        var graph = await store.ReadSpawnGraphAsync(Id(1), Ct);
        await store.SetSpawnEdgeStatusAsync(Id(1), graph.Revision, Id(9), CodexThreadSpawnEdgeStatus.Closed, Ct);
        Assert.Equal(3, database.WriteCalls);
        await store.SetSpawnEdgeStatusAsync(Id(1), graph.Revision, Id(2), CodexThreadSpawnEdgeStatus.Open, Ct);
        Assert.Equal(new[] { Id(2), Id(4), Id(3) }, await store.ListSpawnDescendantsAsync(Id(1), CodexThreadSpawnEdgeStatus.Open, Ct));
    }

    [Fact]
    public async Task UpsertReparentsAndRejectsCyclesAndStaleRevisions()
    {
        var database = new Storage();
        var store = new CodexThreadStore(database, "account");
        var stale = (await store.ReadSpawnGraphAsync(Id(1), Ct)).Revision;
        await AddAsync(store, 1, 2);
        await AddAsync(store, 2, 3);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.UpsertSpawnEdgeAsync(Id(1), stale,
            new(Id(1), Id(4), CodexThreadSpawnEdgeStatus.Open), Ct));
        var graph = await store.ReadSpawnGraphAsync(Id(1), Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertSpawnEdgeAsync(Id(1), graph.Revision,
            new(Id(3), Id(2), CodexThreadSpawnEdgeStatus.Open), Ct));
        await AddAsync(store, 1, 3);
        Assert.Equal(new[] { Id(2), Id(3) }, await store.ListSpawnChildrenAsync(Id(1), cancellationToken: Ct));
        Assert.Equal(3, database.WriteCalls);
    }

    [Fact]
    public async Task CompareExchangeConflictAndLostAcknowledgementAreNeverReplayed()
    {
        var database = new Storage { ConflictOnWrite = true };
        var store = new CodexThreadStore(database, "account");
        var graph = await store.ReadSpawnGraphAsync(Id(1), Ct);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.UpsertSpawnEdgeAsync(Id(1), graph.Revision,
            new(Id(1), Id(2), CodexThreadSpawnEdgeStatus.Open), Ct));
        database.ConflictOnWrite = false;
        database.LoseAcknowledgement = true;
        await Assert.ThrowsAsync<IOException>(() => store.UpsertSpawnEdgeAsync(Id(1), graph.Revision,
            new(Id(1), Id(2), CodexThreadSpawnEdgeStatus.Open), Ct));
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.UpsertSpawnEdgeAsync(Id(1), graph.Revision,
            new(Id(1), Id(2), CodexThreadSpawnEdgeStatus.Open), Ct));
        Assert.Equal(2, database.WriteCalls);
        Assert.Equal(new[] { Id(2) }, await store.ListSpawnChildrenAsync(Id(1), cancellationToken: Ct));
    }

    [Fact]
    public async Task InvalidGraphCannotBeUsedForMutation()
    {
        var database = new Storage();
        var store = new CodexThreadStore(database, "account");
        database.Transform = graph => graph with { AccountId = "foreign" };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadSpawnGraphAsync(Id(1), Ct));
        database.Transform = graph => graph with
        {
            Edges = [new(Id(2), Id(3), CodexThreadSpawnEdgeStatus.Open), new(Id(3), Id(2), CodexThreadSpawnEdgeStatus.Open)]
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadSpawnGraphAsync(Id(1), Ct));
        database.Transform = graph => graph with
        {
            Edges = [new(Id(1), Id(2), CodexThreadSpawnEdgeStatus.Open), new(Id(3), Id(2), CodexThreadSpawnEdgeStatus.Open)]
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadSpawnGraphAsync(Id(1), Ct));
        Assert.Equal(0, database.WriteCalls);
    }
}
