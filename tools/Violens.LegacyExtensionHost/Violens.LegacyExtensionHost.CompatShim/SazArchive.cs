using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Violens.CompatShim;

/// <summary>
/// Reads and writes .saz files, Fiddler's published archive format: a zip
/// with <c>raw/N_c.txt</c> (request), <c>raw/N_s.txt</c> (response) and
/// <c>raw/N_m.xml</c> (flags and timers) per session, plus
/// <c>_index.htm</c> and <c>[Content_Types].xml</c>. WebSocket
/// (<c>_w.txt</c>) and gRPC (<c>_g.txt</c>) entries are skipped, and timers
/// aren't kept. Encrypted archives aren't supported (.NET's zip classes
/// can't do zip encryption), so both directions throw rather than silently
/// read nothing or write an unprotected file.
/// </summary>
internal static class SazArchive
{
    /// <summary>
    /// The most one entry is read to (a message, or its metadata), as VioLens
    /// itself limits a buffered body, but lower: this is a 32-bit process.
    /// A few KB of ZIP can expand to gigabytes.
    /// </summary>
    internal const long MaxEntryBytes = 128L * 1024 * 1024;

    /// <summary>The most one archive is read to, all entries together.</summary>
    internal const long MaxArchiveBytes = 1024L * 1024 * 1024;

    private static readonly Encoding HeaderEncoding = Encoding.GetEncoding("ISO-8859-1");
    private static readonly byte[] HeaderBodySeparatorCrLf = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
    private static readonly byte[] HeaderBodySeparatorLfOnly = { (byte)'\n', (byte)'\n' };

    public static Session[] Read(string filename, bool decrypt)
    {
        if (string.IsNullOrEmpty(filename) || !File.Exists(filename))
        {
            FiddlerApplication.Log.LogFormat("ReadSessionArchive: file not found -- {0}", new object[] { filename });
            return Array.Empty<Session>();
        }

        try
        {
            using var archive = ZipFile.OpenRead(filename);

            var byId = new Dictionary<int, SazEntrySet>();
            foreach (var entry in archive.Entries)
            {
                if (!TryParseRawEntryName(entry.FullName, out var id, out var kind))
                {
                    continue; // _index.htm, [Content_Types.xml], unrecognized -- skip.
                }

                if (!byId.TryGetValue(id, out var set))
                {
                    set = new SazEntrySet();
                    byId[id] = set;
                }

                switch (kind)
                {
                    case 'c': set.Client = entry; break;
                    case 's': set.Server = entry; break;
                    case 'm': set.Metadata = entry; break;
                    // WebSocket and gRPC entries: skipped.
                }
            }

            var result = new List<Session>();
            var budget = new long[] { MaxArchiveBytes };
            foreach (var id in byId.Keys.OrderBy(x => x))
            {
                var set = byId[id];
                var session = new Session { id = id };

                try
                {
                    // Metadata first -- ParseRawRequest below uses any "https"-ish
                    // session flag as a hint when guessing the request's scheme.
                    if (set.Metadata != null)
                    {
                        ParseMetadata(session, ReadEntryText(set.Metadata, budget));
                    }

                    if (set.Client != null)
                    {
                        ParseRawRequest(session, ReadEntryBytes(set.Client, budget));
                    }

                    if (set.Server != null)
                    {
                        ParseRawResponse(session, ReadEntryBytes(set.Server, budget));
                    }
                }
                catch (EntryTooLargeException ex)
                {
                    FiddlerApplication.Log.LogFormat("ReadSessionArchive: session {0} skipped -- {1}", new object[] { id, ex.Message });
                    if (budget[0] < 0)
                    {
                        break;
                    }

                    continue;
                }

                result.Add(session);
            }

            FiddlerApplication.Log.LogFormat(
                "ReadSessionArchive: loaded {0} session(s) from {1}.",
                new object[] { result.Count, Path.GetFileName(filename) });
            return result.ToArray();
        }
        catch (InvalidDataException ex)
        {
            // A corrupt or encrypted archive both land here; say so rather than
            // return no sessions.
            var message =
                $"Couldn't read '{Path.GetFileName(filename)}' as a plain .saz (ZIP) archive -- if it's " +
                "password-protected, encrypted .saz files aren't supported yet by this legacy host. See SazArchive's remarks.";
            FiddlerApplication.Log.LogFormat("ReadSessionArchive: {0}", new object[] { message });
            throw new NotSupportedException(message, ex);
        }
    }

