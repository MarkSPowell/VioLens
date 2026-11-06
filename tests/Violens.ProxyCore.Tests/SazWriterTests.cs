using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

public class SazWriterTests
{
    [Fact]
    public void Write_ProducesAZipWithTheExpectedPerSessionEntries()
    {
        var request = new CapturedRequest(
            "GET",
            "/foo?x=1",
            "HTTP/1.1",
            [("Host", "example.test")],
            []);
        var response = new CapturedResponse(
            "HTTP/1.1",
            200,
            "OK",
            [("Content-Type", "text/plain")],
            Encoding.ASCII.GetBytes("hello"));
        var session = new Session(1, "example.test", DateTimeOffset.UtcNow, request, response);

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, [session]);

        zipStream.Position = 0;
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));

        var requestEntry = archive.GetEntry("raw/1_c.txt");
        Assert.NotNull(requestEntry);
        var requestText = ReadEntryText(requestEntry!);
        Assert.StartsWith("GET /foo?x=1 HTTP/1.1\r\n", requestText);
        Assert.Contains("Host: example.test\r\n", requestText);

        var responseEntry = archive.GetEntry("raw/1_s.txt");
        Assert.NotNull(responseEntry);
        var responseText = ReadEntryText(responseEntry!);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", responseText);
        Assert.EndsWith("hello", responseText);

        var metadataEntry = archive.GetEntry("raw/1_m.xml");
        Assert.NotNull(metadataEntry);
        var metadataDocument = XDocument.Load(metadataEntry!.Open());
        Assert.Equal("Session", metadataDocument.Root!.Name.LocalName);
        Assert.NotNull(metadataDocument.Root.Element("SessionTimers"));
        Assert.NotNull(metadataDocument.Root.Element("SessionFlags"));
    }

    [Fact]
    public void Write_RebuildsContentLengthAndDropsTransferEncodingForAChunkedBody()
    {
        // The captured response here stands in for what Http1MessageReader
        // hands back after decoding a chunked body: the original
        // Transfer-Encoding header is still present (it's just relayed as
        // one of the captured headers) but there's no Content-Length at
        // all, since the wire never carried one for a chunked message.
        var request = new CapturedRequest("GET", "/", "HTTP/1.1", [("Host", "example.test")], []);
        var response = new CapturedResponse(
            "HTTP/1.1",
            200,
            "OK",
            [("Transfer-Encoding", "chunked")],
            Encoding.ASCII.GetBytes("hello world"));
        var session = new Session(1, "example.test", DateTimeOffset.UtcNow, request, response);

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, [session]);

        zipStream.Position = 0;
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var responseText = ReadEntryText(archive.GetEntry("raw/1_s.txt")!);

        Assert.DoesNotContain("Transfer-Encoding", responseText);
        Assert.Contains("Content-Length: 11\r\n", responseText);
        Assert.EndsWith("hello world", responseText);
    }

    [Fact]
    public void Write_ListsTheSessionsInAnIndexPageWithEverythingEncoded()
    {
        var request = new CapturedRequest("GET", "/search?q=<script>alert(1)</script>", "HTTP/1.1", [("Host", "example.test")], []);
        var response = new CapturedResponse("HTTP/1.1", 404, "Not Found", [("Content-Type", "text/html")], Encoding.ASCII.GetBytes("missing"));

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, [new Session(7, "example.test", DateTimeOffset.UtcNow, request, response)]);
        zipStream.Position = 0;
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var index = ReadEntryText(archive.GetEntry("_index.htm")!);
        Assert.Contains("""<a href="raw/7_c.txt">C</a> <a href="raw/7_s.txt">S</a> <a href="raw/7_m.xml">M</a>""", index);
        Assert.Contains("<td>404</td><td>GET</td><td>example.test</td>", index);
        Assert.Contains("q=&lt;script&gt;alert(1)&lt;/script&gt;", index);
        Assert.DoesNotContain("<script>", index);
        Assert.Contains("""Extension="htm" ContentType="text/html""", ReadEntryText(archive.GetEntry("[Content_Types].xml")!));
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }
}
