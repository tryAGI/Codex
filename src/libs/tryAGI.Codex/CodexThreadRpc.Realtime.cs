using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>The validated native realtime start request; raw schema fields remain available to the host.
/// Provider credentials, call creation, SDP negotiation and tool authorization belong to the host.</summary>
public sealed record CodexNativeRealtimeStart(CodexThreadDocument Thread, JsonElement Parameters,
    CodexRealtimeSessionOptions SessionOptions, CodexRealtimeCoordinatorOptions CoordinatorOptions);

/// <summary>The host-created binding. EndProvider is null for a caller-owned call which must not be hung up.
/// AnswerSdp is supplied only when the host actually negotiated WebRTC.</summary>
public sealed record CodexNativeRealtimeBinding(CodexRealtimeCoordinator Coordinator,
    string? AnswerSdp = null, Func<CodexRealtimeConnection?, CancellationToken, Task>? EndProvider = null);

public sealed partial class CodexThreadRpc
{
    private sealed class VoiceRun(CodexNativeRealtimeBinding binding)
    {
        internal CodexNativeRealtimeBinding Binding { get; } = binding;
        internal Guid EndId { get; } = Guid.NewGuid();
        internal Task? Pump;
    }

    private readonly ConcurrentDictionary<Guid, VoiceRun> realtime = new();
    private readonly SemaphoreSlim realtimeAdmission = new(1, 1);
    private Func<CodexNativeRealtimeStart, CancellationToken, Task<CodexNativeRealtimeBinding>>? realtimeFactory;

    /// <summary>Install the host's native realtime transport factory once. It must register the
    /// logical session in this RPC's store before returning the coordinator. No subprocess is used.</summary>
    public void EnableRealtime(Func<CodexNativeRealtimeStart, CancellationToken, Task<CodexNativeRealtimeBinding>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (Interlocked.CompareExchange(ref realtimeFactory, factory, null) is not null)
            throw new InvalidOperationException("Native realtime transport factory is already installed.");
        store.RealtimeHistoryCommitted += OfferRealtimeCommit;
    }

