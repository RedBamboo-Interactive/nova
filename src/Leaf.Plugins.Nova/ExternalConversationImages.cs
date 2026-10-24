using System.Buffers.Binary;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// The image bounds external conversations share with the Sentinel and RedCompute's image input:
/// PNG, JPEG, still WebP or GIF, at most 5 MiB and 4096 px per side, at most four per turn. The
/// type comes from the bytes; dimensions come from the image header, without decoding pixels.
/// </summary>
internal static class ExternalConversationImages
{
    public const int MaxImages = UntrustedContentReviewRequest.MaxImages;
    public const int MaxImageBytes = UntrustedContentReviewRequest.MaxImageBytes;
    public const int MaxDimension = 4096;

    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        return null;
    }

    /// <summary>The real media type when the bytes are an image the model may be shown, else null.</summary>
    public static string? Displayable(byte[] bytes)
    {
        if (bytes.Length > MaxImageBytes) return null;
        var mediaType = Sniff(bytes);
        if (mediaType is null) return null;
        var size = mediaType switch
        {
            "image/png" => Png(bytes),
            "image/jpeg" => Jpeg(bytes),
            "image/webp" => Webp(bytes),
            _ => Gif(bytes),
        };
        return size is { Width: >= 1 and <= MaxDimension, Height: >= 1 and <= MaxDimension } ? mediaType : null;
    }

    private static (int Width, int Height)? Png(byte[] bytes)
        => bytes.Length >= 24 && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            ? ((int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16))),
                (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20))))
            : null;

    private static (int Width, int Height)? Gif(byte[] bytes)
        => bytes.Length >= 10
            ? (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)))
            : null;

    private static (int Width, int Height)? Jpeg(byte[] bytes)
    {
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xff) return null;
            var marker = bytes[offset + 1];
            if (marker == 0xff) { offset++; continue; }
            if (marker is 0xd9 or 0xda) return null; // no frame header before the scan
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2));
            if (length < 2 || offset + 2 + length > bytes.Length) return null;
            if (marker is >= 0xc0 and <= 0xcf and not 0xc4 and not 0xc8 and not 0xcc)
                return length >= 7
                    ? (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 7)),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5)))
                    : null;
            offset += 2 + length;
        }
        return null;
    }

    /// <summary>Still WebP only: an animated one would need a re-encode to show one frame.</summary>
    private static (int Width, int Height)? Webp(byte[] bytes)
    {
        if (bytes.Length < 30) return null;
        var chunk = bytes.AsSpan(12, 4);
        var data = bytes.AsSpan(20);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            if ((data[0] & 0x02) != 0) return null;
            for (var offset = 12; offset + 8 <= bytes.Length;)
            {
                var type = bytes.AsSpan(offset, 4);
                if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8)) return null;
                var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
                if (size > int.MaxValue - 9) return null;
                offset += 8 + (int)size + (int)(size % 2);
            }
            return ((data[4] | data[5] << 8 | data[6] << 16) + 1, (data[7] | data[8] << 8 | data[9] << 16) + 1);
        }
        if (chunk.SequenceEqual("VP8 "u8))
            return data[3] == 0x9d && data[4] == 0x01 && data[5] == 0x2a
                ? (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3fff)
                : null;
        if (chunk.SequenceEqual("VP8L"u8) && data[0] == 0x2f)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }
        return null;
    }
}
