// Ported from Clearinet (https://github.com/ericlaw1979/Clearinet),
// Transcoders/PcapDecoder.cs. Copyright (c) 2026 Eric Lawrence. MIT License:
// see THIRD-PARTY-NOTICES.md.

using System.Globalization;
using System.Net;
using System.Text;

namespace Violens.ProxyCore.Sessions.Pcap;

/// <summary>One HTTP/1.x message read from a reassembled TCP stream.</summary>
internal sealed class PcapMessage
{
    internal string Method = string.Empty, Target = string.Empty, Version = string.Empty, Reason = string.Empty;
    internal int Status;
    internal List<(string Name, string Value)> Headers = [];

    /// <summary>The body with any chunked framing removed (as much of it as was captured).</summary>
    internal byte[] Body = [];

    /// <summary>False when the capture ends before the message does.</summary>
    internal bool Complete;

    internal int Start, BodyStart, End;
}

/// <summary>A request and the response it got (null when none was captured), with where they came from and when.</summary>
internal sealed class PcapExchange
{
    internal required PcapMessage Request;
    internal PcapMessage? Response;
    internal required string ClientAddress, ServerAddress;
    internal int ClientPort, ServerPort;

    /// <summary>Any 1xx responses before the final one, as their status lines.</summary>
    internal string Informational = string.Empty;

    internal DateTime ConnectionStart, RequestStart, RequestEnd, ResponseStart, ResponseEnd;
    internal int ConnectionNumber;
}

/// <summary>
/// Turns a capture's packets into HTTP exchanges: Ethernet, 802.11 (when
/// not encrypted), raw IP and Linux "cooked" frames; IPv4 and IPv6; TCP
/// reassembled in sequence order, keeping only the part with no gaps; then
/// HTTP/1.x requests and responses paired in order on each connection.
/// TLS and HTTP/2 connections are counted and skipped, since a packet
/// capture can't be decrypted. Problems are reported through
/// <see cref="Notes"/>, never by inventing data.
/// </summary>
internal sealed class PcapDecoder
{
    private const int MaxStream = 32 * 1024 * 1024, MaxHeaders = 65536;
    private const int MaxExchanges = 100_000;
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<string, int> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Connection> _active = new(StringComparer.Ordinal);
    private readonly List<Connection> _connections = [];
    private readonly List<PcapExchange> _exchanges = [];
    private long _payloadBytes;
    private int _segments;
    private int _work;

    private sealed class Segment
    {
        internal long Start;
        internal required byte[] Data;
        internal DateTime Time;
        internal int Order;
    }

    private sealed class Direction
    {
        internal required string Address;
        internal int Port;
        internal uint Basis, SynSequence;
        internal bool Anchored, Syn, Fin, Reset, Damaged;
        internal long FinAt;
        internal readonly List<Segment> Segments = [];
    }

    private sealed class Connection
    {
        internal required Direction A, B;
        internal bool Ended;
        internal DateTime FirstSeen;
    }

    private sealed class TcpStream
    {
        internal byte[] Data = [];
        internal bool Closed;
        internal readonly List<Segment> Spans = [];

        /// <summary>The earliest (or latest) arrival time of the bytes from start to end.</summary>
        internal DateTime Time(int start, int end, bool first)
        {
            if (Spans.Count == 0)
            {
                return DateTime.MinValue;
            }

            int low = 0, high = Spans.Count;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (Spans[mid].Start + Spans[mid].Data.Length <= start)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            var result = DateTime.MinValue;
            for (var i = low; i < Spans.Count && Spans[i].Start < Math.Max(end, start + 1); i++)
            {
                var value = Spans[i].Time;
                if (value != DateTime.MinValue && (result == DateTime.MinValue || (first ? value < result : value > result)))
                {
                    result = value;
                }
            }

            return result;
        }
    }

    internal PcapDecoder(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
    }

    /// <summary>How many TCP connections the capture held.</summary>
    internal int ConnectionCount => _connections.Count;

    /// <summary>What was skipped or incomplete, and how often.</summary>
    internal IReadOnlyDictionary<string, int> Notes => _notes;

