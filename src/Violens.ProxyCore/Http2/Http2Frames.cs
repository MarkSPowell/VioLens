using System.Buffers.Binary;

namespace Violens.ProxyCore.Http2;

/// <summary>Frame types (RFC 9113 section 6).</summary>
internal enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
}

/// <summary>Frame flags. Their meaning depends on the frame type; ACK and END_STREAM share a bit.</summary>
[Flags]
internal enum Http2FrameFlags : byte
{
    None = 0x0,
    EndStream = 0x1,
    Ack = 0x1,
    EndHeaders = 0x4,
    Padded = 0x8,
    Priority = 0x20,
}

/// <summary>Error codes for RST_STREAM and GOAWAY (RFC 9113 section 7).</summary>
internal enum Http2ErrorCode : uint
{
    NoError = 0x0,
    ProtocolError = 0x1,
    InternalError = 0x2,
    FlowControlError = 0x3,
    SettingsTimeout = 0x4,
    StreamClosed = 0x5,
    FrameSizeError = 0x6,
    RefusedStream = 0x7,
    Cancel = 0x8,
    CompressionError = 0x9,
    ConnectError = 0xa,
    EnhanceYourCalm = 0xb,
    InadequateSecurity = 0xc,
    Http11Required = 0xd,
}

/// <summary>SETTINGS parameters (RFC 9113 section 6.5.2).</summary>
internal enum Http2SettingId : ushort
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6,
}

/// <summary>A problem that ends the whole connection with GOAWAY.</summary>
internal sealed class Http2ConnectionException(Http2ErrorCode code, string message) : Exception(message)
{
    public Http2ErrorCode Code { get; } = code;
}

/// <summary>A problem with one stream, which ends it with RST_STREAM.</summary>
internal sealed class Http2StreamException(int streamId, Http2ErrorCode code, string message) : Exception(message)
{
    public int StreamId { get; } = streamId;

    public Http2ErrorCode Code { get; } = code;
}

/// <summary>
/// Thrown to a stream's reader or writer when the stream can't carry on: the
/// peer reset it, or the connection ended.
/// </summary>
internal sealed class Http2StreamClosedException(string message, Http2ErrorCode code = Http2ErrorCode.Cancel)
    : IOException(message)
{
    public Http2ErrorCode Code { get; } = code;
}

/// <summary>One frame: its 9-octet header and its payload.</summary>
internal sealed record Http2Frame(Http2FrameType Type, Http2FrameFlags Flags, int StreamId, byte[] Payload)
{
    public bool Has(Http2FrameFlags flag) => (Flags & flag) == flag;
}

/// <summary>Frame encoding and decoding (RFC 9113 section 4).</summary>
internal static class Http2FrameCodec
{
    public const int HeaderLength = 9;

    /// <summary>The smallest SETTINGS_MAX_FRAME_SIZE, and the default.</summary>
    public const int MinMaxFrameSize = 16384;

    /// <summary>The largest SETTINGS_MAX_FRAME_SIZE allowed.</summary>
    public const int MaxMaxFrameSize = (1 << 24) - 1;

    /// <summary>The largest flow-control window (2^31 - 1).</summary>
    public const int MaxWindowSize = int.MaxValue;

    /// <summary>What a client sends first, before its SETTINGS frame (RFC 9113 section 3.4).</summary>
    public static ReadOnlySpan<byte> ClientPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    public static byte[] Build(Http2FrameType type, Http2FrameFlags flags, int streamId, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderLength + payload.Length];
        WriteHeader(frame, payload.Length, type, flags, streamId);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static void WriteHeader(Span<byte> destination, int length, Http2FrameType type, Http2FrameFlags flags, int streamId)
    {
        destination[0] = (byte)(length >> 16);
        destination[1] = (byte)(length >> 8);
        destination[2] = (byte)length;
        destination[3] = (byte)type;
        destination[4] = (byte)flags;
        BinaryPrimitives.WriteInt32BigEndian(destination[5..], streamId & int.MaxValue);
    }

