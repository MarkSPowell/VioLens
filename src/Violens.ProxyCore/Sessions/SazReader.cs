using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Sessions;

/// <summary>
/// Reads a .saz file into a <see cref="SessionStore"/>. Messages are parsed
/// with the same reader as live traffic, so binary bodies survive
/// byte for byte. HTTP/2 pseudo-headers that other tools save as header
/// lines are dropped, with <c>:authority</c> becoming the Host header and
/// <c>:scheme</c> the session's scheme, as for live HTTP/2 traffic. Session
/// flags (comments, colours, notes) come back from _m.xml. Sessions are added like live captures, with new Ids. A
/// session that can't be read is skipped and listed in
/// <see cref="SazImportResult.Skipped"/>.
/// </summary>
public static class SazReader
{
    public static async Task<SazImportResult> ImportAsync(
        string path, SessionStore sessionStore, CancellationToken cancellationToken = default)
    {
        // Awaited, so the file stays open until the import has read it.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await ImportAsync(stream, sessionStore, cancellationToken);
    }

    public static async Task<SazImportResult> ImportAsync(
        Stream source, SessionStore sessionStore, CancellationToken cancellationToken = default)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

        var imported = 0;
        var skipped = new List<string>();

        foreach (var number in FindSessionNumbers(archive))
        {
            try
            {
                await ImportOneAsync(archive, number, sessionStore, cancellationToken);
                imported++;
            }
            catch (Exception ex)
            {
                skipped.Add($"#{number}: {ex.Message}");
            }
        }

