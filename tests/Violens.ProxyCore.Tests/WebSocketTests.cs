using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Violens.ProxyCore.WebSockets;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Reading WebSocket messages out of a connection's bytes (<see cref="WebSocketFrameReader"/>), and the listener recording them.</summary>
public class WebSocketTests
{
    private static readonly byte[] Mask = [0x37, 0xFA, 0x21, 0x3D];

    /// <summary>One frame: first byte (FIN, RSV1, opcode), then length, mask and the (masked) payload.</summary>
    private static byte[] Frame(byte first, byte[] payload, byte[]? mask = null)
    {
        var frame = new List<byte> { first };
        var maskBit = mask is null ? (byte)0 : (byte)0x80;
        if (payload.Length < 126)
        {
            frame.Add((byte)(maskBit | payload.Length));
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame.Add((byte)(maskBit | 126));
            frame.Add((byte)(payload.Length >> 8));
            frame.Add((byte)payload.Length);
        }
        else
        {
            frame.Add((byte)(maskBit | 127));
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                frame.Add((byte)((long)payload.Length >> shift));
            }
        }

        if (mask is null)
        {
            frame.AddRange(payload);
        }
        else
        {
            frame.AddRange(mask);
            frame.AddRange(payload.Select((b, i) => (byte)(b ^ mask[i % 4])));
        }

