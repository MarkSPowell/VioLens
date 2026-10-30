using System.IO.Compression;
using System.Text;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

public class SazReaderTests
{
    [Fact]
    public async Task ImportAsync_RoundTripsWhatSazWriterWrote()
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
        var written = new Session(1, "example.test", DateTimeOffset.UtcNow, request, response);

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, [written]);
        zipStream.Position = 0;

        var store = new SessionStore();
        var result = await SazReader.ImportAsync(zipStream, store);

        Assert.Equal(1, result.Imported);
        Assert.Empty(result.Skipped);

        var imported = Assert.Single(store.Snapshot());
        Assert.Equal("example.test", imported.Host);
        Assert.Equal("GET", imported.Request.Method);
        Assert.Equal("/foo?x=1", imported.Request.Target);
        Assert.Equal(200, imported.Response.StatusCode);
        Assert.Equal("hello", Encoding.ASCII.GetString(imported.Response.Body));
    }

    [Fact]
    public async Task ImportAsync_KeepsSessionFlagsThroughASaveAndReopen()
    {
        var request = new CapturedRequest("CONNECT", "pinned.example.test:443", "HTTP/1.1", [("Host", "pinned.example.test:443")], []);
        var response = new CapturedResponse("HTTP/1.1", 200, "Connection Established", [], []);
        var flags = new Dictionary<string, string>
        {
            ["x-no-decrypt"] = "the host is in the skip-decryption list",
            ["ui-comments"] = "Checked <with> \"quotes\" & ampersands",
        };
        var written = new Session(1, "pinned.example.test", DateTimeOffset.UtcNow, request, response, Flags: flags, Scheme: "http");

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, [written]);
        zipStream.Position = 0;

        var store = new SessionStore();
        await SazReader.ImportAsync(zipStream, store);

        var imported = Assert.Single(store.Snapshot());
        Assert.Equal(flags, imported.Flags);
        Assert.Equal("pinned.example.test:443", SessionUrl.Of(imported));
    }

    [Fact]
    public async Task ImportAsync_ReadsFiddlerStyleFlagsAndToleratesNone()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
            {
                ("raw/1_c.txt", "GET / HTTP/1.1\r\nHost: a.test\r\n\r\n"),
                ("raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"),
                ("raw/1_m.xml", """<Session><SessionTimers /><SessionFlags><SessionFlag N="x-clientip" V="127.0.0.1" /><SessionFlag N="" V="ignored" /></SessionFlags></Session>"""),
                ("raw/2_c.txt", "GET / HTTP/1.1\r\nHost: b.test\r\n\r\n"),
                ("raw/2_s.txt", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"),
                ("raw/2_m.xml", "<Session><SessionFlags /></Session>"),
            })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.ASCII);
                writer.Write(text);
            }
        }

        zipStream.Position = 0;
        var store = new SessionStore();
        await SazReader.ImportAsync(zipStream, store);

        var sessions = store.Snapshot();
        Assert.Equal("127.0.0.1", Assert.Single(sessions[0].Flags!).Value);
        Assert.Null(sessions[1].Flags);
    }

    /// <summary>A SAZ file holding one session, written by hand as another tool might.</summary>
    private static MemoryStream ArchiveWith(string requestText, string responseText)
    {
        var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("raw/1_c.txt", requestText), ("raw/1_s.txt", responseText) })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.ASCII);
                writer.Write(text);
            }
        }

        zipStream.Position = 0;
        return zipStream;
    }

    [Fact]
    public async Task ImportAsync_TurnsHttp2PseudoHeadersBackIntoAHostAndScheme()
    {
        using var zipStream = ArchiveWith(
            "GET /api/items HTTP/2.0\r\n:method: GET\r\n:authority: api.example.test\r\n:scheme: https\r\n:path: /api/items\r\naccept: */*\r\n\r\n",
            "HTTP/2.0 200 OK\r\n:status: 200\r\ncontent-type: application/json\r\ncontent-length: 2\r\n\r\n[]");

        var store = new SessionStore();
        var result = await SazReader.ImportAsync(zipStream, store);

        Assert.Empty(result.Skipped);
        var session = Assert.Single(store.Snapshot());
        Assert.Equal("api.example.test", session.Host);
        Assert.Equal("https://api.example.test/api/items", SessionUrl.Of(session));
        Assert.Equal(HttpVersions.Http2, session.Request.HttpVersion);
        Assert.DoesNotContain(session.Request.Headers, h => h.Name.Length == 0 || h.Name.StartsWith(':'));
        Assert.DoesNotContain(session.Response.Headers, h => h.Name.Length == 0 || h.Name.StartsWith(':'));
        Assert.Equal("accept", session.Request.Headers[^1].Name);
        Assert.Equal("[]", Encoding.ASCII.GetString(session.Response.Body));
    }

    [Fact]
    public async Task ImportAsync_ReadsAnAbsoluteHttpsUrlInTheRequestLine()
    {
        using var zipStream = ArchiveWith(
            "GET https://shop.example.test:8443/cart?id=3 HTTP/1.1\r\nAccept: */*\r\n\r\n",
            "HTTP/1.1 204 No Content\r\n\r\n");

        var store = new SessionStore();
        await SazReader.ImportAsync(zipStream, store);

        var session = Assert.Single(store.Snapshot());
        Assert.Equal("shop.example.test", session.Host);
        Assert.Equal("/cart?id=3", session.Request.Target);
        Assert.Equal("https://shop.example.test:8443/cart?id=3", SessionUrl.Of(session));
    }

    [Fact]
    public async Task ImportAsync_AssignsFreshIdsRatherThanReusingTheArchivesOwnNumbering()
    {
        // The store already has sessions, so imported ones can't reuse ids 1
        // and 2. Each request has its own Host header, since that's where
        // SazReader gets the host from.
        var requestA = new CapturedRequest("GET", "/a", "HTTP/1.1", [("Host", "a.test")], []);
        var requestB = new CapturedRequest("GET", "/b", "HTTP/1.1", [("Host", "b.test")], []);
        var response = new CapturedResponse("HTTP/1.1", 200, "OK", [], []);
        var sessions = new[]
        {
            new Session(1, "a.test", DateTimeOffset.UtcNow, requestA, response),
            new Session(2, "b.test", DateTimeOffset.UtcNow, requestB, response),
        };

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, sessions);
        zipStream.Position = 0;

        var store = new SessionStore();
        store.Add("already-here.test", DateTimeOffset.UtcNow, requestA, response); // takes id 1

        var result = await SazReader.ImportAsync(zipStream, store);

        Assert.Equal(2, result.Imported);
        var all = store.Snapshot();
        Assert.Equal(3, all.Count);
        // Fresh, store-assigned, still increasing -- never a collision with
        // the pre-existing session's id, and never the archive's own 1/2.
        Assert.Equal([1, 2, 3], all.Select(s => s.Id));
        Assert.Equal("already-here.test", all[0].Host);
        Assert.Equal("a.test", all[1].Host);
        Assert.Equal("b.test", all[2].Host);
    }

    [Fact]
    public async Task ImportAsync_SkipsAMalformedSessionButStillImportsTheRest()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteRawEntry(archive, "raw/1_c.txt", "GET /one HTTP/1.1\r\nHost: good.test\r\n\r\n");
            WriteRawEntry(archive, "raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");

            // Session 2 has a request file but no response file -- the
            // per-session failure this test is really after.
            WriteRawEntry(archive, "raw/2_c.txt", "GET /two HTTP/1.1\r\nHost: broken.test\r\n\r\n");

            WriteRawEntry(archive, "raw/3_c.txt", "GET /three HTTP/1.1\r\nHost: good.test\r\n\r\n");
            WriteRawEntry(archive, "raw/3_s.txt", "HTTP/1.1 204 No Content\r\n\r\n");
        }

        zipStream.Position = 0;
        var store = new SessionStore();
        var result = await SazReader.ImportAsync(zipStream, store);

        Assert.Equal(2, result.Imported);
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains("#2", skipped);

        var imported = store.Snapshot();
        Assert.Equal(2, imported.Count);
        Assert.Equal("/one", imported[0].Request.Target);
        Assert.Equal("/three", imported[1].Request.Target);
    }

    [Fact]
    public async Task ImportAsync_PreservesABinaryBodyByteForByte()
    {
        var binaryBody = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var request = new CapturedRequest("GET", "/image.png", "HTTP/1.1", [("Host", "example.test")], []);
        var response = new CapturedResponse(
            "HTTP/1.1",
            200,
            "OK",
            [("Content-Type", "image/png")],
            binaryBody);
        var written = new Session(1, "example.test", DateTimeOffset.UtcNow, request, response);

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, [written]);
        zipStream.Position = 0;

        var store = new SessionStore();
        await SazReader.ImportAsync(zipStream, store);

        var imported = Assert.Single(store.Snapshot());
        Assert.Equal(binaryBody, imported.Response.Body);
    }

    [Fact]
    public async Task ImportAsync_StripsThePortFromTheHostHeader()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteRawEntry(archive, "raw/1_c.txt", "GET / HTTP/1.1\r\nHost: example.test:8443\r\n\r\n");
            WriteRawEntry(archive, "raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        }

        zipStream.Position = 0;
        var store = new SessionStore();
        var result = await SazReader.ImportAsync(zipStream, store);

        Assert.Equal(1, result.Imported);
        var imported = Assert.Single(store.Snapshot());
        // The port stays in the Host header; Session.Host is just the name.
        Assert.Equal("example.test", imported.Host);
    }

    [Fact]
    public async Task ImportAsync_FallsBackToUnknownHostWhenThereIsNoHostHeaderAtAll()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteRawEntry(archive, "raw/1_c.txt", "GET / HTTP/1.0\r\n\r\n");
            WriteRawEntry(archive, "raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        }

        zipStream.Position = 0;
        var store = new SessionStore();
        await SazReader.ImportAsync(zipStream, store);

        var imported = Assert.Single(store.Snapshot());
        Assert.Equal("(unknown host)", imported.Host);
    }

    private static void WriteRawEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.ASCII.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public async Task PlainHttpSessionsKeepTheirScheme()
    {
        var store = new SessionStore();
        var request = new CapturedRequest("GET", "/page?x=1", "HTTP/1.1", [("Host", "example.test:8080")], []);
        var response = new CapturedResponse("HTTP/1.1", 200, "OK", [], Encoding.ASCII.GetBytes("hi"));
        var written = store.Add("example.test", DateTimeOffset.UtcNow, request, response, flags: null, "http");

        using var zipStream = new MemoryStream();
        SazWriter.Write(zipStream, [written]);
        zipStream.Position = 0;

        // Written the way Fiddler Classic writes plain HTTP: the full URL in the request line.
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
        {
            using var reader = new StreamReader(archive.GetEntry($"raw/{written.Id}_c.txt")!.Open());
            Assert.StartsWith("GET http://example.test:8080/page?x=1 HTTP/1.1", reader.ReadLine());
        }

        zipStream.Position = 0;
        var imported = new SessionStore();
        await SazReader.ImportAsync(zipStream, imported);

        var session = Assert.Single(imported.Snapshot());
        Assert.Equal("http", session.Scheme);
        Assert.Equal("/page?x=1", session.Request.Target);
        Assert.Equal("example.test:8080", ContentDecoder.FindHeader(session.Request.Headers, "Host"));
    }
}
