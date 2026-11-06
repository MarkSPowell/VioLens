using System.Buffers.Binary;
using System.Net;
using System.Text;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Packet capture import (<see cref="PcapReader"/>), from captures built here packet by packet.</summary>
public class PcapTests
{
    private static readonly DateTime Base = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private const byte Fin = 1, Syn = 2, Rst = 4, PshAck = 24;

    private static byte[] Text(string value) => Encoding.Latin1.GetBytes(value);

    private static readonly byte[] Get = Text("GET /page?q=1 HTTP/1.1\r\nHost: example.test\r\nUser-Agent: test\r\n\r\n");
    private static readonly byte[] Ok = Text("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello");

    // -- Building packets --------------------------------------------------

    /// <summary>A TCP segment in an IPv4 or IPv6 packet, between 10.0.0.1:50000 (or fd00::1) and the server on port 80.</summary>
    private static byte[] Tcp(bool fromClient, uint sequence, byte flags, byte[] payload, bool ipv6 = false, int clientPort = 50000)
    {
        var tcp = new byte[20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, (ushort)(fromClient ? clientPort : 80));
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), (ushort)(fromClient ? 80 : clientPort));
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), sequence);
        tcp[12] = 5 << 4;
        tcp[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(14), 65535);
        payload.CopyTo(tcp, 20);

        var client = IPAddress.Parse(ipv6 ? "fd00::1" : "10.0.0.1").GetAddressBytes();
        var server = IPAddress.Parse(ipv6 ? "fd00::2" : "10.0.0.2").GetAddressBytes();
        var (source, destination) = fromClient ? (client, server) : (server, client);
        if (ipv6)
        {
            var ip6 = new byte[40];
            ip6[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(ip6.AsSpan(4), (ushort)tcp.Length);
            ip6[6] = 6;
            ip6[7] = 64;
            source.CopyTo(ip6, 8);
            destination.CopyTo(ip6, 24);
            return [.. ip6, .. tcp];
        }

        var ip = new byte[20];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)(20 + tcp.Length));
        ip[8] = 64;
        ip[9] = 6;
        source.CopyTo(ip, 12);
        destination.CopyTo(ip, 16);
        return [.. ip, .. tcp];
    }

    private static byte[] Ethernet(byte[] ip, bool vlan = false)
    {
        var type = (ushort)((ip[0] >> 4) == 6 ? 0x86dd : 0x0800);
        var header = new byte[vlan ? 18 : 14];
        if (vlan)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), 0x8100);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), 42);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16), type);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), type);
        }

        return [.. header, .. ip];
    }

    /// <summary>A classic PCAP file; each packet's time is Base plus its milliseconds.</summary>
    private static byte[] Classic(uint link, IEnumerable<(int Ms, byte[] Frame)> packets, bool little = true, bool nano = false)
    {
        using var file = new MemoryStream();
        void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            else BinaryPrimitives.WriteUInt32BigEndian(b, value);
            file.Write(b);
        }

        void U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            else BinaryPrimitives.WriteUInt16BigEndian(b, value);
            file.Write(b);
        }

        U32(nano ? 0xa1b23c4d : 0xa1b2c3d4);
        U16(2);
        U16(4);
        U32(0);
        U32(0);
        U32(65535);
        U32(link);
        foreach (var (ms, frame) in packets)
        {
            var time = Base.AddMilliseconds(ms);
            var seconds = (uint)(time - DateTime.UnixEpoch).TotalSeconds;
            var fraction = (uint)((time - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerSecond);
            U32(seconds);
            U32(nano ? fraction * 100 : fraction / 10);
            U32((uint)frame.Length);
            U32((uint)frame.Length);
            file.Write(frame);
        }

        return file.ToArray();
    }

    /// <summary>A PCAPNG file with one interface and enhanced packet blocks (microsecond times).</summary>
    private static byte[] Ng(uint link, IEnumerable<(int Ms, byte[] Frame)> packets, bool little = true)
    {
        using var file = new MemoryStream();
        byte[] U32(uint value)
        {
            var b = new byte[4];
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            else BinaryPrimitives.WriteUInt32BigEndian(b, value);
            return b;
        }

        byte[] U16(ushort value)
        {
            var b = new byte[2];
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            else BinaryPrimitives.WriteUInt16BigEndian(b, value);
            return b;
        }

        void Block(uint type, byte[] body)
        {
            var length = (uint)(12 + body.Length);
            file.Write(U32(type));
            file.Write(U32(length));
            file.Write(body);
            file.Write(U32(length));
        }

        Block(0x0a0d0d0a, [.. U32(0x1a2b3c4d), .. U16(1), .. U16(0), .. new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 }]);
        Block(1, [.. U16((ushort)link), .. U16(0), .. U32(65535)]);
        foreach (var (ms, frame) in packets)
        {
            var micros = (ulong)((Base.AddMilliseconds(ms) - DateTime.UnixEpoch).Ticks / 10);
            var padded = new byte[(frame.Length + 3) & ~3];
            frame.CopyTo(padded, 0);
            Block(6, [.. U32(0), .. U32((uint)(micros >> 32)), .. U32((uint)micros), .. U32((uint)frame.Length), .. U32((uint)frame.Length), .. padded]);
        }

        return file.ToArray();
    }

    /// <summary>A whole connection on Ethernet: handshake, one request and response, and closing.</summary>
    private static List<(int Ms, byte[] Frame)> Connection(byte[] request, byte[] response, bool ipv6 = false, bool vlan = false)
    {
        byte[] E(byte[] ip) => Ethernet(ip, vlan);
        return
        [
            (0, E(Tcp(true, 1000, Syn, [], ipv6))),
            (10, E(Tcp(false, 5000, Syn | 16, [], ipv6))),
            (20, E(Tcp(true, 1001, PshAck, request, ipv6))),
            (70, E(Tcp(false, 5001, PshAck, response, ipv6))),
            (80, E(Tcp(true, 1001 + (uint)request.Length, Fin | 16, [], ipv6))),
            (90, E(Tcp(false, 5001 + (uint)response.Length, Fin | 16, [], ipv6))),
        ];
    }

    private static async Task<(PcapImportResult Result, IReadOnlyList<Session> Sessions)> Import(byte[] capture)
    {
        var store = new SessionStore();
        var result = await PcapReader.ImportAsync(new MemoryStream(capture), store);
        return (result, store.Snapshot());
    }

    // -- Tests -------------------------------------------------------------

    [Fact]
    public async Task ImportsAnExchangeFromAClassicCapture()
    {
        var (result, sessions) = await Import(Classic(1, Connection(Get, Ok)));

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Connections);
        var session = Assert.Single(sessions);
        Assert.Equal("example.test", session.Host);
        Assert.Equal("http", session.Scheme);
        Assert.Equal("http://example.test/page?q=1", SessionUrl.Of(session));
        Assert.Equal("GET", session.Request.Method);
        Assert.Equal("HTTP/1.1", session.Request.HttpVersion);
        Assert.Equal(200, session.Response.StatusCode);
        Assert.Equal("OK", session.Response.ReasonPhrase);
        Assert.Equal("hello", Encoding.ASCII.GetString(session.Response.Body));
        Assert.Equal("text/plain", ContentDecoder.FindHeader(session.Response.Headers, "Content-Type"));
        Assert.Equal(Base.AddMilliseconds(20), session.StartedAt.UtcDateTime);

        Assert.Equal("10.0.0.1", session.Flags!["x-clientIP"]);
        Assert.Equal("50000", session.Flags["x-clientport"]);
        Assert.Equal("10.0.0.2", session.Flags["x-hostIP"]);
        Assert.False(session.Flags.ContainsKey(PcapReader.IncompleteFlag));

        var timings = session.Timings!;
        Assert.Equal(20, timings.Connect, 3);
        Assert.Equal(50, timings.Wait, 3);
        Assert.False(timings.ConnectionReused);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ReadsClassicByteOrdersAndNanosecondTimes(bool little, bool nano)
    {
        var (_, sessions) = await Import(Classic(1, Connection(Get, Ok), little, nano));

        var session = Assert.Single(sessions);
        Assert.Equal(Base.AddMilliseconds(20), session.StartedAt.UtcDateTime);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsPcapngWithIpv6AndVlanTags(bool little)
    {
        var (result, sessions) = await Import(Ng(1, Connection(Get, Ok, ipv6: true, vlan: true), little));

        Assert.Equal(1, result.Imported);
        var session = Assert.Single(sessions);
        Assert.Equal("fd00::1", session.Flags!["x-clientIP"]);
        Assert.Equal("hello", Encoding.ASCII.GetString(session.Response.Body));
        Assert.Equal(Base.AddMilliseconds(20), session.StartedAt.UtcDateTime);
    }

    [Fact]
    public async Task ReadsRawIpAndLinuxCookedCaptures()
    {
        var raw = Connection(Get, Ok).Select(p => (p.Ms, p.Frame[14..])).ToList();
        var (rawResult, _) = await Import(Classic(101, raw));
        Assert.Equal(1, rawResult.Imported);

        // Linux "cooked" (SLL): a 16-byte header ending in the protocol.
        var cooked = raw.Select(p =>
        {
            var header = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), 0x0800);
            return (p.Ms, (byte[])[.. header, .. p.Item2]);
        }).ToList();
        var (cookedResult, _) = await Import(Classic(113, cooked));
        Assert.Equal(1, cookedResult.Imported);
    }

    [Fact]
    public async Task ReadsUnencryptedWifiFrames()
    {
        static byte[] Wifi(byte[] ip)
        {
            var header = new byte[24 + 8];
            BinaryPrimitives.WriteUInt16LittleEndian(header, 0x0008); // data frame
            header[24] = 0xaa;
            header[25] = 0xaa;
            header[26] = 3;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(30), 0x0800);
            return [.. header, .. ip];
        }

        var packets = Connection(Get, Ok).Select(p => (p.Ms, Wifi(p.Frame[14..]))).ToList();
        var (result, _) = await Import(Classic(105, packets));

        Assert.Equal(1, result.Imported);
    }

    [Fact]
    public async Task ReassemblesOutOfOrderAndRetransmittedSegments()
    {
        var body = string.Concat(Enumerable.Repeat("0123456789", 30));
        var response = Text($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        var first = response[..100];
        var second = response[100..200];
        var third = response[200..];
        List<(int, byte[])> packets =
        [
            (0, Ethernet(Tcp(true, 1001, PshAck, Get))),
            (10, Ethernet(Tcp(false, 5001, PshAck, first))),
            (12, Ethernet(Tcp(false, 5201, PshAck, third))),
            (14, Ethernet(Tcp(false, 5101, PshAck, second))),
            (16, Ethernet(Tcp(false, 5101, PshAck, second))), // retransmission
            (18, Ethernet(Tcp(false, 5001 + (uint)response.Length, Fin | 16, []))),
        ];

        var (_, sessions) = await Import(Classic(1, packets));

        var session = Assert.Single(sessions);
        Assert.Equal(body, Encoding.ASCII.GetString(session.Response.Body));
        Assert.False(session.Flags!.ContainsKey(PcapReader.IncompleteFlag));
    }

    [Fact]
    public async Task RemovesChunkedFramingButKeepsTheHeader()
    {
        var response = Text("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n7;x=y\r\n, world\r\n0\r\nX-Trailer: 1\r\n\r\n");

        var (_, sessions) = await Import(Classic(1, Connection(Get, response)));

        var session = Assert.Single(sessions);
        Assert.Equal("hello, world", Encoding.ASCII.GetString(session.Response.Body));
        Assert.Equal("chunked", ContentDecoder.FindHeader(session.Response.Headers, "Transfer-Encoding"));
    }

    [Fact]
    public async Task SplitsKeepAliveRequestsIntoSessions()
    {
        var second = Text("POST /submit HTTP/1.1\r\nHost: example.test\r\nContent-Length: 3\r\n\r\na=b");
        var created = Text("HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n");
        List<(int, byte[])> packets =
        [
            (0, Ethernet(Tcp(true, 1000, Syn, []))),
            (10, Ethernet(Tcp(true, 1001, PshAck, Get))),
            (20, Ethernet(Tcp(false, 5001, PshAck, Ok))),
            (30, Ethernet(Tcp(true, 1001 + (uint)Get.Length, PshAck, second))),
            (40, Ethernet(Tcp(false, 5001 + (uint)Ok.Length, PshAck, created))),
        ];

        var (_, sessions) = await Import(Classic(1, packets));

        Assert.Equal(2, sessions.Count);
        Assert.Equal("POST", sessions[1].Request.Method);
        Assert.Equal("a=b", Encoding.ASCII.GetString(sessions[1].Request.Body));
        Assert.Equal(201, sessions[1].Response.StatusCode);
        Assert.True(sessions[1].Timings!.ConnectionReused);
        Assert.Equal(sessions[0].Timings!.ConnectionId, sessions[1].Timings!.ConnectionId);
    }

    [Fact]
    public async Task ReadsTheBodyOfAResponseEndedByClosing()
    {
        var response = Text("HTTP/1.0 200 OK\r\n\r\nuntil close");

        var (_, sessions) = await Import(Classic(1, Connection(Get, response)));

        var session = Assert.Single(sessions);
        Assert.Equal("until close", Encoding.ASCII.GetString(session.Response.Body));
        Assert.False(session.Flags!.ContainsKey(PcapReader.IncompleteFlag));
    }

    [Fact]
    public async Task KeepsARequestWithNoResponseWithoutInventingOne()
    {
        var (result, sessions) = await Import(Classic(1, [(0, Ethernet(Tcp(true, 1001, PshAck, Get)))]));

        var session = Assert.Single(sessions);
        Assert.Equal(0, session.Response.StatusCode);
        Assert.Contains("no response", session.Flags![PcapReader.IncompleteFlag]);
        Assert.Contains(result.Notes, note => note.Text.Contains("no response"));
    }

    [Fact]
    public async Task MarksABodyThatWasntAllCaptured()
    {
        var response = Text("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\npartial");
        List<(int, byte[])> packets =
        [
            (0, Ethernet(Tcp(true, 1001, PshAck, Get))),
            (10, Ethernet(Tcp(false, 5001, PshAck, response))),
        ];

        var (_, sessions) = await Import(Classic(1, packets));

        var session = Assert.Single(sessions);
        Assert.Equal("partial", Encoding.ASCII.GetString(session.Response.Body));
        Assert.Contains("response body", session.Flags![PcapReader.IncompleteFlag]);
    }

    [Fact]
    public async Task KeepsOnlyTheDataBeforeAGap()
    {
        var response = Text("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n0123456789");
        List<(int, byte[])> packets =
        [
            (0, Ethernet(Tcp(true, 1001, PshAck, Get))),
            (10, Ethernet(Tcp(false, 5001, PshAck, response[..^5]))),
            (20, Ethernet(Tcp(false, 5001 + (uint)response.Length + 100, PshAck, Text("later")))),
        ];

        var (result, sessions) = await Import(Classic(1, packets));

        var session = Assert.Single(sessions);
        Assert.Equal("01234", Encoding.ASCII.GetString(session.Response.Body));
        Assert.Contains(result.Notes, note => note.Text.Contains("missing packets"));
    }

    [Fact]
    public async Task RecordsInterimResponses()
    {
        var response = Text("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");

        var (_, sessions) = await Import(Classic(1, Connection(Get, response)));

        var session = Assert.Single(sessions);
        Assert.Equal(200, session.Response.StatusCode);
        Assert.Equal("100 Continue", session.Flags!["x-pcap-informational"]);
    }

    [Fact]
    public async Task UsesTheServerAddressWhenThereIsNoHostHeader()
    {
        var request = Text("GET /old HTTP/1.0\r\n\r\n");

        var (_, sessions) = await Import(Classic(1, Connection(request, Ok)));

        var session = Assert.Single(sessions);
        Assert.Equal("10.0.0.2", session.Host);
        Assert.Equal("http://10.0.0.2/old", SessionUrl.Of(session));
    }

    [Fact]
    public async Task StoresAProxyRequestPathOnly()
    {
        var request = Text("GET http://other.test:8080/x HTTP/1.1\r\nHost: other.test:8080\r\n\r\n");

        var (_, sessions) = await Import(Classic(1, Connection(request, Ok)));

        var session = Assert.Single(sessions);
        Assert.Equal("/x", session.Request.Target);
        Assert.Equal("http://other.test:8080/x", SessionUrl.Of(session));
    }

    [Fact]
    public async Task SkipsTlsAndSaysSo()
    {
        var clientHello = new byte[] { 22, 3, 1, 0, 4, 1, 0, 0, 0 };
        var serverHello = new byte[] { 22, 3, 3, 0, 4, 2, 0, 0, 0 };
        List<(int, byte[])> packets =
        [
            (0, Ethernet(Tcp(true, 1001, PshAck, clientHello))),
            (10, Ethernet(Tcp(false, 5001, PshAck, serverHello))),
        ];

        var (result, sessions) = await Import(Classic(1, packets));

        Assert.Empty(sessions);
        Assert.Equal(0, result.Imported);
        var note = Assert.Single(result.Notes);
        Assert.Equal(PcapReader.TlsSkippedNote, note.Text);
    }

    [Fact]
    public async Task SkipsUdpAndOtherConnectionsButKeepsTheRest()
    {
        var udp = Ethernet(Tcp(true, 1, PshAck, Text("dns")));
        udp[14 + 9] = 17; // protocol: UDP
        var packets = Connection(Get, Ok);
        packets.Insert(0, (0, udp));

        var (result, sessions) = await Import(Classic(1, packets));

        Assert.Single(sessions);
        Assert.Contains(result.Notes, note => note.Text.Contains("Non-TCP"));
    }

    [Fact]
    public async Task TellsApartTwoConnectionsOnTheSamePorts()
    {
        var packets = Connection(Get, Ok);
        var again = Connection(Text("GET /second HTTP/1.1\r\nHost: example.test\r\n\r\n"), Ok)
            .Select(p => (p.Ms + 1000, p.Frame));
        packets.AddRange(again);

        var (result, sessions) = await Import(Classic(1, packets));

        Assert.Equal(2, result.Connections);
        Assert.Equal(["/page?q=1", "/second"], sessions.Select(s => s.Request.Target));
    }

    [Fact]
    public async Task RejectsAFileThatIsntACapture()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Text("this is not a packet capture")));
    }

    [Fact]
    public async Task RejectsACaptureThatIsCutShort()
    {
        var capture = Classic(1, Connection(Get, Ok));

        await Assert.ThrowsAsync<InvalidDataException>(() => Import(capture[..^10]));
    }

    [Fact]
    public async Task RejectsAPacketLongerThanItsSnapLength()
    {
        var capture = Classic(1, Connection(Get, Ok));
        BinaryPrimitives.WriteUInt32LittleEndian(capture.AsSpan(16), 10); // snap length

        await Assert.ThrowsAsync<InvalidDataException>(() => Import(capture));
    }

    [Fact]
    public async Task SkipsAConnectionWithMalformedHttp()
    {
        var bad = Text("GET / HTTP/1.1\r\nBad Header Line\r\n\r\n");
        var packets = Connection(bad, Ok);
        packets.AddRange(Connection(Get, Ok).Select(p => (p.Ms + 1000, p.Frame)));

        var (result, sessions) = await Import(Classic(1, packets));

        Assert.Single(sessions);
        Assert.Contains(result.Notes, note => note.Text.Contains("invalid or folded HTTP header"));
    }

    [Fact]
    public async Task SurvivesRandomDamage()
    {
        var capture = Classic(1, Connection(Get, Ok));
        var random = new Random(1234);
        for (var i = 0; i < 300; i++)
        {
            var damaged = (byte[])capture.Clone();
            for (var j = 0; j < 4; j++)
            {
                damaged[random.Next(24, damaged.Length)] = (byte)random.Next(256);
            }

            try
            {
                await Import(damaged);
            }
            catch (InvalidDataException)
            {
                // A clean refusal is fine; anything else isn't.
            }
        }
    }

    [Fact]
    public async Task ImportedSessionsSaveToSaz()
    {
        var (_, sessions) = await Import(Classic(1, Connection(Get, Ok)));

        using var buffer = new MemoryStream();
        SazWriter.Write(buffer, sessions);
        buffer.Position = 0;
        var store = new SessionStore();
        var result = await SazReader.ImportAsync(buffer, store);

        Assert.Equal(1, result.Imported);
        var session = Assert.Single(store.Snapshot());
        Assert.Equal("http://example.test/page?q=1", SessionUrl.Of(session));
        Assert.Equal("10.0.0.1", session.Flags!["x-clientIP"]);
    }
}
