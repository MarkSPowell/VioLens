using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Violens.Extensibility.Inspection;
using Violens.Extensibility.Inspection.Inspectors;
using Violens.ProxyCore;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.Extensibility.Tests;

/// <summary>Image bloat: bytes in an image file that don't change how it looks.</summary>
public class ImageBloatTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] PngChunk(string type, int dataLength)
    {
        var chunk = new byte[12 + dataLength];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)dataLength);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        return chunk; // data and CRC left as zeros; the analyser doesn't check CRCs
    }

    private static byte[] Png(int textLength, int trailing = 0) =>
    [
        .. PngSignature,
        .. PngChunk("IHDR", 13),
        .. PngChunk("tEXt", textLength),
        .. PngChunk("IDAT", 5),
        .. PngChunk("IEND", 0),
        .. new byte[trailing],
    ];

    [Fact]
    public void PngTextChunksAndTrailingDataAreBloat()
    {
        var png = Png(textLength: 10, trailing: 5);

        var report = ImageBloatAnalyzer.Analyze(png);

        Assert.NotNull(report);
        Assert.Equal("PNG", report!.Format);
        Assert.Equal(png.Length, report.TotalBytes);
        Assert.Equal(
            new[] { ("Text", 22L), ("Data after end of image", 5L) },
            report.Items.Select(i => (i.Kind, i.Bytes)).ToArray());
        Assert.Equal(27, report.BloatBytes);
        Assert.Equal($"27 / {png.Length} bytes (30%)", report.ColumnText);
        Assert.False(report.IsHeavy);
    }

    [Fact]
    public void ALargeShareOfALargeFileIsHeavy()
    {
        var report = ImageBloatAnalyzer.Analyze(Png(textLength: 2000));

        Assert.True(report!.IsHeavy);
        Assert.StartsWith("Bloat: 2,012 / ", report.Breakdown);
        Assert.Contains("Text 2,012", report.Breakdown);
    }

    [Fact]
    public void APngWithNothingExtraHasNoBloat()
    {
        byte[] png = [.. PngSignature, .. PngChunk("IHDR", 13), .. PngChunk("sRGB", 1), .. PngChunk("IDAT", 5), .. PngChunk("IEND", 0)];

        var report = ImageBloatAnalyzer.Analyze(png);

        Assert.Equal(0, report!.BloatBytes);
        Assert.StartsWith("No bloat", report.Breakdown);
    }

    [Fact]
    public void JpegMetadataCommentsAndTrailingDataAreBloat()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        // APP0 JFIF: needed.
        jpeg.AddRange([0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        // APP1 EXIF, 28-byte length.
        jpeg.AddRange([0xFF, 0xE1, 0x00, 0x1C, .. "Exif\0\0"u8.ToArray(), .. new byte[20]]);
        // Comment.
        jpeg.AddRange([0xFF, 0xFE, 0x00, 0x07, .. "hello"u8.ToArray()]);
        // Start of scan (8-byte header), then scan data with a stuffed 0xFF.
        jpeg.AddRange([0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0x12, 0xFF, 0x00, 0x34]);
        // End of image, then three extra bytes.
        jpeg.AddRange([0xFF, 0xD9, 9, 9, 9]);

        var report = ImageBloatAnalyzer.Analyze([.. jpeg]);

        Assert.NotNull(report);
        Assert.Equal("JPEG", report!.Format);
        Assert.Equal(3, report.Items.Count);
        Assert.StartsWith("EXIF", report.Items[0].Kind);
        Assert.Equal(30, report.Items[0].Bytes);
        Assert.Equal(("Comment", 9L), (report.Items[1].Kind, report.Items[1].Bytes));
        Assert.Equal(("Data after end of image", 3L), (report.Items[2].Kind, report.Items[2].Bytes));
    }

    [Fact]
    public void AJpegEndingRightAtItsEndMarkerHasNoBloat()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0x12, 0x34, 0xFF, 0xD9];

        Assert.Equal(0, ImageBloatAnalyzer.Analyze(jpeg)!.BloatBytes);
    }

    [Fact]
    public void GifCommentsAreBloatButTheAnimationLoopIsNot()
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([1, 0, 1, 0, 0x00, 0, 0]); // 1x1, no global colour table
        gif.AddRange([0x21, 0xFE, 3, (byte)'a', (byte)'b', (byte)'c', 0]); // comment
        gif.AddRange([0x21, 0xFF, 11, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0]); // loop
        gif.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0x00, 2, 2, 0x4C, 0x01, 0]); // image
        gif.Add(0x3B);

        var report = ImageBloatAnalyzer.Analyze([.. gif]);

        var item = Assert.Single(report!.Items);
        Assert.Equal(("Comment", 7L), (item.Kind, item.Bytes));
    }

    [Fact]
    public void WebPExifIsBloat()
    {
        var webp = new List<byte>();
        webp.AddRange("RIFF"u8.ToArray());
        webp.AddRange([30, 0, 0, 0]);
        webp.AddRange("WEBP"u8.ToArray());
        webp.AddRange([.. "VP8L"u8.ToArray(), 5, 0, 0, 0, 0x2F, 0, 0, 0, 0, 0]); // 5 bytes + 1 pad
        webp.AddRange([.. "EXIF"u8.ToArray(), 4, 0, 0, 0, 1, 2, 3, 4]);

        var report = ImageBloatAnalyzer.Analyze([.. webp]);

        Assert.Equal("WebP", report!.Format);
        var item = Assert.Single(report.Items);
        Assert.Equal(("EXIF", 12L), (item.Kind, item.Bytes));
    }

    [Fact]
    public void NonImagesAndBrokenFilesGiveNoReport()
    {
        Assert.Null(ImageBloatAnalyzer.Analyze(Encoding.UTF8.GetBytes("<html></html>")));

        var truncated = Png(textLength: 10)[..30];
        Assert.Null(ImageBloatAnalyzer.Analyze(truncated));
    }

    [Fact]
    public void ResponsesAreDecodedFirst()
    {
        var png = Png(textLength: 10);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(png);
        }

        var response = new CapturedResponse("HTTP/1.1", 200, "OK", [("Content-Type", "image/png"), ("Content-Encoding", "gzip")], buffer.ToArray());

        Assert.Equal(22, ImageBloatAnalyzer.AnalyzeResponse(response)!.BloatBytes);
    }

    [Fact]
    public void AnEncodedResponseThatIsntAnImageIsntDecoded()
    {
        // Worked out for every response, unasked (the Image Bloat column), so
        // it mustn't decompress a body that says it isn't an image: that's
        // where a decompression bomb would go off with no click at all.
        var png = Png(textLength: 10);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(png);
        }

        var response = new CapturedResponse("HTTP/1.1", 200, "OK", [("Content-Type", "text/html"), ("Content-Encoding", "gzip")], buffer.ToArray());

        Assert.Null(ImageBloatAnalyzer.AnalyzeResponse(response));
    }

    [Fact]
    public void TheImageViewTabListsTheBloat()
    {
        var response = new CapturedResponse("HTTP/1.1", 200, "OK", [("Content-Type", "image/png")], Png(textLength: 10));
        var session = new Session(1, "example.test", DateTimeOffset.UnixEpoch, new CapturedRequest("GET", "/a.png", "HTTP/1.1", [], []), response, SessionState.Done);

        var image = Assert.IsType<ImageContent>(new ImageInspector().Inspect(new InspectorContext(session, InspectorSide.Response)));

        Assert.Contains("Bloat: 22 /", image.Summary);
        Assert.Contains("Text 22", image.Summary);
    }
}