    internal IReadOnlyList<PcapExchange> Read(Stream input)
    {
        new PcapFile(input, Note, _cancellationToken).Read(Packet);
        for (var i = 0; i < _connections.Count; i++)
        {
            try
            {
                DecodeConnection(_connections[i], i + 1);
            }
            catch (InvalidDataException ex)
            {
                Note(ex.Message);
            }
        }

        return _exchanges;
    }

    private void Note(string text)
    {
        _notes.TryGetValue(text, out var count);
        _notes[text] = count + 1;
    }

    private void CheckWork()
    {
        if ((++_work & 1023) == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void Packet(byte[] data, uint link, DateTime time, int scope, bool truncated)
    {
        CheckWork();
        int ip = 0, end = data.Length;
        if (link == 1)
        {
            // Ethernet, with up to four VLAN tags.
            if (end < 14)
            {
                Note("Ethernet frames cut short");
                return;
            }

            var protocol = PcapFile.U16(data, 12, little: false);
            ip = 14;
            var tags = 0;
            while (protocol is 0x8100 or 0x88a8 or 0x9100)
            {
                if (++tags > 4 || end - ip < 4)
                {
                    Note("Ethernet frames with an unsupported VLAN stack");
                    return;
                }

                protocol = PcapFile.U16(data, ip + 2, little: false);
                ip += 4;
            }

            if (protocol is not (0x0800 or 0x86dd))
            {
                Note("Non-IP Ethernet frames skipped");
                return;
            }

            if (ip >= end || data[ip] >> 4 != (protocol == 0x0800 ? 4 : 6))
            {
                Note("Ethernet frames with the wrong IP version");
                return;
            }
        }
        else if (link == 105)
        {
            // 802.11 Wi-Fi, unencrypted data frames only.
            if (end < 24)
            {
                Note("Wi-Fi frames cut short");
                return;
            }

            var control = PcapFile.U16(data, 0, little: true);
            if ((control & 12) != 8)
            {
                Note("Wi-Fi management and control frames skipped");
                return;
            }

            if ((control & 0x4000) != 0)
            {
                Note("Encrypted Wi-Fi frames skipped (capture in monitor mode with decryption, or on the computer itself)");
                return;
            }

            var qos = (control & 0x0080) != 0;
            ip = 24 + ((control & 0x0300) == 0x0300 ? 6 : 0) + (qos ? 2 : 0) + (qos && (control & 0x8000) != 0 ? 4 : 0);
            if (end - ip < 8)
            {
                Note("Wi-Fi frames cut short");
                return;
            }

            if (qos && (data[ip - ((control & 0x8000) != 0 ? 6 : 2)] & 0x80) != 0)
            {
                Note("Aggregated Wi-Fi frames skipped");
                return;
            }

            var snap = data[ip] == 0xaa && data[ip + 1] == 0xaa && data[ip + 2] == 3 && data[ip + 3] == 0 && data[ip + 4] == 0 && (data[ip + 5] == 0 || data[ip + 5] == 0xf8);
            var protocol = PcapFile.U16(data, ip + 6, little: false);
            ip += 8;
            if (!snap || protocol is not (0x0800 or 0x86dd))
            {
                Note("Non-IP Wi-Fi frames skipped");
                return;
            }

            if (ip >= end || data[ip] >> 4 != (protocol == 0x0800 ? 4 : 6))
            {
                Note("Wi-Fi frames with the wrong IP version");
                return;
            }
        }
        else if (link == 113)
        {
            // Linux "cooked" capture (tcpdump -i any).
            if (end < 16)
            {
                Note("Linux cooked frames cut short");
                return;
            }

            var protocol = PcapFile.U16(data, 14, little: false);
            ip = 16;
            if (protocol is not (0x0800 or 0x86dd))
            {
                Note("Non-IP Linux cooked frames skipped");
                return;
            }
        }
        else if (link is not (101 or 12 or 228 or 229))
        {
            Note("Packets of an unsupported link type (" + link.ToString(CultureInfo.InvariantCulture) + ") skipped");
            return;
        }

        if (end - ip < 1)
        {
            Note("IP packets cut short");
            return;
        }

        var version = data[ip] >> 4;
        if ((link == 228 && version != 4) || (link == 229 && version != 6))
        {
            Note("Raw IP packets with the wrong IP version");
            return;
        }

        int tcp, ipEnd;
        byte next;
        string source, destination;
        if (version == 4)
        {
            if (end - ip < 20)
            {
                Note("IPv4 headers cut short");
                return;
            }

            int header = (data[ip] & 15) * 4, length = PcapFile.U16(data, ip + 2, little: false);
            if (header < 20 || header > end - ip || length < header)
            {
                Note("IPv4 packets with an invalid length");
                return;
            }

            if ((PcapFile.U16(data, ip + 6, little: false) & 0x3fff) != 0)
            {
                Note("Fragmented IPv4 packets skipped");
                return;
            }

            source = Address(data, ip + 12, 4);
            destination = Address(data, ip + 16, 4);
            next = data[ip + 9];
            ipEnd = Math.Min(end, ip + length);
            truncated |= ip + length > end;
            tcp = ip + header;
        }
        else if (version == 6)
        {
            if (end - ip < 40)
            {
                Note("IPv6 headers cut short");
                return;
            }

            var length = PcapFile.U16(data, ip + 4, little: false);
            if (length == 0)
            {
                Note("IPv6 jumbograms and empty packets skipped");
                return;
            }

            source = Address(data, ip + 8, 16);
            destination = Address(data, ip + 24, 16);
            next = data[ip + 6];
            ipEnd = Math.Min(end, ip + 40 + length);
            truncated |= ip + 40 + length > end;
            tcp = ip + 40;
            var extensions = 0;
            while (next is 0 or 43 or 60 or 51)
            {
                if (++extensions > 8 || ipEnd - tcp < 2)
                {
                    Note("IPv6 packets with an invalid extension chain");
                    return;
                }

                var size = next == 51 ? (data[tcp + 1] + 2) * 4 : (data[tcp + 1] + 1) * 8;
                if (size > ipEnd - tcp)
                {
                    Note("IPv6 extension headers cut short");
                    return;
                }

                next = data[tcp];
                tcp += size;
            }

            if (next == 44)
            {
                Note("Fragmented IPv6 packets skipped");
                return;
            }

            if (next == 50)
            {
                Note("Encrypted IPsec packets skipped");
                return;
            }
        }
        else
        {
            Note("Packets of an unsupported IP version skipped");
            return;
        }

        if (next != 6)
        {
            // UDP (including QUIC, so HTTP/3) and everything else.
            Note("Non-TCP packets skipped");
            return;
        }

        if (ipEnd - tcp < 20)
        {
            Note("TCP headers cut short");
            return;
        }

        var tcpHeader = (data[tcp + 12] >> 4) * 4;
        if (tcpHeader < 20 || tcpHeader > ipEnd - tcp)
        {
            Note("TCP packets with an invalid header length");
            return;
        }

        int sourcePort = PcapFile.U16(data, tcp, little: false), destinationPort = PcapFile.U16(data, tcp + 2, little: false);
        var sequence = PcapFile.U32(data, tcp + 4, little: false);
        var flags = data[tcp + 13];
        bool syn = (flags & 2) != 0, ack = (flags & 16) != 0;
        string endpointA = source + ":" + sourcePort, endpointB = destination + ":" + destinationPort;
        var key = scope + "|" + (string.CompareOrdinal(endpointA, endpointB) < 0 ? endpointA + "|" + endpointB : endpointB + "|" + endpointA);
        var found = _active.TryGetValue(key, out var connection);
        var direction = found && connection!.A.Address == source && connection.A.Port == sourcePort ? connection.A : found ? connection!.B : null;

        // A new initial SYN starts a new connection on the same addresses and
        // ports, unless it's a retransmission of the one in flight.
        if (!found || (syn && !ack && (connection!.Ended || !direction!.Syn || direction.SynSequence != sequence)))
        {
            if (_connections.Count >= 10_000)
            {
                throw new InvalidDataException("the capture has more than 10,000 TCP connections");
            }

            connection = new Connection
            {
                A = new Direction { Address = source, Port = sourcePort },
                B = new Direction { Address = destination, Port = destinationPort },
                FirstSeen = time,
            };
            _active[key] = connection;
            _connections.Add(connection);
            direction = connection.A;
        }

        if (syn)
        {
            if (direction!.Syn && direction.SynSequence != sequence)
            {
                direction.Damaged = true;
            }

            direction.Syn = true;
            direction.SynSequence = sequence;
        }

        int payloadStart = tcp + tcpHeader, payloadLength = ipEnd - payloadStart;
        var dataSequence = unchecked(sequence + (syn ? 1u : 0u));
        if (!direction!.Anchored && (syn || payloadLength > 0 || (flags & 1) != 0))
        {
            direction.Anchored = true;
            direction.Basis = dataSequence;
        }

        long position = unchecked((int)(dataSequence - direction.Basis));
        direction.Damaged |= truncated;
        if (payloadLength > 0)
        {
            _payloadBytes += payloadLength;
            if (_payloadBytes > 128L * 1024 * 1024 || ++_segments > 250_000)
            {
                throw new InvalidDataException("the capture holds more TCP data than VioLens can import (128 MB)");
            }

            direction.Segments.Add(new Segment
            {
                Start = position,
                Data = data.AsSpan(payloadStart, payloadLength).ToArray(),
                Time = time,
                Order = _segments,
            });
        }

        if ((flags & 1) != 0)
        {
            var finAt = position + payloadLength;
            if (direction.Fin && direction.FinAt != finAt)
            {
                direction.Damaged = true;
            }

            direction.Fin = true;
            direction.FinAt = finAt;
        }

        if ((flags & 4) != 0)
        {
            direction.Reset = true;
            connection!.Ended = true;
        }

        if (connection!.A.Fin && connection.B.Fin)
        {
            connection.Ended = true;
        }
    }

    private static string Address(byte[] data, int offset, int length) =>
        new IPAddress(data.AsSpan(offset, length)).ToString();

    private TcpStream Assemble(Direction direction)
    {
        var stream = new TcpStream();
        direction.Segments.Sort((a, b) =>
        {
            var compare = a.Start.CompareTo(b.Start);
            return compare == 0 ? a.Order.CompareTo(b.Order) : compare;
        });
        long origin = direction.Syn
            ? unchecked((int)(unchecked(direction.SynSequence + 1) - direction.Basis))
            : direction.Segments.Count > 0 ? direction.Segments[0].Start : 0;
        var cursor = origin;
        var gap = false;
        using (var bytes = new MemoryStream())
        {
            foreach (var segment in direction.Segments)
            {
                CheckWork();
                if (segment.Start < origin)
                {
                    throw new InvalidDataException("Connections with data before their SYN skipped");
                }

                if (segment.Start > cursor)
                {
                    gap = true;
                    break;
                }

                var overlap = (int)Math.Min(segment.Data.Length, cursor - segment.Start);
                var buffer = bytes.GetBuffer();
                if (!buffer.AsSpan((int)(segment.Start - origin), overlap).SequenceEqual(segment.Data.AsSpan(0, overlap)))
                {
                    throw new InvalidDataException("Connections with conflicting TCP retransmissions skipped");
                }

                var count = segment.Data.Length - overlap;
                if (count > 0)
                {
                    if (bytes.Length + count > MaxStream)
                    {
                        throw new InvalidDataException("Connections carrying more than 32 MB one way skipped");
                    }

                    // Only newly accepted bytes are kept: a retransmission
                    // can't change the original bytes or when they arrived.
                    var accepted = segment.Data.AsSpan(overlap, count).ToArray();
                    stream.Spans.Add(new Segment { Start = cursor - origin, Data = accepted, Time = segment.Time });
                    bytes.Write(accepted);
                    cursor += count;
                }
            }

            stream.Data = bytes.ToArray();
        }

        if (gap || (direction.Fin && direction.FinAt > cursor))
        {
            Note("TCP streams with missing packets (only the part before the gap kept)");
        }

        if (direction.Damaged)
        {
            Note("TCP streams cut short or inconsistent");
        }

        if (direction.Fin && direction.FinAt < cursor)
        {
            throw new InvalidDataException("Connections with data after their FIN skipped");
        }

        stream.Closed = direction.Fin && direction.FinAt == cursor && !gap && !direction.Damaged && !direction.Reset;
        return stream;
    }

    private void DecodeConnection(Connection connection, int number)
    {
        TcpStream a = Assemble(connection.A), b = Assemble(connection.B);
        bool aRequest = IsRequest(a.Data), bRequest = IsRequest(b.Data);
        if (aRequest == bRequest)
        {
            if (a.Data.Length == 0 && b.Data.Length == 0)
            {
                return;
            }

            if (IsTls(a.Data) || IsTls(b.Data))
            {
                Note(PcapReader.TlsSkippedNote);
            }
            else if (Starts(a.Data, "PRI * HTTP/2.0") || Starts(b.Data, "PRI * HTTP/2.0"))
            {
                Note("Plain-text HTTP/2 connections skipped");
            }
            else
            {
                Note("Connections that aren't HTTP, or that start mid-exchange, skipped");
            }

            return;
        }

        TcpStream requests = aRequest ? a : b, responses = aRequest ? b : a;
        Direction client = aRequest ? connection.A : connection.B, server = aRequest ? connection.B : connection.A;
        int requestOffset = 0, responseOffset = 0;
        while (requestOffset < requests.Data.Length)
        {
            CheckWork();
            var request = ParseMessage(requests, requestOffset, isRequest: true, null);
            if (request is null)
            {
                Note("Requests whose headers weren't all captured skipped");
                break;
            }

            PcapMessage? response = null;
            var informational = new StringBuilder();
            var interims = 0;
            while (responseOffset < responses.Data.Length)
            {
                response = ParseMessage(responses, responseOffset, isRequest: false, request.Method);
                if (response is null || response.Status is < 100 or >= 200 or 101)
                {
                    break;
                }

                if (++interims > 100)
                {
                    throw new InvalidDataException("Connections with more than 100 interim responses skipped");
                }

                if (informational.Length > 0)
                {
                    informational.Append("; ");
                }

                informational.Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(response.Reason);
                responseOffset = response.End;
                response = null;
            }

            if (_exchanges.Count >= MaxExchanges)
            {
                throw new InvalidDataException("the capture has more than 100,000 HTTP exchanges");
            }

            var exchange = new PcapExchange
            {
                Request = request,
                Response = response,
                ClientAddress = client.Address,
                ClientPort = client.Port,
                ServerAddress = server.Address,
                ServerPort = server.Port,
                Informational = informational.ToString(),
                ConnectionNumber = number,
                ConnectionStart = connection.FirstSeen,
                RequestStart = requests.Time(request.Start, request.BodyStart, first: true),
                RequestEnd = requests.Time(request.Start, request.End, first: false),
                ResponseStart = response is null ? DateTime.MinValue : responses.Time(response.Start, response.BodyStart, first: true),
                ResponseEnd = response is null ? DateTime.MinValue : responses.Time(response.Start, response.End, first: false),
            };
            _exchanges.Add(exchange);
            if (!request.Complete)
            {
                Note("Requests whose body wasn't all captured (imported as far as it goes)");
            }

            if (response is null)
            {
                Note("Requests with no response captured (imported without one)");
            }
            else if (!response.Complete)
            {
                Note("Responses whose body wasn't all captured (imported as far as it goes)");
            }

            requestOffset = request.End;
            if (response is not null)
            {
                responseOffset = response.End;
            }

            if (response is not null && (response.Status == 101 || (request.Method == "CONNECT" && response.Status is >= 200 and < 300)))
            {
                Note("Tunnels and upgraded connections (WebSocket) not followed past the handshake");
                break;
            }

            if (!request.Complete || response is { Complete: false })
            {
                break;
            }

            // A missing response can't be looked for further on: that could
            // pair a later response with the wrong request.
            if (response is null)
            {
                responseOffset = responses.Data.Length;
            }
        }

        if (responseOffset < responses.Data.Length)
        {
            Note("Server data with no request to match skipped");
        }
    }

    private PcapMessage? ParseMessage(TcpStream stream, int offset, bool isRequest, string? requestMethod)
    {
        var data = stream.Data;
        var headerEnd = Find(data, offset, data.Length, headers: true, MaxHeaders);
        if (headerEnd < 0)
        {
            return null;
        }

        var bodyStart = headerEnd + 4;
        var lines = Latin1.GetString(data, offset, headerEnd - offset).Split("\r\n");
        ValidateValue(lines[0]);
        var message = new PcapMessage { Start = offset, BodyStart = bodyStart, Complete = true };
        if (isRequest)
        {
            var words = lines[0].Split(' ');
            if (words.Length != 3 || !Token(words[0]) || !IsVersion(words[2]) || words[1].Length == 0)
            {
                throw new InvalidDataException("Connections with an invalid HTTP request line skipped");
            }

            foreach (var c in words[1])
            {
                if (c <= 32 || c == 127)
                {
                    throw new InvalidDataException("Connections with an invalid HTTP request target skipped");
                }
            }

            message.Method = words[0];
            message.Target = words[1];
            message.Version = words[2];
            ValidateTarget(message.Method, message.Target);
        }
        else
        {
            var words = lines[0].Split(' ', 3);
            if (words.Length < 2 || !IsVersion(words[0]) || words[1].Length != 3 ||
                !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) || status is < 100 or > 599)
            {
                throw new InvalidDataException("Connections with an invalid HTTP status line skipped");
            }

            message.Version = words[0];
            message.Status = status;
            message.Reason = words.Length > 2 ? words[2] : string.Empty;
        }

        long contentLength = -1;
        var encodings = new List<string>();
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0 || !Token(lines[i][..colon]))
            {
                throw new InvalidDataException("Connections with an invalid or folded HTTP header skipped");
            }

            string name = lines[i][..colon], value = lines[i][(colon + 1)..].Trim(' ', '\t');
            ValidateValue(value);
            message.Headers.Add((name, value));
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in value.Split(','))
                {
                    if (!long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > MaxStream)
                    {
                        throw new InvalidDataException("Connections with an invalid or oversized Content-Length skipped");
                    }

                    if (contentLength >= 0 && contentLength != length)
                    {
                        throw new InvalidDataException("Connections with conflicting Content-Length headers skipped");
                    }

                    contentLength = length;
                }
            }

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in value.Split(','))
                {
                    var coding = part.Trim().ToLowerInvariant();
                    if (!Token(coding))
                    {
                        throw new InvalidDataException("Connections with an unsupported transfer coding skipped");
                    }

                    encodings.Add(coding);
                }
            }
        }

        if (encodings.Count > 0 && contentLength >= 0)
        {
            throw new InvalidDataException("Connections with both Transfer-Encoding and Content-Length skipped");
        }

        var chunked = encodings.IndexOf("chunked");
        if (chunked >= 0 && (chunked != encodings.Count - 1 || encodings.LastIndexOf("chunked") != chunked))
        {
            throw new InvalidDataException("Connections with chunked encoding in the wrong place skipped");
        }

        var noBody = !isRequest && (requestMethod == "HEAD" || message.Status < 200 || message.Status is 204 or 304 ||
            (requestMethod == "CONNECT" && message.Status is >= 200 and < 300));
        if (noBody)
        {
            message.End = bodyStart;
        }
        else if (chunked >= 0)
        {
            var end = ChunkEnd(data, bodyStart, out var body);
            message.Complete = end >= 0;
            message.End = end < 0 ? data.Length : end;
            message.Body = body;
            return message;
        }
        else if (encodings.Count > 0)
        {
            if (isRequest)
            {
                throw new InvalidDataException("Connections with a request transfer coding other than chunked skipped");
            }

            message.End = data.Length;
            message.Complete = stream.Closed;
        }
        else if (contentLength >= 0)
        {
            message.Complete = contentLength <= data.Length - bodyStart;
            message.End = bodyStart + (int)Math.Min(contentLength, data.Length - bodyStart);
        }
        else if (isRequest)
        {
            message.End = bodyStart;
        }
        else
        {
            // No framing: the body runs until the server closes the connection.
            message.End = data.Length;
            message.Complete = stream.Closed;
        }

        message.Body = data.AsSpan(bodyStart, message.End - bodyStart).ToArray();
        return message;
    }

    /// <summary>
    /// Where a chunked body ends (after its trailers), or -1 if the capture
    /// ends first; <paramref name="body"/> gets the data of every chunk read.
    /// </summary>
    private int ChunkEnd(byte[] data, int offset, out byte[] body)
    {
        using var decoded = new MemoryStream();
        try
        {
            while (offset < data.Length)
            {
                CheckWork();
                var line = Find(data, offset, data.Length, headers: false, 8192);
                if (line < 0)
                {
                    return -1;
                }

                var size = Latin1.GetString(data, offset, line - offset);
                var extension = size.IndexOf(';');
                if (extension >= 0)
                {
                    ValidateValue(size[extension..]);
                    size = size[..extension];
                }

                if (size.Length is 0 or > 8 || !uint.TryParse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var count) || count > MaxStream)
                {
                    throw new InvalidDataException("Connections with an invalid or oversized chunk skipped");
                }

                offset = line + 2;
                if (count == 0)
                {
                    var trailersStart = offset;
                    while (true)
                    {
                        var trailer = Find(data, offset, data.Length, headers: false, MaxHeaders - (offset - trailersStart));
                        if (trailer < 0)
                        {
                            return -1;
                        }

                        if (trailer == offset)
                        {
                            return trailer + 2;
                        }

                        var field = Latin1.GetString(data, offset, trailer - offset);
                        var colon = field.IndexOf(':');
                        if (colon <= 0 || !Token(field[..colon]))
                        {
                            throw new InvalidDataException("Connections with an invalid chunk trailer skipped");
                        }

                        ValidateValue(field[(colon + 1)..]);
                        offset = trailer + 2;
                    }
                }

                if ((long)count + 2 > data.Length - offset)
                {
                    // The capture ends inside this chunk: keep what arrived.
                    decoded.Write(data, offset, Math.Min((int)count, data.Length - offset));
                    return -1;
                }

                decoded.Write(data, offset, (int)count);
                offset += (int)count;
                if (data[offset] != 13 || data[offset + 1] != 10)
                {
                    throw new InvalidDataException("Connections with an invalid chunk ending skipped");
                }

                offset += 2;
            }

            return -1;
        }
        finally
        {
            body = decoded.ToArray();
        }
    }

    /// <summary>The next CRLF (or, for headers, CRLFCRLF) within maximum bytes; -1 if the data ends first.</summary>
    private int Find(byte[] data, int start, int end, bool headers, int maximum)
    {
        var width = headers ? 4 : 2;
        var limit = (int)Math.Min(end, (long)start + Math.Max(0, maximum));
        var pattern = headers ? "\r\n\r\n"u8 : "\r\n"u8;
        if (limit - start >= width)
        {
            var found = data.AsSpan(start, limit - start).IndexOf(pattern);
            if (found >= 0)
            {
                _work += found >> 4;
                CheckWork();
                return start + found;
            }
        }

        if (end - start >= maximum)
        {
            throw new InvalidDataException("Connections with headers or lines over the size limit skipped");
        }

        return -1;
    }

    private static bool IsRequest(byte[] data)
    {
        var end = Array.IndexOf(data, (byte)13, 0, Math.Min(data.Length, 8192));
        if (end < 0)
        {
            return false;
        }

        var words = Latin1.GetString(data, 0, end).Split(' ');
        return words.Length == 3 && Token(words[0]) && IsVersion(words[2]);
    }

    private static bool IsVersion(string value) => value is "HTTP/1.0" or "HTTP/1.1";

    private static bool IsTls(byte[] data) => data.Length >= 3 && data[0] >= 20 && data[0] <= 23 && data[1] == 3;

    private static bool Starts(byte[] data, string value) =>
        data.Length >= value.Length && Latin1.GetString(data, 0, value.Length) == value;

    private static bool Token(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateTarget(string method, string target)
    {
        Uri? uri;
        if (method == "CONNECT")
        {
            var colon = target.LastIndexOf(':');
            if (colon < 1 || !int.TryParse(target[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535 ||
                target.IndexOfAny(['/', '?', '#']) >= 0 || !Uri.TryCreate("http://" + target + "/", UriKind.Absolute, out uri) || uri.UserInfo.Length != 0)
            {
                throw new InvalidDataException("Connections with an invalid CONNECT target skipped");
            }

            return;
        }

        if (target[0] == '/' || target == "*")
        {
            return;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidDataException("Connections with an unsupported request target skipped");
        }
    }

    private static void ValidateValue(string value)
    {
        foreach (var c in value)
        {
            if ((c < 32 && c != '\t') || c == 127)
            {
                throw new InvalidDataException("Connections with a control character in a header skipped");
            }
        }
    }
}
