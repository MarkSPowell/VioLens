using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Sessions;

/// <summary>
/// Reads a .saz file into a <see cref="SessionStore"/>. Messages are parsed
/// with the same reader as live traffic, so binary bodies survive
/// byte for byte. Sessions are added like live captures, with new Ids. A
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

        response = response with { HttpVersion = HttpVersions.Normalize(response.HttpVersion) };

        // A plain HTTP request is saved with its full URL in the request line
        // (Fiddler Classic does the same); store it path-only with the scheme
        // on the session, like a captured one.
        var scheme = "https";
        if (request.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            Proxy.PlainHttpRequest.TryNormalize(request, out var normalized, out _, out _))
        {
            request = normalized;
            scheme = "http";
        }

        var host = ExtractHost(request.Headers);
        var (timestamp, timings) = TryReadTimers(archive.GetEntry($"raw/{number}_m.xml"));
        var startedAt = timestamp ?? DateTimeOffset.Now;

        sessionStore.Add(host, startedAt, request, response, flags: null, scheme, timings);
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
