using System.Globalization;
using System.IO.Compression;
using System.Text;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Sessions;

/// <summary>
/// Writes sessions as a Fiddler-compatible .saz file. The format isn't
/// published; this follows what the open-source saz-tools parser expects: a
/// zip whose raw/ folder holds &lt;n&gt;_c.txt, &lt;n&gt;_s.txt and
/// &lt;n&gt;_m.xml per session (1-based), plus [Content_Types].xml and
/// _index.htm, a page listing the sessions with links to their files, for
/// someone who unzips the archive (Fiddler Classic writes one too; readers
/// ignore it).
/// SessionTimers come from <see cref="Session.Timings"/>, as ISO 8601
/// timestamps; Fiddler's exact expected format is unconfirmed.
/// </summary>
public static class SazWriter
{
    public static void Write(string path, IReadOnlyList<Session> sessions)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(stream, sessions);
    }

    public static void Write(Stream destination, IReadOnlyList<Session> sessions)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        WriteContentTypes(archive);
        WriteIndex(archive, sessions);

        foreach (var session in sessions)
        {
            WriteRequest(archive, session);
            WriteResponse(archive, session);
            WriteMetadata(archive, session);
        }
    }

    /// <summary>
    /// _index.htm: one row per session with links to its request, response
    /// and metadata files. Everything from the traffic is HTML-encoded, so a
    /// captured URL or header can't put markup or script into the page.
    /// </summary>
    private static void WriteIndex(ZipArchive archive, IReadOnlyList<Session> sessions)
    {
        static string Encode(string text) => System.Net.WebUtility.HtmlEncode(text);

        var page = new StringBuilder();
        page.Append(
            $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>{{Encode(ProxyEngine.Identity.ProductName)}} sessions</title>
            <style>body{font-family:sans-serif;font-size:13px} table{border-collapse:collapse} th,td{border:1px solid #ccc;padding:2px 6px;text-align:left;vertical-align:top} td.url{word-break:break-all}</style>
            </head><body>
            <table>
            <tr><th>&nbsp;</th><th>#</th><th>Result</th><th>Method</th><th>Host</th><th>URL</th><th>Body</th><th>Content-Type</th></tr>

            """);
        foreach (var session in sessions)
        {
            var id = session.Id.ToString(CultureInfo.InvariantCulture);
            var contentType = ContentDecoder.FindHeader(session.Response.Headers, "Content-Type") ?? string.Empty;
            page.Append(CultureInfo.InvariantCulture,
                $"""<tr><td><a href="raw/{id}_c.txt">C</a> <a href="raw/{id}_s.txt">S</a> <a href="raw/{id}_m.xml">M</a></td>""");
            page.Append(CultureInfo.InvariantCulture,
                $"<td>{id}</td><td>{session.Response.StatusCode}</td><td>{Encode(session.Request.Method)}</td><td>{Encode(session.Host)}</td>");
            page.Append(CultureInfo.InvariantCulture,
                $"""<td class="url">{Encode(SessionUrl.Of(session))}</td><td>{session.Response.Body.Length:N0}</td><td>{Encode(contentType)}</td></tr>""");
            page.Append('\n');
        }

        page.Append("</table>\n</body></html>\n");

        var entry = archive.CreateEntry("_index.htm", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(page.ToString());
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        var entry = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="htm" ContentType="text/html" />
              <Default Extension="txt" ContentType="text/plain" />
              <Default Extension="xml" ContentType="application/xml" />
            </Types>
            """);
    }

    private static void WriteRequest(ZipArchive archive, Session session)
    {
        var entry = archive.CreateEntry($"raw/{session.Id}_c.txt", CompressionLevel.Optimal);
        using var stream = entry.Open();
        // Bodies are already decoded, so headers are rebuilt: Transfer-Encoding
        // dropped, Content-Length set to the body's length. Plain HTTP requests
        // keep their full URL, as Fiddler Classic writes them.
        var request = string.Equals(session.Scheme, "http", StringComparison.OrdinalIgnoreCase) && session.Request.Target.StartsWith('/')
            ? session.Request with { Target = SessionUrl.Of(session) }
            : session.Request;
        stream.Write(HttpMessageWriter.BuildRequestBytes(request with { HttpVersion = HttpVersions.ForSaz(request.HttpVersion) }));
    }

    private static void WriteResponse(ZipArchive archive, Session session)
    {
        var entry = archive.CreateEntry($"raw/{session.Id}_s.txt", CompressionLevel.Optimal);
        using var stream = entry.Open();
        var response = session.Response;
        stream.Write(HttpMessageWriter.BuildResponseBytes(response with { HttpVersion = HttpVersions.ForSaz(response.HttpVersion) }));
    }

    private static void WriteMetadata(ZipArchive archive, Session session)
    {
        var entry = archive.CreateEntry($"raw/{session.Id}_m.xml", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var t = session.Timings;
        static double Ms(double phase) => Math.Max(0, phase);
        var start = session.StartedAt;
        var requestRead = start.AddMilliseconds(t is null ? 0 : Ms(t.Blocked));
        var connected = requestRead.AddMilliseconds(t is null ? 0 : Ms(t.Dns) + Ms(t.Connect) + Ms(t.Ssl));
        var sent = connected.AddMilliseconds(t is null ? 0 : Ms(t.Send));
        var firstByte = sent.AddMilliseconds(t is null ? 0 : Ms(t.Wait));
        var done = firstByte.AddMilliseconds(t is null ? 0 : Ms(t.Receive));
        static string At(DateTimeOffset when) => when.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture);
        static string Whole(double phase) => ((long)Math.Round(Math.Max(0, phase))).ToString(CultureInfo.InvariantCulture);

        writer.Write(
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Session>
              <SessionTimers ClientConnected="{At(start)}" ClientBeginRequest="{At(start)}" GotRequestHeaders="{At(start)}" ClientDoneRequest="{At(requestRead)}" GatewayTime="0" DNSTime="{Whole(t?.Dns ?? 0)}" TCPConnectTime="{Whole(t?.Connect ?? 0)}" HTTPSHandshakeTime="{Whole(t?.Ssl ?? 0)}" ServerConnected="{At(connected)}" FiddlerBeginRequest="{At(connected)}" ServerGotRequest="{At(sent)}" ServerBeginResponse="{At(firstByte)}" GotResponseHeaders="{At(firstByte)}" ServerDoneResponse="{At(done)}" ClientBeginResponse="{At(firstByte)}" ClientDoneResponse="{At(done)}" />
              <SessionFlags />
            </Session>
            """);
    }
}
