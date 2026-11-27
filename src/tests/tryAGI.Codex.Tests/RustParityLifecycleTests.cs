using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

/// <summary>Independent scripted transport acceptance, derived from the pinned Rust lifecycle suite.</summary>
public sealed class RustParityLifecycleTests
{
    private sealed class MemoryStorage : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];

        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                return Task.FromResult(rows.TryGetValue(id, out var row) ? (JsonElement?)row.Clone() : null);
        }

        public Task<bool> CompareExchangeAsync(Guid id, long? expectedRevision, JsonElement body,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? revision = rows.TryGetValue(id, out var old) ? old.GetProperty("revision").GetInt64() : null;
                if (revision != expectedRevision) return Task.FromResult(false);
                rows[id] = body.Clone();
                return Task.FromResult(true);
            }
        }
    }

    private sealed class NoNetworkCredentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Offline lifecycle tests must not acquire provider credentials.");
    }

    // Rust core/tests/suite/realtime_conversation.rs: conversation_records_history_without_an_event_observer.
    // Use two logical sessions on the same backing thread and a scripted caller-owned sideband.
    // A closed stream is not proof that the logical call ended: reconcile before ending it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoSequentialSessionsWithoutObserverRetainOrderedSpeechExactlyOnce(bool unclean)
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new MemoryStorage();
        var store = new CodexThreadStore(storage, "fixture-account");
        using var settings = JsonDocument.Parse("{}");
        var thread = await store.CreateAsync(Guid.NewGuid(), settings.RootElement, ct);
        using var client = new CodexClient(new NoNetworkCredentials());
        var sessionIds = new[] { "voice-1", "voice-2" };
        for (var i = 0; i < sessionIds.Length; i++)
        {
            var sessionId = sessionIds[i];
            var callId = "rtc_fixture_" + (i + 1);
            thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, sessionId, callId,
                "v2", "fixture-owner", ct);
            thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, sessionId,
                "fixture-owner", CodexRealtimeHistoryMode.Canonical, ct);
            var incoming = Channel.CreateUnbounded<string>();
            var outgoing = Channel.CreateUnbounded<string>();
            var attachments = new List<Guid>();
            await using var controller = new CodexRealtimeSessionController(store, thread.Id, sessionId,
                "fixture-owner", async (request, cancellationToken) =>
                {
                    Assert.Equal(callId, request.CallId);
                    attachments.Add(request.AttachmentId);
                    return await client.AttachExistingRealtimeCallAsync(request.CallId,
                        new CodexRealtimeAttachmentStream(incoming.Reader, outgoing.Writer), request.Protocol,
                        request.StartBefore, cancellationToken);
                }, new CodexRealtimeReconnectOptions
                {
                    MaximumAttempts = 1, Timeout = TimeSpan.FromSeconds(5), DrainTimeout = TimeSpan.FromSeconds(2),
                    RetryDelay = TimeSpan.Zero,
                });

            Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await controller.AttachAsync(ct));
            await incoming.Writer.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "session.updated", session = new { id = sessionId },
            }), ct);
            await incoming.Writer.WriteAsync("""{"type":"response.output_text.delta","event_id":"fixture-assistant","delta":"assistant first"}""", ct);
            await incoming.Writer.WriteAsync("""{"type":"conversation.item.input_audio_transcription.delta","event_id":"fixture-user","delta":"user second"}""", ct);
            // Replay the same provider identity. Deduplication is session-scoped, not thread-scoped.
            await incoming.Writer.WriteAsync("""{"type":"conversation.item.input_audio_transcription.delta","event_id":"fixture-user","delta":"user second"}""", ct);
            incoming.Writer.TryComplete(unclean ? new IOException("Synthetic transport failure.") : null);
            Assert.Equal(CodexRealtimeLifecycleStatus.Retryable, await controller.PumpAsync(cancellationToken: ct));

            var recoveredStore = new CodexThreadStore(storage, "fixture-account");
            thread = await recoveredStore.ReadAsync(thread.Id, ct);
            var fenced = (thread.RealtimeSessions ?? []).Single(s => s.SessionId == sessionId);
            Assert.Equal("recoveryRequired", fenced.State);
            Assert.Equal(new[] { "assistant first", "user second" }, fenced.History!.Frames.Select(f => f.Text));
            Assert.Empty(fenced.History.Carryover);
            Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await controller.AttachAsync(ct));

            var checks = 0;
            Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.RecoverAsync((context, _) =>
            {
                checks++;
                Assert.Equal(sessionId, context.Session.SessionId);
                Assert.Equal(callId, context.Session.CallId);
                Assert.Equal(new[] { "assistant first", "user second" }, context.Session.History!.Frames.Select(f => f.Text));
                return Task.FromResult(CodexRealtimeRecoveryDecision.Ended);
            }, reconnect: false, cancellationToken: ct));
            Assert.Equal(CodexRealtimeLifecycleStatus.Ended, await controller.RecoverAsync((_, _) =>
                throw new InvalidOperationException("An already ended call must not be probed again."),
                reconnect: false, cancellationToken: ct));
            Assert.Equal(1, checks);
            Assert.Single(attachments);
            Assert.False(outgoing.Reader.TryRead(out _));

            thread = await recoveredStore.ReadAsync(thread.Id, ct);
            var ended = (thread.RealtimeSessions ?? []).Single(s => s.SessionId == sessionId);
            Assert.Equal("ended", ended.State);
            Assert.All(ended.History!.Frames, f => Assert.Equal("carried", f.Disposition));
            var carryover = Assert.Single(ended.History.Carryover);
            Assert.Equal(ended.History.Frames.Select(f => f.Id), carryover.SourceFrameIds);
            Assert.Contains("assistant: assistant first\nuser: user second", carryover.Input.GetProperty("content").GetString());
        }

        var reopened = await new CodexThreadStore(storage, "fixture-account").ReadAsync(thread.Id, ct);
        Assert.Equal(sessionIds, (reopened.RealtimeSessions ?? []).Select(s => s.SessionId));
        Assert.Empty(reopened.Turns);
        var inputs = CodexThreadStore.RealtimeCarryoverBeforeTurn(reopened, 0);
        Assert.Equal(2, inputs.Count);
        Assert.All(inputs, input => Assert.Contains("assistant: assistant first\nuser: user second",
            input.GetProperty("content").GetString()));
    }
}
