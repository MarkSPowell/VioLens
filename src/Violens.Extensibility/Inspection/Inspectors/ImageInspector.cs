using System.Buffers.Binary;
using Violens.ProxyCore.Http;

namespace Violens.Extensibility.Inspection.Inspectors;

/// <summary>
/// Fiddler Classic's "ImageView": shows an image body as a picture, with its
/// format, size in bytes and pixel dimensions.
///
/// Offered when the Content-Type is an image type, or the body starts with
/// a known image signature (PNG, JPEG, GIF, WebP, BMP, ICO) whatever the
/// Content-Type says. SVG is text, so it's left to the Raw tab. For PNG,
/// JPEG, GIF and WebP the summary also lists any image bloat (see
/// <see cref="ImageBloatAnalyzer"/>). Drawing is
/// the host's job (<see cref="ImageContent"/>); the dimensions come from the
/// file header here, so they show even when the host can't draw the format.
/// </summary>
public sealed class ImageInspector : IInspector
{
    public string Id => "violens.image";

    public string DisplayName => "ImageView";

    public int SortOrder => 16;

    public bool CanInspect(InspectorContext context)
    {
        if (context.Body.Length == 0)
        {
            return false;
        }

        var mediaType = ContentDecoder.MediaType(context.FindHeader("Content-Type"));
        if (mediaType == "image/svg+xml")
        {
            return false;
        }

        return mediaType.StartsWith("image/", StringComparison.Ordinal) || DetectFormat(context.Body) is not null;
    }

    public InspectorContent Inspect(InspectorContext context)
    {
        if (!ContentDecoder.TryDecode(context.Body, context.FindHeader("Content-Encoding"), out var bytes, out var error))
        {
            return new ErrorContent(error!);
        }

        var mediaType = ContentDecoder.MediaType(context.FindHeader("Content-Type"));
        var format = DetectFormat(bytes);
        var parts = new List<string> { format ?? (mediaType.Length > 0 ? mediaType : "Unknown format") };
        if (TryGetDimensions(bytes, format, out var width, out var height))
        {
            parts.Add($"{width:N0} × {height:N0} pixels");
        }

        parts.Add($"{bytes.Length:N0} bytes");
        if (format is null)
        {
            parts.Add("not a recognized image format, so it may not display");
        }

        var summary = string.Join(", ", parts);
        if (ImageBloatAnalyzer.Analyze(bytes) is { } bloat)
        {
            summary += "\n" + bloat.Breakdown;
        }

        return new ImageContent(bytes, mediaType, summary);
    }

    /// <summary>The image format named by the body's leading bytes, or null.</summary>
    public static string? DetectFormat(byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "PNG";
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return "JPEG";
        }

        if (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8')
        {
            return "GIF";
        }

        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' &&
            b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
        {
            return "WebP";
        }

        if (b.Length >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M')
        {
            return "BMP";
        }

        if (b.Length >= 6 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0)
        {
            return "ICO";
        }

        return null;
    }

    /// <summary>Pixel dimensions from the image's header, for the formats whose header is simple to read.</summary>
    public static bool TryGetDimensions(byte[] bytes, string? format, out int width, out int height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> b = bytes;
        switch (format)
        {
            case "PNG" when b.Length >= 24:
                width = BinaryPrimitives.ReadInt32BigEndian(b[16..]);
                height = BinaryPrimitives.ReadInt32BigEndian(b[20..]);
                return true;

            case "GIF" when b.Length >= 10:
                width = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
                height = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
                return true;

            case "BMP" when b.Length >= 26:
                width = BinaryPrimitives.ReadInt32LittleEndian(b[18..]);
                height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b[22..]));
                return true;

            case "ICO" when b.Length >= 8:
                // First icon in the directory; 0 means 256.
                width = b[6] == 0 ? 256 : b[6];
                height = b[7] == 0 ? 256 : b[7];
                return true;

            case "JPEG":
                return TryGetJpegDimensions(b, out width, out height);

            case "WebP" when b.Length >= 30:
                return TryGetWebPDimensions(b, out width, out height);

            default:
                return false;
        }
    }

    private static bool TryGetJpegDimensions(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = 0;
        height = 0;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                return false;
            }

            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            // SOF0..SOF15, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]);
                return true;
            }

            i += 2 + length;
        }

        return false;
    }

    private static bool TryGetWebPDimensions(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = 0;
        height = 0;
        var chunk = System.Text.Encoding.ASCII.GetString(b.Slice(12, 4));
        switch (chunk)
        {
            case "VP8X":
                width = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
                height = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
                return true;
            case "VP8 ":
                width = BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF;
                return true;
            case "VP8L":
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
                return true;
            default:
                return false;
        }
    }
}
