using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Violens.ProxyCore.Http;

namespace Violens.Extensibility.Inspection;

/// <summary>One kind of bloat found in an image, and how many bytes of the file it takes.</summary>
public sealed record ImageBloatItem(string Kind, long Bytes);

/// <summary>
/// What <see cref="ImageBloatAnalyzer"/> found in one image: its format, its
/// size, and the bytes that don't affect how it looks.
/// </summary>
public sealed record ImageBloatReport(string Format, long TotalBytes, IReadOnlyList<ImageBloatItem> Items)
{
    /// <summary>Bloat at or above this share of the file, and at least <see cref="HeavyMinimumBytes"/>, is highlighted in the session list.</summary>
    public const double HeavyShare = 0.25;

    /// <summary>The smallest amount of bloat worth highlighting.</summary>
    public const long HeavyMinimumBytes = 1024;

    public long BloatBytes => Items.Sum(item => item.Bytes);

    /// <summary>Bloat as a share of the file, 0 to 1.</summary>
    public double Share => TotalBytes == 0 ? 0 : (double)BloatBytes / TotalBytes;

    public bool IsHeavy => BloatBytes >= HeavyMinimumBytes && Share >= HeavyShare;

    /// <summary>The Image Bloat column: "bloat / total bytes (percent)".</summary>
    public string ColumnText =>
        string.Create(CultureInfo.InvariantCulture, $"{BloatBytes:N0} / {TotalBytes:N0} bytes ({Share * 100:0}%)");

    /// <summary>For the ImageView tab: what the bloat is made of, largest first.</summary>
    public string Breakdown
    {
        get
        {
            if (Items.Count == 0)
            {
                return "No bloat: everything in the file affects how the image looks.";
            }

            var parts = Items
                .OrderByDescending(item => item.Bytes)
                .Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.Kind} {item.Bytes:N0}"));
            return $"Bloat: {ColumnText}. {string.Join(", ", parts)} bytes.";
        }
    }
}

