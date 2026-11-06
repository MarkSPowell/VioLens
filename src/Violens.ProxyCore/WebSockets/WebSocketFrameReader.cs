using System.IO.Compression;

namespace Violens.ProxyCore.WebSockets;

/// <summary>
/// The permessage-deflate compression (RFC 7692) a WebSocket connection
/// agreed to, from the server's Sec-WebSocket-Extensions response header.
/// "No context takeover" means each message in that direction is
/// compressed on its own; otherwise each builds on the ones before it.
/// </summary>
internal sealed record WebSocketCompression(bool ClientNoContextTakeover, bool ServerNoContextTakeover)
{
    /// <summary>The compression agreed in <paramref name="extensionsHeader"/>, or null if none.</summary>
    public static WebSocketCompression? FromHeader(string? extensionsHeader)
    {
        if (string.IsNullOrWhiteSpace(extensionsHeader))
        {
            return null;
        }

        foreach (var extension in extensionsHeader.Split(','))
        {
            var parts = extension.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !string.Equals(parts[0], "permessage-deflate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool Has(string name) => parts.Skip(1).Any(p =>
                string.Equals(p.Split('=')[0].Trim(), name, StringComparison.OrdinalIgnoreCase));

            return new WebSocketCompression(Has("client_no_context_takeover"), Has("server_no_context_takeover"));
        }

        return null;
    }
}

/// <summary>
/// Follows one direction of a WebSocket connection: fed the bytes as they
/// pass through, in whatever chunks they arrive, it reads the RFC 6455
/// frames, unmasks them, puts fragmented messages back together,
/// decompresses permessage-deflate messages, and adds each finished message
/// to the <see cref="WebSocketLog"/>.
///
/// It never throws and never holds up the traffic: if the bytes stop
/// making sense as frames, it reports that on the log and ignores the rest.
/// </summary>
internal sealed class WebSocketFrameReader : IDisposable
{
    /// <summary>The most of one message's content kept; the rest is counted but not kept.</summary>
    public const int MaxStoredMessageBytes = 1024 * 1024;

    /// <summary>The largest compressed message that's decompressed.</summary>
    public const int MaxCompressedMessageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The most one compressed message is decompressed to. Only its start
    /// is kept anyway; this stops a small message of zeros from costing
    /// gigabytes of decompression work.
    /// </summary>
    internal const int MaxInflatedMessageBytes = 64 * 1024 * 1024;

    private static readonly byte[] DeflateTail = [0x00, 0x00, 0xFF, 0xFF];

    private readonly WebSocketDirection _direction;
    private readonly WebSocketLog _log;
    private readonly bool _deflate;
    private readonly bool _noContextTakeover;
    private Inflater? _inflater;
    private bool _inflaterBroken;

    // The frame header being read: 2 to 14 bytes.
    private readonly byte[] _header = new byte[14];
    private int _headerFilled;
    private int _headerNeeded = 2;
    private bool _inPayload;

    // The frame whose payload is being read.
    private bool _fin;
    private bool _rsv1;
    private int _opcode;
    private bool _masked;
    private readonly byte[] _mask = new byte[4];
    private long _payloadLength;
    private long _payloadRemaining;
    private long _maskOffset;

    // The data message being put together (opcode 0 when none is).
    private int _messageOpcode;
    private bool _messageCompressed;
    private long _messageLength;
    private MemoryStream _messageBytes = new();
    private bool _messageTooBig;

    // The control frame (Close, Ping, Pong) being read.
    private readonly MemoryStream _controlBytes = new();

    private bool _failed;

    /// <param name="compression">The connection's permessage-deflate settings, or null when it isn't compressed.</param>
    public WebSocketFrameReader(WebSocketDirection direction, WebSocketLog log, WebSocketCompression? compression)
    {
        _direction = direction;
        _log = log;
        _deflate = compression is not null;
        _noContextTakeover = compression is not null &&
                             (direction == WebSocketDirection.Sent ? compression.ClientNoContextTakeover : compression.ServerNoContextTakeover);
    }

    /// <summary>True once the bytes stopped making sense as WebSocket frames.</summary>
    public bool Failed => _failed;