        return new SazImportResult(imported, skipped);
    }

    private static async Task ImportOneAsync(
        ZipArchive archive, int number, SessionStore sessionStore, CancellationToken cancellationToken)
    {
        var requestEntry = archive.GetEntry($"raw/{number}_c.txt")
            ?? throw new InvalidDataException($"missing request file (raw/{number}_c.txt)");
        var responseEntry = archive.GetEntry($"raw/{number}_s.txt")
            ?? throw new InvalidDataException($"missing response file (raw/{number}_s.txt)");

        CapturedRequest request;
        await using (var requestStream = requestEntry.Open())
        {
            request = await Http1MessageReader.ReadRequestAsync(requestStream, cancellationToken)
                ?? throw new InvalidDataException("request file was empty");
        }

        request = request with { HttpVersion = HttpVersions.Normalize(request.HttpVersion) };

        CapturedResponse response;
        await using (var responseStream = responseEntry.Open())
        {
            // Content-Length is always present in VioLens's own archives, and a
            // response without one is read to the end of its entry, so HEAD doesn't
            // matter here.
            response = await Http1MessageReader.ReadResponseAsync(responseStream, isResponseToHeadRequest: false, cancellationToken)
                ?? throw new InvalidDataException("response file was empty");
        }

        response = response with
        {
            HttpVersion = HttpVersions.Normalize(response.HttpVersion),
            Headers = WithoutPseudoHeaders(response.Headers, out _),
        };

        var scheme = "https";
        request = request with { Headers = WithoutPseudoHeaders(request.Headers, out var pseudo) };
        if (pseudo.TryGetValue(":scheme", out var pseudoScheme) && pseudoScheme is "http" or "https")
        {
            scheme = pseudoScheme;
        }

        if (pseudo.TryGetValue(":authority", out var authority) && ContentDecoder.FindHeader(request.Headers, "Host") is null)
        {
            request = request with { Headers = [("Host", authority), .. request.Headers] };
        }

        // A plain HTTP request is saved with its full URL in the request line
        // (Fiddler Classic does the same); store it path-only with the scheme
        // on the session, like a captured one. Some tools save HTTPS
        // requests that way too.
        if (request.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            Proxy.PlainHttpRequest.TryNormalize(request, out var normalized, out _, out _))
        {
            request = normalized;
            scheme = "http";
        }
        else if (request.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                 Uri.TryCreate(request.Target, UriKind.Absolute, out var url))
        {
            var headers = ContentDecoder.FindHeader(request.Headers, "Host") is null
                ? [("Host", url.IsDefaultPort ? url.Host : url.Authority), .. request.Headers]
                : request.Headers;
            request = request with { Target = url.PathAndQuery, Headers = headers };
        }

        var host = ExtractHost(request.Headers);
        var metadata = archive.GetEntry($"raw/{number}_m.xml");
        var (timestamp, timings) = TryReadTimers(metadata);
        var startedAt = timestamp ?? DateTimeOffset.Now;

        sessionStore.Add(host, startedAt, request, response, TryReadFlags(metadata), scheme, timings);
    }

    /// <summary>
    /// The session's flags from _m.xml's <c>&lt;SessionFlag N="…" V="…" /&gt;</c>
    /// elements, as Fiddler and <see cref="SazWriter"/> write them. Null when
    /// there are none or the file can't be read; the first of a repeated name wins.
    /// </summary>
    private static Dictionary<string, string>? TryReadFlags(ZipArchiveEntry? metadataEntry)
    {
        if (metadataEntry is null)
        {
            return null;
        }

        try
        {
            using var stream = metadataEntry.Open();
            var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var flag in XDocument.Load(stream).Root?.Element("SessionFlags")?.Elements("SessionFlag") ?? [])
            {
                if (flag.Attribute("N")?.Value is { Length: > 0 } name)
                {
                    flags.TryAdd(name, flag.Attribute("V")?.Value ?? string.Empty);
                }
            }

            return flags.Count > 0 ? flags : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes out HTTP/2 pseudo-headers (<c>:authority</c>, <c>:method</c>,
    /// <c>:path</c>, <c>:scheme</c>, <c>:status</c>) that other tools write
    /// into a SAZ file's message text as if they were headers. The message
    /// reader splits <c>:authority: example.com</c> at its first colon, giving
    /// an empty name and the value <c>authority: example.com</c>; that's put
    /// back together here and returned in <paramref name="pseudo"/>, so the
    /// caller can use it the way live HTTP/2 traffic is handled (see
    /// Http2Messages.TryToRequest).
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> WithoutPseudoHeaders(
        IReadOnlyList<(string Name, string Value)> headers, out Dictionary<string, string> pseudo)
    {
        pseudo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!headers.Any(h => h.Name.Length == 0))
        {
            return headers;
        }

        var kept = new List<(string Name, string Value)>(headers.Count);
        foreach (var (name, value) in headers)
        {
            var colon = value.IndexOf(':');
            if (name.Length == 0 && colon > 0)
            {
                pseudo.TryAdd(":" + value[..colon].Trim(), value[(colon + 1)..].Trim());
            }
            else if (name.Length > 0)
            {
                kept.Add((name, value));
            }
        }

        return kept;
    }

    private static List<int> FindSessionNumbers(ZipArchive archive)
    {
        const string prefix = "raw/";
        const string suffix = "_c.txt";

        var numbers = new List<int>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith(prefix, StringComparison.Ordinal) &&
                entry.FullName.EndsWith(suffix, StringComparison.Ordinal))
            {
                var numberText = entry.FullName[prefix.Length..^suffix.Length];
                if (int.TryParse(numberText, out var number))
                {
                    numbers.Add(number);
                }
            }
        }

        numbers.Sort();
        return numbers;
    }

    /// <summary>The host from the Host header, without any port; _m.xml doesn't record it.</summary>
    private static string ExtractHost(IReadOnlyList<(string Name, string Value)> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = value.LastIndexOf(':');
                return colonIndex > 0 ? value[..colonIndex] : value;
            }
        }

        // No Host header (old HTTP/1.0); don't fail the session over it.
        return "(unknown host)";
    }

    /// <summary>
    /// The start time and timings from _m.xml, read the way
    /// <see cref="SazWriter"/> writes them. Fiddler's own timestamp format is
    /// unconfirmed, so these may be missing; the session then starts "now".
    /// Timings are null when every timestamp is the same.
    /// </summary>
    private static (DateTimeOffset? StartedAt, SessionTimings? Timings) TryReadTimers(ZipArchiveEntry? metadataEntry)
    {
        if (metadataEntry is null)
        {
            return (null, null);
        }

        try
        {
            using var stream = metadataEntry.Open();
            var timers = XDocument.Load(stream).Root?.Element("SessionTimers");
            DateTimeOffset? At(string name) =>
                timers?.Attribute(name)?.Value is { } text &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null;
            double Whole(string name) =>
                double.TryParse(timers?.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : 0;

            var start = At("ClientBeginRequest");
            if (start is null || At("ServerConnected") is not { } connected || At("ServerGotRequest") is not { } sent ||
                At("ServerBeginResponse") is not { } firstByte || (At("ClientDoneResponse") ?? At("ServerDoneResponse")) is not { } done ||
                done <= start)
            {
                return (start, null);
            }

            var dns = Whole("DNSTime");
            var connect = Whole("TCPConnectTime");
            var ssl = Whole("HTTPSHandshakeTime");
            var opened = dns + connect + ssl > 0;
            var timings = new SessionTimings
            {
                Blocked = Math.Max(0, (connected - start.Value).TotalMilliseconds - (dns + connect + ssl)),
                Dns = opened ? dns : SessionTimings.NotApplicable,
                Connect = opened ? connect : SessionTimings.NotApplicable,
                Ssl = opened && ssl > 0 ? ssl : SessionTimings.NotApplicable,
                Send = Math.Max(0, (sent - connected).TotalMilliseconds),
                Wait = Math.Max(0, (firstByte - sent).TotalMilliseconds),
                Receive = Math.Max(0, (done - firstByte).TotalMilliseconds),
                ConnectionReused = !opened,
            };
            return (start, timings);
        }
        catch
        {
            return (null, null);
        }
    }
}

/// <summary>How many sessions were imported, and why any were skipped.</summary>
public sealed record SazImportResult(int Imported, IReadOnlyList<string> Skipped);
