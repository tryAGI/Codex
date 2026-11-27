using System.Buffers;
using System.Buffers.Binary;

namespace tryAGI.Codex;

// Bounded, header-only counterpart to Rust's original-detail image dimension estimate.
// Never fetch URLs, decompress pixels, or allocate based on untrusted image dimensions.
internal static class CodexRetainedImageDimensions
{
    private const int MaximumHeaderBytes = 192 * 1024;

    internal static int? EstimateOriginalTokens(string? url)
    {
        if (url is null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = url.IndexOf(',');
        if (comma is < 6 or > 4096) return null;
        var metadata = url[5..comma].Split(';');
        if (!metadata[0].StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            !metadata.Skip(1).Any(part => part.Equals("base64", StringComparison.OrdinalIgnoreCase)))
            return null;
        var payload = url.AsSpan(comma + 1);
        if (payload.Length < 4 || payload.Length % 4 != 0) return null;

        // Most formats have fixed-position headers. Only JPEG may require scanning
        // variable-length metadata segments before the frame header.
        Span<byte> first = stackalloc byte[96];
        if (!Convert.TryFromBase64Chars(payload[..Math.Min(payload.Length, 128)], first, out var count))
            return null;
        var header = first[..count];
        if (Dimensions(header) is { } dimensions) return Patches(dimensions);
        if (header.Length < 2 || header[0] != 0xff || header[1] != 0xd8 ||
            payload.Length <= 128) return null;

        var chars = Math.Min(payload.Length, MaximumHeaderBytes / 3 * 4);
        var buffer = ArrayPool<byte>.Shared.Rent(chars / 4 * 3);
        try
        {
            return Convert.TryFromBase64Chars(payload[..chars], buffer, out count) &&
                Jpeg(buffer.AsSpan(0, count)) is { } jpeg ? Patches(jpeg) : null;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static int? Patches((uint Width, uint Height) dimensions)
    {
        if (dimensions.Width == 0 || dimensions.Height == 0) return null;
        return (int)Math.Min(10_000L, ((dimensions.Width + 31L) / 32) * ((dimensions.Height + 31L) / 32));
    }

    private static (uint Width, uint Height)? Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]));
        if (bytes.Length >= 10 && (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)))
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]));
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8))
        {
            var dib = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..18]);
            if (dib == 12)
                return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..20]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..22]));
            if (dib >= 40)
            {
                var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..22]);
                var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..26]);
                if (width > 0 && height != int.MinValue && height != 0)
                    return ((uint)width, (uint)Math.Abs(height));
            }
            return null;
        }
        if (bytes.Length >= 20 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            var kind = bytes[12..16];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);
            if (kind.SequenceEqual("VP8X"u8) && size >= 10 && bytes.Length >= 30)
                return (1U + UInt24(bytes[24..27]), 1U + UInt24(bytes[27..30]));
            if (kind.SequenceEqual("VP8L"u8) && size >= 5 && bytes.Length >= 25 && bytes[20] == 0x2f)
            {
                var packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..25]);
                if ((packed >> 29) == 0)
                    return (1 + (packed & 0x3fff), 1 + ((packed >> 14) & 0x3fff));
            }
            if (kind.SequenceEqual("VP8 "u8) && size >= 10 && bytes.Length >= 30 &&
                (bytes[20] & 1) == 0 && bytes[23..26].SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                return ((uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..28]) & 0x3fff),
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..30]) & 0x3fff));
            return null;
        }
        return Jpeg(bytes);
    }

    private static uint UInt24(ReadOnlySpan<byte> bytes)
        => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);

    private static (uint Width, uint Height)? Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0xff || bytes[1] != 0xd8) return null;
        var index = 2;
        while (index < bytes.Length)
        {
            if (bytes[index++] != 0xff) return null;
            while (index < bytes.Length && bytes[index] == 0xff) index++;
            if (index >= bytes.Length) return null;
            var marker = bytes[index++];
            if (marker is 0xda or 0xd9 or 0x00 or 0xd8) return null;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (bytes.Length - index < 2) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[index..]);
            if (length < 2 || length > bytes.Length - index) return null;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) return null;
                var components = bytes[index + 7];
                if (components == 0 || length != 8 + 3 * components) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 3)..]));
            }
            index += length;
        }
        return null;
    }
}