    /// <summary>
    /// Reads the next frame. Null at a clean end of the stream between frames.
    /// A frame longer than <paramref name="maxFrameSize"/> is a FRAME_SIZE_ERROR
    /// and its payload isn't read.
    /// </summary>
    public static async Task<Http2Frame?> ReadAsync(Stream source, int maxFrameSize, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        var read = await ReadAtLeastAsync(source, header, cancellationToken);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderLength)
        {
            throw new IOException("The connection closed in the middle of a frame header.");
        }

        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        var type = (Http2FrameType)header[3];
        var flags = (Http2FrameFlags)header[4];
        var streamId = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & int.MaxValue;
        if (length > maxFrameSize)
        {
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError, $"A {type} frame of {length} octets is larger than the {maxFrameSize} allowed.");
        }

        var payload = length == 0 ? [] : new byte[length];
        if (length > 0 && await ReadAtLeastAsync(source, payload, cancellationToken) < length)
        {
            throw new IOException("The connection closed in the middle of a frame.");
        }

        return new Http2Frame(type, flags, streamId, payload);
    }

    /// <summary>
    /// Removes padding (RFC 9113 section 6.1), returning the data between the
    /// pad length and the padding. Padding as long as the payload is a
    /// PROTOCOL_ERROR.
    /// </summary>
    public static ReadOnlyMemory<byte> Unpad(Http2Frame frame)
    {
        if (!frame.Has(Http2FrameFlags.Padded))
        {
            return frame.Payload;
        }

        if (frame.Payload.Length == 0)
        {
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, $"A padded {frame.Type} frame has no pad length.");
        }

        var padLength = frame.Payload[0];
        if (padLength >= frame.Payload.Length)
        {
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"A {frame.Type} frame's padding is as long as its payload.");
        }

        return frame.Payload.AsMemory(1, frame.Payload.Length - 1 - padLength);
    }

    public static byte[] Settings(IEnumerable<(Http2SettingId Id, uint Value)> settings)
    {
        var list = settings.ToList();
        var payload = new byte[list.Count * 6];
        for (var i = 0; i < list.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(i * 6), (ushort)list[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan((i * 6) + 2), list[i].Value);
        }

        return Build(Http2FrameType.Settings, Http2FrameFlags.None, 0, payload);
    }

    public static IEnumerable<(Http2SettingId Id, uint Value)> ParseSettings(byte[] payload)
    {
        for (var i = 0; i + 6 <= payload.Length; i += 6)
        {
            yield return ((Http2SettingId)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(i)),
                BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i + 2)));
        }
    }

    public static byte[] WindowUpdate(int streamId, int increment)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, increment);
        return Build(Http2FrameType.WindowUpdate, Http2FrameFlags.None, streamId, payload);
    }

    public static byte[] RstStream(int streamId, Http2ErrorCode code)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)code);
        return Build(Http2FrameType.RstStream, Http2FrameFlags.None, streamId, payload);
    }

    public static byte[] GoAway(int lastStreamId, Http2ErrorCode code, string debug)
    {
        var debugBytes = System.Text.Encoding.UTF8.GetBytes(debug);
        var payload = new byte[8 + debugBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, lastStreamId);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)code);
        debugBytes.CopyTo(payload, 8);
        return Build(Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload);
    }

    public static byte[] Ping(ReadOnlySpan<byte> opaqueData, bool ack) =>
        Build(Http2FrameType.Ping, ack ? Http2FrameFlags.Ack : Http2FrameFlags.None, 0, opaqueData);

    /// <summary>
    /// A HEADERS frame, followed by CONTINUATION frames when the block is
    /// larger than <paramref name="maxFrameSize"/>, as one buffer so nothing
    /// can be sent between them.
    /// </summary>
    public static byte[] Headers(int streamId, ReadOnlySpan<byte> block, bool endStream, int maxFrameSize)
    {
        var frameCount = Math.Max(1, (block.Length + maxFrameSize - 1) / maxFrameSize);
        var output = new byte[(frameCount * HeaderLength) + block.Length];
        var offset = 0;
        for (var i = 0; i < frameCount; i++)
        {
            var chunk = block.Slice(i * maxFrameSize, Math.Min(maxFrameSize, block.Length - (i * maxFrameSize)));
            var type = i == 0 ? Http2FrameType.Headers : Http2FrameType.Continuation;
            var flags = i == frameCount - 1 ? Http2FrameFlags.EndHeaders : Http2FrameFlags.None;
            if (i == 0 && endStream)
            {
                flags |= Http2FrameFlags.EndStream;
            }

            WriteHeader(output.AsSpan(offset), chunk.Length, type, flags, streamId);
            chunk.CopyTo(output.AsSpan(offset + HeaderLength));
            offset += HeaderLength + chunk.Length;
        }

        return output;
    }

    private static async Task<int> ReadAtLeastAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
