using System.IO.Compression;
using System.Text;
using Violens.ProxyCore.Http;
using Xunit;

namespace Violens.ProxyCore.Tests;

public class ContentDecoderTests
{
    [Fact]
    public void DecodesAGzipBody()
    {
        var body = Gzip(Encoding.UTF8.GetBytes("hello"));

        Assert.True(ContentDecoder.TryDecode(body, "gzip", out var decoded, out var error));
        Assert.Null(error);
        Assert.Equal("hello", Encoding.UTF8.GetString(decoded));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    public void StopsDecodingABodyThatExpandsPastTheLimit(string coding)
    {
        // 8 MB of zeros compresses to a few kilobytes: a small body that
        // would decode to far more than it's allowed to.
        var original = new byte[8 * 1024 * 1024];
        var body = Compress(original, coding);

        var ok = ContentDecoder.TryDecode(body, coding, maxDecodedBytes: 1024 * 1024, out var decoded, out var error);

        Assert.False(ok);
        Assert.Same(body, decoded);
        Assert.Contains("larger than", error);
    }

    [Fact]
    public void ABodyWithinTheLimitStillDecodes()
    {
        var original = new byte[512 * 1024];
        var body = Gzip(original);

        Assert.True(ContentDecoder.TryDecode(body, "gzip", maxDecodedBytes: 1024 * 1024, out var decoded, out _));
        Assert.Equal(original.Length, decoded.Length);
    }

    private static byte[] Compress(byte[] data, string coding) => coding switch
    {
        "gzip" => Gzip(data),
        "br" => Write(data, s => new BrotliStream(s, CompressionLevel.Fastest, leaveOpen: true)),
        _ => Write(data, s => new ZLibStream(s, CompressionLevel.Fastest, leaveOpen: true)),
    };

    private static byte[] Gzip(byte[] data) => Write(data, s => new GZipStream(s, CompressionLevel.Fastest, leaveOpen: true));

    private static byte[] Write(byte[] data, Func<Stream, Stream> compressor)
    {
        using var buffer = new MemoryStream();
        using (var stream = compressor(buffer))
        {
            stream.Write(data);
        }

        return buffer.ToArray();
    }

    [Theory]
    [InlineData("text/html; charset=utf-7")]
    [InlineData("text/html; charset=no-such-charset")]
    public void DecodeTextFallsBackToUtf8ForACharsetItCantUse(string contentType) =>
        Assert.Equal("héllo", ContentDecoder.DecodeText(Encoding.UTF8.GetBytes("héllo"), contentType));
}
