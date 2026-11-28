using System.Buffers.Binary;

namespace tryAGI.Codex.Tests;

// This opt-in test has no provider credentials, network signaling or physical audio output.
[Trait("Category", "OfflineWebRtcMedia")]
public sealed class NativeRealtimeWebRtcMediaPeerTests
{
    [Fact]
    public async Task LocalPeersNegotiateAndDecodeBidirectionalSyntheticAudio()
    {
        var executable = Environment.GetEnvironmentVariable("CODEX_NATIVE_WEBRTC_BROWSER");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(executable), "Explicit isolated Chromium executable required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var peer = await NativeRealtimeWebRtcMediaPeer.StartAsync(executable!, ct);
        await peer.CreateAsync("left", true, ct);
        await peer.CreateAsync("right", false, ct);
        var offer = await peer.OfferAsync("left", ct);
        Assert.Contains("m=audio ", offer);
        Assert.Contains("a=fingerprint:", offer);
        var answer = await peer.AnswerAsync("right", offer, ct);
        Assert.Contains("m=audio ", answer);
        await peer.ApplyAnswerAsync("left", answer, ct);

        async Task<NativeRealtimeWebRtcMediaPeer.Snapshot> WaitAsync(string name,
            Func<NativeRealtimeWebRtcMediaPeer.Snapshot, bool> satisfied)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = await peer.SnapshotAsync(name, ct);
                Assert.DoesNotContain(snapshot.State, new[] { "closed", "failed" });
                if (satisfied(snapshot)) return snapshot;
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{name}: {snapshot}");
                await Task.Delay(50, ct);
            }
        }

        await WaitAsync("left", x => x.State == "connected");
        await WaitAsync("right", x => x.State == "connected");

        static bool ReceivedDecodedAudio(NativeRealtimeWebRtcMediaPeer.Snapshot before,
            NativeRealtimeWebRtcMediaPeer.Snapshot after)
            => after.PacketsReceived > before.PacketsReceived && after.BytesReceived > before.BytesReceived &&
                after.TotalSamplesReceived - before.TotalSamplesReceived - (after.ConcealedSamples - before.ConcealedSamples) >= 1024 &&
                after.NonSilentSamples - before.NonSilentSamples >= 1024;

        var rightBefore = await peer.SnapshotAsync("right", ct);
        await peer.PlayAsync("left", Tone(440), ct);
        var right = await WaitAsync("right", x => ReceivedDecodedAudio(rightBefore, x));
        var leftBefore = await peer.SnapshotAsync("left", ct);
        await peer.PlayAsync("right", Tone(660), ct);
        var left = await WaitAsync("left", x => ReceivedDecodedAudio(leftBefore, x));
        // The right peer has sent its tone only after the first receive assertion.
        Assert.True((await peer.SnapshotAsync("right", ct)).PacketsSent > right.PacketsSent);
        Assert.True(left.PacketsSent > 0);
        foreach (var media in new[] { left, right })
        {
            Assert.True(media.DecodedSamples >= media.NonSilentSamples);
            Assert.Contains("opus", media.Codec ?? "", StringComparison.OrdinalIgnoreCase);
        }
        await peer.CloseAsync("left", ct);
        await peer.CloseAsync("right", ct);
    }

    private static byte[] Tone(int frequency)
    {
        const int sampleRate = 24_000;
        var pcm = new byte[sampleRate * 2];
        for (var i = 0; i < sampleRate; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2),
                (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 5000));
        return pcm;
    }
}
