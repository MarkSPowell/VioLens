using System.Buffers.Binary;
using System.Text;
using Violens.ProxyCore.Http2;
using Xunit;
using static Violens.ProxyCore.Tests.Http2.Http2TestClient;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary><see cref="Http2Connection"/> as client and server, talking to itself.</summary>
public class Http2ConnectionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task CarriesARequestAndResponseWithBodiesAndTrailers()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        var (client, socket) = await ConnectAsync(server.Port);
        using var _ = socket;
        await using var __ = client;

        var stream = await client.OpenStreamAsync(Request("POST", "/echo", server.Port), endStream: false, CancellationToken.None);
        await stream.WriteDataAsync("hello, "u8.ToArray(), endStream: false, CancellationToken.None);
        await stream.WriteDataAsync("world"u8.ToArray(), endStream: true, CancellationToken.None);

        var response = await stream.ReadHeadersAsync(CancellationToken.None).WaitAsync(Wait);
        var body = await stream.ReadAllDataAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Contains((":status", "200"), response);
        Assert.Contains(("x-path", "/echo"), response);
        Assert.Equal("hello, world", Text(body));
        Assert.Equal([("x-length", "12")], stream.Trailers);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task FlowControlCarriesBodiesMuchLargerThanTheWindows()
    {
        // Default-sized windows (64 KB) on both sides, for a 5 MB body each way.
        var small = new Http2Settings { InitialWindowSize = 65535, ConnectionWindowSize = 65535 };
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync, small);
        var (client, socket) = await ConnectAsync(server.Port, small);
        using var _ = socket;
        await using var __ = client;

        var payload = new byte[5 * 1024 * 1024];
        new Random(1).NextBytes(payload);
        var stream = await client.OpenStreamAsync(Request("POST", "/big", server.Port), endStream: false, CancellationToken.None);
        var sending = stream.WriteDataAsync(payload, endStream: true, CancellationToken.None);

        await stream.ReadHeadersAsync(CancellationToken.None).WaitAsync(Wait);
        var echoed = await stream.ReadAllDataAsync(CancellationToken.None).WaitAsync(Wait);
        await sending.WaitAsync(Wait);

        Assert.Equal(payload, echoed);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task AStreamThatIsntReadDoesntHoldUpTheOthers()
    {
        await using var server = new Http2TestServer(async stream =>
        {
            var request = await stream.ReadHeadersAsync(CancellationToken.None);
            var path = request.First(h => h.Name == ":path").Value;
            await stream.WriteHeadersAsync([(":status", "200")], endStream: false, CancellationToken.None);
            await stream.WriteDataAsync(new byte[path == "/big" ? 4 * 1024 * 1024 : 10], endStream: true, CancellationToken.None);
        });
        var (client, socket) = await ConnectAsync(server.Port, new Http2Settings { InitialWindowSize = 65535 });
        using var _ = socket;
        await using var __ = client;

        // Nobody reads /big's body, so it stalls once its 64 KB window is full.
        var big = await client.OpenStreamAsync(Request("GET", "/big", server.Port), endStream: true, CancellationToken.None);
        await big.ReadHeadersAsync(CancellationToken.None).WaitAsync(Wait);

        var small = await client.OpenStreamAsync(Request("GET", "/small", server.Port), endStream: true, CancellationToken.None);
        await small.ReadHeadersAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(10, (await small.ReadAllDataAsync(CancellationToken.None).WaitAsync(Wait)).Length);

        // And /big still completes once it's read.
        Assert.Equal(4 * 1024 * 1024, (await big.ReadAllDataAsync(CancellationToken.None).WaitAsync(Wait)).Length);
    }

    [Fact]
    public async Task ManyStreamsAtOnceStayWithinTheServersLimit()
    {
        var active = 0;
        var peak = 0;
        await using var server = new Http2TestServer(
            async stream =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref peak, now);
                try
                {
                    await Http2TestServer.EchoAsync(stream);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            new Http2Settings { MaxConcurrentStreams = 10 });
        var (client, socket) = await ConnectAsync(server.Port);
        using var _ = socket;
        await using var __ = client;

        var requests = Enumerable.Range(0, 200).Select(async i =>
        {
            var stream = await client.OpenStreamAsync(Request("POST", $"/{i}", server.Port), endStream: false, CancellationToken.None);
            await stream.WriteDataAsync(Encoding.ASCII.GetBytes($"request {i}"), endStream: true, CancellationToken.None);
            await stream.ReadHeadersAsync(CancellationToken.None);
            return Text(await stream.ReadAllDataAsync(CancellationToken.None));
        });

        var bodies = await Task.WhenAll(requests).WaitAsync(Wait);

        Assert.Equal(Enumerable.Range(0, 200).Select(i => $"request {i}"), bodies);
        Assert.InRange(peak, 1, 10);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task AResetReachesTheOtherSide()
    {
        var serverSaw = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new Http2TestServer(async stream =>
        {
            await stream.ReadHeadersAsync(CancellationToken.None);
            try
            {
                await stream.ReadAllDataAsync(CancellationToken.None);
                serverSaw.SetException(new InvalidOperationException("The body ended normally."));
            }
            catch (Exception ex)
            {
                serverSaw.SetResult(ex);
            }
        });
        var (client, socket) = await ConnectAsync(server.Port);
        using var _ = socket;
        await using var __ = client;

        var stream = await client.OpenStreamAsync(Request("POST", "/", server.Port), endStream: false, CancellationToken.None);
        await stream.WriteDataAsync("partial"u8.ToArray(), endStream: false, CancellationToken.None);
        await stream.ResetAsync(Http2ErrorCode.Cancel);

        var seen = Assert.IsType<Http2StreamClosedException>(await serverSaw.Task.WaitAsync(Wait));
        Assert.Equal(Http2ErrorCode.Cancel, seen.Code);
        await Assert.ThrowsAsync<Http2StreamClosedException>(() => stream.WriteDataAsync("more"u8.ToArray(), endStream: true, CancellationToken.None));
    }

    [Fact]
    public async Task AfterGoAwayTheClientOpensNoMoreStreams()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        var (client, socket) = await ConnectAsync(server.Port);
        using var _ = socket;
        await using var __ = client;

        var first = await client.OpenStreamAsync(Request("GET", "/", server.Port), endStream: true, CancellationToken.None);
        await first.ReadHeadersAsync(CancellationToken.None).WaitAsync(Wait);
        await server.Connections.Single().GoAwayAsync(CancellationToken.None);

        await WaitUntilAsync(async () =>
        {
            try
            {
                var stream = await client.OpenStreamAsync(Request("GET", "/", server.Port), endStream: true, CancellationToken.None);
                await stream.ReadHeadersAsync(CancellationToken.None);
                return false;
            }
            catch (Http2StreamClosedException ex)
            {
                return ex.Code == Http2ErrorCode.RefusedStream;
            }
        });
    }

    [Fact]
    public async Task AnswersPingWithTheSameData()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port);

        await raw.WriteAsync(Http2FrameCodec.Ping("12345678"u8, ack: false));
        var pong = await raw.ReadUntilAsync(f => f.Type == Http2FrameType.Ping);

        Assert.NotNull(pong);
        Assert.True(pong.Has(Http2FrameFlags.Ack));
        Assert.Equal("12345678"u8.ToArray(), pong.Payload);
    }

    [Fact]
    public async Task AssemblesHeadersSplitAcrossContinuationFrames()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port);

        var block = raw.Encode([.. RawHttp2Client.Get("/split"), ("x-long", new string('a', 300))]);
        await raw.WriteFrameAsync(Http2FrameType.Headers, Http2FrameFlags.EndStream, 1, block[..10]);
        await raw.WriteFrameAsync(Http2FrameType.Continuation, Http2FrameFlags.None, 1, block[10..100]);
        await raw.WriteFrameAsync(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 1, block[100..]);

        var response = await raw.ReadUntilAsync(f => f.Type == Http2FrameType.Headers && f.StreamId == 1);
        Assert.NotNull(response);
    }

    [Theory]
    [MemberData(nameof(ConnectionErrors))]
    public async Task AnswersConnectionErrorsWithGoAway(string _, byte[] frames, uint expected)
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port);

        await raw.WriteAsync(frames);

        Assert.Equal((Http2ErrorCode)expected, await raw.ReadGoAwayAsync());
    }

    public static TheoryData<string, byte[], uint> ConnectionErrors()
    {
        var headers = new Violens.ProxyCore.Http2.Hpack.HpackEncoder().Encode(RawHttp2Client.Get());
        return new()
        {
            { "DATA on stream 0", Http2FrameCodec.Build(Http2FrameType.Data, Http2FrameFlags.None, 0, [1]), (uint)Http2ErrorCode.ProtocolError },
            { "WINDOW_UPDATE of 0", Http2FrameCodec.WindowUpdate(0, 0), (uint)Http2ErrorCode.ProtocolError },
            { "SETTINGS on a stream", Http2FrameCodec.Build(Http2FrameType.Settings, Http2FrameFlags.None, 1, []), (uint)Http2ErrorCode.ProtocolError },
            { "SETTINGS of 5 octets", Http2FrameCodec.Build(Http2FrameType.Settings, Http2FrameFlags.None, 0, [0, 1, 0, 0, 0]), (uint)Http2ErrorCode.FrameSizeError },
            { "PING of 7 octets", Http2FrameCodec.Build(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[7]), (uint)Http2ErrorCode.FrameSizeError },
            { "frame over the size limit", Http2FrameCodec.Build(Http2FrameType.Data, Http2FrameFlags.None, 1, new byte[20000]), (uint)Http2ErrorCode.FrameSizeError },
            { "even stream id", Http2FrameCodec.Headers(2, headers, endStream: true, 16384), (uint)Http2ErrorCode.ProtocolError },
            { "CONTINUATION alone", Http2FrameCodec.Build(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 1, headers), (uint)Http2ErrorCode.ProtocolError },
            { "PUSH_PROMISE", Http2FrameCodec.Build(Http2FrameType.PushPromise, Http2FrameFlags.EndHeaders, 1, new byte[4]), (uint)Http2ErrorCode.ProtocolError },
            { "broken HPACK", Http2FrameCodec.Headers(1, [0xbe], endStream: true, 16384), (uint)Http2ErrorCode.CompressionError },
            { "SETTINGS_ENABLE_PUSH of 2", Http2FrameCodec.Settings([(Http2SettingId.EnablePush, 2)]), (uint)Http2ErrorCode.ProtocolError },
            { "SETTINGS_INITIAL_WINDOW_SIZE too large", Http2FrameCodec.Settings([(Http2SettingId.InitialWindowSize, 1u << 31)]), (uint)Http2ErrorCode.FlowControlError },
            { "connection window overflow", Http2FrameCodec.WindowUpdate(0, int.MaxValue), (uint)Http2ErrorCode.FlowControlError },
            { "RST_STREAM on an idle stream", Http2FrameCodec.RstStream(9, Http2ErrorCode.Cancel), (uint)Http2ErrorCode.ProtocolError },
        };
    }

    [Fact]
    public async Task TheFirstFrameMustBeSettings()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port, sendSettings: false);

        await raw.WriteAsync(Http2FrameCodec.Ping("12345678"u8, ack: false));

        Assert.Equal(Http2ErrorCode.ProtocolError, await raw.ReadGoAwayAsync());
    }

    [Fact]
    public async Task HeadersOnAClosedStreamAreAConnectionError()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port);
        await raw.WriteAsync(Http2FrameCodec.Headers(1, raw.Encode(RawHttp2Client.Get()), endStream: true, 16384));
        await raw.ReadUntilAsync(f => f.Type == Http2FrameType.Data && f.Has(Http2FrameFlags.EndStream) || f.Type == Http2FrameType.Headers && f.Has(Http2FrameFlags.EndStream));

        await raw.WriteAsync(Http2FrameCodec.Headers(1, raw.Encode(RawHttp2Client.Get()), endStream: true, 16384));

        Assert.Equal(Http2ErrorCode.StreamClosed, await raw.ReadGoAwayAsync());
    }

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task ResetsMalformedRequests(string _, (string, string)[] headers, byte[]? body)
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port);

        await raw.WriteAsync(Http2FrameCodec.Headers(1, raw.Encode(headers), endStream: body is null, 16384));
        if (body is not null)
        {
            await raw.WriteFrameAsync(Http2FrameType.Data, Http2FrameFlags.EndStream, 1, body);
        }

        Assert.Equal(Http2ErrorCode.ProtocolError, await raw.ReadResetAsync(1));
    }

    public static TheoryData<string, (string, string)[], byte[]?> MalformedRequests() => new()
    {
        { "upper-case name", [.. RawHttp2Client.Get(), ("X-Upper", "1")], null },
        { "connection header", [.. RawHttp2Client.Get(), ("connection", "keep-alive")], null },
        { "te other than trailers", [.. RawHttp2Client.Get(), ("te", "gzip")], null },
        { "no :path", [(":method", "GET"), (":scheme", "https"), (":authority", "example.test")], null },
        { "empty :path", [(":method", "GET"), (":scheme", "https"), (":path", ""), (":authority", "example.test")], null },
        { "duplicate :method", [(":method", "GET"), .. RawHttp2Client.Get()], null },
        { "pseudo-header after a field", [(":method", "GET"), (":scheme", "https"), ("accept", "*/*"), (":path", "/")], null },
        { "response pseudo-header", [.. RawHttp2Client.Get(), (":status", "200")], null },
        { "unknown pseudo-header", [(":method", "GET"), (":scheme", "https"), (":path", "/"), (":foo", "bar")], null },
        { "content-length bigger than the body", [(":method", "POST"), (":scheme", "https"), (":path", "/"), ("content-length", "10")], "short"u8.ToArray() },
        { "content-length smaller than the body", [(":method", "POST"), (":scheme", "https"), (":path", "/"), ("content-length", "2")], "longer"u8.ToArray() },
    };

    [Fact]
    public async Task ResetsAStreamThatDependsOnItself()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port);

        var priority = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(priority, 1);
        await raw.WriteFrameAsync(Http2FrameType.Priority, Http2FrameFlags.None, 1, priority);

        Assert.Equal(Http2ErrorCode.ProtocolError, await raw.ReadResetAsync(1));
    }

    [Fact]
    public async Task RefusesStreamsOverTheLimit()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new Http2TestServer(
            async stream =>
            {
                await stream.ReadHeadersAsync(CancellationToken.None);
                await release.Task;
            },
            new Http2Settings { MaxConcurrentStreams = 1 });
        await using var raw = await RawHttp2Client.ConnectAsync(server.Port);

        await raw.WriteAsync(Http2FrameCodec.Headers(1, raw.Encode(RawHttp2Client.Get()), endStream: true, 16384));
        await raw.WriteAsync(Http2FrameCodec.Headers(3, raw.Encode(RawHttp2Client.Get()), endStream: true, 16384));

        Assert.Equal(Http2ErrorCode.RefusedStream, await raw.ReadResetAsync(3));
        release.SetResult();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20);
        }
    }
}