        return [.. frame];
    }

    private static IReadOnlyList<WebSocketMessage> Read(WebSocketDirection direction, WebSocketCompression? compression, params byte[][] chunks)
    {
        var log = new WebSocketLog(DateTimeOffset.Now);
        using var reader = new WebSocketFrameReader(direction, log, compression);
        foreach (var chunk in chunks)
        {
            reader.Feed(chunk);
        }

        Assert.Null(log.Problem);
        return log.Messages;
    }

    [Fact]
    public void ReadsAnUnmaskedTextMessage()
    {
        // RFC 6455 section 5.7: a single-frame unmasked text message.
        var message = Assert.Single(Read(WebSocketDirection.Received, null, [0x81, 0x05, 0x48, 0x65, 0x6C, 0x6C, 0x6F]));

        Assert.Equal(WebSocketMessageKind.Text, message.Kind);
        Assert.Equal(WebSocketDirection.Received, message.Direction);
        Assert.Equal("Hello", message.Text);
        Assert.Equal(5, message.Length);
        Assert.Equal(1, message.Number);
    }

    [Fact]
    public void UnmasksAClientMessage()
    {
        // RFC 6455 section 5.7: the same message, masked as a client sends it.
        var message = Assert.Single(Read(
            WebSocketDirection.Sent, null, [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58]));

        Assert.Equal("Hello", message.Text);
    }

    [Fact]
    public void PutsAFragmentedMessageBackTogether()
    {
        // RFC 6455 section 5.7: "Hel" then "lo", with a ping between them.
        var messages = Read(
            WebSocketDirection.Received,
            null,
            [0x01, 0x03, 0x48, 0x65, 0x6C],
            Frame(0x89, Encoding.ASCII.GetBytes("ping!")),
            [0x80, 0x02, 0x6C, 0x6F]);

        Assert.Equal(2, messages.Count);
        Assert.Equal(WebSocketMessageKind.Ping, messages[0].Kind);
        Assert.Equal("ping!", messages[0].Text);
        Assert.Equal("Hello", messages[1].Text);
        Assert.Equal(5, messages[1].Length);
    }

    [Fact]
    public void FollowsFramesSplitAnywhere()
    {
        // The bytes arrive one at a time, splitting headers, masks and payloads.
        var bytes = Frame(0x82, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), Mask)
            .Concat(Frame(0x81, Encoding.UTF8.GetBytes("ünïcödé"), Mask))
            .ToArray();

        var messages = Read(WebSocketDirection.Sent, null, bytes.Select(b => new[] { b }).ToArray());

        Assert.Equal(2, messages.Count);
        Assert.Equal(WebSocketMessageKind.Binary, messages[0].Kind);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => (byte)i), messages[0].Payload);
        Assert.Equal("ünïcödé", messages[1].Text);
    }

    [Fact]
    public void ReadsTheLongestLengthForm()
    {
        var payload = new byte[70_000];
        payload[^1] = 42;

        var message = Assert.Single(Read(WebSocketDirection.Received, null, Frame(0x82, payload)));

        Assert.Equal(70_000, message.Length);
        Assert.Equal(42, message.Payload[^1]);
    }

    [Fact]
    public void ReadsACloseCodeAndReason()
    {
        var message = Assert.Single(Read(WebSocketDirection.Received, null, Frame(0x88, [0x03, 0xE8, (byte)'b', (byte)'y', (byte)'e'])));

        Assert.Equal(WebSocketMessageKind.Close, message.Kind);
        Assert.Equal(1000, message.CloseCode);
        Assert.Equal("bye", message.CloseReason);
    }

    [Fact]
    public void DecompressesWithContextTakeover()
    {
        // RFC 7692 section 7.2.3.2: "Hello" twice, the second building on the first.
        var compression = WebSocketCompression.FromHeader("permessage-deflate");
        var messages = Read(
            WebSocketDirection.Received,
            compression,
            [0xC1, 0x07, 0xF2, 0x48, 0xCD, 0xC9, 0xC9, 0x07, 0x00],
            [0xC1, 0x05, 0xF2, 0x00, 0x11, 0x00, 0x00]);

        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal("Hello", m.Text));
        Assert.All(messages, m => Assert.True(m.Compressed));
        Assert.Equal(7, messages[0].Length);
        Assert.Equal(5, messages[1].Length);
    }

    [Fact]
    public void DecompressesMaskedClientMessages()
    {
        // Two messages from one deflate stream (zlib, raw, sync flush, tail removed).
        const string json = "{\"op\":\"subscribe\",\"channel\":\"prices\"}";
        var first = Convert.FromHexString("aa56ca2f50b2522a2e4d2a4e2eca4c4a55d2514ace48cccb4bcd018a16146526a7162bd50200");
        var second = Convert.FromHexString("aa26461100");

        var messages = Read(
            WebSocketDirection.Sent,
            WebSocketCompression.FromHeader("permessage-deflate; client_max_window_bits"),
            Frame(0xC1, first, Mask),
            Frame(0xC1, second, Mask));

        Assert.Equal(new[] { json, json }, messages.Select(m => m.Text));
    }

    [Fact]
    public void DecompressesEachMessageAloneWithNoContextTakeover()
    {
        // RFC 7692 section 7.2.3.1: "Hello" compressed on its own, twice.
        var messages = Read(
            WebSocketDirection.Received,
            WebSocketCompression.FromHeader("permessage-deflate; server_no_context_takeover"),
            [0xC1, 0x07, 0xF2, 0x48, 0xCD, 0xC9, 0xC9, 0x07, 0x00],
            [0xC1, 0x07, 0xF2, 0x48, 0xCD, 0xC9, 0xC9, 0x07, 0x00]);

        Assert.All(messages, m => Assert.Equal("Hello", m.Text));
    }

    [Theory]
    [InlineData("permessage-deflate", false, false)]
    [InlineData("permessage-deflate; client_no_context_takeover; server_max_window_bits=10", true, false)]
    [InlineData("x-webkit-deflate-frame, permessage-deflate; server_no_context_takeover", false, true)]
    public void ReadsTheAgreedCompression(string header, bool clientNoContext, bool serverNoContext)
    {
        Assert.Equal(new WebSocketCompression(clientNoContext, serverNoContext), WebSocketCompression.FromHeader(header));
    }

    [Fact]
    public void NoCompressionWithoutPermessageDeflate()
    {
        Assert.Null(WebSocketCompression.FromHeader(null));
        Assert.Null(WebSocketCompression.FromHeader("x-custom"));
    }

    [Fact]
    public void StopsFollowingBytesThatArentFrames()
    {
        var log = new WebSocketLog(DateTimeOffset.Now);
        using var reader = new WebSocketFrameReader(WebSocketDirection.Received, log, null);

        reader.Feed(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n"));

        Assert.True(reader.Failed);
        Assert.NotNull(log.Problem);
    }

    [Fact]
    public void KeepsOnlyTheStartOfAHugeMessage()
    {
        var payload = new byte[WebSocketFrameReader.MaxStoredMessageBytes + 10];

        var message = Assert.Single(Read(WebSocketDirection.Received, null, Frame(0x82, payload)));

        Assert.True(message.Truncated);
        Assert.Equal(WebSocketFrameReader.MaxStoredMessageBytes, message.Payload.Length);
        Assert.Equal(payload.Length, message.Length);
    }

    [Fact]
    public void RecordsOnlySoManyMessagesPerConnection()
    {
        var log = new WebSocketLog(DateTimeOffset.Now);
        for (var i = 0; i < WebSocketLog.MaxStoredMessages + 5; i++)
        {
            log.Add(WebSocketDirection.Received, WebSocketMessageKind.Text, 1, [(byte)'a']);
        }

        Assert.Equal(WebSocketLog.MaxStoredMessages, log.Count);
        Assert.Equal(5, log.NotRecorded);
        Assert.Contains("recorded", log.Problem);
    }

    [Fact]
    public void StopsDecompressingAMessageThatGrowsPastTheLimit()
    {
        // A small message of zeros that inflates past the limit, then a
        // second message that builds on it (context takeover).
        var (zeros, hello) = TwoDeflatedMessages(new byte[WebSocketFrameReader.MaxInflatedMessageBytes + 1024 * 1024], "Hello"u8.ToArray());
        Assert.True(zeros.Length < WebSocketFrameReader.MaxCompressedMessageBytes);

        var messages = Read(
            WebSocketDirection.Received,
            WebSocketCompression.FromHeader("permessage-deflate"),
            Frame(0xC2, zeros),
            Frame(0xC1, hello));

        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].Truncated);
        Assert.Contains("more than", messages[0].Note);
        Assert.Contains("builds on an earlier message", messages[1].Note);
    }

    [Fact]
    public void ANewMessageStillDecompressesAfterOneGrewTooLargeWithNoContextTakeover()
    {
        var (zeros, _) = TwoDeflatedMessages(new byte[WebSocketFrameReader.MaxInflatedMessageBytes + 1024 * 1024], "x"u8.ToArray());
        var messages = Read(
            WebSocketDirection.Received,
            WebSocketCompression.FromHeader("permessage-deflate; server_no_context_takeover"),
            Frame(0xC2, zeros),
            [0xC1, 0x07, 0xF2, 0x48, 0xCD, 0xC9, 0xC9, 0x07, 0x00]);

        Assert.True(messages[0].Truncated);
        Assert.Equal("Hello", messages[1].Text);
    }

    /// <summary>Two messages from one raw deflate stream, each sync-flushed with the 00 00 FF FF tail removed (RFC 7692).</summary>
    private static (byte[] First, byte[] Second) TwoDeflatedMessages(byte[] first, byte[] second)
    {
        using var output = new MemoryStream();
        using var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(first);
        deflate.Flush();
        var a = output.ToArray();
        deflate.Write(second);
        deflate.Flush();
        var b = output.ToArray()[a.Length..];
        return (a[..^4], b[..^4]);
    }

    [Fact]
    public async Task HarKeepsTheMessages()
    {
        var log = new WebSocketLog(DateTimeOffset.Now);
        log.Add(WebSocketDirection.Sent, WebSocketMessageKind.Text, 5, Encoding.UTF8.GetBytes("hello"));
        log.Add(WebSocketDirection.Received, WebSocketMessageKind.Binary, 3, [1, 2, 3]);
        log.Add(WebSocketDirection.Received, WebSocketMessageKind.Ping, 0, []);
        log.Close();

        var store = new SessionStore();
        store.Add(
            "chat.example.test",
            DateTimeOffset.Now,
            new CapturedRequest("GET", "/socket", "HTTP/1.1", [("Host", "chat.example.test"), ("Upgrade", "websocket")], []),
            new CapturedResponse("HTTP/1.1", 101, "Switching Protocols", [("Upgrade", "websocket"), ("Connection", "Upgrade")], []),
            flags: null,
            "https",
            timings: null,
            log);

        using var har = new MemoryStream();
        HarWriter.Write(har, store.Snapshot());

        using (var document = JsonDocument.Parse(har.ToArray()))
        {
            var entry = document.RootElement.GetProperty("log").GetProperty("entries")[0];
            Assert.Equal("websocket", entry.GetProperty("_resourceType").GetString());
            var messages = entry.GetProperty("_webSocketMessages");
            Assert.Equal(2, messages.GetArrayLength()); // text and binary only, as browsers write
            Assert.Equal("send", messages[0].GetProperty("type").GetString());
            Assert.Equal("hello", messages[0].GetProperty("data").GetString());
            Assert.Equal("AQID", messages[1].GetProperty("data").GetString());
        }

        har.Position = 0;
        var imported = new SessionStore();
        await HarReader.ImportAsync(har, imported);
        var back = Assert.Single(imported.Snapshot()).WebSocket;
        Assert.NotNull(back);
        Assert.False(back!.IsOpen);
        Assert.Equal(new[] { "hello", "\u0001\u0002\u0003" }, back.Messages.Select(m => m.Text));
        Assert.Equal(WebSocketMessageKind.Binary, back.Messages[1].Kind);
        Assert.Equal(WebSocketDirection.Received, back.Messages[1].Direction);
    }

    [Fact]
    public void FindLooksInTextMessages()
    {
        var log = new WebSocketLog(DateTimeOffset.Now);
        log.Add(WebSocketDirection.Received, WebSocketMessageKind.Text, 20, Encoding.UTF8.GetBytes("{\"price\": 101.25}"));
        var session = new SessionStore().Add(
            "chat.example.test",
            DateTimeOffset.Now,
            new CapturedRequest("GET", "/socket", "HTTP/1.1", [("Host", "chat.example.test")], []),
            new CapturedResponse("HTTP/1.1", 101, "Switching Protocols", [], []),
            flags: null,
            "https",
            timings: null,
            log);

        bool Finds(SessionSearchOptions options) =>
            SessionSearch.TryCreate(options, out var search, out _) && search!.Matches(session);

        Assert.True(Finds(new SessionSearchOptions("101.25")));
        Assert.False(Finds(new SessionSearchOptions("101.25", Target: SessionSearchTarget.Requests)));
        Assert.False(Finds(new SessionSearchOptions("101.25", Parts: SessionSearchParts.Headers)));
    }

    [Fact]
    public async Task TheListenerRecordsMessagesAndPassesThemThroughUnchanged()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using X509Certificate2 root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // A WebSocket server: answers the handshake, checks the client's
        // frame arrives byte for byte, replies, then closes.
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var clientFrame = Frame(0x81, Encoding.UTF8.GetBytes("hello server"), Mask);
        var serverFrames = Frame(0x81, Encoding.UTF8.GetBytes("hello client")).Concat(Frame(0x88, [0x03, 0xE8])).ToArray();
        var serverTask = Task.Run(async () =>
        {
            using var connection = await server.AcceptTcpClientAsync(timeout.Token);
            var stream = connection.GetStream();
            await ReadHeadAsync(stream, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: x\r\n\r\n"), timeout.Token);
            var received = new byte[clientFrame.Length];
            await stream.ReadExactlyAsync(received, timeout.Token);
            Assert.Equal(clientFrame, received);
            await stream.WriteAsync(serverFrames, timeout.Token);
        });

        var store = new SessionStore();
        var recorded = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SessionAdded += s => recorded.TrySetResult(s);
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{port}/socket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"), timeout.Token);

            Assert.StartsWith("HTTP/1.1 101", await ReadHeadAsync(stream, timeout.Token));
            await stream.WriteAsync(clientFrame, timeout.Token);
            var reply = new byte[serverFrames.Length];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal(serverFrames, reply);
            await serverTask;

            var session = await recorded.Task.WaitAsync(timeout.Token);
            var log = session.WebSocket;
            Assert.NotNull(log);
            while (log!.Count < 3)
            {
                await Task.Delay(20, timeout.Token);
            }

            var messages = log.Messages;
            Assert.Equal("hello server", messages[0].Text);
            Assert.Equal(WebSocketDirection.Sent, messages[0].Direction);
            Assert.Equal("hello client", messages[1].Text);
            Assert.Equal(WebSocketDirection.Received, messages[1].Direction);
            Assert.Equal(1000, messages[2].CloseCode);
        }
        finally
        {
            listener.Stop();
            server.Stop();
        }
    }

    /// <summary>Reads a message head byte by byte, so nothing after it is consumed.</summary>
    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                throw new EndOfStreamException();
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }
}
