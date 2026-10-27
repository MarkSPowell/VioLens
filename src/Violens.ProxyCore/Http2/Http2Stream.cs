using System.Threading.Channels;

namespace Violens.ProxyCore.Http2;

/// <summary>
/// One HTTP/2 stream: a request and its response. Read the peer's header
/// blocks with <see cref="ReadHeadersAsync"/> and its body with
/// <see cref="ReadDataAsync"/>; send with the Write methods. Reading the body
/// is what returns flow-control credit to the peer, so a stream that isn't
/// read soon stops receiving, without holding up the connection's other
/// streams.
/// </summary>
internal sealed class Http2Stream
{
    private readonly Http2Connection _connection;
    private readonly Channel<object> _inbound = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private ReadOnlyMemory<byte> _current;
    private bool _endAfterCurrent;
    private bool _ended;

    internal Http2Stream(Http2Connection connection, int id, int sendWindow, int receiveWindow)
    {
        _connection = connection;
        Id = id;
        SendWindow = sendWindow;
        ReceiveWindow = receiveWindow;
    }

    public int Id { get; }

    /// <summary>Whether the last header block <see cref="ReadHeadersAsync"/> returned ended the peer's side: a request or response with no body.</summary>
    public bool HeadersEndedStream { get; private set; }

    /// <summary>Whether this side has ended the stream, or it was reset.</summary>
    public bool IsLocalSideEnded => LocalClosed || IsReset;

    /// <summary>The trailers that ended the peer's side, once <see cref="ReadDataAsync"/> has returned 0; null if there were none.</summary>
    public IReadOnlyList<(string Name, string Value)>? Trailers { get; private set; }

    // State below is guarded by the connection's lock.
    internal long SendWindow { get; set; }

    internal int ReceiveWindow { get; set; }

    internal int PendingCredit { get; set; }

    internal bool LocalClosed { get; set; }

    internal bool RemoteClosed { get; set; }

    internal bool IsReset { get; set; }

    internal bool ReceivedFinalHeaders { get; set; }

    internal long? ExpectedContentLength { get; set; }

    internal long ReceivedDataLength { get; set; }

    /// <summary>
    /// The next header block from the peer: a request's headers, or for a
    /// client each interim (1xx) response and then the final one.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, string Value)>> ReadHeadersAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken))
            {
                throw new Http2StreamClosedException($"Stream {Id} ended without headers.");
            }

            if (!_inbound.Reader.TryRead(out var item))
            {
                continue;
            }

            if (item is HeadersItem headers)
            {
                _ended |= headers.EndStream;
                HeadersEndedStream = headers.EndStream;
                return headers.Headers;
            }

            throw new Http2StreamClosedException($"Stream {Id} sent data before its headers.", Http2ErrorCode.ProtocolError);
        }
    }

    /// <summary>
    /// Reads body bytes into <paramref name="buffer"/>; 0 once the peer's side
    /// has ended (see <see cref="Trailers"/>). Throws
    /// <see cref="Http2StreamClosedException"/> if the stream is reset or the
    /// connection ends first.
    /// </summary>
    public async ValueTask<int> ReadDataAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (_current.IsEmpty)
        {
            if (_endAfterCurrent || _ended)
            {
                _ended = true;
                return 0;
            }

            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken))
            {
                _ended = true;
                return 0;
            }

            if (!_inbound.Reader.TryRead(out var item))
            {
                continue;
            }

            switch (item)
            {
                case DataItem data:
                    _current = data.Data;
                    _endAfterCurrent = data.EndStream;
                    break;
                case HeadersItem trailers:
                    Trailers = trailers.Headers;
                    _ended = true;
                    return 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        await _connection.ReturnCreditAsync(this, count, cancellationToken);
        return count;
    }

    /// <summary>Reads the rest of the body.</summary>
    public async Task<byte[]> ReadAllDataAsync(CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await ReadDataAsync(buffer, cancellationToken)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    public Task WriteHeadersAsync(IReadOnlyList<(string Name, string Value)> headers, bool endStream, CancellationToken cancellationToken) =>
        _connection.SendHeadersAsync(this, headers, endStream, cancellationToken);

    /// <summary>Sends body bytes, waiting for flow-control credit as needed.</summary>
    public Task WriteDataAsync(ReadOnlyMemory<byte> data, bool endStream, CancellationToken cancellationToken) =>
        _connection.SendDataAsync(this, data, endStream, cancellationToken);

    /// <summary>Ends this side with trailers.</summary>
    public Task WriteTrailersAsync(IReadOnlyList<(string Name, string Value)> trailers, CancellationToken cancellationToken) =>
        _connection.SendHeadersAsync(this, trailers, endStream: true, cancellationToken);

    /// <summary>Ends the stream abruptly with RST_STREAM.</summary>
    public Task ResetAsync(Http2ErrorCode code) => _connection.ResetStreamAsync(this, code);

    internal void Deliver(object item) => _inbound.Writer.TryWrite(item);

    internal void EndInbound() => _inbound.Writer.TryComplete();

    internal void Fail(Exception exception) => _inbound.Writer.TryComplete(exception);

    internal sealed record HeadersItem(List<(string Name, string Value)> Headers, bool EndStream);

    internal sealed record DataItem(ReadOnlyMemory<byte> Data, bool EndStream);
}
