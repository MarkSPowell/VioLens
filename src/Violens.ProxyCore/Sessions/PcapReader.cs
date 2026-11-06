using System.Globalization;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions.Pcap;

namespace Violens.ProxyCore.Sessions;

/// <summary>
/// Imports the plain HTTP/1.x traffic in a packet capture (classic PCAP or
/// PCAPNG, from Wireshark, tcpdump, pktmon and the like) into a
/// <see cref="SessionStore"/>. The packet reading and TCP reassembly are
/// ported from Clearinet.
///
/// A packet capture holds HTTPS encrypted, so TLS connections (and HTTP/2
/// and HTTP/3, which browsers only use over TLS) are counted in
/// <see cref="PcapImportResult.Notes"/> and skipped. Bodies are stored as
/// captured, with chunked framing removed as for live traffic. A message
/// the capture doesn't hold all of is imported as far as it goes, with the
/// <see cref="IncompleteFlag"/>; a request with no response gets status 0
/// rather than an invented one. Sessions get the client's and server's
/// addresses as flags, and timings from when the packets were captured.
/// </summary>
public static class PcapReader
{
    /// <summary>Set on a session whose request or response the capture didn't hold all of; the value says which.</summary>
    public const string IncompleteFlag = "x-pcap-incomplete";

    /// <summary>The note that counts skipped TLS connections, so a caller can explain an import that found nothing.</summary>
    public const string TlsSkippedNote = "Encrypted (TLS) connections skipped: a packet capture can't be decrypted";

    /// <summary>The largest capture file that can be imported.</summary>
    public const long MaxCaptureBytes = PcapFile.MaxCapture;

