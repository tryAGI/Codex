using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

// Test-only media peer. The browser never receives SDK credentials or performs provider signaling.
internal sealed class NativeRealtimeWebRtcMediaPeer : IAsyncDisposable
{
    private readonly DirectoryInfo profile;
    private readonly Process browser;
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim commands = new(1);
    private bool started;
    private Uri? browserEndpoint;
    private int nextId;
    private readonly ConcurrentQueue<string> startupDiagnostics = new();

    internal sealed record Snapshot(string State, long PacketsSent, long PacketsReceived,
        long BytesReceived, long DecodedSamples, long NonSilentSamples, string? Codec,
        long TotalSamplesReceived, long ConcealedSamples, double TotalAudioEnergy);

    private NativeRealtimeWebRtcMediaPeer(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("An explicit installed Chromium executable is required.", nameof(executable));
        profile = Directory.CreateTempSubdirectory("codex-webrtc-media-");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(profile.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        // Only the environment needed to start the isolated browser is inherited.
        var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "TEMP", "TMP", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE",
            "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA", "ALLUSERSPROFILE",
            "PROGRAMFILES", "PROGRAMFILES(X86)", "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)",
            "PROCESSOR_ARCHITECTURE", "OS", "LANG", "LC_ALL",
            "DISPLAY", "XDG_RUNTIME_DIR", "COMSPEC", "PATHEXT", "NUMBER_OF_PROCESSORS",
        };
        foreach (var key in start.Environment.Keys.Where(key => !safe.Contains(key)).ToArray())
            start.Environment.Remove(key);
        foreach (var argument in new[]
        {
            "--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0",
            "--user-data-dir=" + profile.FullName, "--no-first-run", "--no-default-browser-check",
            "--disable-background-networking", "--disable-sync", "--disable-default-apps", "--mute-audio",
            "--autoplay-policy=no-user-gesture-required", "about:blank",
        })
            start.ArgumentList.Add(argument);
        browser = new Process { StartInfo = start };
        browser.OutputDataReceived += static (_, _) => { };
        browser.ErrorDataReceived += (_, line) =>
        {
            // Record only bounded startup diagnostics; never include media, SDP or provider traffic.
            if (line.Data is { } value && startupDiagnostics.Count < 8)
                startupDiagnostics.Enqueue(value.Length <= 300 ? value : value[..300]);
        };
    }