    public static bool Write(string filename, Session[] sessions, string password, bool encrypt)
    {
        if (encrypt || !string.IsNullOrEmpty(password))
        {
            // See this class's remarks: writing an unencrypted file here
            // while the caller believes it asked for (and got) an encrypted
            // one would be a silent security downgrade, so this refuses
            // outright instead.
            var message =
                "Encrypted/password-protected .saz files aren't supported yet by this legacy host -- refusing " +
                "rather than silently writing an unprotected archive. See SazArchive's remarks.";
            FiddlerApplication.Log.LogFormat("WriteSessionArchive: {0}", new object[] { message });
            throw new NotSupportedException(message);
        }

        sessions ??= Array.Empty<Session>();

        try
        {
            if (File.Exists(filename))
            {
                File.Delete(filename);
            }

            using (var archive = ZipFile.Open(filename, ZipArchiveMode.Create))
            {
                var nextId = 1;
                foreach (var session in sessions)
                {
                    var id = session.id != 0 ? session.id : nextId;
                    WriteRawRequest(archive, id, session);
                    WriteRawResponse(archive, id, session);
                    WriteMetadata(archive, id, session);
                    nextId++;
                }

                WriteContentTypes(archive);
                WriteIndexHtml(archive, sessions);
            }

            FiddlerApplication.Log.LogFormat(
                "WriteSessionArchive: wrote {0} session(s) to {1}.",
                new object[] { sessions.Length, Path.GetFileName(filename) });
            return true;
        }
        catch (Exception ex)
        {
            // Matches the metadata-confirmed signature (bool return, not
            // void/exception) -- a real caller checking the return value
            // gets a clean false; the reason still lands in the on-screen
            // log rather than being swallowed entirely.
            FiddlerApplication.Log.LogFormat(
                "WriteSessionArchive: failed writing {0} -- {1}",
                new object[] { filename, ex.Message });
            return false;
        }
    }

    private sealed class SazEntrySet
    {
        public ZipArchiveEntry Client;
        public ZipArchiveEntry Server;
        public ZipArchiveEntry Metadata;
    }

