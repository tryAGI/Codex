using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class Storage : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        public bool ThrowAfterCommit { get; set; }
        public bool ThrowBeforeCommit { get; set; }
        public Func<JsonElement, JsonElement, JsonElement>? CompetingWriteOnce { get; set; }

        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                return Task.FromResult(rows.TryGetValue(id, out var row) ? (JsonElement?)row.Clone() : null);
        }

        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? prior = rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (prior != revision) return Task.FromResult(false);
                if (CompetingWriteOnce is { } compete)
                {
                    CompetingWriteOnce = null;
                    rows[id] = compete(row, body).Clone();
                    return Task.FromResult(false);
                }
                if (ThrowBeforeCommit) { ThrowBeforeCommit = false; throw new IOException("Write failed before commit."); }
                rows[id] = body.Clone();
                if (ThrowAfterCommit) { ThrowAfterCommit = false; throw new IOException("Commit receipt lost."); }
                return Task.FromResult(true);
            }
        }
    }

    internal static JsonElement Json(string value)
    {
        using var parsed = JsonDocument.Parse(value);
        return parsed.RootElement.Clone();
    }

    internal static CodexRealtimeEvent Event(string value)
    {
        var raw = Json(value);
        return new(raw.GetProperty("type").GetString()!, raw, 0);
    }

    internal static CodexStoredRealtimeSession Session(CodexThreadDocument document)
        => Assert.Single(document.RealtimeSessions ?? []);

    internal static async Task<(Storage Storage, CodexThreadStore Store, CodexThreadDocument Thread, Guid Attachment)> Setup(
        CodexRealtimeHistoryMode mode = CodexRealtimeHistoryMode.Canonical)
    {
        var storage = new Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, "session", "rtc_test", "v3", "owner", Ct);
        thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, "session", "owner", mode, Ct);
        var attachment = Guid.NewGuid();
        thread = await store.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct);
        return (storage, store, thread, attachment);
    }

    internal static async Task<CodexThreadDocument> Observe(CodexThreadStore store, CodexThreadDocument doc, Guid attachment,
        string id, string payload)
        => (await store.RecordRealtimeObservationAsync(doc.Id, doc.Revision, "session", "owner", attachment, id, Event(payload), Ct)).Thread;

    [Fact]
    public async Task VoiceOnlySessionSealsTailBeforeEndAndPreservesInterleavedOrder()
    {
        var (storage, store, thread, attachment) = await Setup();
        thread = await Observe(store, thread, attachment, "first", """{"type":"input_transcript.added","item":{"text":"hello "}}""");
        thread = await Observe(store, thread, attachment, "second", """{"type":"output_transcript.added","item":{"text":"hi"}}""");
        thread = await Observe(store, thread, attachment, "third", """{"type":"turn.done","turn":{"role":"assistant","transcript":"hi!"}}""");
        thread = await Observe(store, thread, attachment, "fourth", """{"type":"input_transcript.added","item":{"text":"world"}}""");
        var end = Guid.NewGuid();
        thread = (await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", attachment, end, Ct)).Thread;
        var recovered = new CodexThreadStore(storage, "account");
        thread = await recovered.ReadAsync(thread.Id, Ct);
        var history = CodexThreadStore.RecoverRealtimeHistory(Session(thread));
        Assert.Equal(new[] { "hello world", "hi!" }, history.Frames.Select(f => f.Text));
        Assert.All(history.Frames, f => Assert.Equal("carried", f.Disposition));
        var carry = Assert.Single(history.Carryover);
        Assert.Equal("sessionEnd", carry.Reason);
        Assert.Equal(history.Frames.Select(f => f.Id), carry.SourceFrameIds);
        Assert.Contains("user: hello world\nassistant: hi!", carry.Input.GetProperty("content").GetString());
        thread = await recovered.CompleteRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", end, Ct);
        Assert.Equal("ended", Session(thread).State);
        Assert.Single(CodexThreadStore.RealtimeCarryoverBeforeTurn(thread, 0));
    }

    [Fact]
    public async Task ReconnectRebuildsPendingSpeechAndReplayIsIdempotentAfterLostCommit()
    {
        var (storage, store, thread, attachment) = await Setup();
        var source = Event("""{"type":"input_transcript.added","event_id":"event-a","item":{"text":"one "}}""");
        storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() => store.RecordRealtimeObservationAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, "obs-a", source, Ct));
        var recovered = new CodexThreadStore(storage, "account");
        thread = await recovered.ReadAsync(thread.Id, Ct);
        var retry = await recovered.RecordRealtimeObservationAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, "obs-a", source, Ct);
        Assert.False(retry.Admitted);
        Assert.Equal(thread.Revision, retry.Thread.Revision);
        thread = await recovered.RequireRealtimeHistoryRecoveryAsync(thread.Id, thread.Revision, "session", "owner", attachment, Ct);
        thread = await recovered.ConfirmRealtimeHistoryRecoveryAsync(thread.Id, thread.Revision, "session", "owner", false, Ct);
        var nextAttachment = Guid.NewGuid();
        thread = await recovered.AttachRealtimeSessionAsync(thread.Id, thread.Revision, "session", "owner", nextAttachment, Ct);
        thread = (await recovered.RecordRealtimeObservationAsync(thread.Id, thread.Revision,
            "session", "owner", nextAttachment, "replayed-provider-event", source, Ct)).Thread;
        thread = await Observe(recovered, thread, nextAttachment, "obs-b", """{"type":"input_transcript.added","item":{"text":"two"}}""");
        Assert.Equal("one two", Assert.Single(CodexThreadStore.RecoverRealtimeHistory(Session(thread)).Frames).Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.RecordRealtimeObservationAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, "stale", source, Ct));
        storage.ThrowBeforeCommit = true;
        await Assert.ThrowsAsync<IOException>(() => recovered.SealRealtimeHistoryAsync(thread.Id, thread.Revision,
            "session", "owner", nextAttachment, "seal", cancellationToken: Ct));
        var reloaded = await recovered.ReadAsync(thread.Id, Ct);
        Assert.Equal("pending", Assert.Single(Session(reloaded).History!.Frames).Disposition);
        var sealedHistory = await recovered.SealRealtimeHistoryAsync(thread.Id, reloaded.Revision,
            "session", "owner", nextAttachment, "seal", cancellationToken: Ct);
        var repeat = await recovered.SealRealtimeHistoryAsync(thread.Id, sealedHistory.Thread.Revision,
            "session", "owner", nextAttachment, "seal", cancellationToken: Ct);
        Assert.False(repeat.Admitted);
        Assert.Single(Session(repeat.Thread).History!.Carryover);
    }

    [Theory]
    [InlineData(CodexRealtimeHistoryMode.Canonical, 1)]
    [InlineData(CodexRealtimeHistoryMode.LegacyFlush, 1)]
    [InlineData(CodexRealtimeHistoryMode.LegacyNoFlush, 0)]
    public async Task EnrollmentControlsLegacyCarryoverWithoutLosingFrames(CodexRealtimeHistoryMode mode, int count)
    {
        var (_, store, thread, attachment) = await Setup(mode);
        thread = await Observe(store, thread, attachment, "one", """{"type":"input_transcript.added","item":{"text":"voice only"}}""");
        var end = await store.BeginRealtimeEndAsync(thread.Id, thread.Revision, "session", "owner", attachment, Guid.NewGuid(), Ct);
        var history = Session(end.Thread).History!;
        Assert.Equal(count, history.Carryover.Length);
        Assert.Equal("voice only", Assert.Single(history.Frames).Text);
    }

    [Fact]
    public async Task NextTurnAtomicallySealsTailAndForkRetainsSavedInputWithoutCallOwnership()
    {
        var (_, store, thread, attachment) = await Setup();
        thread = await Observe(store, thread, attachment, "a", """{"type":"input_transcript.added","item":{"text":"a & <b>"}}""");
        var turn = Guid.NewGuid();
        thread = await store.StartTurnAsync(thread.Id, thread.Revision, turn, "reasoning",
            [Json("""{"role":"user","content":"next"}""")], cancellationToken: Ct);
        var carryover = Assert.Single(CodexThreadStore.RealtimeCarryoverBeforeTurn(thread, 0));
        Assert.Contains("a &amp; &lt;b&gt;", carryover.GetProperty("content").GetString());
        Assert.Equal("next", Assert.Single(thread.Turns).Input.Single().GetProperty("content").GetString());
        Assert.Empty(CodexThreadStore.RealtimeCarryoverBeforeTurn(thread, 1));
        var prior = thread.Revision;
        // A duplicate seal cannot append the same frames.
        var repeated = await store.SealRealtimeHistoryAsync(thread.Id, prior, "session", "owner", attachment,
            "turn:" + turn.ToString("N"), cancellationToken: Ct);
        Assert.False(repeated.Admitted);
        var terminal = await store.CompleteAsync(thread.Id, thread.Revision, turn, "reasoning",
            "completed", [], Ct);
        var fork = await store.ForkAsync(thread.Id, terminal.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.RealtimeSessions);
        Assert.Single(CodexThreadStore.RealtimeCarryoverBeforeTurn(fork, 0));
    }

    [Fact]
    public async Task PendingHandoffBlocksTailUntilAdmissionSettles()
    {
        var (_, store, thread, attachment) = await Setup();
        thread = await Observe(store, thread, attachment, "speech", """{"type":"input_transcript.added","item":{"text":"please"}}""");
        thread = await Observe(store, thread, attachment, "handoff", """{"type":"delegation.created","item":{"id":"d","type":"delegation","target":"client","content":[{"type":"input_text","text":"please"}]}}""");
        Assert.Single(Session(thread).History!.Handoffs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRealtimeEndAsync(thread.Id, thread.Revision,
            "session", "owner", attachment, Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StartTurnAsync(thread.Id, thread.Revision,
            Guid.NewGuid(), "reasoning", [], cancellationToken: Ct));
        var rejected = store.ResolveRealtimeHandoffHistory(thread, "session", "d", null);
        Assert.Equal("rejected", Assert.Single(Session(rejected).History!.Handoffs).State);
        Assert.Equal("pending", Assert.Single(Session(rejected).History!.Frames).Disposition);
    }
}
