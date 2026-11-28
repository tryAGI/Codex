using System.Buffers.Binary;
using System.Net;

namespace tryAGI.Codex.Tests;

// No browser, provider credentials, external network signaling or physical audio output.
[Trait("Category", "OfflineWebRtcMedia")]
public sealed class NativeRealtimeWebRtcMediaPeerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalPeersNegotiateAndDecodeBidirectionalSyntheticAudio(bool peerInitiatedClose)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var left = NativeRealtimeWebRtcMediaPeer.CreateLocal();
        await using var right = NativeRealtimeWebRtcMediaPeer.CreateLocal();
        var offer = left.CreateOffer();
        Assert.Contains("m=audio ", offer);
        Assert.Contains("a=fingerprint:", offer);
        var answer = right.CreateAnswer(offer);
        Assert.Contains("m=audio ", answer);
        left.SetRemoteAnswer(answer);
        await Task.WhenAll(left.ConnectAsync(true, ct), right.ConnectAsync(false, ct));

        async Task<NativeRealtimeWebRtcMediaPeer.Snapshot> ReceiveAsync(NativeRealtimeWebRtcMediaPeer receiver,
            NativeRealtimeWebRtcMediaPeer.Snapshot baseline)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = receiver.ReadSnapshot();
                Assert.Equal("connected", snapshot.State);
                if (snapshot.PacketsReceived > baseline.PacketsReceived &&
                    snapshot.BytesReceived > baseline.BytesReceived &&
                    snapshot.DecodedSamples - baseline.DecodedSamples >= 1024 &&
                    snapshot.NonSilentSamples - baseline.NonSilentSamples >= 1024)
                    return snapshot;
                await Task.Delay(10, ct);
            }
        }

        var rightBefore = right.ReadSnapshot();
        await left.PlayAsync(Tone(440), ct);
        var fromLeft = await ReceiveAsync(right, rightBefore);
        var leftBefore = left.ReadSnapshot();
        await right.PlayAsync(Tone(660), ct);
        var fromRight = await ReceiveAsync(left, leftBefore);
        foreach (var media in new[] { fromLeft, fromRight })
        {
            Assert.Equal("audio/opus", media.Codec);
            Assert.Equal(0, media.ConcealedSamples);
            Assert.True(media.DecodedSamples >= media.NonSilentSamples);
        }
        Assert.Equal(50, left.ReadSnapshot().PacketsSent);
        Assert.Equal(50, right.ReadSnapshot().PacketsSent);
        if (peerInitiatedClose)
        {
            await left.CloseAsync(ct);
            await right.CloseAsync(ct);
        }
        else
            await Task.WhenAll(left.CloseAsync(ct), right.CloseAsync(ct));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("::1", false)]
    [InlineData("8.8.8.8", true)]
    public void ProviderCandidatePolicyRejectsPrivateDestinations(string address, bool expected)
        => Assert.Equal(expected, NativeRealtimeWebRtcMediaPeer.IsPublicIpv4(IPAddress.Parse(address)));

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