/// <summary>
/// Finds "image bloat": bytes in an image file that don't change how it
/// looks -- metadata such as EXIF (camera details, often with a thumbnail),
/// XMP, editing-program data, comments and text, or data after the end of the
/// image. Removing them makes the file smaller with no visible change.
/// Inspired by Eric Lawrence's ImageBloat extension for Fiddler; the rules
/// here are VioLens's own, taken from the file format specifications.
///
/// What counts as needed, per format:
/// <list type="bullet">
/// <item>PNG: the header, palette, image data and end chunks; transparency;
/// colour information (gamma, chromaticities, sRGB, ICC profile,
/// significant bits, cICP and HDR metadata); and animation (APNG) chunks.
/// Every other chunk -- text, timestamps, EXIF, histograms, physical pixel
/// size, background colour, and program-specific chunks -- is bloat.</item>
/// <item>JPEG: the segments needed to decode (frame, Huffman and
/// quantisation tables, scans, restart interval), JFIF and Adobe headers,
/// and ICC profiles. EXIF, XMP, Photoshop data, comments, other application
/// segments and anything after the end of the image are bloat. EXIF can hold
/// an orientation that browsers honour, so the breakdown says so.</item>
/// <item>GIF: everything except comments, non-animation application
/// extensions and data after the end of the file.</item>
/// <item>WebP: everything except EXIF, XMP and unknown chunks.</item>
/// </list>
/// Byte counts include each block's own framing (headers, lengths,
/// checksums). A file that can't be walked cleanly gives no report rather
/// than a wrong one.
/// </summary>
public static class ImageBloatAnalyzer
{
    private static readonly HashSet<string> NeededPngChunks = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND",
        "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "cICP", "mDCV", "mDCv", "cLLI", "cLLi",
        "acTL", "fcTL", "fdAT",
    };

    /// <summary>Analyses a response's body (decoding any Content-Encoding first), or returns null when it isn't a recognised image.</summary>
    public static ImageBloatReport? AnalyzeResponse(CapturedResponse response)
    {
        // Worked out for every response, unasked, so a compressed body is
        // only decoded when it might be an image: one that says it's
        // something else isn't decompressed for nothing.
        var contentType = ContentDecoder.MediaType(ContentDecoder.FindHeader(response.Headers, "Content-Type"));
        if (contentType.Length > 0 && !contentType.StartsWith("image/", StringComparison.Ordinal) &&
            ContentDecoder.FindHeader(response.Headers, "Content-Encoding") is not null)
        {
            return null;
        }

        if (response.Body.Length == 0 ||
            !ContentDecoder.TryDecode(response.Body, response.Headers, out var bytes, out _))
        {
            return null;
        }

        return Analyze(bytes);
    }

    /// <summary>Analyses an image file, or returns null when it isn't a PNG, JPEG, GIF or WebP that can be read.</summary>
    public static ImageBloatReport? Analyze(byte[] bytes)
    {
        try
        {
            return Inspectors.ImageInspector.DetectFormat(bytes) switch
            {
                "PNG" => AnalyzePng(bytes),
                "JPEG" => AnalyzeJpeg(bytes),
                "GIF" => AnalyzeGif(bytes),
                "WebP" => AnalyzeWebP(bytes),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static ImageBloatReport? AnalyzePng(byte[] bytes)
    {
        var items = new Items();
        var position = 8L;
        var sawEnd = false;
        while (position + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)position, 4));
            var type = Encoding.ASCII.GetString(bytes, (int)position + 4, 4);
            var chunkBytes = 12L + length;
            if (position + chunkBytes > bytes.Length)
            {
                return null;
            }

            if (!NeededPngChunks.Contains(type))
            {
                items.Add(PngChunkKind(type), chunkBytes);
            }

            position += chunkBytes;
            if (type == "IEND")
            {
                sawEnd = true;
                break;
            }
        }

        if (!sawEnd)
        {
            return null;
        }

        items.Add("Data after end of image", bytes.Length - position);
        return new ImageBloatReport("PNG", bytes.Length, items.ToList());
    }

    private static string PngChunkKind(string type) => type switch
    {
        "tEXt" or "zTXt" or "iTXt" => "Text",
        "eXIf" => "EXIF",
        "tIME" => "Timestamp",
        _ => $"{type} chunk",
    };

    private static ImageBloatReport? AnalyzeJpeg(byte[] bytes)
    {
        var items = new Items();
        var sawExif = false;
        var position = 2;
        while (position + 2 <= bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                return null;
            }

            var marker = bytes[position + 1];
            if (marker == 0xFF)
            {
                position++; // fill byte
                continue;
            }

            if (marker == 0xD9)
            {
                // End of image; anything after it is extra.
                position += 2;
                items.Add("Data after end of image", bytes.Length - position);
                return new ImageBloatReport("JPEG", bytes.Length, sawExif ? AddOrientationNote(items.ToList()) : items.ToList());
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (position + 4 > bytes.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2, 2));
            var segmentBytes = 2 + length;
            if (length < 2 || position + segmentBytes > bytes.Length)
            {
                return null;
            }

            var kind = JpegSegmentBloatKind(marker, bytes.AsSpan(position + 4, length - 2));
            if (kind is not null)
            {
                items.Add(kind, segmentBytes);
                sawExif |= kind == "EXIF";
            }

            position += segmentBytes;
            if (marker == 0xDA)
            {
                // Entropy-coded scan data follows: skip to the next marker
                // (0xFF not followed by 0x00 stuffing or a restart marker).
                while (position + 1 < bytes.Length &&
                       !(bytes[position] == 0xFF && bytes[position + 1] != 0x00 && bytes[position + 1] is not (>= 0xD0 and <= 0xD7)))
                {
                    position++;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<ImageBloatItem> AddOrientationNote(List<ImageBloatItem> items) =>
        items.Select(item => item.Kind == "EXIF" ? item with { Kind = "EXIF (keep its orientation tag if the image relies on it)" } : item).ToList();

    /// <summary>The bloat kind for a JPEG segment, or null when it's needed.</summary>
    private static string? JpegSegmentBloatKind(byte marker, ReadOnlySpan<byte> payload)
    {
        switch (marker)
        {
            case 0xFE:
                return "Comment";
            case 0xE0 when StartsWith(payload, "JFIF\0"u8) || StartsWith(payload, "JFXX\0"u8):
                return StartsWith(payload, "JFXX\0"u8) ? "JFIF thumbnail" : null;
            case 0xE1 when StartsWith(payload, "Exif\0"u8):
                return "EXIF";
            case 0xE1 when StartsWith(payload, "http://ns.adobe.com/xap/1.0/"u8) || StartsWith(payload, "http://ns.adobe.com/xmp/extension/"u8):
                return "XMP";
            case 0xE2 when StartsWith(payload, "ICC_PROFILE\0"u8):
                return null;
            case 0xE2 when StartsWith(payload, "MPF\0"u8):
                return "Multi-picture index";
            case 0xED:
                return "Photoshop data";
            case 0xEE when StartsWith(payload, "Adobe"u8):
                return null;
            case >= 0xE0 and <= 0xEF:
                return $"APP{marker - 0xE0} data";
            default:
                return null;
        }
    }

    private static ImageBloatReport? AnalyzeGif(byte[] bytes)
    {
        var items = new Items();
        var position = 13;
        var flags = bytes[10];
        if ((flags & 0x80) != 0)
        {
            position += 3 * (1 << ((flags & 0x07) + 1));
        }

        while (position < bytes.Length)
        {
            switch (bytes[position])
            {
                case 0x3B:
                    position++;
                    items.Add("Data after end of image", bytes.Length - position);
                    return new ImageBloatReport("GIF", bytes.Length, items.ToList());

                case 0x21:
                {
                    var label = bytes[position + 1];
                    var start = position;
                    var applicationId = label == 0xFF && bytes[position + 2] == 11
                        ? Encoding.ASCII.GetString(bytes, position + 3, 11)
                        : null;
                    position = SkipSubBlocks(bytes, position + 2);
                    var kind = label switch
                    {
                        0xFE => "Comment",
                        0xFF when applicationId is "NETSCAPE2.0" or "ANIMEXTS1.0" => null,
                        0xFF when applicationId == "XMP DataXMP" => "XMP",
                        0xFF => "Application data",
                        _ => null,
                    };
                    if (kind is not null)
                    {
                        items.Add(kind, position - start);
                    }

                    break;
                }

                case 0x2C:
                {
                    var imageFlags = bytes[position + 9];
                    position += 10;
                    if ((imageFlags & 0x80) != 0)
                    {
                        position += 3 * (1 << ((imageFlags & 0x07) + 1));
                    }

                    position = SkipSubBlocks(bytes, position + 1); // after the LZW minimum code size
                    break;
                }

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>Skips GIF data sub-blocks starting at <paramref name="position"/>; returns the position after the zero-length terminator.</summary>
    private static int SkipSubBlocks(byte[] bytes, int position)
    {
        while (true)
        {
            var size = bytes[position];
            position += 1 + size;
            if (size == 0)
            {
                return position;
            }
        }
    }

    private static ImageBloatReport? AnalyzeWebP(byte[] bytes)
    {
        var items = new Items();
        var riffEnd = Math.Min(bytes.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        var position = 12L;
        while (position + 8 <= riffEnd)
        {
            var fourCc = Encoding.ASCII.GetString(bytes, (int)position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)position + 4, 4));
            var chunkBytes = 8L + size + (size & 1);
            if (position + 8 + size > riffEnd)
            {
                return null;
            }

            var kind = fourCc switch
            {
                "VP8 " or "VP8L" or "VP8X" or "ALPH" or "ANIM" or "ANMF" or "ICCP" => null,
                "EXIF" => "EXIF",
                "XMP " => "XMP",
                _ => $"{fourCc.Trim()} chunk",
            };
            if (kind is not null)
            {
                items.Add(kind, Math.Min(chunkBytes, riffEnd - position));
            }

            position += chunkBytes;
        }

        items.Add("Data after end of image", bytes.Length - riffEnd);
        return new ImageBloatReport("WebP", bytes.Length, items.ToList());
    }

    private static bool StartsWith(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> prefix) => payload.StartsWith(prefix);

    /// <summary>Bloat totals by kind, in the order first seen.</summary>
    private sealed class Items
    {
        private readonly List<ImageBloatItem> _items = [];

        public void Add(string kind, long bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            var index = _items.FindIndex(item => item.Kind == kind);
            if (index < 0)
            {
                _items.Add(new ImageBloatItem(kind, bytes));
            }
            else
            {
                _items[index] = _items[index] with { Bytes = _items[index].Bytes + bytes };
            }
        }

        public List<ImageBloatItem> ToList() => [.. _items];
    }
}