    /// <summary>Takes the next bytes that passed in this direction.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            FeedCore(data);
        }
        catch (Exception ex)
        {
            Fail($"Couldn't follow the messages after this point: {ex.Message}");
        }
    }

    private void FeedCore(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty && !_failed)
        {
            if (!_inPayload)
            {
                var take = Math.Min(_headerNeeded - _headerFilled, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFilled));
                _headerFilled += take;
                data = data[take..];

                if (_headerFilled == 2)
                {
                    var length7 = _header[1] & 0x7F;
                    _headerNeeded = 2 + (length7 == 126 ? 2 : length7 == 127 ? 8 : 0) + ((_header[1] & 0x80) != 0 ? 4 : 0);
                }

                if (_headerFilled == _headerNeeded)
                {
                    StartFrame();
                }

                continue;
            }

            var count = (int)Math.Min(_payloadRemaining, data.Length);
            TakePayload(data[..count]);
            data = data[count..];
            _payloadRemaining -= count;
            if (_payloadRemaining == 0)
            {
                EndFrame();
            }
        }
    }

    private void StartFrame()
    {
        _fin = (_header[0] & 0x80) != 0;
        _rsv1 = (_header[0] & 0x40) != 0;
        _opcode = _header[0] & 0x0F;
        _masked = (_header[1] & 0x80) != 0;

        var length7 = _header[1] & 0x7F;
        var offset = 2;
        if (length7 == 126)
        {
            _payloadLength = (_header[2] << 8) | _header[3];
            offset = 4;
        }
        else if (length7 == 127)
        {
            ulong length = 0;
            for (var i = 0; i < 8; i++)
            {
                length = (length << 8) | _header[2 + i];
            }

            if (length > long.MaxValue)
            {
                Fail("A frame claimed an impossible length, so these don't look like WebSocket frames.");
                return;
            }

            _payloadLength = (long)length;
            offset = 10;
        }
        else
        {
            _payloadLength = length7;
        }

        if (_masked)
        {
            Array.Copy(_header, offset, _mask, 0, 4);
        }

        _headerFilled = 0;
        _headerNeeded = 2;
        _maskOffset = 0;
        _payloadRemaining = _payloadLength;

        switch (_opcode)
        {
            case 0: // continuation
                if (_messageOpcode == 0)
                {
                    Fail("A continuation frame arrived with no message to continue.");
                    return;
                }

                break;
            case 1 or 2:
                if (_messageOpcode != 0)
                {
                    Fail("A new message started before the last one finished.");
                    return;
                }

                _messageOpcode = _opcode;
                _messageCompressed = _rsv1;
                _messageLength = 0;
                _messageTooBig = false;
                _messageBytes = new MemoryStream();
                break;
            case 8 or 9 or 10:
                if (!_fin || _payloadLength > 125)
                {
                    Fail("A control frame was fragmented or too long, so these don't look like WebSocket frames.");
                    return;
                }

                _controlBytes.SetLength(0);
                break;
            default:
                Fail($"An unknown frame type ({_opcode}) arrived, so the rest isn't shown.");
                return;
        }

        _inPayload = true;
        if (_payloadRemaining == 0)
        {
            EndFrame();
        }
    }

    private void TakePayload(ReadOnlySpan<byte> chunk)
    {
        if (_opcode >= 8)
        {
            _controlBytes.Write(Unmask(chunk));
            return;
        }

        // Compressed messages are kept whole (up to a limit) so they can be
        // decompressed; uncompressed ones only up to what's shown.
        var limit = _messageCompressed ? MaxCompressedMessageBytes : MaxStoredMessageBytes;
        var room = (int)Math.Max(0, Math.Min(chunk.Length, limit - _messageBytes.Length));
        if (room > 0)
        {
            _messageBytes.Write(Unmask(chunk[..room]));
            _maskOffset += chunk.Length - room;
        }
        else
        {
            _maskOffset += chunk.Length;
        }

        if (room < chunk.Length)
        {
            _messageTooBig = true;
        }
    }

    private byte[] Unmask(ReadOnlySpan<byte> chunk)
    {
        var bytes = chunk.ToArray();
        if (_masked)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= _mask[(int)((_maskOffset + i) & 3)];
            }
        }

        _maskOffset += bytes.Length;
        return bytes;
    }

    private void EndFrame()
    {
        _inPayload = false;
        if (_opcode >= 8)
        {
            _log.Add(_direction, (WebSocketMessageKind)_opcode, _payloadLength, _controlBytes.ToArray());
            return;
        }

        _messageLength += _payloadLength;
        if (_fin)
        {
            EndMessage();
        }
    }

    private void EndMessage()
    {
        var kind = (WebSocketMessageKind)_messageOpcode;
        var payload = _messageBytes.ToArray();
        var truncated = _messageTooBig;
        string? note = null;

        if (_messageCompressed)
        {
            if (!_deflate)
            {
                note = "Marked as compressed, but no compression was agreed for this connection, so it's shown as sent.";
            }
            else if (_messageTooBig)
            {
                note = "Too large to decompress, so it's shown as sent, cut short.";
                if (!_noContextTakeover)
                {
                    _inflaterBroken = true; // later messages build on this one
                }
            }
            else if (_inflaterBroken)
            {
                note = "Couldn't be decompressed: it builds on an earlier message that couldn't be.";
            }
            else
            {
                try
                {
                    if (_inflater is null || _noContextTakeover)
                    {
                        _inflater?.Dispose();
                        _inflater = new Inflater();
                    }

                    payload = _inflater.Inflate(payload, DeflateTail, MaxStoredMessageBytes, MaxInflatedMessageBytes, out truncated, out var gaveUp);
                    if (gaveUp)
                    {
                        // The deflate state is now part-way through this
                        // message, so it can't be used for the next one.
                        note = $"Decompresses to more than {MaxInflatedMessageBytes / (1024 * 1024)} MB, so only its start is shown.";
                        _inflater.Dispose();
                        _inflater = null;
                        _inflaterBroken = !_noContextTakeover;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    note = $"Couldn't be decompressed ({ex.Message}), so it's shown as sent.";
                    _inflaterBroken = !_noContextTakeover;
                }
            }
        }

        if (payload.Length > MaxStoredMessageBytes)
        {
            payload = payload[..MaxStoredMessageBytes];
            truncated = true;
        }

        _log.Add(_direction, kind, _messageLength, payload, _messageCompressed, truncated, note);
        _messageOpcode = 0;
        _messageBytes = new MemoryStream();
    }

    private void Fail(string problem)
    {
        _failed = true;
        _log.ReportProblem(problem);
    }

    public void Dispose()
    {
        _inflater?.Dispose();
        _messageBytes.Dispose();
        _controlBytes.Dispose();
    }

    /// <summary>
    /// A raw-deflate decompressor that keeps its state between messages
    /// (context takeover): each message, followed by the 00 00 FF FF the
    /// sender left off, continues the same deflate stream.
    /// </summary>
    private sealed class Inflater : IDisposable
    {
        private readonly FeedStream _input = new();
        private readonly DeflateStream _deflate;

        public Inflater() => _deflate = new DeflateStream(_input, CompressionMode.Decompress);

        /// <summary>
        /// Decompresses one message, keeping at most <paramref name="maxKeep"/>
        /// bytes. Stops after <paramref name="maxInflate"/> bytes
        /// (<paramref name="gaveUp"/>), after which this inflater can't be
        /// used again.
        /// </summary>
        public byte[] Inflate(byte[] compressed, byte[] tail, int maxKeep, long maxInflate, out bool truncated, out bool gaveUp)
        {
            _input.Append(compressed);
            _input.Append(tail);

            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            truncated = false;
            gaveUp = false;
            long inflated = 0;
            int read;
            while ((read = _deflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                // All of it has to be read to keep the state right; only the
                // start is kept.
                var keep = (int)Math.Max(0, Math.Min(read, maxKeep - output.Length));
                output.Write(buffer, 0, keep);
                truncated |= keep < read;
                inflated += read;
                if (inflated > maxInflate)
                {
                    truncated = true;
                    gaveUp = true;
                    break;
                }
            }

            return output.ToArray();
        }

        public void Dispose() => _deflate.Dispose();
    }

    /// <summary>
    /// A stream that hands out the bytes appended to it, then reports "no
    /// more for now" (0) until more are appended. DeflateStream reads from
    /// it again on the next Read.
    /// </summary>
    private sealed class FeedStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;

        public void Append(byte[] bytes)
        {
            if (bytes.Length > 0)
            {
                _chunks.Enqueue(bytes);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (buffer.Length > 0 && _chunks.Count > 0)
            {
                var chunk = _chunks.Peek();
                var take = Math.Min(buffer.Length, chunk.Length - _offset);
                chunk.AsSpan(_offset, take).CopyTo(buffer);
                buffer = buffer[take..];
                total += take;
                _offset += take;
                if (_offset == chunk.Length)
                {
                    _chunks.Dequeue();
                    _offset = 0;
                }
            }

            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
