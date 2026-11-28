using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using OpusSharp;
using tryAGI.WebRTC;

namespace tryAGI.Codex.Tests;

// The SDK owns authenticated signaling and sideband; this test peer owns media only.
internal sealed class NativeRealtimeWebRtcMediaPeer : IAsyncDisposable
{
    private const int FrameSamples = 480;
    private readonly PeerConnection peer;
    private readonly OpusEncoder encoder;
    private readonly OpusDecoder decoder;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private Task? audioReader, eventReader;
    private uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    private uint? source;
    private long lastSentAt, sent, received, bytesReceived, decodedSamples, nonSilentSamples;
    private bool disposed;

    internal sealed record Snapshot(string State, long PacketsSent, long PacketsReceived,
        long BytesReceived, long DecodedSamples, long NonSilentSamples, string? Codec,
        long TotalSamplesReceived, long ConcealedSamples);

    private NativeRealtimeWebRtcMediaPeer(IPAddress address, bool local)
    {
        var codec = OpusConfig.VoiceHighQualityMono(SampleRate.Hz24k);
        codec.Dtx = false;
        encoder = new OpusEncoder(codec);
        try
        {
            decoder = new OpusDecoder(codec);
            try
            {
                peer = new PeerConnection(new PeerConnectionOptions
                {
                    LocalEndPoint = new IPEndPoint(address, 0),
                    DataChannels = true,
                    AudioQueueCapacity = 64,
                    MaximumAudioSources = 1,
                    ConnectionTimeout = TimeSpan.FromSeconds(20),
                    CandidateFilter = candidate => local
                        ? IPAddress.IsLoopback(candidate.EndPoint.Address)
                        : IsPublicIpv4(candidate.EndPoint.Address),
                });
            }
            catch { decoder.Dispose(); throw; }
        }
        catch { encoder.Dispose(); throw; }
    }

    internal static NativeRealtimeWebRtcMediaPeer CreateLocal() => new(IPAddress.Loopback, true);

    internal static NativeRealtimeWebRtcMediaPeer CreateProvider(string localAddress)
    {
        if (!IPAddress.TryParse(localAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) || !NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Any(candidate => candidate.Address.Equals(address)))
            throw new ArgumentException("Select an active local non-loopback IPv4 interface.", nameof(localAddress));
        return new(address, false);
    }

