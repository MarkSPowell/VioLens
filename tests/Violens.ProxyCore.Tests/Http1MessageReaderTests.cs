using System.Text;
using Violens.ProxyCore.Http;
using Xunit;

namespace Violens.ProxyCore.Tests;

public class Http1MessageReaderTests
{
    [Fact]
    public async Task ReadRequestAsync_ParsesStartLineHeadersAndContentLengthBody()
    {
        var raw = "POST /foo?x=1 HTTP/1.1\r\nHost: example.test\r\nContent-Length: 5\r\n\r\nhello";
        using var source = StreamOf(raw);

        var request = await Http1MessageReader.ReadRequestAsync(source, CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal("POST", request!.Method);
        Assert.Equal("/foo?x=1", request.Target);
        Assert.Equal("HTTP/1.1", request.HttpVersion);
        Assert.Contains(request.Headers, h => h.Name == "Host" && h.Value == "example.test");
        Assert.Contains(request.Headers, h => h.Name == "Content-Length" && h.Value == "5");
        Assert.Equal("hello", Encoding.ASCII.GetString(request.Body));
    }

    [Fact]
    public async Task ReadRequestAsync_ReturnsNullWhenConnectionClosesBeforeAnotherRequest()
    {
        using var source = new MemoryStream();

        var request = await Http1MessageReader.ReadRequestAsync(source, CancellationToken.None);

        Assert.Null(request);
    }

    [Fact]
    public async Task ReadRequestAsync_RequestWithNoBodyHeadersHasEmptyBody()
    {
        var raw = "GET /foo HTTP/1.1\r\nHost: example.test\r\n\r\n";
        using var source = StreamOf(raw);

        var request = await Http1MessageReader.ReadRequestAsync(source, CancellationToken.None);

        Assert.NotNull(request);
        Assert.Empty(request!.Body);
    }

    [Fact]
    public async Task ReadRequestAsync_DecodesAChunkedBody()
    {
        var raw = "POST /x HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n";
        using var source = StreamOf(raw);

        var request = await Http1MessageReader.ReadRequestAsync(source, CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal("hello world", Encoding.ASCII.GetString(request!.Body));
    }

    [Fact]
    public async Task ReadResponseAsync_ParsesStatusLineHeadersAndBody()
    {
        var raw = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nhi";
        using var source = StreamOf(raw);

        var response = await Http1MessageReader.ReadResponseAsync(source, isResponseToHeadRequest: false, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(200, response!.StatusCode);
        Assert.Equal("OK", response.ReasonPhrase);
        Assert.Equal("hi", Encoding.ASCII.GetString(response.Body));
    }

    [Fact]
    public async Task ReadResponseAsync_A204NeverReadsABodyEvenIfOneIsClaimed()
    {
        // 204 responses aren't allowed to carry a body at all; a
        // Content-Length here would be a malformed server, and trying to
        // honor it would just hang waiting for bytes that never arrive.
        var raw = "HTTP/1.1 204 No Content\r\n\r\n";
        using var source = StreamOf(raw);

        var response = await Http1MessageReader.ReadResponseAsync(source, isResponseToHeadRequest: false, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Empty(response!.Body);
    }

    [Fact]
    public async Task ReadResponseAsync_ResponseToAHeadRequestNeverReadsABody()
    {
        // The server would claim Content-Length: 100 but never actually
        // send those bytes for a HEAD request -- reading them anyway would
        // hang forever.
        var raw = "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n";
        using var source = StreamOf(raw);

        var response = await Http1MessageReader.ReadResponseAsync(source, isResponseToHeadRequest: true, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Empty(response!.Body);
    }

    [Fact]
    public async Task ReadResponseAsync_ReturnsNullWhenUpstreamClosesWithoutAnswering()
    {
        using var source = new MemoryStream();

        var response = await Http1MessageReader.ReadResponseAsync(source, isResponseToHeadRequest: false, CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task ReadRequestPreambleAsync_ParsesTheStartLineAndHeadersWithoutTouchingTheBody()
    {
        // The body here is never read at all -- the whole point of a
        // preamble read is deciding whether a breakpoint applies before
        // paying for that.
        var raw = "POST /foo HTTP/1.1\r\nHost: example.test\r\nContent-Length: 999999\r\n\r\n";
        using var source = StreamOf(raw);

        var preamble = await Http1MessageReader.ReadRequestPreambleAsync(source, CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Equal("POST", preamble!.Method);
        Assert.Equal("/foo", preamble.Target);
        Assert.Empty(preamble.Body);
    }

    [Fact]
    public async Task ReadResponsePreambleAsync_ParsesTheStatusLineAndHeadersWithoutTouchingTheBody()
    {
        var raw = "HTTP/1.1 404 Not Found\r\nContent-Length: 999999\r\n\r\n";
        using var source = StreamOf(raw);

        var preamble = await Http1MessageReader.ReadResponsePreambleAsync(source, CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Equal(404, preamble!.StatusCode);
        Assert.Equal("Not Found", preamble.ReasonPhrase);
        Assert.Empty(preamble.Body);
    }

    [Theory]
    [InlineData(100, false, true)]
    [InlineData(204, false, true)]
    [InlineData(304, false, true)]
    [InlineData(200, true, true)]
    [InlineData(200, false, false)]
    public void ResponseHasNoBody_MatchesTheCasesThatNeverCarryOne(int statusCode, bool isHeadRequest, bool expected)
    {
        Assert.Equal(expected, Http1MessageReader.ResponseHasNoBody(statusCode, isHeadRequest));
    }

    [Fact]
    public async Task RelayBodyAsync_RelaysAContentLengthBodyLiveAndStillReturnsItInFull()
    {
        var headers = new List<(string Name, string Value)> { ("Content-Length", "5") };
        using var source = StreamOf("hello");
        using var destination = new MemoryStream();

        var captured = await Http1MessageReader.RelayBodyAsync(source, destination, headers, CancellationToken.None);

        Assert.Equal("hello", Encoding.ASCII.GetString(captured.Captured));
        Assert.Equal("hello", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task RelayBodyAsync_RelaysAChunkedBodyLiveAndStillDecodesItInFull()
    {
        var headers = new List<(string Name, string Value)> { ("Transfer-Encoding", "chunked") };
        using var source = StreamOf("5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n");
        using var destination = new MemoryStream();

        var captured = await Http1MessageReader.RelayBodyAsync(source, destination, headers, CancellationToken.None);

        // The decoded, logical body -- chunk framing stripped out, same as
        // ReadBodyAsync would return.
        Assert.Equal("hello world", Encoding.ASCII.GetString(captured.Captured));

        // What actually went out the wire, though, keeps the original
        // chunk framing intact rather than being rebuilt -- that's what
        // makes this safe to pair with a preamble that still declares
        // Transfer-Encoding: chunked.
        Assert.Equal("5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task RelayBodyAsync_WithNeitherFramingHeaderRelaysNothing()
    {
        var headers = new List<(string Name, string Value)>();
        using var source = new MemoryStream();
        using var destination = new MemoryStream();

        var captured = await Http1MessageReader.RelayBodyAsync(source, destination, headers, CancellationToken.None);

        Assert.Empty(captured.Captured);
        Assert.Empty(destination.ToArray());
    }

    [Theory]
    [InlineData("Content-Length", "11", "hello world")]
    [InlineData("Transfer-Encoding", "chunked", "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n")]
    public async Task RelayBodyAsync_RelaysEverythingButKeepsOnlyUpToTheLimit(string header, string value, string wire)
    {
        var headers = new List<(string Name, string Value)> { (header, value) };
        using var source = StreamOf(wire);
        using var destination = new MemoryStream();

        var captured = await Http1MessageReader.RelayBodyAsync(source, destination, headers, CancellationToken.None, maxCapturedBytes: 7);

        Assert.Equal(wire, Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Equal("hello w", Encoding.ASCII.GetString(captured.Captured));
        Assert.Equal(11, captured.Length);
        Assert.True(captured.Truncated);
    }

    [Fact]
    public async Task RelayResponseBodyAsync_KeepsOnlyUpToTheLimitOfACloseDelimitedBody()
    {
        using var source = StreamOf("until the server closes");
        using var destination = new MemoryStream();

        var captured = await Http1MessageReader.RelayResponseBodyAsync(source, destination, [], CancellationToken.None, maxCapturedBytes: 5);

        Assert.Equal("until the server closes", Encoding.ASCII.GetString(destination.ToArray()));
        Assert.Equal("until", Encoding.ASCII.GetString(captured.Captured));
        Assert.Equal(23, captured.Length);
    }

    [Fact]
    public async Task RelayBodyAsync_ABodyWithinTheLimitIsNotTruncated()
    {
        var headers = new List<(string Name, string Value)> { ("Content-Length", "5") };
        using var source = StreamOf("hello");
        using var destination = new MemoryStream();

        var captured = await Http1MessageReader.RelayBodyAsync(source, destination, headers, CancellationToken.None, maxCapturedBytes: 5);

        Assert.False(captured.Truncated);
        Assert.Equal(5, captured.Length);
    }

    [Fact]
    public async Task ReadRequestPreambleAsync_RefusesMoreHeaderLinesThanTheLimit()
    {
        var raw = new StringBuilder("GET / HTTP/1.1\r\n");
        for (var i = 0; i <= Http1MessageReader.MaxHeaderCount; i++)
        {
            raw.Append("X-Flood: a\r\n");
        }

        using var source = StreamOf(raw.Append("\r\n").ToString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Http1MessageReader.ReadRequestPreambleAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task ReadResponsePreambleAsync_RefusesHeadersLargerInTotalThanTheLimit()
    {
        // Each line is under the per-line cap, and there are few enough of
        // them to pass the count limit, but together they're too big.
        var value = new string('a', 15 * 1024);
        var raw = new StringBuilder("HTTP/1.1 200 OK\r\n");
        for (var i = 0; i < Http1MessageReader.MaxHeaderBytes / value.Length + 1; i++)
        {
            raw.Append("X-Flood: ").Append(value).Append("\r\n");
        }

        using var source = StreamOf(raw.Append("\r\n").ToString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Http1MessageReader.ReadResponsePreambleAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task ReadResponsePreambleAsync_AcceptsManyOrdinaryHeaders()
    {
        var raw = new StringBuilder("HTTP/1.1 200 OK\r\n");
        for (var i = 0; i < 200; i++)
        {
            raw.Append("Set-Cookie: c").Append(i).Append("=value; Path=/; HttpOnly\r\n");
        }

        using var source = StreamOf(raw.Append("\r\n").ToString());

        var response = await Http1MessageReader.ReadResponsePreambleAsync(source, CancellationToken.None);

        Assert.Equal(200, response!.Headers.Count);
    }

    [Fact]
    public async Task ReadBodyAsync_DoesNotAllocateTheDeclaredLengthBeforeTheBytesArrive()
    {
        // A 2 GB Content-Length followed by one byte and a close: memory
        // should follow the bytes received, not the length claimed.
        var headers = new List<(string Name, string Value)> { ("Content-Length", "2000000000") };
        using var source = StreamOf("x");
        var before = GC.GetTotalAllocatedBytes(precise: true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Http1MessageReader.ReadBodyAsync(source, headers, CancellationToken.None));

        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < 256L * 1024 * 1024);
    }

    [Fact]
    public async Task ReadBodyAsync_RefusesAContentLengthTooLargeToBuffer()
    {
        var headers = new List<(string Name, string Value)> { ("Content-Length", "9000000000") };
        using var source = StreamOf("x");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Http1MessageReader.ReadBodyAsync(source, headers, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBodyAsync_StillReadsABodyThatArrivesInPieces()
    {
        var body = new byte[300_000];
        Random.Shared.NextBytes(body);
        var headers = new List<(string Name, string Value)> { ("Content-Length", body.Length.ToString()) };
        using var source = new TrickleStream(body, 4096);

        var read = await Http1MessageReader.ReadBodyAsync(source, headers, CancellationToken.None);

        Assert.Equal(body, read);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.ASCII.GetBytes(text));

    /// <summary>Returns at most <c>piece</c> bytes per read, like a socket.</summary>
    private sealed class TrickleStream(byte[] data, int piece) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, piece));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, piece)], cancellationToken);
    }
}
