using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

internal static class LiveRealtimeLimits
{
    internal const int MaxPcmBytes = 48_000 * 8;

    internal static void ValidateRouteOptIn(bool direct, bool webRtc)
    {
        if (direct && webRtc)
            throw new InvalidOperationException("Select exactly one authorized provider probe.");
    }

    internal static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));
    internal static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static bool SafeModel(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static async Task<byte[]> ReadPcmAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 960 or > MaxPcmBytes || file.Length % 2 != 0)
            throw new InvalidDataException("Expected bounded mono 24 kHz little-endian PCM16.");
        var pcm = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(pcm, ct);
        if (file.ReadByte() != -1 || pcm.All(b => b == 0) || pcm.AsSpan().StartsWith("RIFF"u8))
            throw new InvalidDataException("Expected raw non-silent PCM16, not a WAV container.");
        return pcm;
    }

    internal sealed record Usage(string RequestDigest, string? ResponseDigest, CodexObservedTokens? Tokens);

    internal sealed class Budget(int limit) : ICodexUsageObserver
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, string> pending = new(StringComparer.Ordinal);
        private readonly List<Usage> receipts = [];
        internal int Reserved { get; private set; }
        internal int Completed { get; private set; }
        internal int Incomplete { get; private set; }
        internal int NotDispatched { get; private set; }
        internal Usage[] Receipts { get { lock (gate) return receipts.ToArray(); } }

        public Task ReserveAsync(string requestId, string model, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (limit is < 1 or > 5 || Reserved >= limit || !pending.TryAdd(requestId, "reserved"))
                    throw new InvalidOperationException("Live reasoning request ceiling or identity violated.");
                Reserved++;
            }
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string requestId, string model, JsonElement response, CancellationToken ct)
        {
            lock (gate)
            {
                Settle(requestId);
                Completed++;
                var usage = CodexObservedUsage.Read(JsonSerializer.SerializeToElement(new { type = "response.completed", response }));
                receipts.Add(new(Digest(requestId), usage?.ResponseId is { } id ? Digest(id) : null, usage?.Tokens));
            }
            return Task.CompletedTask;
        }

        public Task IncompleteAsync(string requestId, string model, CancellationToken ct)
        {
            lock (gate) { Settle(requestId); Incomplete++; }
            return Task.CompletedTask;
        }

        public Task NotDispatchedAsync(string requestId, string model, CancellationToken ct)
        {
            lock (gate) { Settle(requestId); NotDispatched++; }
            return Task.CompletedTask;
        }

        private void Settle(string requestId)
        {
            if (!pending.TryGetValue(requestId, out var status) || status != "reserved")
                throw new InvalidOperationException("Unknown or already settled live reservation.");
            pending[requestId] = "settled";
        }
    }
}

public sealed class NativeRealtimeLiveGuardTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ProviderProbeFlagsCannotAuthorizeTwoRoutesTogether(bool direct, bool webRtc)
    {
        if (direct && webRtc)
            Assert.Throws<InvalidOperationException>(() => LiveRealtimeLimits.ValidateRouteOptIn(direct, webRtc));
        else
            LiveRealtimeLimits.ValidateRouteOptIn(direct, webRtc);
    }

    [Fact]
    public async Task ReasoningAdmissionIsBoundedAndUnknownUsageIsNotInvented()
    {
        var ct = TestContext.Current.CancellationToken;
        var budget = new LiveRealtimeLimits.Budget(2);
        await budget.ReserveAsync("one", "fixture-model", ct);
        await budget.CompleteAsync("one", "fixture-model", JsonSerializer.SerializeToElement(new { id = "provider-id" }), ct);
        await budget.ReserveAsync("two", "fixture-model", ct);
        await budget.NotDispatchedAsync("two", "fixture-model", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.ReserveAsync("three", "fixture-model", ct));
        Assert.Equal(2, budget.Reserved);
        Assert.Equal(1, budget.Completed);
        Assert.Equal(1, budget.NotDispatched);
        Assert.Null(Assert.Single(budget.Receipts).Tokens);
        Assert.DoesNotContain("provider-id", JsonSerializer.Serialize(budget.Receipts));
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.IncompleteAsync("one", "fixture-model", ct));
    }

    [Fact]
    public async Task RawPcmInputRejectsOversizeSilentAndContainerFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "codex-pcm-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var bytes in new[] { new byte[960], new byte[LiveRealtimeLimits.MaxPcmBytes + 2], new byte[961] })
            {
                await File.WriteAllBytesAsync(path, bytes, ct);
                await Assert.ThrowsAsync<InvalidDataException>(() => LiveRealtimeLimits.ReadPcmAsync(path, ct));
            }
            var pcm = new byte[960];
            "RIFF"u8.CopyTo(pcm);
            await File.WriteAllBytesAsync(path, pcm, ct);
            await Assert.ThrowsAsync<InvalidDataException>(() => LiveRealtimeLimits.ReadPcmAsync(path, ct));
            pcm[0] = 1;
            await File.WriteAllBytesAsync(path, pcm, ct);
            Assert.Equal(pcm, await LiveRealtimeLimits.ReadPcmAsync(path, ct));
        }
        finally { File.Delete(path); }
    }
}
