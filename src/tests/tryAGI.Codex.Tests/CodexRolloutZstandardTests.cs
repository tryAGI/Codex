using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class CodexRolloutZstandardTests
{
    // Produced by the upstream Zstandard CLI 1.5.7, streaming level 3, as used by Rust rollout compression.
    private const string Fixture = "KLUv/QRYvQQAksogHmBnqwMIu0TxZjt5R0/a4hU0CL7dfgoqvpskyBbkAUWMAtGPsRdYeQ8/fJM1QLcClNd93VNsvFwNVj57i41PkKpv7buSq3tQvxQx2jiQREj+Cf0gZSCdg/DBSQMZnAPgCRGSYuXCckK5kI8hEto49y1xb6lerXyIWBuvlWHL1be1TgQFAEQjjpyWL1gGkCE/OC7hDCixfiU=";
    private const string Header = """{"timestamp":"t","type":"session_meta","payload":{"id":"c8958e23-460a-4e93-8fc5-e58eea65a630"}}""";
    private const string Item = """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}}""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CodexRolloutZstandard Open()
    {
        var path = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "/opt/homebrew/opt/zstd/lib/libzstd.dylib"
            : OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? "/usr/lib/x86_64-linux-gnu/libzstd.so.1" : "";
        Assert.SkipUnless(File.Exists(path), "This host has no explicitly supported installed libzstd.");
        return CodexRolloutZstandard.Open(path);
    }

    [Fact]
    public async Task UpstreamFixturePreservesLogicalOffsetsAndOriginalBytes()
    {
        await using var codec = Open();
        var fixture = Convert.FromBase64String(Fixture);
        using var compressed = new MemoryStream(fixture, writable: false);
        using var snapshot = new MemoryStream();
        var identity = await codec.DecodeAsync(compressed, snapshot, 1024, 1024, Ct);
        var logical = Encoding.UTF8.GetBytes(Header + "\n" + Item + "\n");
        Assert.Equal(logical, snapshot.ToArray());
        Assert.Equal(logical.Length, identity.LogicalBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(logical)), identity.LogicalSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture)), identity.EncodedSha256);

        var entries = new List<CodexRolloutReadEntry>();
        await foreach (var entry in CodexRolloutReader.ReadAsync(snapshot, identity.LogicalSha256, cancellationToken: Ct))
            entries.Add(entry);
        Assert.Equal(2, entries.Count);
        Assert.Equal(Header.Length + 1, entries[0].NextCursor.ByteOffset);
        await foreach (var entry in CodexRolloutReader.ReadAsync(snapshot, identity.LogicalSha256,
            entries[0].NextCursor, cancellationToken: Ct))
            Assert.Equal(Item, entry.Record!.Envelope.GetRawText());
        Assert.Equal(fixture, compressed.ToArray());
    }

    [Fact]
    public async Task RoundTripAcrossBufferBoundariesPreservesLogicalDigest()
    {
        await using var codec = Open();
        var content = new byte[384 * 1024 + 7];
        new Random(21).NextBytes(content);
        using var original = new MemoryStream(content, writable: false);
        using var compressed = new MemoryStream();
        var encoded = await codec.EncodeAsync(original, compressed, content.Length, content.Length * 2L, Ct);
        compressed.Position = 0;
        using var snapshot = new MemoryStream();
        var decoded = await codec.DecodeAsync(compressed, snapshot, content.Length * 2L, content.Length, Ct);
        Assert.Equal(encoded, decoded);
        Assert.True(decoded.HasSameLogicalSource(encoded));
        Assert.Equal(content, snapshot.ToArray());
    }

    [Fact]
    public async Task ConcatenatedAndSkippableFramesDecodeWithoutLosingRecords()
    {
        await using var codec = Open();
        var frame = Convert.FromBase64String(Fixture);
        // Skippable frame magic 0x184D2A50, payload length 3.
        byte[] skippable = [0x50, 0x2a, 0x4d, 0x18, 3, 0, 0, 0, 1, 2, 3];
        using var source = new MemoryStream([.. frame, .. skippable, .. frame], writable: false);
        using var snapshot = new MemoryStream();
        await codec.DecodeAsync(source, snapshot, 2048, 2048, Ct);
        Assert.Equal(Header + "\n" + Item + "\n" + Header + "\n" + Item + "\n",
            Encoding.UTF8.GetString(snapshot.ToArray()));
    }

    [Fact]
    public async Task TruncationAndBoundsNeverCertifyPartialOutput()
    {
        await using var codec = Open();
        var bytes = Convert.FromBase64String(Fixture);
        using var broken = new MemoryStream(bytes[..^1], writable: false);
        using var partial = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.DecodeAsync(broken, partial, 1024, 1024, Ct));
        using var original = new MemoryStream(bytes, writable: false);
        using var small = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.DecodeAsync(original, small, 1024, 16, Ct));
        Assert.True(small.Length <= 16);
        original.Position = 0;
        using var noOutput = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.DecodeAsync(original, noOutput, bytes.Length - 1, 1024, Ct));
        Assert.Empty(noOutput.ToArray());
    }

    [Fact]
    public async Task CancellationAndNonemptyDestinationRefuseMigration()
    {
        await using var codec = Open();
        var bytes = Convert.FromBase64String(Fixture);
        using var source = new MemoryStream(bytes, writable: false);
        using var destination = new MemoryStream();
        destination.WriteByte(1);
        destination.Position = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => codec.DecodeAsync(source, destination, 1024, 1024, Ct));
        Assert.Equal(new byte[] { 1 }, destination.ToArray());
        using var fresh = new MemoryStream();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => codec.DecodeAsync(source, fresh, 1024, 1024, cancelled.Token));
        Assert.Empty(fresh.ToArray());
    }

    [Fact]
    public void LibraryPathMustBeExplicitAndAbsolute() =>
        Assert.Throws<ArgumentException>(() => CodexRolloutZstandard.Open("libzstd"));
}
