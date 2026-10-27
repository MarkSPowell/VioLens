using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary>HTTP/2 sessions in the session list's Version column and in SAZ and HAR files.</summary>
public sealed class Http2SessionFileTests
{
    [Theory]
    [InlineData("HTTP/2", "HTTP/2", "HTTP/2")]
    [InlineData("HTTP/1.1", "HTTP/1.1", "HTTP/1.1")]
    [InlineData("HTTP/2", "HTTP/1.1", "HTTP/2 → HTTP/1.1")]
    [InlineData("HTTP/2", null, "HTTP/2")]
    public void DescribeShowsTheServersVersionOnlyWhenItDiffers(string client, string? server, string expected) =>
        Assert.Equal(expected, HttpVersions.Describe(client, server));

    [Fact]
    public async Task SazFilesSayHttp20AndReadBackAsHttp2()
    {
        using var zip = new MemoryStream();
        SazWriter.Write(zip, [Http2Session()]);

        zip.Position = 0;
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.StartsWith("POST /items HTTP/2.0\r\n", ReadEntry(archive, "raw/1_c.txt"));
            Assert.StartsWith("HTTP/2.0 200\r\n", ReadEntry(archive, "raw/1_s.txt"));
        }

        zip.Position = 0;
        var store = new SessionStore();
        await SazReader.ImportAsync(zip, store);

        var imported = Assert.Single(store.Snapshot());
        Assert.Equal("HTTP/2", imported.Request.HttpVersion);
        Assert.Equal("HTTP/2", imported.Response.HttpVersion);
        Assert.Equal("ok", Encoding.ASCII.GetString(imported.Response.Body));
    }

    [Fact]
    public async Task HarFilesSayHttp20AsChromeDoesAndReadBackAsHttp2()
    {
        using var buffer = new MemoryStream();
        HarWriter.Write(buffer, [Http2Session()]);

        using (var har = JsonDocument.Parse(buffer.ToArray()))
        {
            var entry = har.RootElement.GetProperty("log").GetProperty("entries")[0];
            Assert.Equal("http/2.0", entry.GetProperty("request").GetProperty("httpVersion").GetString());
            Assert.Equal("http/2.0", entry.GetProperty("response").GetProperty("httpVersion").GetString());
        }

        buffer.Position = 0;
        var store = new SessionStore();
        await HarReader.ImportAsync(buffer, store);

        var imported = Assert.Single(store.Snapshot());
        Assert.Equal("HTTP/2", imported.Request.HttpVersion);
        Assert.Equal("HTTP/2", imported.Response.HttpVersion);
    }

    private static Session Http2Session() =>
        new(
            1,
            "api.example.test",
            DateTimeOffset.UtcNow,
            new CapturedRequest("POST", "/items", "HTTP/2", [("host", "api.example.test"), ("content-type", "text/plain")], Encoding.ASCII.GetBytes("x")),
            new CapturedResponse("HTTP/2", 200, string.Empty, [("content-type", "text/plain")], Encoding.ASCII.GetBytes("ok")));

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.ASCII);
        return reader.ReadToEnd();
    }
}
