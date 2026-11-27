using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class AgentIdentityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class Storage : ICodexAgentIdentityStorage
    {
        internal CodexAgentIdentitySnapshot Snapshot { get; set; } = new(
            new("account", Id(1), "revision", []), []);
        internal int Reads { get; private set; }

        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Identity restoration must not read history.");

        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected write.");

        public Task<CodexThreadSpawnGraph> ReadSpawnGraphAsync(string accountId, Guid rootThreadId,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Snapshot must contain graph and metadata together.");

        public Task<bool> CompareExchangeSpawnEdgeAsync(string accountId, Guid rootThreadId, string expectedGraphRevision,
            CodexThreadSpawnEdge edge, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected write.");

        public Task<CodexAgentIdentitySnapshot> ReadAgentIdentitySnapshotAsync(string accountId, Guid rootThreadId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Snapshot);
        }
    }

    private static CodexThreadSpawnEdge Edge(int parent, int child, bool closed = false)
        => new(Id(parent), Id(child), closed ? CodexThreadSpawnEdgeStatus.Closed : CodexThreadSpawnEdgeStatus.Open);

    private static CodexNamedAgentIdentity Child(int parent, int child, string path, string? role = null)
        => new(Id(child), Id(parent), Id(1), path, role);

    [Fact]
    public void AgentPathMatchesRustAbsoluteAndRelativeGrammar()
    {
        Assert.Equal("/root", CodexAgentPath.Parse("/root"));
        Assert.Equal("root", CodexAgentPath.Name("/root"));
        Assert.Equal("/morpheus", CodexAgentPath.Parse("/morpheus"));
        Assert.Equal("/root/worker_2", CodexAgentPath.Join("/root", "worker_2"));
        Assert.Equal("/root/worker_2/child", CodexAgentPath.Resolve("/root/worker_2", "child"));
        Assert.Equal("/root/another/child", CodexAgentPath.Resolve("/root/worker_2", "/root/another/child"));
        Assert.Equal("/root", CodexAgentPath.Resolve("/root/worker_2", "/root"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/root/")]
    [InlineData("/root/root")]
    [InlineData("/root/../child")]
    [InlineData("/root/Worker")]
    [InlineData("/root/a-b")]
    [InlineData("/root//child")]
    [InlineData("/morpheus/child")]
    [InlineData("/root/é")]
    public void InvalidAbsolutePathsFailClosed(string path)
        => Assert.Throws<ArgumentException>(() => CodexAgentPath.Parse(path));

    [Theory]
    [InlineData("")]
    [InlineData("../sibling")]
    [InlineData("child/")]
    [InlineData("root")]
    [InlineData("child//grandchild")]
    public void InvalidRelativeReferencesFailClosed(string reference)
        => Assert.Throws<ArgumentException>(() => CodexAgentPath.Resolve("/root/worker", reference));

    [Fact]
    public async Task RestoreUsesOneSnapshotAndAncestorFirstOrderWithoutHistoryReads()
    {
        var storage = new Storage
        {
            Snapshot = new(new("account", Id(1), "one", [Edge(4, 3), Edge(1, 4), Edge(1, 2), Edge(2, 5), Edge(1, 6)]),
                [Child(4, 3, "/root/right/child"), Child(1, 4, "/root/right", "worker"),
                 Child(2, 5, "/root/left/child"), Child(1, 2, "/root/left")])
        };
        var store = new CodexThreadStore(storage, "account");
        var directory = await store.ReadAgentIdentitiesAsync(Id(1), Ct);
        Assert.True(store.SupportsAgentIdentities);
        Assert.Equal(1, storage.Reads);
        Assert.Equal(new[] { Id(2), Id(4), Id(3), Id(5) }, directory.RestorableDescendants.Select(i => i.ThreadId));
        Assert.Equal(new[] { Id(4), Id(3) }, directory.RestoreChain(Id(3)).Select(i => i.ThreadId));
        Assert.Equal(Id(5), directory.Resolve(Id(2), "child").ThreadId);
        Assert.Equal(Id(3), directory.Resolve(Id(2), "/root/right/child").ThreadId);
        Assert.Equal("worker", directory.GetActive(Id(4)).Role);
        Assert.Throws<KeyNotFoundException>(() => directory.GetActive(Id(6)));
        Assert.Throws<KeyNotFoundException>(() => directory.GetRecorded(Id(6)));
    }

    [Fact]
    public async Task ClosedBranchesAreNotRestorableAndHistoricalNamesCanBeReused()
    {
        var storage = new Storage
        {
            Snapshot = new(new("account", Id(1), "one", [Edge(1, 2, true), Edge(2, 3), Edge(1, 4)]),
                [Child(1, 2, "/root/worker"), Child(2, 3, "/root/worker/child"), Child(1, 4, "/root/worker")])
        };
        var directory = await new CodexThreadStore(storage, "account").ReadAgentIdentitiesAsync(Id(1), Ct);
        Assert.Equal(Id(4), Assert.Single(directory.RestorableDescendants).ThreadId);
        Assert.Equal(Id(4), directory.Resolve(Id(1), "worker").ThreadId);
        Assert.Equal(Id(2), directory.GetRecorded(Id(2)).ThreadId);
        Assert.Throws<KeyNotFoundException>(() => directory.GetActive(Id(3)));
        Assert.Throws<KeyNotFoundException>(() => directory.Resolve(Id(2), "child"));
    }

    [Fact]
    public async Task RejectsForeignGraphInconsistentParentMissingAncestorAndActiveCollision()
    {
        var graph = new CodexThreadSpawnGraph("account", Id(1), "one", [Edge(1, 2), Edge(2, 3), Edge(1, 4)]);
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        storage.Snapshot = new(graph with { AccountId = "foreign" }, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentIdentitiesAsync(Id(1), Ct));
        storage.Snapshot = new(graph, [Child(4, 3, "/root/other/child")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentIdentitiesAsync(Id(1), Ct));
        storage.Snapshot = new(graph, [Child(2, 3, "/root/worker/child")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentIdentitiesAsync(Id(1), Ct));
        storage.Snapshot = new(graph, [Child(1, 2, "/root/worker"), Child(2, 3, "/root/wrong/child")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentIdentitiesAsync(Id(1), Ct));
        storage.Snapshot = new(graph, [Child(1, 2, "/root/worker"), Child(1, 4, "/root/worker")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAgentIdentitiesAsync(Id(1), Ct));
    }
}