    public static async Task<PcapImportResult> ImportAsync(
        string path, SessionStore sessionStore, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: false);
        return await ImportAsync(stream, sessionStore, cancellationToken);
    }

    public static Task<PcapImportResult> ImportAsync(
        Stream source, SessionStore sessionStore, CancellationToken cancellationToken = default)
    {
        if (source.CanSeek && source.Length - source.Position > MaxCaptureBytes)
        {
            throw new InvalidDataException("the capture is larger than VioLens can import (256 MB)");
        }

        // Reading and reassembly are CPU-bound and synchronous.
        return Task.Run(() =>
        {
            var decoder = new PcapDecoder(cancellationToken);
            var exchanges = decoder.Read(source);
            foreach (var exchange in exchanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(exchange, sessionStore);
            }

            var notes = decoder.Notes
                .Select(note => new PcapImportNote(note.Key, note.Value))
                .ToList();
            return new PcapImportResult(exchanges.Count, decoder.ConnectionCount, notes);
        }, cancellationToken);
    }

    private static void Add(PcapExchange exchange, SessionStore sessionStore)
    {
        var message = exchange.Request;
        var request = new CapturedRequest(message.Method, message.Target, message.Version, message.Headers, message.Body);
        var scheme = "http";
        if (message.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            Proxy.PlainHttpRequest.TryNormalize(request, out var normalized, out _, out _))
        {
            // A request sent to a proxy carries the full URL; store it
            // path-only, like a captured one.
            request = normalized;
        }
        else if (message.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                 Uri.TryCreate(message.Target, UriKind.Absolute, out var url))
        {
            scheme = "https";
            var headers = ContentDecoder.FindHeader(request.Headers, "Host") is null
                ? [("Host", url.IsDefaultPort ? url.Host : url.Authority), .. request.Headers]
                : request.Headers;
            request = request with { Target = url.PathAndQuery, Headers = headers };
        }
        else if (message.Method != "CONNECT" && ContentDecoder.FindHeader(request.Headers, "Host") is null)
        {
            // HTTP/1.0 without a Host header: the server's address stands in.
            request = request with { Headers = [("Host", Authority(exchange.ServerAddress, exchange.ServerPort)), .. request.Headers] };
        }

        var response = exchange.Response is { } r
            ? new CapturedResponse(r.Version, r.Status, r.Reason, r.Headers, r.Body)
            : new CapturedResponse(message.Version, 0, "Response not captured", [], []);

        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-clientIP"] = exchange.ClientAddress,
            ["x-clientport"] = exchange.ClientPort.ToString(CultureInfo.InvariantCulture),
            ["x-hostIP"] = exchange.ServerAddress,
            ["x-serverport"] = exchange.ServerPort.ToString(CultureInfo.InvariantCulture),
            ["x-imported-from"] = "packet capture",
        };
        if (exchange.Informational.Length > 0)
        {
            flags["x-pcap-informational"] = exchange.Informational;
        }

        var incomplete = new List<string>();
        if (!message.Complete)
        {
            incomplete.Add("the request body wasn't all captured");
        }

        if (exchange.Response is null)
        {
            incomplete.Add("no response was captured");
        }
        else if (!exchange.Response.Complete)
        {
            incomplete.Add("the response body wasn't all captured");
        }

        if (incomplete.Count > 0)
        {
            flags[IncompleteFlag] = string.Join("; ", incomplete);
        }

        var startedAt = exchange.RequestStart != DateTime.MinValue
            ? new DateTimeOffset(exchange.RequestStart, TimeSpan.Zero).ToLocalTime()
            : DateTimeOffset.Now;
        sessionStore.Add(Host(request, message, exchange), startedAt, request, response, flags, scheme, Timings(exchange));
    }

    private static string Host(CapturedRequest request, PcapMessage message, PcapExchange exchange)
    {
        var authority = message.Method == "CONNECT"
            ? message.Target
            : ContentDecoder.FindHeader(request.Headers, "Host") ?? exchange.ServerAddress;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            return close > 0 ? authority[1..close] : authority;
        }

        var colon = authority.LastIndexOf(':');
        return colon > 0 && authority.IndexOf(':') == colon ? authority[..colon] : authority;
    }

    private static string Authority(string address, int port) =>
        (address.Contains(':') ? "[" + address + "]" : address) +
        (port == 80 ? string.Empty : ":" + port.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Send, wait and receive from the packets' times. When the capture
    /// holds the connection's start, the time from there to the request is
    /// counted as connecting for the connection's first request; a reused
    /// connection's requests have no connect phase. Null when times are missing.
    /// </summary>
    private static SessionTimings? Timings(PcapExchange exchange)
    {
        if (exchange.RequestStart == DateTime.MinValue || exchange.RequestEnd == DateTime.MinValue)
        {
            return null;
        }

        static double Between(DateTime from, DateTime to) =>
            from == DateTime.MinValue || to == DateTime.MinValue ? SessionTimings.NotApplicable : Math.Max(0, (to - from).TotalMilliseconds);

        var answered = exchange.Response is not null && exchange.ResponseStart != DateTime.MinValue;
        var firstOnConnection = exchange.ConnectionStart != DateTime.MinValue && exchange.ConnectionStart < exchange.RequestStart &&
                                exchange.Request.Start == 0;
        return new SessionTimings
        {
            Connect = firstOnConnection ? Between(exchange.ConnectionStart, exchange.RequestStart) : SessionTimings.NotApplicable,
            Send = Between(exchange.RequestStart, exchange.RequestEnd),
            Wait = answered ? Between(exchange.RequestEnd, exchange.ResponseStart) : SessionTimings.NotApplicable,
            Receive = answered ? Between(exchange.ResponseStart, exchange.ResponseEnd) : SessionTimings.NotApplicable,
            ConnectionId = exchange.ConnectionNumber,
            ConnectionReused = !firstOnConnection && exchange.Request.Start > 0,
        };
    }
}

/// <summary>Something an import skipped or couldn't capture in full, and how many times.</summary>
public sealed record PcapImportNote(string Text, int Count);

/// <summary>How many sessions a packet capture import added, from how many TCP connections, and what it skipped.</summary>
public sealed record PcapImportResult(int Imported, int Connections, IReadOnlyList<PcapImportNote> Notes);
