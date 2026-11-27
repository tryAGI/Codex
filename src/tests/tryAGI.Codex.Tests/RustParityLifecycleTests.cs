using System.Net;
using System.Text;
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

    private sealed class SyntheticCredentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CodexCredentials("synthetic-acceptance-token", "fixture-account"));
    }

    private sealed class SyntheticBudget : ICodexUsageObserver
    {
        internal int Reserved, Completed, Incomplete, NotDispatched;
        public Task ReserveAsync(string id, string model, CancellationToken ct)
        { Interlocked.Increment(ref Reserved); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        { Interlocked.Increment(ref Completed); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct)
        { Interlocked.Increment(ref Incomplete); return Task.CompletedTask; }
        public Task NotDispatchedAsync(string id, string model, CancellationToken ct)
        { Interlocked.Increment(ref NotDispatched); return Task.CompletedTask; }
    }

    private static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class SyntheticResponses : HttpMessageHandler
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement> Requests = new();
        internal readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement[]> Replies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.DoesNotContain("synthetic-acceptance-token", body.GetRawText());
            Requests.Enqueue(body);
            if (!Replies.TryDequeue(out var output))
                throw new InvalidOperationException("Unexpected reasoning request.");
            var id = "resp_fixture_" + Requests.Count;
            var frames = new List<JsonElement> { JsonSerializer.SerializeToElement(new { type = "response.created", response = new { id } }) };
            frames.AddRange(output.Select((item, output_index) => JsonSerializer.SerializeToElement(new { type = "response.output_item.done", output_index, item })));
            frames.Add(JsonSerializer.SerializeToElement(new { type = "response.completed", response = new { id, output } }));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Concat(frames.Select(frame => $"data: {JsonSerializer.Serialize(frame)}\n\n")),
                    Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    // Native orchestration against synthetic transports: no real provider and no audio playback.
    [Fact]
    public async Task AudioDelegationToolFeedbackFollowupAndStandaloneSpeechStayOnOneThread()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var storage = new MemoryStorage();
        var store = new CodexThreadStore(storage, "fixture-account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Parse("{}"), ct);
        const string sessionId = "fixture-voice";
        const string owner = "fixture-owner";
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, sessionId, "rtc_fixture", "v3", owner, ct);
        thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, sessionId, owner,
            CodexRealtimeHistoryMode.Canonical, ct);

        var credentials = new SyntheticCredentials();
        var budget = new SyntheticBudget();
        using var provider = new SyntheticResponses();
        provider.Replies.Enqueue([
            Parse("""{"type":"reasoning","id":"rs_fixture","summary":[],"encrypted_content":"synthetic-opaque"}"""),
            Parse("""{"type":"function_call","id":"fc_fixture","call_id":"call_fixture","name":"read_fixture","arguments":"{}"}"""),
        ]);
        provider.Replies.Enqueue([Parse("""{"type":"message","id":"msg_first","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"fixture answer one"}]}""")]);
        provider.Replies.Enqueue([Parse("""{"type":"message","id":"msg_second","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"fixture answer two"}]}""")]);
        // The reasoning client is created per turn. Keep one shared synthetic transport alive.
        var agent = new CodexThreadAgent(store, credentials, budget, clientFactory:
            (auth, usage) => new CodexClient(auth, handler: new NonOwningHandler(provider), usageObserver: usage));
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Parse("{}"), (_, _, _) => throw new InvalidOperationException("Unexpected alternate reasoning executor."),
            (_, _) => Task.CompletedTask);
        using var voice = new CodexClient(new NoNetworkCredentials());
        var incoming = Channel.CreateUnbounded<string>();
        var outgoing = Channel.CreateUnbounded<string>();
        var heard = Channel.CreateUnbounded<byte[]>();
        var controller = new CodexRealtimeSessionController(store, thread.Id, sessionId, owner,
            (request, token) => voice.AttachExistingRealtimeCallAsync(request.CallId,
                new CodexRealtimeAttachmentStream(incoming.Reader, outgoing.Writer), request.Protocol,
                request.StartBefore, token));
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolCount = 0;
        var tool = Parse("""{"type":"function","name":"read_fixture","parameters":{"type":"object","properties":{},"additionalProperties":false}}""");
        await using var coordinator = new CodexRealtimeCoordinator(store, agent, rpc, controller, thread.Id, sessionId,
            owner, "fixture-account", _ => new CodexResponseOptions("fixture-model", "fixture instructions")
            {
                Identity = new("fixture-reasoning", thread.Id.ToString(), Guid.NewGuid().ToString()),
                Tools = [tool],
            }, async (call, token) =>
            {
                Assert.Equal("read_fixture", call.Name);
                Assert.Equal("call_fixture", call.Id);
                Assert.Equal("{}", call.Input);
                Interlocked.Increment(ref toolCount);
                toolStarted.TrySetResult();
                await releaseTool.Task.WaitAsync(token);
                return new CodexToolOutput("synthetic tool receipt");
            }, new CodexRealtimeCoordinatorOptions { DirectAudio = true, ChannelMode = CodexRealtimeReasoningChannelMode.BemTags });

        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await coordinator.AttachAsync(ct));
        var pump = coordinator.RunAsync(audioObserver: (item, _) =>
        {
            Assert.NotNull(item.Audio);
            heard.Writer.TryWrite(item.Audio!.Pcm16.ToArray());
            return Task.CompletedTask;
        }, ct: ct);
        var trace = new List<JsonElement>();

        async Task<JsonElement> Wire(Func<JsonElement, bool> match)
        {
            while (true)
            {
                var frame = Parse(await outgoing.Reader.ReadAsync(ct));
                trace.Add(frame);
                if (match(frame)) return frame;
            }
        }

        static string? Type(JsonElement frame) => frame.GetProperty("type").GetString();
        static string? Text(JsonElement frame) => frame.TryGetProperty("content", out var content)
            ? content.EnumerateArray().Select(item => item.GetProperty("text").GetString()).FirstOrDefault()
            : null;
        async Task Send(string json) => await incoming.Writer.WriteAsync(json, ct);
        async Task WaitUntil(Func<bool> predicate)
        {
            while (!predicate())
            {
                if (coordinator.ReasoningFailure is { } error) throw new InvalidOperationException("Native reasoning failed.", error);
                if (pump.IsCompleted) throw new InvalidOperationException("Native realtime pump ended early.", pump.Exception);
                await Task.Delay(10, ct);
            }
        }
        async Task<CodexThreadDocument> WaitTurns(int count)
        {
            while (true)
            {
                var current = await store.ReadAsync(thread.Id, ct);
                if (current.Turns.Length == count && current.Turns.All(t => t.Status == "completed")) return current;
                if (coordinator.ReasoningFailure is { } error) throw new InvalidOperationException("Native reasoning failed.", error);
                await Task.Delay(10, ct);
            }
        }

        try
        {
            await WaitUntil(() => coordinator.TryAppendAudio(new byte[] { 0, 0, 1, 0 }));
            var audio = await Wire(frame => Type(frame) == "input_audio.append");
            Assert.Equal("AAABAA==", audio.GetProperty("audio").GetString());
            await Send("""{"type":"input_transcript.added","event_id":"fixture-transcript","item":{"text":"read fixture"}}""");
            const string handoff = """{"type":"delegation.created","event_id":"fixture-handoff-1","item":{"type":"delegation","id":"handoff_one","target":"client","content":[{"type":"input_text","text":"read fixture"}]}}""";
            await Send(handoff);
            await WaitUntil(() => toolStarted.Task.IsCompleted);
            await Send(handoff);
            Assert.True(coordinator.TryAppendAudio(new byte[] { 2, 0, 3, 0 }));
            var duringTool = await Wire(frame => Type(frame) == "input_audio.append");
            Assert.Equal("AgADAA==", duringTool.GetProperty("audio").GetString());
            await Send("""{"type":"output_audio.delta","audio":"BAAFAA=="}""");
            Assert.Equal(new byte[] { 4, 0, 5, 0 }, await heard.Reader.ReadAsync(ct));
            Assert.Single(provider.Requests);
            Assert.False(releaseTool.Task.IsCompleted);

            releaseTool.TrySetResult();
            var first = await Wire(frame => Type(frame) == "delegation.context.append" && Text(frame)!.Contains("fixture answer one"));
            Assert.Equal("handoff_one", first.GetProperty("delegation_item_id").GetString());
            Assert.Equal("speakable", first.GetProperty("channel").GetString());
            await Send("""{"type":"output_audio.delta","audio":"BgAHAA=="}""");
            Assert.Equal(new byte[] { 6, 0, 7, 0 }, await heard.Reader.ReadAsync(ct));
            var completed = await WaitTurns(1);
            Assert.Equal(1, toolCount);
            Assert.Equal(2, provider.Requests.Count);
            Assert.Contains("synthetic-opaque", provider.Requests.Last().GetRawText());
            Assert.Contains("synthetic tool receipt", provider.Requests.Last().GetRawText());

            Assert.True(coordinator.TryAppendSpeech("fixture unsolicited speech"));
            var standalone = await Wire(frame => Type(frame) == "session.context.append" && Text(frame) == "fixture unsolicited speech");
            Assert.Equal("speakable", standalone.GetProperty("channel").GetString());
            Assert.False(standalone.TryGetProperty("delegation_item_id", out _));
            await Send("""{"type":"output_audio.delta","audio":"CAAJAA=="}""");
            Assert.Equal(new byte[] { 8, 0, 9, 0 }, await heard.Reader.ReadAsync(ct));
            Assert.Equal(2, provider.Requests.Count);
            Assert.Single((await store.ReadAsync(thread.Id, ct)).Turns);

            await Send("""{"type":"delegation.created","event_id":"fixture-handoff-2","item":{"type":"delegation","id":"handoff_two","target":"client","content":[{"type":"input_text","text":"follow up"}]}}""");
            var second = await Wire(frame => Type(frame) == "delegation.context.append" && Text(frame)!.Contains("fixture answer two"));
            Assert.Equal("handoff_two", second.GetProperty("delegation_item_id").GetString());
            var final = await WaitTurns(2);
            Assert.Equal(completed.Turns[0].Id, final.Turns[0].Id);
            Assert.Equal(3, provider.Requests.Count);
            Assert.Contains("fixture answer one", provider.Requests.Last().GetRawText());
            Assert.Equal(2, final.RealtimeSessions!.Single().HandoffAdmissions!.Length);
            Assert.Equal(1, toolCount);
            Assert.Equal(3, budget.Reserved);
            Assert.Equal(3, budget.Completed);
            Assert.Equal(0, budget.Incomplete);
            Assert.Equal(0, budget.NotDispatched);
            Assert.Equal(CodexRealtimeLifecycleStatus.Detached, await coordinator.DetachAsync(ct));
            await pump.WaitAsync(ct);
        }
        finally
        {
            releaseTool.TrySetResult();
            await deadline.CancelAsync();
        }
    }


    // Pinned Rust core/tests/suite/realtime_conversation.rs:
    // conversation_webrtc_live_reconnects_sideband_after_unclean_disconnect.
    // This exercises the native controller/coordinator with a synthetic sideband, not live WebRTC.
    [Fact]
    public async Task RecoveredSidebandAdmitsNewHandoffOnlyAfterDurableTranscript()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var store = new CodexThreadStore(new MemoryStorage(), "fixture-account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Parse("{}"), ct);
        const string session = "reconnect-fixture";
        const string call = "rtc_reconnect_fixture";
        const string owner = "fixture-owner";
        thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, session, call, "v3", owner, ct);
        thread = await store.ConfigureRealtimeHistoryAsync(thread.Id, thread.Revision, session, owner,
            CodexRealtimeHistoryMode.Canonical, ct);
        using var provider = new SyntheticResponses();
        provider.Replies.Enqueue([Parse("""{"type":"message","id":"msg_after_recovery","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"recovered answer"}]}""")]);
        var budget = new SyntheticBudget();
        var credentials = new SyntheticCredentials();
        var agent = new CodexThreadAgent(store, credentials, budget, clientFactory:
            (auth, usage) => new CodexClient(auth, handler: new NonOwningHandler(provider), usageObserver: usage));
        var rpc = new CodexThreadRpc(store, _ => Task.FromResult<IReadOnlyList<CodexThreadDocument>>([]),
            Parse("{}"), (_, _, _) => throw new InvalidOperationException("Unexpected alternate executor."),
            (_, _) => Task.CompletedTask);
        using var voice = new CodexClient(new NoNetworkCredentials());
        var inputs = new[] { Channel.CreateUnbounded<string>(), Channel.CreateUnbounded<string>() };
        var outputs = new[] { Channel.CreateUnbounded<string>(), Channel.CreateUnbounded<string>() };
        var requests = new List<CodexRealtimeAttachRequest>();
        var controller = new CodexRealtimeSessionController(store, thread.Id, session, owner,
            (request, token) =>
            {
                Assert.Equal(call, request.CallId);
                var index = requests.Count;
                requests.Add(request);
                return voice.AttachExistingRealtimeCallAsync(request.CallId,
                    new CodexRealtimeAttachmentStream(inputs[index].Reader, outputs[index].Writer),
                    request.Protocol, request.StartBefore, token);
            }, new CodexRealtimeReconnectOptions
            {
                MaximumAttempts = 1, Timeout = TimeSpan.FromSeconds(3),
                DrainTimeout = TimeSpan.FromSeconds(2), RetryDelay = TimeSpan.Zero,
            });
        await using var coordinator = new CodexRealtimeCoordinator(store, agent, rpc, controller,
            thread.Id, session, owner, "fixture-account",
            _ => new CodexResponseOptions("fixture-model", "fixture instructions")
            {
                Identity = new("fixture-reasoning", thread.Id.ToString(), Guid.NewGuid().ToString()),
            }, (_, _) => throw new InvalidOperationException("No tool was advertised."),
            new CodexRealtimeCoordinatorOptions { ChannelMode = CodexRealtimeReasoningChannelMode.BemTags });

        const string first = """{"type":"input_transcript.added","event_id":"before","item":{"text":"before disconnect "}}""";
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached, await coordinator.AttachAsync(ct));
        var firstPump = coordinator.RunAsync(ct: ct);
        await inputs[0].Writer.WriteAsync(first, ct);
        inputs[0].Writer.TryComplete(new IOException("Synthetic sideband loss."));
        Assert.Equal(CodexRealtimeLifecycleStatus.Retryable, await firstPump.WaitAsync(ct));
        var fenced = await store.ReadAsync(thread.Id, ct);
        var state = Assert.Single(fenced.RealtimeSessions!);
        Assert.Equal("recoveryRequired", state.State);
        Assert.Equal("before disconnect ", Assert.Single(state.History!.Frames).Text);
        Assert.Empty(fenced.Turns);
        Assert.Empty(provider.Requests);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired, await coordinator.AttachAsync(ct));
        Assert.Single(requests);
        Assert.Equal(CodexRealtimeLifecycleStatus.HistoryRecoveryRequired,
            await controller.RecoverAsync((_, _) => Task.FromResult(CodexRealtimeRecoveryDecision.Unknown),
                cancellationToken: ct));
        Assert.Single(requests);
        Assert.Equal(CodexRealtimeLifecycleStatus.Attached,
            await controller.RecoverAsync((context, _) =>
            {
                Assert.Equal(session, context.Session.SessionId);
                Assert.Equal(call, context.Session.CallId);
                Assert.Equal("before disconnect ", Assert.Single(context.Session.History!.Frames).Text);
                return Task.FromResult(CodexRealtimeRecoveryDecision.Open);
            }, cancellationToken: ct));
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].AttachmentId, requests[1].AttachmentId);

        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPump = coordinator.RunAsync(observer: async (item, token) =>
        {
            if (item.Payload.TryGetProperty("event_id", out var eventId) && eventId.GetString() == "after")
            {
                // The observer must see the committed transcript, never an optimistic projection.
                var saved = await store.ReadAsync(thread.Id, token);
                Assert.Contains("after reconnect", string.Concat(Assert.Single(saved.RealtimeSessions!).History!.Frames.Select(f => f.Text)));
                observed.TrySetResult();
            }
        }, ct: ct);
        await inputs[1].Writer.WriteAsync(first, ct); // Provider replay on a new attachment.
        await inputs[1].Writer.WriteAsync("""{"type":"input_transcript.added","event_id":"after","item":{"text":"after reconnect"}}""", ct);
        await observed.Task.WaitAsync(ct);
        const string handoff = """{"type":"delegation.created","event_id":"handoff-after","item":{"type":"delegation","id":"recovered-handoff","target":"client","content":[{"type":"input_text","text":"summarize"}]}}""";
        await inputs[1].Writer.WriteAsync(handoff, ct);
        await inputs[1].Writer.WriteAsync(handoff, ct);

        try
        {
            while (true)
            {
                var frame = Parse(await outputs[1].Reader.ReadAsync(ct));
                if (frame.GetProperty("type").GetString() != "delegation.context.append") continue;
                Assert.Equal("recovered-handoff", frame.GetProperty("delegation_item_id").GetString());
                Assert.Contains("recovered answer", frame.GetRawText());
                break;
            }
            CodexThreadDocument final;
            while (true)
            {
                if (coordinator.ReasoningFailure is { } error) throw new InvalidOperationException("Recovered reasoning failed.", error);
                final = await store.ReadAsync(thread.Id, ct);
                if (final.Turns.Length == 1 && final.Turns[0].Status == "completed") break;
                await Task.Delay(10, ct);
            }
            var input = Assert.Single(provider.Requests).GetRawText();
            Assert.Contains("before disconnect", input);
            Assert.Contains("after reconnect", input);
            Assert.Equal(1, input.Split("before disconnect", StringSplitOptions.None).Length - 1);
            var history = Assert.Single(final.RealtimeSessions!);
            Assert.Single(history.HandoffAdmissions!);
            Assert.Equal("recovered-handoff", history.HandoffAdmissions![0].HandoffId);
            Assert.False(outputs[0].Reader.TryRead(out _));
            Assert.Equal(1, budget.Reserved);
            Assert.Equal(1, budget.Completed);
            Assert.Equal(0, budget.Incomplete);
            Assert.Equal(CodexRealtimeLifecycleStatus.Detached, await coordinator.DetachAsync(ct));
            await secondPump.WaitAsync(ct);
        }
        finally { await deadline.CancelAsync(); }
    }

    // HttpClient disposes its handler per client. A new adapter borrows the shared synthetic fixture.
    private sealed class NonOwningHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(inner, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => invoker.SendAsync(request, ct);
        protected override void Dispose(bool disposing)
        {
            if (disposing) invoker.Dispose();
            base.Dispose(disposing);
        }
    }
}