    // Reject private and special-use destinations rather than probing them.
    internal static bool IsPublicIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] is > 0 and < 224 &&
            b[0] is not (10 or 127) &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99)) &&
            !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    internal string CreateOffer()
    {
        peer.CompleteGathering();
        return peer.CreateOffer();
    }

    internal string CreateAnswer(string offer)
    {
        peer.CompleteGathering();
        return peer.CreateAnswer(offer);
    }

    internal void SetRemoteAnswer(string answer) => peer.SetRemoteAnswer(answer);

    internal async Task ConnectAsync(bool openEvents, CancellationToken ct)
    {
        var connecting = peer.ConnectAsync(ct);
        try
        {
            await peer.MediaReady.WaitAsync(ct);
            audioReader = ReadAudioAsync(lifetime.Token);
            await connecting;
            if (openEvents)
            {
                var channel = await peer.OpenDataChannelAsync(
                    new DataChannelParameters("oai-events", "", true, DataChannelReliability.Reliable, 0, 256), ct);
                eventReader = DrainEventsAsync(channel, lifetime.Token);
            }
            else
                eventReader = AcceptEventsAsync(lifetime.Token);
        }
        catch
        {
            await peer.DisposeAsync();
            try { await connecting; }
            catch (Exception error) when (error is not OutOfMemoryException) { /* Preserve the original failure. */ }
            throw;
        }
    }

    private static async Task DrainEventsAsync(DataChannel channel, CancellationToken ct)
    {
        // Sideband is the sole source of reasoning events; never dispatch duplicate data-channel events.
        await foreach (var _ in channel.ReceiveMessagesAsync(ct)) { }
    }

    private async Task AcceptEventsAsync(CancellationToken ct)
    {
        await foreach (var channel in peer.AcceptDataChannelsAsync(ct))
            await DrainEventsAsync(channel, ct);
    }

    internal async Task PlayAsync(byte[] pcm, CancellationToken ct)
    {
        if (pcm.Length is < 960 or > LiveRealtimeLimits.MaxPcmBytes || pcm.Length % 2 != 0)
            throw new ArgumentException("Expected bounded 24 kHz mono PCM16.", nameof(pcm));
        var samples = new short[FrameSamples];
        var encoded = new byte[1275];
        var pace = Stopwatch.StartNew();
        for (var offset = 0; offset < pcm.Length; offset += FrameSamples * 2)
        {
            ct.ThrowIfCancellationRequested();
            EnsureHealthy();
            Array.Clear(samples);
            var count = Math.Min(FrameSamples, (pcm.Length - offset) / 2);
            for (var i = 0; i < count; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset + i * 2, 2));
            var length = encoder.Encode(samples, FrameSamples, encoded);
            if (length <= 0 || length > peer.MaximumAudioPayloadBytes)
                throw new InvalidDataException("Opus packet exceeds the negotiated media budget.");

            // Opus RTP always advances at 48 kHz even with a 24 kHz PCM encoder.
            if (offset == 0 && lastSentAt != 0)
            {
                var gap = Stopwatch.GetElapsedTime(lastSentAt).TotalMilliseconds;
                if (gap > 40)
                    timestamp = unchecked(timestamp + (uint)Math.Min(uint.MaxValue, (gap - 20) * 48));
            }
            await peer.SendOpusAsync(encoded.AsMemory(0, length), timestamp, offset == 0, ct);
            timestamp = unchecked(timestamp + 960);
            lastSentAt = Stopwatch.GetTimestamp();
            lock (gate) sent++;
            var remaining = TimeSpan.FromMilliseconds(20 * (offset / (FrameSamples * 2) + 1)) - pace.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        }
    }

    private async Task ReadAudioAsync(CancellationToken ct)
    {
        var pcm = new short[2880];
        await foreach (var packet in peer.ReceiveAudioAsync(ct))
        {
            if (source is { } prior && prior != packet.SynchronizationSource)
                throw new InvalidDataException("Unexpected second audio source.");
            source = packet.SynchronizationSource;
            var count = decoder.DecodeInto(packet.Payload, pcm, false);
            if (count <= 0 || count > pcm.Length)
                throw new InvalidDataException("Invalid decoded Opus sample count.");
            var nonSilent = 0;
            for (var i = 0; i < count; i++)
                if (Math.Abs((int)pcm[i]) > 32) nonSilent++;
            lock (gate)
            {
                received++;
                bytesReceived += packet.Payload.Length;
                decodedSamples += count;
                nonSilentSamples += nonSilent;
            }
        }
    }

    private void EnsureHealthy()
    {
        if (audioReader?.IsFaulted == true || eventReader?.IsFaulted == true ||
            peer.State == PeerConnectionState.Failed || peer.GetDiagnostics().DroppedAudioPackets != 0)
            throw new IOException("Native WebRTC media transport or decoder failed.");
    }

    internal Snapshot ReadSnapshot()
    {
        EnsureHealthy();
        lock (gate)
        {
            // These counts are authenticated RTP packets decoded by libopus, not physical playback.
            // No PLC/FEC samples are synthesized by this probe.
            return new(peer.State.ToString().ToLowerInvariant(), sent, received, bytesReceived,
                decodedSamples, nonSilentSamples, received == 0 ? null : "audio/opus", decodedSamples, 0);
        }
    }

    internal async Task CloseAsync(CancellationToken ct)
    {
        // Stop application reads before the SCTP owner disposes its channels.
        // A remote graceful close may race this local shutdown.
        await lifetime.CancelAsync();
        await peer.CloseAsync(ct);
        if (await peer.Completion.WaitAsync(ct) is not null || peer.GetDiagnostics().DroppedAudioPackets != 0)
            throw new IOException("Native WebRTC did not close cleanly.");
        await JoinReadersAsync();
    }

    private Task JoinReadersAsync() => Task.WhenAll(ObserveReaderAsync(audioReader, dataChannel: false),
        ObserveReaderAsync(eventReader, dataChannel: true));

    private async Task ObserveReaderAsync(Task? reader, bool dataChannel)
    {
        if (reader is null) return;
        try { await reader; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException error) when (dataChannel && error.ObjectName == "DataChannelAssociation" &&
            lifetime.IsCancellationRequested && peer.Completion.IsCompletedSuccessfully && peer.Completion.Result is null)
        {
            // The known owner-close notification is benign only after verified graceful peer termination.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        try { await peer.DisposeAsync(); }
        finally
        {
            try
            {
                await JoinReadersAsync();
            }
            finally
            {
                lifetime.Dispose();
                encoder.Dispose();
                decoder.Dispose();
            }
        }
    }
}