    internal static async Task<NativeRealtimeWebRtcMediaPeer> StartAsync(string executable, CancellationToken ct)
    {
        var peer = new NativeRealtimeWebRtcMediaPeer(executable);
        try
        {
            peer.started = peer.browser.Start();
            if (!peer.started) throw new InvalidOperationException("Chromium did not start.");
            peer.browser.BeginOutputReadLine();
            peer.browser.BeginErrorReadLine();
            var portFile = Path.Combine(peer.profile.FullName, "DevToolsActivePort");
            var startup = Stopwatch.StartNew();
            int port;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(portFile))
                {
                    var lines = await File.ReadAllLinesAsync(portFile, ct);
                    const string prefix = "/devtools/browser/";
                    if (lines.Length == 2 && int.TryParse(lines[0], out port) && port is > 0 and <= 65535 &&
                        lines[1].StartsWith(prefix, StringComparison.Ordinal) &&
                        Guid.TryParseExact(lines[1][prefix.Length..], "D", out _))
                    {
                        peer.browserEndpoint = new Uri($"ws://127.0.0.1:{port}{lines[1]}");
                        break;
                    }
                }
                // Windows can exit its initial launcher normally while the browser initializes the profile.
                if (startup.Elapsed >= TimeSpan.FromSeconds(15) || (peer.browser.HasExited && peer.browser.ExitCode != 0))
                    throw new InvalidOperationException($"Isolated Chromium did not publish its endpoint (exit {(peer.browser.HasExited ? peer.browser.ExitCode : null)}): {string.Join(" | ", peer.startupDiagnostics)}");
                await Task.Delay(50, ct);
            }
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct));
            var target = targets.RootElement.EnumerateArray().Single(item => item.GetProperty("type").GetString() == "page" &&
                item.GetProperty("url").GetString() == "about:blank");
            var id = target.GetProperty("id").GetString();
            if (id is not { Length: > 0 and <= 128 } || !id.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Unexpected isolated Chromium page identity.");
            await peer.socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/page/{id}"), ct);
            await peer.EvaluateAsync(Setup, ct);
            return peer;
        }
        catch (Exception startupError)
        {
            try { await peer.DisposeAsync(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Browser startup and cleanup failed.", startupError, cleanupError);
            }
            throw;
        }
    }

    private static string Js(string value) => JsonSerializer.Serialize(value);

    internal async Task CreateAsync(string name, bool dataChannel, CancellationToken ct)
        => _ = await EvaluateAsync($"codexMedia.create({Js(name)}, {(dataChannel ? "true" : "false")})", ct);

    internal async Task<string> OfferAsync(string name, CancellationToken ct)
        => (await EvaluateAsync($"codexMedia.offer({Js(name)})", ct)).GetString()
            ?? throw new InvalidDataException("Missing local offer.");

    internal async Task<string> AnswerAsync(string name, string offer, CancellationToken ct)
        => (await EvaluateAsync($"codexMedia.answer({Js(name)}, {Js(offer)})", ct)).GetString()
            ?? throw new InvalidDataException("Missing local answer.");

    internal async Task ApplyAnswerAsync(string name, string answer, CancellationToken ct)
        => _ = await EvaluateAsync($"codexMedia.applyAnswer({Js(name)}, {Js(answer)})", ct);

    internal async Task PlayAsync(string name, byte[] pcm, CancellationToken ct)
    {
        if (pcm.Length is < 960 or > LiveRealtimeLimits.MaxPcmBytes || pcm.Length % 2 != 0)
            throw new ArgumentException("Expected bounded 24 kHz mono PCM16.", nameof(pcm));
        await EvaluateAsync($"codexMedia.play({Js(name)}, {Js(Convert.ToBase64String(pcm))})", ct);
    }

    internal async Task<Snapshot> SnapshotAsync(string name, CancellationToken ct)
    {
        var value = await EvaluateAsync($"codexMedia.snapshot({Js(name)})", ct);
        return new(value.GetProperty("state").GetString() ?? "unknown",
            value.GetProperty("packetsSent").GetInt64(), value.GetProperty("packetsReceived").GetInt64(),
            value.GetProperty("bytesReceived").GetInt64(), value.GetProperty("decodedSamples").GetInt64(),
            value.GetProperty("nonSilentSamples").GetInt64(), value.GetProperty("codec").GetString(),
            value.GetProperty("totalSamplesReceived").GetInt64(), value.GetProperty("concealedSamples").GetInt64(),
            value.GetProperty("totalAudioEnergy").GetDouble());
    }

    internal async Task CloseAsync(string name, CancellationToken ct)
        => _ = await EvaluateAsync($"codexMedia.close({Js(name)})", ct);

    private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        await commands.WaitAsync(ct);
        try
        {
            var id = checked(++nextId);
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id, method = "Runtime.evaluate",
                @params = new { expression, awaitPromise = true, returnByValue = true },
            });
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, ct);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 1024 * 1024)
                        throw new InvalidDataException("Unexpected or oversized isolated browser reply.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var response = JsonDocument.Parse(message.ToArray());
                var root = response.RootElement;
                if (!root.TryGetProperty("id", out var replyId)) continue;
                if (replyId.GetInt32() != id || root.TryGetProperty("error", out _) ||
                    !root.TryGetProperty("result", out var result) || result.TryGetProperty("exceptionDetails", out _) ||
                    !result.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var value))
                    throw new InvalidOperationException("Isolated browser media command failed.");
                return value.Clone();
            }
        }
        finally { commands.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        socket.Abort();
        socket.Dispose();
        commands.Dispose();
        try
        {
            // The private profile publishes a unique browser endpoint even if its launcher exits.
            if (browserEndpoint is { } endpoint)
            {
                using var shutdown = new ClientWebSocket();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await shutdown.ConnectAsync(endpoint, deadline.Token);
                    await shutdown.SendAsync("{\"id\":1,\"method\":\"Browser.close\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
                    await Task.Delay(250);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException)
                {
                    // A closed endpoint is expected during shutdown; profile deletion still verifies cleanup.
                }
            }
            if (started && !browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await browser.WaitForExitAsync(timeout.Token);
            }
            for (var attempt = 0; Directory.Exists(profile.FullName); attempt++)
            {
                try { profile.Delete(recursive: true); break; }
                catch (IOException) when (attempt < 50) { await Task.Delay(100); }
            }
        }
        finally { browser.Dispose(); }
    }

    // The receiver counts decoded samples with zero output gain; it does not claim audible playback.
    private const string Setup = """
        (() => {
            const peers = new Map();
            const get = name => {
                const p = peers.get(name);
                if (!p) throw Error("Unknown peer");
                return p;
            };
            async function local(p, description) {
                await p.pc.setLocalDescription(description);
                if (p.pc.iceGatheringState !== "complete") {
                    await new Promise((resolve, reject) => {
                        const finish = () => {
                            if (p.pc.iceGatheringState !== "complete") return;
                            clearTimeout(timer);
                            p.pc.removeEventListener("icegatheringstatechange", finish);
                            resolve();
                        };
                        const timer = setTimeout(() => {
                            p.pc.removeEventListener("icegatheringstatechange", finish);
                            reject(Error("ICE gathering timeout"));
                        }, 10000);
                        p.pc.addEventListener("icegatheringstatechange", finish);
                        finish();
                    });
                }
                return p.pc.localDescription.sdp;
            }
            globalThis.codexMedia = {
                async create(name, dataChannel) {
                    if (peers.has(name)) throw Error("Duplicate peer");
                    const context = new AudioContext({ sampleRate: 48000, sinkId: { type: "none" } });
                    const destination = context.createMediaStreamDestination();
                    const pc = new RTCPeerConnection({ iceServers: [], bundlePolicy: "max-bundle" });
                    const p = { pc, context, destination, nodes: [], decodedSamples: 0, nonSilentSamples: 0 };
                    peers.set(name, p);
                    pc.addTrack(destination.stream.getAudioTracks()[0], destination.stream);
                    if (dataChannel) p.channel = pc.createDataChannel("oai-events");
                    pc.ontrack = event => {
                        if (event.track.kind !== "audio" || p.nodes.length) return;
                        const remote = new MediaStream([event.track]);
                        // Chromium needs an active media element to drive the remote audio playout pipeline.
                        // The element, process and WebAudio destination remain silent.
                        const audio = document.createElement("audio");
                        audio.autoplay = true;
                        audio.muted = true;
                        audio.volume = 0;
                        audio.srcObject = remote;
                        document.body.appendChild(audio);
                        p.audio = audio;
                        p.playout = audio.play().then(() => true, () => false);
                        const source = context.createMediaStreamSource(remote);
                        const processor = context.createScriptProcessor(1024, 1, 1);
                        const mute = context.createGain();
                        mute.gain.value = 0;
                        processor.onaudioprocess = e => {
                            const samples = e.inputBuffer.getChannelData(0);
                            p.decodedSamples += samples.length;
                            for (const sample of samples) if (Math.abs(sample) > 0.001) p.nonSilentSamples++;
                        };
                        source.connect(processor);
                        processor.connect(mute);
                        mute.connect(context.destination);
                        p.nodes = [source, processor, mute];
                    };
                    await context.resume();
                    return true;
                },
                async offer(name) {
                    const p = get(name);
                    return local(p, await p.pc.createOffer());
                },
                async answer(name, sdp) {
                    const p = get(name);
                    await p.pc.setRemoteDescription({ type: "offer", sdp });
                    return local(p, await p.pc.createAnswer());
                },
                async applyAnswer(name, sdp) {
                    await get(name).pc.setRemoteDescription({ type: "answer", sdp });
                    return true;
                },
                async play(name, encoded) {
                    const p = get(name);
                    if (p.pc.connectionState !== "connected") throw Error("Peer not connected");
                    const bytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
                    const view = new DataView(bytes.buffer);
                    const buffer = p.context.createBuffer(1, bytes.length / 2, 24000);
                    const samples = buffer.getChannelData(0);
                    for (let i = 0; i < samples.length; i++) samples[i] = view.getInt16(i * 2, true) / 32768;
                    const source = p.context.createBufferSource();
                    source.buffer = buffer;
                    source.connect(p.destination);
                    await new Promise(resolve => { source.onended = resolve; source.start(); });
                    source.disconnect();
                    return true;
                },
                async snapshot(name) {
                    const p = get(name);
                    const stats = await p.pc.getStats();
                    let packetsSent = 0, packetsReceived = 0, bytesReceived = 0, codec = null;
                    let totalSamplesReceived = 0, concealedSamples = 0, totalAudioEnergy = 0;
                    for (const row of stats.values()) {
                        if (row.kind !== "audio") continue;
                        if (row.type === "outbound-rtp") packetsSent += row.packetsSent ?? 0;
                        if (row.type === "inbound-rtp") {
                            packetsReceived += row.packetsReceived ?? 0;
                            bytesReceived += row.bytesReceived ?? 0;
                            codec = stats.get(row.codecId)?.mimeType ?? codec;
                            totalSamplesReceived += row.totalSamplesReceived ?? 0;
                            concealedSamples += row.concealedSamples ?? 0;
                            totalAudioEnergy += row.totalAudioEnergy ?? 0;
                        }
                    }
                    return { state: p.pc.connectionState, packetsSent, packetsReceived, bytesReceived, codec,
                        decodedSamples: p.decodedSamples, nonSilentSamples: p.nonSilentSamples,
                        totalSamplesReceived, concealedSamples, totalAudioEnergy };
                },
                async close(name) {
                    const p = get(name);
                    p.pc.close();
                    if (p.audio) {
                        p.audio.pause();
                        p.audio.srcObject = null;
                        p.audio.remove();
                    }
                    for (const node of p.nodes) node.disconnect();
                    p.destination.stream.getTracks().forEach(track => track.stop());
                    await p.context.close();
                    return true;
                }
            };
            return true;
        })()
        """;
}
