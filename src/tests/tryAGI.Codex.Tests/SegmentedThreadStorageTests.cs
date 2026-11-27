using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class SegmentedThreadStorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Roots : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        public bool LoseNextCommitReceipt { get; set; }
        public Func<CancellationToken, Task>? BeforeCas { get; set; }
        public int Publications { get; private set; }

        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(rows.TryGetValue(threadId, out var row) ? (JsonElement?)row.Clone() : null);
        }

        public async Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
            CancellationToken cancellationToken)
        {
            if (BeforeCas is { } before) await before(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? revision = rows.TryGetValue(threadId, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (revision != expectedRevision) return false;
                rows[threadId] = document.Clone();
                Publications++;
                if (LoseNextCommitReceipt)
                {
                    LoseNextCommitReceipt = false;
                    throw new IOException("Lost durable root CAS receipt.");
                }
                return true;
            }
        }

        public JsonElement Snapshot(Guid id)
        {
            lock (gate) return rows[id].Clone();
        }

        public void ReplaceForFaultInjection(Guid id, JsonElement raw)
        {
            lock (gate) rows[id] = raw.Clone();
        }
    }

    private sealed class Pages : ICodexThreadSegmentStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<(Guid Id, string Digest), byte[]> values = [];
        public int Created { get; private set; }
        public int PutCalls { get; private set; }
        public int Reads { get; private set; }
        public bool LoseNextPutReceipt { get; set; }
        public Func<CancellationToken, Task>? BeforeRead { get; set; }

        public async Task<ReadOnlyMemory<byte>?> ReadSegmentAsync(Guid threadId, string sha256, CancellationToken cancellationToken)
        {
            if (BeforeRead is { } before) await before(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                Reads++;
                return values.TryGetValue((threadId, sha256), out var data) ? new ReadOnlyMemory<byte>(data) : null;
            }
        }

        public Task PutSegmentAsync(Guid threadId, string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)));
            lock (gate)
            {
                PutCalls++;
                if (values.TryGetValue((threadId, sha256), out var prior))
                    Assert.True(prior.AsSpan().SequenceEqual(bytes.Span));
                else
                {
                    values.Add((threadId, sha256), bytes.ToArray());
                    Created++;
                }
                if (LoseNextPutReceipt)
                {
                    LoseNextPutReceipt = false;
                    throw new IOException("Lost durable page receipt.");
                }
            }
            return Task.CompletedTask;
        }

        public void Damage(Guid id, string digest, bool remove)
        {
            lock (gate)
            {
                if (remove) values.Remove((id, digest));
                else values[(id, digest)][0] ^= 1;
            }
        }
    }

    private static JsonElement Json(string text) => RealtimeHistoryTests.Json(text);

    private static JsonElement Settings(string prefix, string payload = "")
        => Json(new JsonObject { ["prefix"] = prefix, ["payload"] = payload }.ToJsonString());

    private static string Payload()
    {
        var bytes = new byte[1024 * 1024];
        new Random(173).NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string[] Hashes(JsonElement manifest)
        => manifest.GetProperty("pages").EnumerateArray().Select(page => page.GetProperty("sha256").GetString()!).ToArray();

    [Fact]
    public async Task InlineDocumentMigratesOnlyAtManifestCasAndRetainsExactNativeSnapshot()
    {
        var roots = new Roots();
        var pages = new Pages();
        var plain = new CodexThreadStore(roots, "account");
        var thread = await plain.CreateAsync(Guid.NewGuid(), Settings("inline"), Ct);
        var storage = new CodexSegmentedThreadStorage(roots, pages, "account");
        var segmented = new CodexThreadStore(storage, "account");
        var initial = await segmented.ReadAsync(thread.Id, Ct);
        Assert.Equal(thread.Revision, initial.Revision);
        Assert.False(roots.Snapshot(thread.Id).TryGetProperty("kind", out _));
        Assert.Equal(0, pages.PutCalls);

        var next = await segmented.UpdateSettingsAsync(thread.Id, initial.Revision, Settings("segmented", Payload()), Ct);
        var manifest = roots.Snapshot(thread.Id);
        Assert.Equal("codexThreadSegments", manifest.GetProperty("kind").GetString());
        Assert.Equal(next.Revision, manifest.GetProperty("revision").GetInt64());
        Assert.True(Hashes(manifest).Length > 1);
        var reloaded = await storage.ReadAsync(thread.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.Equal(CodexThreadStore.Encode(next).GetRawText(), reloaded.Value.GetRawText());

        var anotherAccount = new CodexSegmentedThreadStorage(roots, pages, "different-account");
        var reads = pages.Reads;
        await Assert.ThrowsAsync<InvalidDataException>(() => anotherAccount.ReadAsync(thread.Id, Ct));
        Assert.Equal(reads, pages.Reads);
    }

    [Fact]
    public async Task ContentDefinedPagesReuseUnchangedBytesAfterAChangedPrefix()
    {
        var roots = new Roots();
        var pages = new Pages();
        var store = new CodexThreadStore(new CodexSegmentedThreadStorage(roots, pages, "account"), "account");
        var payload = Payload();
        var first = await store.CreateAsync(Guid.NewGuid(), Settings("a", payload), Ct);
        var original = Hashes(roots.Snapshot(first.Id));
        var created = pages.Created;
        var second = await store.UpdateSettingsAsync(first.Id, first.Revision, Settings(new string('z', 137), payload), Ct);
        var updated = Hashes(roots.Snapshot(first.Id));
        Assert.True(original.Intersect(updated, StringComparer.Ordinal).Count() >= 2);
        Assert.True(pages.Created - created < updated.Length);
        var reloaded = await store.ReadAsync(first.Id, Ct);
        Assert.Equal(second.Revision, reloaded.Revision);
        Assert.Equal(payload, reloaded.Settings.GetProperty("payload").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrCorruptPageRejectsTheWholeSnapshot(bool missing)
    {
        var roots = new Roots();
        var pages = new Pages();
        var storage = new CodexSegmentedThreadStorage(roots, pages, "account");
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Settings("test", Payload()), Ct);
        pages.Damage(thread.Id, Hashes(roots.Snapshot(thread.Id))[0], missing);
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync(thread.Id, Ct));
        Assert.Equal(1, roots.Publications);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("revision")]
    [InlineData("length")]
    [InlineData("order")]
    public async Task ManifestTamperingCannotExposeAnUnverifiedDocument(string fault)
    {
        var roots = new Roots();
        var pages = new Pages();
        var storage = new CodexSegmentedThreadStorage(roots, pages, "account");
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Settings("test", Payload()), Ct);
        var root = JsonNode.Parse(roots.Snapshot(thread.Id).GetRawText())!.AsObject();
        switch (fault)
        {
            case "identity": root["id"] = Guid.NewGuid(); break;
            case "revision": root["revision"] = thread.Revision + 1; break;
            case "length": root["length"] = int.MaxValue; break;
            case "order":
                var list = root["pages"]!.AsArray();
                Assert.True(list.Count > 1);
                var a = list[0]!.DeepClone();
                var b = list[1]!.DeepClone();
                list[0] = b;
                list[1] = a;
                break;
        }
        roots.ReplaceForFaultInjection(thread.Id, Json(root.ToJsonString()));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync(thread.Id, Ct));
    }

    [Fact]
    public async Task LostPageReceiptNeverPublishesAnIncompleteManifestAndRetryIsIdempotent()
    {
        var roots = new Roots();
        var pages = new Pages();
        var storage = new CodexSegmentedThreadStorage(roots, pages, "account");
        var store = new CodexThreadStore(storage, "account");
        var first = await store.CreateAsync(Guid.NewGuid(), Settings("first"), Ct);
        var candidate = CodexThreadStore.Encode(first with { Revision = first.Revision + 1, Settings = Settings("next", Payload()) });
        pages.LoseNextPutReceipt = true;
        await Assert.ThrowsAsync<IOException>(() => storage.CompareExchangeAsync(first.Id, first.Revision, candidate, Ct));
        Assert.Equal(first.Revision, roots.Snapshot(first.Id).GetProperty("revision").GetInt64());
        Assert.Equal(1, roots.Publications);
        Assert.Equal("first", (await store.ReadAsync(first.Id, Ct)).Settings.GetProperty("prefix").GetString());

        Assert.True(await storage.CompareExchangeAsync(first.Id, first.Revision, candidate, Ct));
        var puts = pages.PutCalls;
        Assert.False(await storage.CompareExchangeAsync(first.Id, first.Revision, candidate, Ct));
        Assert.Equal(puts, pages.PutCalls);
        Assert.Equal(2, roots.Publications);
        Assert.Equal(candidate.GetRawText(), (await storage.ReadAsync(first.Id, Ct))!.Value.GetRawText());
    }

    [Fact]
    public async Task LostManifestReceiptRecoversExactlyTheSameRealtimeObservation()
    {
        var roots = new Roots();
        var pages = new Pages();
        CodexThreadStore Restart() => new(new CodexSegmentedThreadStorage(roots, pages, "account"), "account");
        var store = Restart();
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "session", "rtc_segmented", "v3", "owner", Ct);
        var attachment = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct);
        var source = RealtimeHistoryTests.Event("""{"type":"input_transcript.added","event_id":"e","item":{"text":"durable words"}}""");

        roots.LoseNextCommitReceipt = true;
        await Assert.ThrowsAsync<IOException>(() => store.RecordRealtimeObservationAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, "observation", source, Ct));
        var recovered = Restart();
        var persisted = await recovered.ReadAsync(thread.Id, Ct);
        Assert.Equal(thread.Revision + 1, persisted.Revision);
        var duplicate = await recovered.RecordRealtimeObservationAsync(thread.Id, persisted.Revision,
            "session", "owner", attachment, "observation", source, Ct);
        Assert.False(duplicate.Admitted);
        Assert.Equal(persisted.Revision, duplicate.Thread.Revision);
        Assert.Equal("durable words", Assert.Single(RealtimeHistoryTests.Session(duplicate.Thread).History!.Frames).Text);
        Assert.Single(RealtimeHistoryTests.Session(duplicate.Thread).History!.Events);
    }

    [Fact]
    public async Task ConcurrentWritersStageIndependentlyButOnlyOneRootWins()
    {
        var roots = new Roots();
        var pages = new Pages();
        var firstStorage = new CodexSegmentedThreadStorage(roots, pages, "account");
        var secondStorage = new CodexSegmentedThreadStorage(roots, pages, "account");
        var store = new CodexThreadStore(firstStorage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Settings("initial"), Ct);
        var first = CodexThreadStore.Encode(thread with { Revision = thread.Revision + 1, Settings = Settings("first") });
        var second = CodexThreadStore.Encode(thread with { Revision = thread.Revision + 1, Settings = Settings("second") });
        var arrived = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        roots.BeforeCas = async ct =>
        {
            if (Interlocked.Increment(ref arrived) == 2) barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        };
        var writes = await Task.WhenAll(firstStorage.CompareExchangeAsync(thread.Id, thread.Revision, first, Ct),
            secondStorage.CompareExchangeAsync(thread.Id, thread.Revision, second, Ct));
        Assert.Single(writes, value => value);
        Assert.Equal(2, roots.Publications);
        var actual = await firstStorage.ReadAsync(thread.Id, Ct);
        Assert.Equal((writes[0] ? first : second).GetRawText(), actual!.Value.GetRawText());
    }

    [Fact]
    public async Task ReaderOfAnOlderManifestRemainsConsistentDuringConcurrentPublication()
    {
        var roots = new Roots();
        var pages = new Pages();
        var storage = new CodexSegmentedThreadStorage(roots, pages, "account");
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Settings("old", Payload()), Ct);
        var old = CodexThreadStore.Encode(thread);
        var next = CodexThreadStore.Encode(thread with { Revision = thread.Revision + 1, Settings = Settings("new") });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pages.BeforeRead = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        };
        var pending = storage.ReadAsync(thread.Id, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(await storage.CompareExchangeAsync(thread.Id, thread.Revision, next, Ct));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(old.GetRawText(), (await pending)!.Value.GetRawText());
        Assert.Equal(next.GetRawText(), (await storage.ReadAsync(thread.Id, Ct))!.Value.GetRawText());
    }
}