    private static bool TryParseRawEntryName(string fullName, out int id, out char kind)
    {
        id = 0;
        kind = '\0';

        const string prefix = "raw/";
        if (fullName.Length <= prefix.Length || !fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = fullName.Substring(prefix.Length);
        var underscoreIndex = name.LastIndexOf('_');
        if (underscoreIndex <= 0 || underscoreIndex + 1 >= name.Length)
        {
            return false;
        }

        if (!int.TryParse(name.Substring(0, underscoreIndex), out id))
        {
            return false;
        }

        kind = char.ToLowerInvariant(name[underscoreIndex + 1]);
        return true;
    }

    /// <summary>
    /// The entry's bytes, up to <see cref="MaxEntryBytes"/>, taken from what's
    /// left of <paramref name="budget"/> (one element, shared by the archive's
    /// entries). Counts what actually comes out, not the size the archive
    /// claims. Internal for tests.
    /// </summary>
    internal static byte[] ReadEntryBytes(ZipArchiveEntry entry, long[] budget)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new EntryTooLargeException($"{entry.FullName} is larger than {MaxEntryBytes / (1024 * 1024)} MB.");
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            budget[0] -= read;
            if (budget[0] < 0)
            {
                throw new EntryTooLargeException($"the archive expands to more than {MaxArchiveBytes / (1024 * 1024)} MB, so the rest isn't read.");
            }

            if (memory.Length + read > MaxEntryBytes)
            {
                throw new EntryTooLargeException($"{entry.FullName} is larger than {MaxEntryBytes / (1024 * 1024)} MB.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    /// <summary>An entry, or the archive as a whole, expands past its limit.</summary>
    internal sealed class EntryTooLargeException : Exception
    {
        public EntryTooLargeException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// The entry as text, without a leading byte order mark, which
    /// <c>GetString</c> would otherwise keep and <c>XDocument.Parse</c> rejects.
    /// Fiddler's own archives may have one.
    /// </summary>
    private static string ReadEntryText(ZipArchiveEntry entry, long[] budget)
    {
        var text = Encoding.UTF8.GetString(ReadEntryBytes(entry, budget));
        return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
    }

    private static void ParseRawRequest(Session session, byte[] raw)
    {
        var (headerText, body) = SplitHeadersAndBody(raw);
        var lines = SplitLines(headerText);
        if (lines.Count == 0)
        {
            return;
        }

        var requestLine = lines[0].Split(new[] { ' ' }, 3);
        var headers = session.oRequest.headers;
        if (requestLine.Length >= 1)
        {
            headers.HTTPMethod = requestLine[0];
        }
        var requestTarget = requestLine.Length >= 2 ? requestLine[1] : string.Empty;

        for (var i = 1; i < lines.Count; i++)
        {
            AddHeaderLine(headers, lines[i]);
        }

        var host = headers["Host"];
        var scheme = GuessScheme(session, requestTarget);
        headers.UriScheme = scheme;

        var isAbsoluteForm = requestTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                              requestTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var pathAndQuery = isAbsoluteForm ? GetPathAndQueryFromAbsoluteUri(requestTarget) : requestTarget;
        headers.RequestPath = pathAndQuery;

        session.host = host ?? string.Empty;
        session.PathAndQuery = pathAndQuery;
        session.url = (host ?? string.Empty) + pathAndQuery;
        session.fullUrl = scheme + "://" + session.url;
        session.requestBodyBytes = body;
    }

    private static void ParseRawResponse(Session session, byte[] raw)
    {
        var (headerText, body) = SplitHeadersAndBody(raw);
        var lines = SplitLines(headerText);
        if (lines.Count == 0)
        {
            return;
        }

        var headers = session.oResponse.headers;
        headers.HTTPResponseStatus = lines[0];

        var statusParts = lines[0].Split(new[] { ' ' }, 3);
        if (statusParts.Length >= 2 && int.TryParse(statusParts[1], out var code))
        {
            session.responseCode = code;
        }

        for (var i = 1; i < lines.Count; i++)
        {
            AddHeaderLine(headers, lines[i]);
        }

        session.responseBodyBytes = body;
    }

    private static void AddHeaderLine(HTTPHeaders headers, string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        var colonIndex = line.IndexOf(':');
        if (colonIndex <= 0)
        {
            return;
        }

        var name = line.Substring(0, colonIndex).Trim();
        var value = line.Substring(colonIndex + 1).Trim();
        if (name.Length > 0)
        {
            headers[name] = value;
        }
    }

    /// <summary>
    /// An absolute request target settles it; otherwise an "https" flag means
    /// HTTPS, and anything else is taken as HTTP. A guess, not checked against
    /// a real HTTPS archive.
    /// </summary>
    private static string GuessScheme(Session session, string requestTarget)
    {
        if (requestTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "https";
        }
        if (requestTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "http";
        }

        var flag = session["https"];
        if (!string.IsNullOrEmpty(flag) && !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase))
        {
            return "https";
        }

        return "http";
    }

    private static string GetPathAndQueryFromAbsoluteUri(string absoluteUri) =>
        Uri.TryCreate(absoluteUri, UriKind.Absolute, out var uri) ? uri.PathAndQuery : absoluteUri;

    private static void ParseMetadata(Session session, string xmlText)
    {
        if (string.IsNullOrWhiteSpace(xmlText))
        {
            return;
        }

        try
        {
            var doc = XDocument.Parse(xmlText);
            foreach (var flagElement in doc.Descendants("SessionFlag"))
            {
                var name = (string)flagElement.Attribute("N");
                var value = (string)flagElement.Attribute("V") ?? string.Empty;
                if (!string.IsNullOrEmpty(name))
                {
                    session[name] = value;
                }
            }
            // Timers aren't kept.
        }
        catch (System.Xml.XmlException ex)
        {
            FiddlerApplication.Log.LogFormat(
                "ReadSessionArchive: couldn't parse metadata XML for session {0} -- {1}",
                new object[] { session.id, ex.Message });
        }
    }

    private static (string headerText, byte[] body) SplitHeadersAndBody(byte[] raw)
    {
        var separatorIndex = IndexOfSequence(raw, HeaderBodySeparatorCrLf);
        var separatorLength = HeaderBodySeparatorCrLf.Length;
        if (separatorIndex < 0)
        {
            separatorIndex = IndexOfSequence(raw, HeaderBodySeparatorLfOnly);
            separatorLength = HeaderBodySeparatorLfOnly.Length;
        }

        if (separatorIndex < 0)
        {
            // No blank-line separator found -- treat the whole thing as
            // headers with no body (defensive; a well-formed raw capture
            // always has one).
            return (HeaderEncoding.GetString(raw, 0, raw.Length), Array.Empty<byte>());
        }

        var headerText = HeaderEncoding.GetString(raw, 0, separatorIndex);
        var bodyStart = separatorIndex + separatorLength;
        var bodyLength = raw.Length - bodyStart;
        var body = new byte[bodyLength];
        Array.Copy(raw, bodyStart, body, 0, bodyLength);
        return (headerText, body);
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        if (haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var isMatch = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    isMatch = false;
                    break;
                }
            }
            if (isMatch)
            {
                return i;
            }
        }
        return -1;
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

    private static void WriteRawRequest(ZipArchive archive, int id, Session session)
    {
        var entry = archive.CreateEntry($"raw/{id}_c.txt", CompressionLevel.Optimal);
        using var stream = entry.Open();

        var headers = session.oRequest?.headers;
        var method = string.IsNullOrEmpty(headers?.HTTPMethod) ? "GET" : headers.HTTPMethod;
        var target = string.IsNullOrEmpty(headers?.RequestPath)
            ? (string.IsNullOrEmpty(session.PathAndQuery) ? "/" : session.PathAndQuery)
            : headers.RequestPath;

        WriteAsciiLine(stream, $"{method} {target} HTTP/1.1");
        WriteHeaderLines(stream, headers);
        WriteAsciiLine(stream, string.Empty);

        var body = session.requestBodyBytes ?? Array.Empty<byte>();
        stream.Write(body, 0, body.Length);
    }

    private static void WriteRawResponse(ZipArchive archive, int id, Session session)
    {
        var entry = archive.CreateEntry($"raw/{id}_s.txt", CompressionLevel.Optimal);
        using var stream = entry.Open();

        var headers = session.oResponse?.headers;
        var statusLine = !string.IsNullOrEmpty(headers?.HTTPResponseStatus)
            ? headers.HTTPResponseStatus.TrimEnd('\r', '\n')
            : $"HTTP/1.1 {session.responseCode} {GuessStatusText(session.responseCode)}";

        WriteAsciiLine(stream, statusLine);
        WriteHeaderLines(stream, headers);
        WriteAsciiLine(stream, string.Empty);

        var body = session.responseBodyBytes ?? Array.Empty<byte>();
        stream.Write(body, 0, body.Length);
    }

    private static void WriteMetadata(ZipArchive archive, int id, Session session)
    {
        var entry = archive.CreateEntry($"raw/{id}_m.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();

        var sessionElement = new XElement("Session", new XAttribute("SID", id));
        var flagsElement = new XElement("SessionFlags");

        if (session.oFlags != null)
        {
            foreach (System.Collections.DictionaryEntry flag in session.oFlags)
            {
                flagsElement.Add(new XElement(
                    "SessionFlag",
                    new XAttribute("N", (string)flag.Key),
                    new XAttribute("V", (string)flag.Value ?? string.Empty)));
            }
        }

        sessionElement.Add(flagsElement);

        // Written without a byte order mark.
        var writerSettings = new XmlWriterSettings { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };
        using var xmlWriter = XmlWriter.Create(stream, writerSettings);
        new XDocument(new XDeclaration("1.0", "utf-8", null), sessionElement).Save(xmlWriter);
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        var entry = archive.CreateEntry("[Content_Types.xml]", CompressionLevel.Optimal);
        using var stream = entry.Open();
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                  "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                  "<Default Extension=\"txt\" ContentType=\"text/plain\" />" +
                  "<Default Extension=\"xml\" ContentType=\"application/xml\" />" +
                  "</Types>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteIndexHtml(ZipArchive archive, Session[] sessions)
    {
        var entry = archive.CreateEntry("_index.htm", CompressionLevel.Optimal);
        using var stream = entry.Open();
        var html =
            "<html><body><p>Written by VioLens's legacy extension host compat shim -- " +
            sessions.Length +
            " session(s). For manual inspection only; not read back when this archive is opened.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteAsciiLine(Stream stream, string line)
    {
        var bytes = HeaderEncoding.GetBytes(line + "\r\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteHeaderLines(Stream stream, HTTPHeaders headers)
    {
        if (headers == null)
        {
            return;
        }

        // HTTPHeaders.ToString() already writes "Name: Value\r\n" lines.
        var text = headers.ToString();
        var bytes = HeaderEncoding.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string GuessStatusText(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Unknown",
    };
}