    /// <summary>Install the native transport and an explicit owner-scoped startup context provider.
    /// The provider is never called when includeStartupContext is false.</summary>
    public void EnableRealtime(Func<CodexNativeRealtimeStart, CancellationToken, Task<CodexNativeRealtimeBinding>> factory,
        Func<CodexThreadDocument, CancellationToken, Task<CodexRealtimeStartupMetadata?>> startupContextProvider)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(startupContextProvider);
        EnableRealtime(async (request, ct) =>
        {
            if (StartupContextEnabled(request.Parameters))
            {
                var metadata = await startupContextProvider(request.Thread, ct).ConfigureAwait(false);
                if (metadata is not null) request = RealtimeStart(request.Thread, request.Parameters, metadata);
            }
            return await factory(request, ct).ConfigureAwait(false);
        });
    }

    private static bool StartupContextEnabled(JsonElement p) => !p.TryGetProperty("includeStartupContext", out var include) ||
        include.ValueKind == JsonValueKind.Null || Flag(p, "includeStartupContext");

    private static JsonElement ListRealtimeVoices(JsonElement p)
    {
        ValidateFields(p, []);
        static JsonArray Voices(IReadOnlyList<string> voices) => new(voices.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return Element(new JsonObject { ["voices"] = new JsonObject
        {
            ["defaultV1"] = CodexProtocolCatalog.GetDefaultVoice("v1"),
            ["defaultV2"] = CodexProtocolCatalog.GetDefaultVoice("v2"),
            ["v1"] = Voices(CodexProtocolCatalog.V1Voices), ["v2"] = Voices(CodexProtocolCatalog.V2Voices), ["v3"] = Voices(CodexProtocolCatalog.Voices)
        } });
    }

    internal static CodexNativeRealtimeStart RealtimeStart(CodexThreadDocument thread, JsonElement p, CodexRealtimeStartupMetadata? metadata = null)
    {
        ValidateFields(p, ["threadId", "outputModality", "version", "model", "voice", "transport", "prompt",
            "initialItems", "includeStartupContext", "realtimeSessionId", "voiceSessionId", "delegationAckFiller",
            "backendReasoningStatus", "clientManagedHandoffs", "codexResponsesAsItems", "codexResponseItemPrefix",
            "codexResponseHandoffMode", "codexResponseHandoffChannelPrefixes", "flushTranscriptTailOnSessionEnd",
            "realtimeStartInstructions", "realtimeEndInstructions"]);
        var version = Text(p, "version") ?? "v3";
        if (version is not ("v1" or "v2" or "v3")) throw Invalid("Invalid realtime protocol version.");
        var output = Text(p, "outputModality");
        if (output is not ("audio" or "text") || output == "text" && version != "v2")
            throw Invalid("Text-only realtime output requires V2; outputModality is required.");
        var transport = "websocket";
        if (p.TryGetProperty("transport", out var t) && t.ValueKind != JsonValueKind.Null)
        {
            if (t.ValueKind != JsonValueKind.Object) throw Invalid("Invalid realtime transport.");
            transport = Text(t, "type") ?? throw Invalid("Realtime transport type required.");
            ValidateFields(t, transport switch
            {
                "websocket" => ["type"], "webrtc" => ["type", "sdp"], "existingCall" => ["type", "callId"],
                _ => throw Invalid("Unknown realtime transport."),
            });
            if (transport == "webrtc" && string.IsNullOrWhiteSpace(Text(t, "sdp")) ||
                transport == "existingCall" && string.IsNullOrWhiteSpace(Text(t, "callId")))
                throw Invalid("Missing realtime transport identity or SDP.");
        }
        // Existing call ownership is never inferred from the provider's call identifier.
        var voice = Text(p, "voice");
        if (voice is not null && !(version switch
        { "v1" => CodexProtocolCatalog.V1Voices, "v2" => CodexProtocolCatalog.V2Voices, _ => CodexProtocolCatalog.Voices }).Contains(voice))
            throw Invalid("Unsupported voice for the selected realtime adapter.");
        List<CodexRealtimeInitialItem> items = [];
        if (p.TryGetProperty("initialItems", out var initial) && initial.ValueKind != JsonValueKind.Null)
        {
            if (version != "v3" || initial.ValueKind != JsonValueKind.Array)
                throw Invalid("Initial role-bearing items require V3.");
            foreach (var item in initial.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid("Invalid initial item.");
                ValidateFields(item, ["role", "text"]);
                items.Add(new(Text(item, "role") ?? throw Invalid("Missing initial item role."),
                    Text(item, "text") ?? throw Invalid("Missing initial item text.")));
            }
        }
        CodexRealtimeStartupContext.ValidateInitialItems(items);
        foreach (var field in new[] { "realtimeStartInstructions", "realtimeEndInstructions" })
            CodexRealtimeSessionOptions.ValidateModeInstructions(Text(p, field));
        _ = Flag(p, "flushTranscriptTailOnSessionEnd");
        var include = StartupContextEnabled(p);
        var prompt = Text(p, "prompt") ?? "";
        if (include)
        {
            var context = CodexRealtimeStartupContext.Build(CodexRealtimeStartupContext.ConversationMessages(
                thread.Turns.SelectMany(turn => turn.Input.Concat(turn.Output))), metadata?.RecentWork, metadata?.Workspace);
            if (context is not null) prompt = prompt.Length == 0 ? context : prompt + "\n\n" + context;
        }
        bool? filler = p.TryGetProperty("delegationAckFiller", out var f) && f.ValueKind != JsonValueKind.Null
            ? Flag(p, "delegationAckFiller") : null;
        Dictionary<string, IReadOnlyList<string>>? prefixes = null;
        if (p.TryGetProperty("codexResponseHandoffChannelPrefixes", out var cp) && cp.ValueKind != JsonValueKind.Null)
        {
            if (cp.ValueKind != JsonValueKind.Object) throw Invalid("Invalid BEM prefixes.");
            prefixes = new(StringComparer.Ordinal);
            foreach (var property in cp.EnumerateObject())
            {
                if (property.Name is not ("analysis" or "commentary" or "final") || property.Value.ValueKind != JsonValueKind.Array ||
                    property.Value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                    throw Invalid("Invalid BEM channel prefixes.");
                if (!prefixes.TryAdd(property.Name, property.Value.EnumerateArray().Select(x => x.GetString()!).ToArray()))
                    throw Invalid("Duplicate BEM channel.");
            }
        }
        var mode = Text(p, "codexResponseHandoffMode") switch
        {
            null or "thinking" => CodexRealtimeReasoningChannelMode.Thinking,
            "commentary" => CodexRealtimeReasoningChannelMode.Commentary,
            "bemTags" => CodexRealtimeReasoningChannelMode.BemTags,
            _ => throw Invalid("Invalid handoff channel mode."),
        };
        return new(thread, p.Clone(), new CodexRealtimeSessionOptions(prompt)
        {
            Protocol = version, Model = Text(p, "model"), Voice = voice,
            SessionId = Text(p, "realtimeSessionId"), VoiceSessionId = Text(p, "voiceSessionId"),
            OutputModality = output == "text" ? CodexRealtimeOutputModality.Text : CodexRealtimeOutputModality.Audio,
            InitialItems = items, DelegationAckFillerSetting = filler,
        }, new CodexRealtimeCoordinatorOptions
        {
            DirectAudio = transport == "websocket", ClientManagedHandoffs = Flag(p, "clientManagedHandoffs"),
            BackendReasoningStatus = Flag(p, "backendReasoningStatus"), ResponsesAsItems = Flag(p, "codexResponsesAsItems"),
            ResponseItemPrefix = Text(p, "codexResponseItemPrefix"), ChannelMode = mode, ChannelPrefixes = prefixes,
            ModeInstructions = new(Text(p, "realtimeStartInstructions"), Text(p, "realtimeEndInstructions")),
        });
    }

    private async Task<JsonElement> StartRealtimeAsync(JsonElement p, CancellationToken ct)
    {
        var factory = Volatile.Read(ref realtimeFactory) ?? throw Invalid("Native realtime transport has not been configured by the host.");
        var id = Id(p, "threadId");
        await realtimeAdmission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping) || realtime.ContainsKey(id))
                throw Invalid("Thread already has a realtime binding or host is draining.");
            if (realtime.Count >= maximumConcurrentTurns) throw Invalid("Native realtime capacity exhausted.");
            var request = RealtimeStart(await store.ReadAsync(id, ct).ConfigureAwait(false), p);
            var binding = await factory(request, ct).ConfigureAwait(false);
            var coordinator = binding.Coordinator;
            var expectedKind = request.CoordinatorOptions.DirectAudio
                ? CodexRealtimeTransportKind.DirectWebSocket : CodexRealtimeTransportKind.ExistingCall;
            if (coordinator.TransportKind != expectedKind ||
                expectedKind == CodexRealtimeTransportKind.DirectWebSocket && (binding.EndProvider is not null || binding.AnswerSdp is not null))
                throw Invalid("Host returned a different realtime transport ownership contract.");
            if (coordinator.ThreadId != id || !coordinator.IsBoundTo(this) ||
                request.SessionOptions.SessionId is { } expected && coordinator.SessionId != expected)
                throw Invalid("Host returned a different realtime thread or session binding.");
            if (await coordinator.AttachAsync(ct).ConfigureAwait(false) != CodexRealtimeLifecycleStatus.Attached)
                throw Invalid("Realtime attachment did not complete; inspect durable history before retrying.");
            if (coordinator.Protocol != request.SessionOptions.Protocol)
            {
                await coordinator.DetachAsync(ct).ConfigureAwait(false);
                throw Invalid("Attached realtime protocol did not match the start request.");
            }
            var run = new VoiceRun(binding);
            if (!realtime.TryAdd(id, run)) throw new CodexThreadConflictException();
            await EmitAsync("thread/realtime/started", new JsonObject { ["threadId"] = id.ToString(),
                ["realtimeSessionId"] = coordinator.SessionId, ["version"] = coordinator.Protocol }).ConfigureAwait(false);
            if (binding.AnswerSdp is { } sdp)
                await EmitAsync("thread/realtime/sdp", new JsonObject { ["threadId"] = id.ToString(), ["sdp"] = sdp }).ConfigureAwait(false);
            await StartRealtimeNoticesAsync(request.Thread, ct).ConfigureAwait(false);
            run.Pump = RunRealtimePumpAsync(id, run);
            await Task.WhenAny(coordinator.PumpReady, run.Pump).WaitAsync(ct).ConfigureAwait(false);
            if (!coordinator.PumpReady.IsCompletedSuccessfully) throw Invalid("Realtime pump did not start.");
            return Json("{}");
        }
        finally { realtimeAdmission.Release(); }
    }

    private async Task RunRealtimePumpAsync(Guid id, VoiceRun run)
    {
        string? reason = null;
        try
        {
            var status = await run.Binding.Coordinator.RunAsync((item, _) => PublishRealtimeEventAsync(id, item, false),
                (item, _) => PublishRealtimeEventAsync(id, item, true)).ConfigureAwait(false);
            reason = status.ToString();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            reason = "transportOrObserverFailed";
            await EmitAsync("thread/realtime/error", new JsonObject { ["threadId"] = id.ToString(),
                ["message"] = "Native realtime transport or observer failed. Inspect durable history before retrying." }).ConfigureAwait(false);
        }
        await EmitAsync("thread/realtime/closed", new JsonObject { ["threadId"] = id.ToString(), ["reason"] = reason }).ConfigureAwait(false);
    }

    private async Task PublishRealtimeEventAsync(Guid id, CodexRealtimeEvent item, bool audioOnly)
    {
        var thread = id.ToString();
        if (audioOnly)
        {
            if (item.Audio is { } audio && !item.StaleForFeedback)
                await EmitAsync("thread/realtime/outputAudio/delta", new JsonObject { ["threadId"] = thread,
                    ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(audio.Pcm16.Span),
                        ["sampleRate"] = audio.SampleRate, ["numChannels"] = audio.Channels,
                        ["samplesPerChannel"] = audio.SamplesPerChannel, ["itemId"] = audio.ItemId } }).ConfigureAwait(false);
            return;
        }
        if (item.ConversationItem is { } conversation)
            await EmitAsync("thread/realtime/itemAdded", new JsonObject { ["threadId"] = thread,
                ["item"] = JsonNode.Parse(conversation.Item.GetRawText()) }).ConfigureAwait(false);
        if (item.Transcript is { } transcript)
            await EmitAsync(transcript.Final ? "thread/realtime/transcript/done" : "thread/realtime/transcript/delta",
                new JsonObject { ["threadId"] = thread, ["role"] = transcript.Role,
                    [transcript.Final ? "text" : "delta"] = transcript.Text }).ConfigureAwait(false);
        if (item.Error is { } error)
            await EmitAsync("thread/realtime/error", new JsonObject { ["threadId"] = thread,
                ["message"] = error.Code ?? "Realtime provider reported an error." }).ConfigureAwait(false);
    }

    private VoiceRun Voice(JsonElement p) => realtime.TryGetValue(Id(p, "threadId"), out var run)
        ? run : throw Invalid("Thread has no native realtime binding.");

    private JsonElement AppendRealtimeAudio(JsonElement p)
    {
        ValidateFields(p, ["threadId", "audio"]);
        var run = Voice(p);
        if (!p.TryGetProperty("audio", out var audio) || audio.ValueKind != JsonValueKind.Object) throw Invalid("Audio is required.");
        ValidateFields(audio, ["data", "itemId", "numChannels", "sampleRate", "samplesPerChannel"]);
        if (!audio.TryGetProperty("sampleRate", out var rate) || !rate.TryGetInt32(out var hz) || hz != 24000 ||
            !audio.TryGetProperty("numChannels", out var count) || !count.TryGetInt32(out var channels) || channels != 1)
            throw Invalid("Direct audio requires 24kHz mono PCM16.");
        var encoded = Text(audio, "data") ?? throw Invalid("Audio data is required.");
        if (encoded.Length > 90_000) throw Invalid("Audio frame exceeds bound.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw Invalid("Audio is not valid base64."); }
        if (audio.TryGetProperty("samplesPerChannel", out var samples) && samples.ValueKind != JsonValueKind.Null &&
            (!samples.TryGetUInt32(out var sampleCount) || sampleCount != bytes.Length / 2))
            throw Invalid("Audio sample count does not match PCM16 data.");
        if (!run.Binding.Coordinator.TryAppendAudio(bytes)) throw Invalid("Realtime audio queue is full or not running.");
        return Json("{}");
    }

    private JsonElement AppendRealtimeText(JsonElement p, bool speech)
    {
        ValidateFields(p, speech ? ["threadId", "text"] : ["threadId", "text", "role"]);
        var run = Voice(p);
        var text = Text(p, "text") ?? throw Invalid("Realtime text is required.");
        var accepted = speech ? run.Binding.Coordinator.TryAppendSpeech(text)
            : run.Binding.Coordinator.TryAppendText(text, Text(p, "role") ?? "user");
        if (!accepted) throw Invalid("Realtime control queue is full or not running.");
        return Json("{}");
    }

    private async Task<JsonElement> StopRealtimeAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId"]);
        var id = Id(p, "threadId");
        await realtimeAdmission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var run = Voice(p);
            var ended = await run.Binding.Coordinator.EndAsync(run.EndId, run.Binding.EndProvider, ct).ConfigureAwait(false);
            if (ended != CodexRealtimeLifecycleStatus.Ended)
                throw Invalid("Realtime history recovery required before acknowledging stop.");
            ReconcileRealtimeNotices(await store.ReadAsync(id, CancellationToken.None).ConfigureAwait(false));
            await FlushRealtimeNoticesAsync(id, ct).ConfigureAwait(false);
            realtime.TryRemove(id, out _);
            await run.Binding.Coordinator.DisposeAsync().ConfigureAwait(false);
            return Json("{}");
        }
        finally { realtimeAdmission.Release(); }
    }

    private async Task DrainRealtimeAsync(CancellationToken ct)
    {
        await realtimeAdmission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in realtime.ToArray())
            {
                await entry.Value.Binding.Coordinator.DetachAsync(ct).ConfigureAwait(false);
                await entry.Value.Binding.Coordinator.DisposeAsync().ConfigureAwait(false);
                realtime.TryRemove(entry.Key, out _);
            }
        }
        finally { StopRealtimeNotices(); realtimeAdmission.Release(); }
    }

    private async Task<JsonElement> ListRealtimeAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "afterPosition", "cursor", "eventId", "fromObservedAtMs", "limit", "order",
            "requiredThroughPosition", "sessionId", "snapshotEnd", "toObservedAtMs"]);
        var thread = await store.ReadAsync(Id(p, "threadId"), ct).ConfigureAwait(false);
        static long? Number(JsonElement p, string key, bool unsigned)
        {
            if (!p.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || unsigned && number < 0)
                throw Invalid("Invalid realtime list number: " + key);
            return number;
        }
        var session = Text(p, "sessionId");
        var eventId = Text(p, "eventId");
        var order = Text(p, "order") ?? "observedAt";
        if (order is not ("observedAt" or "position")) throw Invalid("Invalid realtime list order.");
        var limit = Number(p, "limit", true) ?? 50;
        if (limit is < 1 or > 256) throw Invalid("Realtime page limit must be between 1 and 256.");
        var from = Number(p, "fromObservedAtMs", false);
        var to = Number(p, "toObservedAtMs", false);
        if (from is not null && to is not null && from > to) throw Invalid("Invalid observation range.");
        var after = Number(p, "afterPosition", true);
        var required = Number(p, "requiredThroughPosition", true);
        var requestedSnapshot = Number(p, "snapshotEnd", true);
        var items = CodexThreadStore.ProjectRealtimeItems(thread);
        var snapshot = requestedSnapshot ?? items.Count;
        var cursor = Text(p, "cursor");
        var filterKey = new JsonArray(JsonValue.Create(thread.Id.ToString()), JsonValue.Create(session), JsonValue.Create(eventId),
            JsonValue.Create(order), JsonValue.Create(from), JsonValue.Create(to), JsonValue.Create(after));
        var filterHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(filterKey.ToJsonString())));
        var offset = 0;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 4 || parts[0] != "rt1" || !long.TryParse(parts[1], out var cursorSnapshot) ||
                !int.TryParse(parts[2], out offset) || offset < 0 || cursorSnapshot < 0 || parts[3] != filterHash ||
                requestedSnapshot is not null && requestedSnapshot != cursorSnapshot)
                throw Invalid("Realtime cursor does not belong to these immutable snapshot filters.");
            snapshot = cursorSnapshot;
        }
        var pending = snapshot > items.Count || required is { } through && through >= items.Count ||
            eventId is not null && !items.Any(x => x["id"]?.GetValue<string>() == eventId);
        var coverage = thread.RealtimeSessions is not { Length: > 0 } ? "unsupported" : pending ? "pending" : "ready";
        var entries = items.Select((item, position) => (Item: item, Position: position))
            .Where(x => x.Position < snapshot && (after is null || x.Position > after) &&
                (session is null || x.Item["realtimeSessionId"]?.GetValue<string>() == session) &&
                (eventId is null || x.Item["id"]?.GetValue<string>() == eventId) &&
                (from is null || x.Item["observedAtMs"]?.GetValue<long>() >= from) &&
                (to is null || x.Item["observedAtMs"]?.GetValue<long>() < to));
        if (order == "observedAt") entries = entries.OrderBy(x => x.Item["observedAtMs"]?.GetValue<long>() ?? long.MaxValue).ThenBy(x => x.Position);
        var page = pending ? [] : entries.Skip(offset).Take(checked((int)limit + 1)).ToArray();
        var data = new JsonArray(page.Take((int)limit).Select(x => (JsonNode?)new JsonObject
        { ["position"] = x.Position, ["item"] = x.Item.DeepClone() }).ToArray());
        var next = page.Length > limit ? $"rt1:{snapshot}:{offset + limit}:{filterHash}" : null;
        return Element(new JsonObject { ["coverage"] = coverage, ["data"] = data,
            ["projectedThroughItemIndex"] = items.Count, ["snapshotEnd"] = snapshot, ["nextCursor"] = next });
    }
}
