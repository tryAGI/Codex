using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadStore
{
    // Post-CAS immutable snapshots. List remains authoritative if the subscriber disconnects.
    internal event Action<Guid, long, JsonElement>? RealtimeHistoryCommitted;

    private void NotifyRealtimeCommit(CodexThreadDocument document, JsonElement body, bool reconciled = false)
    {
        var listeners = RealtimeHistoryCommitted;
        if (listeners is null || document.RealtimeSessions is not { Length: > 0 }) return;
        if (!reconciled && !document.RealtimeSessions.Any(s => s.StartedRevision == document.Revision ||
            s.ClosedRevision == document.Revision || s.History?.Events.LastOrDefault()?.CommitRevision == document.Revision))
            return;
        foreach (var listener in listeners.GetInvocationList())
        {
            try { ((Action<Guid, long, JsonElement>)listener)(document.Id, document.Revision, body); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { /* A subscriber cannot invalidate an acknowledged journal commit. */ }
        }
    }

    private void NotifyReconciledRealtimeSnapshot(CodexThreadDocument document)
    {
        if (RealtimeHistoryCommitted is not null) NotifyRealtimeCommit(document, Encode(document), reconciled: true);
    }
}

public sealed partial class CodexThreadRpc
{
    private readonly ConcurrentDictionary<Guid, RealtimeNoticeStream> realtimeNotices = new();
    private const int MaximumRealtimeNoticeStreams = 256;

    private void OfferRealtimeCommit(Guid threadId, long revision, JsonElement body)
    {
        if (realtimeNotices.TryGetValue(threadId, out var stream)) stream.Offer(revision, body);
    }

    private async Task StartRealtimeNoticesAsync(CodexThreadDocument baseline, CancellationToken ct)
    {
        if (!realtimeNotices.TryGetValue(baseline.Id, out var stream))
        {
            // Retain a bounded cursor after session end for late backing artifacts.
            // A client can always reconcile an evicted cursor through realtime/list.
            if (realtimeNotices.Count >= MaximumRealtimeNoticeStreams)
            {
                foreach (var entry in realtimeNotices)
                    if (!realtime.ContainsKey(entry.Key) && !active.ContainsKey(entry.Key) &&
                        realtimeNotices.TryRemove(entry.Key, out var retired))
                    {
                        retired.Stop();
                        break;
                    }
            }
            if (realtimeNotices.Count >= MaximumRealtimeNoticeStreams)
            {
                Interlocked.Increment(ref notificationFailures);
                return;
            }
            stream = realtimeNotices.GetOrAdd(baseline.Id, _ => new(this, store, baseline));
        }
        // Subscribe before reading the baseline successor. An artifact committed during
        // attachment cannot fall between the initial snapshot and the live cursor.
        var current = await store.ReadAsync(baseline.Id, ct).ConfigureAwait(false);
        stream.Offer(current.Revision, CodexThreadStore.Encode(current));
    }

    private void ReconcileRealtimeNotices(CodexThreadDocument document)
    {
        if (realtimeNotices.TryGetValue(document.Id, out var stream) && stream.Needs(document.Revision))
            stream.Offer(document.Revision, CodexThreadStore.Encode(document));
    }

    private async Task FlushRealtimeNoticesAsync(Guid id, CancellationToken ct)
    {
        if (!realtimeNotices.TryGetValue(id, out var stream)) return;
        try { await stream.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        { Interlocked.Increment(ref notificationFailures); }
    }

    private void StopRealtimeNotices()
    {
        store.RealtimeHistoryCommitted -= OfferRealtimeCommit;
        foreach (var entry in realtimeNotices.ToArray())
            if (realtimeNotices.TryRemove(entry.Key, out var stream)) stream.Stop();
    }

    // One in-flight snapshot and one replaceable pending snapshot per thread. No audio or
    // reasoning producer waits for client notification delivery, and committed items cannot
    // disappear when deltas are coalesced: the newest journal contains the complete prefix.
    private sealed class RealtimeNoticeStream
    {
        private readonly CodexThreadRpc owner;
        private readonly CodexThreadStore journal;
        private readonly Guid threadId;
        private readonly Lock gate = new();
        private readonly Dictionary<string, string> activeText = new(StringComparer.Ordinal);
        private (long Revision, JsonElement Body)? pending;
        private Task? worker;
        private bool stopped;
        private long offered;
        private long projectedRevision;
        private int position;

        internal RealtimeNoticeStream(CodexThreadRpc owner, CodexThreadStore journal, CodexThreadDocument baseline)
        {
            this.owner = owner;
            this.journal = journal;
            threadId = baseline.Id;
            offered = projectedRevision = baseline.Revision;
            position = CodexThreadStore.ProjectRealtimeTimeline(baseline, journal, baseline.Revision).Count;
        }

        internal bool Needs(long revision) { lock (gate) return !stopped && revision > offered; }

        internal void Offer(long revision, JsonElement body)
        {
            lock (gate)
            {
                if (stopped || revision <= offered) return;
                offered = revision;
                pending = (revision, body);
                worker ??= Task.Run(DrainAsync);
            }
        }

        internal Task FlushAsync() { lock (gate) return worker ?? Task.CompletedTask; }

        internal void Stop()
        {
            lock (gate) { stopped = true; pending = null; }
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                (long Revision, JsonElement Body) snapshot;
                lock (gate)
                {
                    if (stopped || pending is not { } next)
                    {
                        worker = null;
                        return;
                    }
                    snapshot = next;
                    pending = null;
                }
                try
                {
                    var document = journal.ValidateSnapshot(snapshot.Body);
                    if (document.Id != threadId || document.Revision != snapshot.Revision)
                        throw new InvalidDataException("Realtime notification snapshot identity changed.");
                    await PublishAsync(document).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Interlocked.Increment(ref owner.notificationFailures); }
            }
        }

        private async Task PublishAsync(CodexThreadDocument document)
        {
            var items = CodexThreadStore.ProjectRealtimeTimeline(document, journal, projectedRevision, includeActiveFrames: true);
            if (items.PrefixCount != position) throw new InvalidDataException("Realtime timeline prefix changed.");
            foreach (var row in items.Rows)
            {
                lock (gate) if (stopped) return;
                var item = row.Materialize();
                var type = (string?)item["type"];
                // Rust suppresses these two model-input events on the live app-server lane.
                if (type is "sessionInputSaved" or "turnInputAccepted") continue;
                if (type is "frame" or "transcriptSegment")
                {
                    await StreamAsync(item).ConfigureAwait(false);
                    activeText.Remove((string)item["id"]!);
                }
                await EmitItemAsync("thread/realtime/item/completed", item).ConfigureAwait(false);
            }
            position = items.Count;
            projectedRevision = document.Revision;

            foreach (var frame in items.ActiveFrames)
            {
                lock (gate) if (stopped) return;
                await StreamAsync(frame).ConfigureAwait(false);
            }
            var activeIds = items.ActiveFrames.Select(item => (string)item["id"]!).ToHashSet(StringComparer.Ordinal);
            foreach (var id in activeText.Keys.Where(id => !activeIds.Contains(id)).ToArray()) activeText.Remove(id);
        }

        private async Task StreamAsync(JsonObject item)
        {
            var id = (string)item["id"]!;
            var content = Transcript(item);
            var text = (string?)content["text"] ?? "";
            if (!activeText.TryGetValue(id, out var previous))
            {
                previous = "";
                var start = (JsonObject)item.DeepClone();
                Transcript(start)["text"] = "";
                await EmitItemAsync("thread/realtime/item/started", start).ConfigureAwait(false);
            }
            if (text.StartsWith(previous, StringComparison.Ordinal))
            {
                if (text.Length > previous.Length)
                    await owner.EmitAsync("thread/realtime/item/transcript/delta", new JsonObject
                    {
                        ["threadId"] = threadId.ToString(), ["itemId"] = id, ["delta"] = text[previous.Length..],
                    }).ConfigureAwait(false);
                activeText[id] = text;
            }
            else activeText.TryAdd(id, previous);
            // Keep the last actually emitted prefix on a divergent legacy revision.
            // Only the completed item can replace that prefix.
        }

        private static JsonObject Transcript(JsonObject item)
            => (string?)item["type"] == "frame" ? (JsonObject)item["channels"]!["voice_transcript"]! : item;

        private Task EmitItemAsync(string method, JsonObject item) => owner.EmitAsync(method, new JsonObject
        {
            ["threadId"] = threadId.ToString(), ["item"] = item.DeepClone(),
        });
    }
}
