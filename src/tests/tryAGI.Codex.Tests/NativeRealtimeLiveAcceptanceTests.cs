using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex.Tests;

// This category is intentionally separate from the existing authenticated CI lane.
public sealed class NativeRealtimeLiveAcceptanceTests
{
    private const string ToolName = "parity_word";
    private const string VoiceInstructions =
        "For every spoken user request, delegate to the backing reasoning agent. Do not answer from your own memory. " +
        "Wait for the agent's answer and speak its words verbatim, including the code phrase. " +
        "Speak standalone agent context verbatim too. Keep responses short.";
    private const string ReasoningInstructions =
        "This is an isolated voice transport verification. On the first spoken request, call parity_word exactly once " +
        "and return its phrase verbatim. On the next spoken request, repeat the same phrase from prior tool history " +
        "without calling a tool. For a host-triggered announcement, return the requested phrase verbatim. " +
        "Use a short final answer with no extra formatting.";
    private static readonly JsonElement Tool = Json("""
        {"type":"function","name":"parity_word","description":"Returns a synthetic test phrase. No external effects.",
         "parameters":{"type":"object","properties":{},"additionalProperties":false},"strict":true}
        """);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class Credentials(string token, string account) : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CodexCredentials(token, account));
        }
    }

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly Lock gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(rows.TryGetValue(id, out var value) ? (JsonElement?)value.Clone() : null);
        }

        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement document, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? before = rows.TryGetValue(id, out var old) ? old.GetProperty("revision").GetInt64() : null;
                if (before != revision) return Task.FromResult(false);
                rows[id] = document.Clone();
                return Task.FromResult(true);
            }
        }
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : throw new InvalidOperationException("Missing explicit live prerequisite: " + name);

    private static void Require(bool condition, string stage)
    {
        if (!condition) throw new InvalidOperationException("Native live acceptance failed at " + stage);
    }

    private static string Phrase()
    {
        string[] words = ["amber", "birch", "cobalt", "daisy", "ember", "fern", "golden", "harbor",
            "ivory", "juniper", "lemon", "maple", "ocean", "pebble", "river", "silver"];
        return string.Join(' ', Enumerable.Range(0, 4).Select(_ => words[RandomNumberGenerator.GetInt32(words.Length)]));
    }

    private static bool TurnContains(CodexStoredTurn turn, string phrase) =>
        turn.Output.Any(item => item.TryGetProperty("type", out var kind) && kind.GetString() == "message" &&
            item.TryGetProperty("role", out var role) && role.GetString() == "assistant" &&
            item.GetRawText().Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private sealed record Stage(string Name, int UserTranscriptEvents, int AssistantTranscriptEvents,
        long OutputPcmBytes, bool AssistantTurnDone, bool PhraseObserved, string[] DelegationDigests,
        string? ProviderSessionDigest, string? ObservedModel, CodexObservedTokens? VoiceTokens,
        Guid? BackingTurnId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WebRtcMediaStage? WebRtcMedia = null);

    private sealed record WebRtcMediaStage(long PacketsSent, long PacketsReceived, long BytesReceived,
        long DecodedSamples, long NonSilentSamples, long ConcealedSamples, string? Codec);

    // Only sanitized counters, hashes and bounded model metadata are retained in the receipt.
    private sealed class VoiceObservation
    {
        private readonly Lock gate = new();
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        private readonly List<string> delegations = [];
        private readonly StringBuilder transcript = new();
        private string? providerSession;
        private string? model;
        private CodexObservedTokens? tokens;
        private string name = "connect";
        private string phrase = "";
        private int users, assistants;
        private long audio;
        private bool done, gatedAudio;

        internal void Begin(string stage, string expectedPhrase, bool needsDelegation)
        {
            lock (gate)
            {
                name = stage; phrase = expectedPhrase; users = assistants = 0; audio = 0;
                done = false; gatedAudio = !needsDelegation;
                delegations.Clear(); transcript.Clear();
            }
        }

        internal Task Observe(CodexRealtimeEvent item, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (item.Error is not null) throw new InvalidOperationException("Provider realtime error.");
                if (item.Session is { } session)
                {
                    providerSession = LiveRealtimeLimits.Digest(session.Id);
                    if (LiveRealtimeLimits.SafeModel(session.Model)) model = session.Model;
                }
                if (item.Delegation is { } handoff && seen.Add(handoff.Id))
                {
                    if (seen.Count > 2) throw new InvalidOperationException("Unexpected extra provider delegation.");
                    delegations.Add(LiveRealtimeLimits.Digest(handoff.Id)); gatedAudio = true;
                }
                if (item.Transcript is { } value && gatedAudio)
                {
                    if (value.Role == "user") users++;
                    else if (value.Role == "assistant")
                    {
                        assistants++;
                        if (transcript.Length + value.Text.Length > 8192)
                            throw new InvalidOperationException("Live transcript exceeded the probe bound.");
                        if (value.ReplaceFinalText) transcript.Clear();
                        transcript.Append(value.Text);
                    }
                }
                // User transcript can precede the handoff. Count it independently.
                else if (item.Transcript is { Role: "user" }) users++;
                if (gatedAudio && !item.StaleForFeedback && item.Audio is { } pcm)
                {
                    audio += pcm.Pcm16.Length;
                    if (audio > 48_000 * 20) throw new InvalidOperationException("Live output PCM exceeded the probe bound.");
                }
                if (gatedAudio && item.Turn is { Role: "assistant" } turn)
                {
                    done = true;
                    if (turn.Transcript.Length > 8192) throw new InvalidOperationException("Live turn exceeded the probe bound.");
                    if (turn.Transcript.Length != 0) { transcript.Clear(); transcript.Append(turn.Transcript); }
                }
                // Session snapshots are not summed as incremental usage.
                if (CodexObservedUsage.Read(item)?.Tokens is { } observed) tokens = observed;
            }
            return Task.CompletedTask;
        }

        internal Stage Snapshot()
        {
            lock (gate)
                return new(name, users, assistants, audio, done, phrase.Length != 0 &&
                    transcript.ToString().Contains(phrase, StringComparison.OrdinalIgnoreCase),
                    delegations.ToArray(), providerSession, model, tokens);
        }
    }

    [Fact]
    [Trait("Category", "LiveRealtimeParity")]
    public Task DirectAudioDelegationToolFollowupAndHostInitiatedSpeech()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CODEX_NATIVE_REALTIME_LIVE") == "1",
            "Native realtime provider acceptance requires a separate explicit opt-in.");
        return RunProbeAsync(webRtc: false);
    }

    [Fact]
    [Trait("Category", "LiveRealtimeWebRtcParity")]
    public Task WebRtcAudioDelegationToolFollowupAndHostInitiatedSpeech()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CODEX_NATIVE_WEBRTC_LIVE") == "1",
            "Native WebRTC provider acceptance requires a separate explicit opt-in.");
        return RunProbeAsync(webRtc: true);
    }

    private static async Task RunProbeAsync(bool webRtc)
    {
        // Validate every local prerequisite before obtaining credentials or dispatching.
        var first = await LiveRealtimeLimits.ReadPcmAsync(Required("CODEX_NATIVE_REALTIME_PCM_FIRST"), TestContext.Current.CancellationToken);
        var followup = await LiveRealtimeLimits.ReadPcmAsync(Required("CODEX_NATIVE_REALTIME_PCM_FOLLOWUP"), TestContext.Current.CancellationToken);
        Require(!first.AsSpan().SequenceEqual(followup), "distinct prerecorded prompts");
        var model = Required("CODEX_MODEL");
        Require(LiveRealtimeLimits.SafeModel(model), "explicit reasoning model");
        var revision = Required("CODEX_NATIVE_REALTIME_REVISION");
        Require(revision.Length == 40 && revision.All(Uri.IsHexDigit), "explicit tested revision");
        var browserExecutable = webRtc ? Required("CODEX_NATIVE_WEBRTC_BROWSER") : null;
        var token = Required("CODEX_ACCESS_TOKEN");
        var account = Required("CODEX_ACCOUNT_ID");
        await using var receipt = new FileStream(Required("CODEX_NATIVE_REALTIME_TRACE"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var startedAt = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await using var media = browserExecutable is null ? null :
            await NativeRealtimeWebRtcMediaPeer.StartAsync(browserExecutable, ct);
        string? offer = null;
        if (media is not null)
        {
            await media.CreateAsync("voice", true, ct);
            offer = await media.OfferAsync("voice", ct);
        }
        var phrase = Phrase();
        var announcement = Phrase();
        var store = new CodexThreadStore(new Storage(), account);
        var thread = await store.CreateAsync(Guid.NewGuid(), JsonSerializer.SerializeToElement(new { model, baseInstructions = ReasoningInstructions }), ct);
        var session = Guid.NewGuid().ToString("N");
        const string owner = "native-live-probe";
        if (!webRtc)
            thread = await store.CreateDirectRealtimeSessionAsync(thread.Id, thread.Revision, session, "v3", owner, ct);
        var credentials = new Credentials(token, account);
        var budget = new LiveRealtimeLimits.Budget(5);
        var agent = new CodexThreadAgent(store, credentials, budget);
        var toolCalls = 0;
        Task<CodexToolOutput> Execute(CodexToolCall call, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Require(call.Name == ToolName && Json(call.Input).EnumerateObject().Count() == 0, "isolated tool identity");
            Require(Interlocked.Increment(ref toolCalls) == 1, "single harmless tool");
            return Task.FromResult(new CodexToolOutput(phrase));
        }
        var rpc = new CodexThreadRpc(store, async t => [await store.ReadAsync(thread.Id, t)],
            JsonSerializer.SerializeToElement(new { model, baseInstructions = ReasoningInstructions }),
            async (run, started, cancellation) =>
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation, ct);
                return (await agent.RunAsync(run.Thread.Id, run.Thread.Revision, run.TurnId, run.OwnerId,
                    run.Options with { Instructions = ReasoningInstructions, Tools = [], RequiredFirstFunction = null },
                    run.Input, Execute, maximumRequests: 1, control: run.Control,
                    turnStarted: (snapshot, _) => started(snapshot), validatedItem: run.ValidatedItem,
                    observeAsync: run.ObserveProvider, cancellationToken: bounded.Token)).Thread;
            }, (_, _) => Task.CompletedTask, maximumConcurrentTurns: 1);
        using var voice = new CodexClient(credentials);
        var settings = new CodexRealtimeSessionOptions(VoiceInstructions)
        {
            Protocol = "v3", SessionId = session, VoiceSessionId = session, DelegationAckFiller = false,
        };
        var reconnectOptions = new CodexRealtimeReconnectOptions
        {
            MaximumAttempts = 1, Timeout = TimeSpan.FromSeconds(15), DrainTimeout = TimeSpan.FromSeconds(5),
        };
        string? providerCallDigest = null;
        string? answer = null;
        CodexRealtimeSessionController controller;
        if (media is not null)
        {
            try
            {
                // The SDK owns the one authenticated signaling POST; the browser sees only SDP/media.
                var call = await voice.CreateRealtimeCallAsync(offer!, settings, ct);
                providerCallDigest = LiveRealtimeLimits.Digest(call.CallId);
                answer = call.AnswerSdp;
                thread = await store.CreateRealtimeSessionAsync(thread.Id, thread.Revision, session, call.CallId,
                    "v3", owner, ct);
                controller = voice.CreateRealtimeSessionController(store, thread.Id, session, owner,
                    new CodexRealtimeAttachmentOptions
                    {
                        SessionId = session, VoiceSessionId = session, ExpectedAccountId = account,
                    }, reconnectOptions);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new InvalidOperationException("Native WebRTC setup failed (" + error.GetType().Name + ").");
            }
        }
        else
            controller = voice.CreateDirectRealtimeSessionController(store, thread.Id, session, owner, settings, reconnectOptions);
        await using var coordinator = new CodexRealtimeCoordinator(store, agent, rpc, controller,
            thread.Id, session, owner, account, snapshot => new(model, ReasoningInstructions)
            {
                Identity = new(thread.Id.ToString(), thread.Id.ToString(), Guid.NewGuid().ToString()),
                Tools = snapshot.Turns.Length == 0 ? [Tool] : [],
                RequiredFirstFunction = snapshot.Turns.Length == 0 ? ToolName : null,
            }, Execute, new() { DirectAudio = !webRtc, MaximumReasoningRequests = 2,
                ChannelMode = CodexRealtimeReasoningChannelMode.BemTags });
        var observation = new VoiceObservation();
        var stages = new List<Stage>();
        Task<CodexRealtimeLifecycleStatus>? pump = null;
        var outcome = "failed";
        string? errorType = null;
        var ended = false;
        var endOperation = Guid.NewGuid();
        string step = "attach";
        var reattached = false;
        var mediaClosed = false;
        NativeRealtimeWebRtcMediaPeer.Snapshot? mediaBaseline = null;

        async Task BeginStageAsync(string name, string expected, bool delegated)
        {
            mediaBaseline = media is null ? null : await media.SnapshotAsync("voice", ct);
            observation.Begin(name, expected, delegated);
        }

        async Task<CodexThreadDocument> WaitStageAsync(int turnCount, string expected, bool delegated)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Require(coordinator.ReasoningFailure is null && pump is { IsCompleted: false }, "active native pump");
                var current = await store.ReadAsync(thread.Id, ct);
                Require(current.Turns.Length <= turnCount, "bounded turn count");
                var stage = observation.Snapshot();
                WebRtcMediaStage? mediaStage = null;
                if (media is not null && mediaBaseline is { } baseline)
                {
                    var received = await media.SnapshotAsync("voice", ct);
                    Require(received.State == "connected", "live WebRTC media connected");
                    var samples = received.TotalSamplesReceived - baseline.TotalSamplesReceived;
                    var concealed = received.ConcealedSamples - baseline.ConcealedSamples;
                    mediaStage = new(received.PacketsSent - baseline.PacketsSent,
                        received.PacketsReceived - baseline.PacketsReceived, received.BytesReceived - baseline.BytesReceived,
                        samples, received.NonSilentSamples - baseline.NonSilentSamples, concealed, received.Codec);
                }
                var hasAudio = media is null ? stage.OutputPcmBytes > 0 :
                    mediaStage is { PacketsReceived: > 0, BytesReceived: > 0, NonSilentSamples: >= 1024 } decoded &&
                    decoded.DecodedSamples - decoded.ConcealedSamples >= 1024 &&
                    string.Equals(decoded.Codec, "audio/opus", StringComparison.OrdinalIgnoreCase) &&
                    (!delegated || decoded.PacketsSent > 0);
                if (current.Turns.Length == turnCount && current.Turns.All(turn => turn.Status == "completed") &&
                    stage.PhraseObserved && stage.AssistantTurnDone && hasAudio)
                {
                    Require(TurnContains(current.Turns[^1], expected), "backing reasoning answer");
                    Require(stage.DelegationDigests.Length == (delegated ? 1 : 0), "exact delegation count");
                    Require(!delegated || stage.UserTranscriptEvents > 0, "actual input transcript");
                    Require(delegated || stage.UserTranscriptEvents == 0, "no new voice input");
                    Require(stage.ProviderSessionDigest is not null, "provider session receipt");
                    stages.Add(stage with { BackingTurnId = current.Turns[^1].Id, WebRtcMedia = mediaStage });
                    return current;
                }
                await Task.Delay(50, ct);
            }
        }

        async Task SendClipAsync(byte[] pcm)
        {
            if (media is not null)
            {
                // WebRTC audio must never also be enqueued on the sideband control connection.
                await media.PlayAsync("voice", pcm, ct);
                await media.PlayAsync("voice", new byte[48_000], ct);
                return;
            }
            // Pace 20 ms of 24 kHz mono PCM16 per frame, then append bounded silence for server VAD.
            foreach (var clip in new[] { pcm, new byte[48_000] })
            {
                for (var offset = 0; offset < clip.Length; offset += 960)
                {
                    ct.ThrowIfCancellationRequested();
                    Require(coordinator.TryAppendAudio(clip.AsMemory(offset, Math.Min(960, clip.Length - offset))),
                        "bounded audio queue admission");
                    await Task.Delay(20, ct);
                }
            }
        }

        Func<CodexRealtimeConnection?, CancellationToken, Task>? endProvider = media is null ? null : async (connection, cancel) =>
        {
            Require(connection is not null, "owned sideband before end");
            await connection!.CloseSessionAsync(cancel);
            await media.CloseAsync("voice", cancel);
            mediaClosed = true;
        };

        try
        {
            if (media is not null)
            {
                step = "apply provider SDP";
                await media.ApplyAnswerAsync("voice", answer!, ct);
            }
            Require(await coordinator.AttachAsync(ct) == CodexRealtimeLifecycleStatus.Attached, "provider attach");
            pump = coordinator.RunAsync(observer: observation.Observe, ct: ct);
            // Await the retained provider readiness event through the sole coordinator pump.
            while (observation.Snapshot().ProviderSessionDigest is null)
            {
                Require(!pump.IsCompleted, "session readiness");
                await Task.Delay(20, ct);
            }
            if (media is not null)
            {
                while ((await media.SnapshotAsync("voice", ct)).State != "connected")
                {
                    Require(!pump.IsCompleted, "media readiness");
                    await Task.Delay(50, ct);
                }
            }
            step = "first audio and tool";
            await BeginStageAsync("audio-delegation-tool-feedback", phrase, true);
            await SendClipAsync(first);
            var one = await WaitStageAsync(1, phrase, true);
            Require(toolCalls == 1, "actual tool receipt");
            Require(one.Turns[0].Dispatches.Any(d => d.Kind == "tool" && d.State == "succeeded" &&
                d.Result is { } result && result.GetRawText().Contains(phrase, StringComparison.Ordinal)), "persisted tool receipt");

            if (media is not null)
            {
                step = "same-call sideband detach and reattach";
                var before = one.RealtimeSessions!.Single();
                Require(before.AttachmentId is not null, "first attachment identity");
                Require(await coordinator.DetachAsync(ct) == CodexRealtimeLifecycleStatus.Detached, "sideband-only detach");
                await pump.WaitAsync(ct);
                Require((await media.SnapshotAsync("voice", ct)).State == "connected", "original media call remains open");
                Require(await coordinator.AttachAsync(ct) == CodexRealtimeLifecycleStatus.Attached, "reattach same call");
                var after = (await store.ReadAsync(thread.Id, ct)).RealtimeSessions!.Single();
                Require(after.CallId == before.CallId && after.AttachmentId is not null &&
                    after.AttachmentId != before.AttachmentId, "same call and new attachment fence");
                pump = coordinator.RunAsync(observer: observation.Observe, ct: ct);
                reattached = true;
            }
            step = "follow-up audio";
            await BeginStageAsync("follow-up", phrase, true);
            await SendClipAsync(followup);
            var two = await WaitStageAsync(2, phrase, true);
            Require(two.Turns[0].Id == one.Turns[0].Id && toolCalls == 1, "same thread continuation");
            Require(two.RealtimeSessions!.Single().HandoffAdmissions?.Length == 2, "two durable handoffs");

            step = "host-triggered reasoning speech";
            await BeginStageAsync("host-triggered-reasoning-speech", announcement, false);
            await rpc.InvokeAsync("thread/resume", JsonSerializer.SerializeToElement(new { threadId = thread.Id.ToString() }), ct);
            await rpc.InvokeAsync("turn/start", JsonSerializer.SerializeToElement(new
            {
                threadId = thread.Id.ToString(),
                input = new[] { new { type = "text", text = "Host event: announce exactly this phrase: " + announcement } },
            }), ct);
            var three = await WaitStageAsync(3, announcement, false);
            Require(three.RealtimeSessions!.Single().HandoffAdmissions?.Length == 2 && toolCalls == 1,
                "no new user voice delegation for standalone speech");

            step = "clean shutdown";
            Require(await coordinator.EndAsync(endOperation, endProvider, ct) == CodexRealtimeLifecycleStatus.Ended, "durable end");
            Require(media is null || (reattached && mediaClosed), "WebRTC reattach and media shutdown");
            ended = true;
            await pump.WaitAsync(ct);
            Require(budget.Incomplete == 0 && budget.Completed == budget.Reserved && budget.Reserved <= 5, "request accounting");
            outcome = "passed";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Do not write provider messages, raw JSON, auth, transcript or private request data.
            errorType = error.GetType().Name;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            if (!ended)
            {
                coordinator.InterruptReasoning();
                try { ended = await coordinator.EndAsync(endOperation, endProvider, cleanup.Token) == CodexRealtimeLifecycleStatus.Ended; }
                catch (Exception error) when (error is not OutOfMemoryException) { errorType ??= error.GetType().Name; }
            }
            try { await rpc.StopAsync(cleanup.Token); }
            catch (Exception error) when (error is not OutOfMemoryException) { outcome = "failed"; errorType ??= error.GetType().Name; }
            await deadline.CancelAsync();
            if (pump is not null)
            {
                try { await pump.WaitAsync(cleanup.Token); }
                catch (Exception error) when (error is not OutOfMemoryException) { outcome = "failed"; errorType ??= error.GetType().Name; }
            }
            var trace = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1, lane = "live", route = webRtc ? "chatgpt-native-webrtc-sideband" : "chatgpt-native-direct-websocket",
                protocol = "v3", requestedVoiceModel = settings.Model, requestedReasoningModel = model,
                requestedReasoningTransport = "sse", declaredCsharpCommit = revision,
                startedAt, finishedAt = DateTimeOffset.UtcNow, outcome, step, errorType,
                threadId = thread.Id, logicalSessionId = session,
                input = new[] { new { sha256 = LiveRealtimeLimits.Digest(first), bytes = first.Length },
                    new { sha256 = LiveRealtimeLimits.Digest(followup), bytes = followup.Length } },
                toolCalls, reasoningRequests = budget.Reserved, completed = budget.Completed,
                incomplete = budget.Incomplete, notDispatched = budget.NotDispatched,
                reasoningUsage = budget.Receipts, stages, cleanShutdown = ended,
                media = webRtc ? "provider-webrtc-rtp-decoded-pcm-not-played" : "provider-pcm-received-not-played",
                interruption = "not-run", reconnect = reattached ? "same-call-detach-reattach" : "not-run",
                coverage = webRtc ? "webrtc-connect-two-voice-turns-same-call-reattach-host-speech-clean-end" :
                    "direct-connect-two-voice-turns-host-triggered-reasoning-speech-clean-end-only",
                storage = "process-local-cas-fixture", tokenOrCreditCeiling = "not-enforced-by-provider",
            })!.AsObject();
            if (webRtc)
            {
                trace["providerCallDigest"] = providerCallDigest;
                trace["mediaClosed"] = mediaClosed;
            }
            await JsonSerializer.SerializeAsync(receipt, trace, cancellationToken: CancellationToken.None);
        }
        Assert.True(outcome == "passed" && ended, $"Native live probe failed at {step} ({errorType ?? "unknown"}). See sanitized receipt.");
    }
}
