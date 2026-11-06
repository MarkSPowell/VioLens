// Ported from Clearinet (https://github.com/ericlaw1979/Clearinet),
// Transcoders/PcapReader.cs. Copyright (c) 2026 Eric Lawrence. MIT License:
// see THIRD-PARTY-NOTICES.md.

namespace Violens.ProxyCore.Sessions.Pcap;

/// <summary>
/// Reads the packets out of a classic PCAP or a PCAPNG capture, checking
/// every length against the file and against fixed limits, so a damaged or
/// hostile file fails cleanly instead of exhausting memory.
/// </summary>
internal sealed class PcapFile
{
    internal const int MaxBlock = 16 * 1024 * 1024;
    internal const long MaxCapture = 256L * 1024 * 1024;
    private const int MaxPackets = 1_000_000;

    /// <summary>One packet: its bytes, link type, time (MinValue if none), interface scope, and whether it was cut short.</summary>
    internal delegate void PacketHandler(byte[] data, uint link, DateTime time, int scope, bool truncated);

    private readonly Stream _input;
    private readonly Action<string> _warn;
    private readonly CancellationToken _cancellationToken;
    private long _consumed;
    private int _packets;

    private sealed class Interface
    {
        internal uint Link, Snap;
        internal decimal Units = 1_000_000;
        internal long Offset;
    }

    internal PcapFile(Stream input, Action<string> warn, CancellationToken cancellationToken)
    {
        _input = input;
        _warn = warn;
        _cancellationToken = cancellationToken;
    }

    internal void Read(PacketHandler packet)
    {
        var magic = ReadBytes(4, allowEof: false)!;
        if (U32(magic, 0, little: false) == 0x0a0d0d0a)
        {
            ReadNg(magic, packet);
        }
        else
        {
            ReadClassic(magic, packet);
        }
    }

    private byte[]? ReadBytes(int length, bool allowEof)
    {
        if (length < 0 || length > MaxBlock || _consumed + length > MaxCapture)
        {
            throw new InvalidDataException("the capture is larger than VioLens can import (256 MB, or a 16 MB block)");
        }

        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = _input.Read(result, offset, Math.Min(length - offset, 65536));
            if (read == 0)
            {
                if (allowEof && offset == 0)
                {
                    return null;
                }

                throw new InvalidDataException("the capture file is cut short");
            }

            _consumed += read;
            offset += read;
        }

        return result;
    }

    private void Emit(PacketHandler packet, byte[] data, uint link, DateTime time, int scope, bool truncated)
    {
        if (++_packets > MaxPackets)
        {
            throw new InvalidDataException("the capture has more than a million packets");
        }

        if (truncated)
        {
            _warn("Packets cut short by the capture's snap length");
        }

        packet(data, link, time, scope, truncated);
    }

    private void ReadClassic(byte[] magic, PacketHandler packet)
    {
        var m = U32(magic, 0, little: true);
        var little = m is 0xa1b2c3d4 or 0xa1b23c4d;
        var nano = m is 0xa1b23c4d or 0x4d3cb2a1;
        if (!little && m != 0xd4c3b2a1 && m != 0x4d3cb2a1)
        {
            throw new InvalidDataException("this isn't a PCAP or PCAPNG packet capture");
        }

        var header = ReadBytes(20, allowEof: false)!;
        if (U16(header, 0, little) != 2 || U16(header, 2, little) != 4)
        {
            throw new InvalidDataException("unsupported PCAP version (only 2.4 is)");
        }

        uint snap = U32(header, 12, little), link = U32(header, 16, little) & 0xffff;
        if (snap == 0 || snap > MaxBlock)
        {
            throw new InvalidDataException("the PCAP snap length isn't valid");
        }

        while (ReadBytes(16, allowEof: true) is { } record)
        {
            uint seconds = U32(record, 0, little), fraction = U32(record, 4, little);
            uint captured = U32(record, 8, little), original = U32(record, 12, little);
            if (captured > snap || captured > original || captured > MaxBlock || fraction >= (nano ? 1_000_000_000u : 1_000_000u))
            {
                throw new InvalidDataException("a PCAP packet's length or time isn't valid");
            }

            var time = DateTime.UnixEpoch.AddTicks((long)seconds * TimeSpan.TicksPerSecond + (nano ? fraction / 100 : fraction * 10L));
            Emit(packet, ReadBytes((int)captured, allowEof: false)!, link, time, 0, captured < original);
        }
    }

    private void ReadNg(byte[] first, PacketHandler packet)
    {
        bool little = true, haveSection = false;
        var nextScope = 0;
        var interfaces = new List<Interface>();
        var scopes = new List<int>();
        byte[]? typeBytes = first;
        while (typeBytes is not null)
        {
            var lengthBytes = ReadBytes(4, allowEof: false)!;
            var section = U32(typeBytes, 0, little: true) == 0x0a0d0d0a;
            var bom = section ? ReadBytes(4, allowEof: false)! : null;
            if (bom is not null)
            {
                var order = U32(bom, 0, little: true);
                if (order != 0x1a2b3c4d && order != 0x4d3c2b1a)
                {
                    throw new InvalidDataException("the PCAPNG byte-order mark isn't valid");
                }

                little = order == 0x1a2b3c4d;
            }
            else if (!haveSection)
            {
                throw new InvalidDataException("a PCAPNG block comes before any section header");
            }

            var length = U32(lengthBytes, 0, little);
            if (length < (section ? 28u : 12u) || length > MaxBlock || (length & 3) != 0)
            {
                throw new InvalidDataException("a PCAPNG block length isn't valid");
            }

            var block = new byte[(int)length];
            Buffer.BlockCopy(typeBytes, 0, block, 0, 4);
            Buffer.BlockCopy(lengthBytes, 0, block, 4, 4);
            if (bom is not null)
            {
                Buffer.BlockCopy(bom, 0, block, 8, 4);
            }

            var tail = ReadBytes((int)length - (section ? 12 : 8), allowEof: false)!;
            Buffer.BlockCopy(tail, 0, block, section ? 12 : 8, tail.Length);
            if (U32(block, block.Length - 4, little) != length)
            {
                throw new InvalidDataException("a PCAPNG block's trailing length doesn't match");
            }

            var type = U32(typeBytes, 0, little);
            if (section)
            {
                if (U16(block, 12, little) != 1 || U16(block, 14, little) != 0)
                {
                    throw new InvalidDataException("unsupported PCAPNG section version");
                }

                interfaces.Clear();
                scopes.Clear();
                haveSection = true;
                Options(block, 24, little, null);
            }
            else if (type == 1)
            {
                // Interface description.
                Require(block, 20);
                if (interfaces.Count >= 4096)
                {
                    throw new InvalidDataException("the capture has more than 4,096 interfaces");
                }

                var iface = new Interface { Link = U16(block, 8, little), Snap = U32(block, 12, little) };
                if (iface.Snap > MaxBlock)
                {
                    throw new InvalidDataException("a PCAPNG snap length is too large");
                }

                Options(block, 16, little, (code, value) =>
                {
                    if (code == 9)
                    {
                        // if_tsresol: a power of 10, or of 2 when the top bit is set.
                        if (value.Length != 1)
                        {
                            throw new InvalidDataException("a PCAPNG timestamp resolution isn't valid");
                        }

                        var exponent = value[0] & 127;
                        var binary = (value[0] & 128) != 0;
                        if (exponent > (binary ? 63 : 18))
                        {
                            throw new InvalidDataException("unsupported PCAPNG timestamp resolution");
                        }

                        iface.Units = 1;
                        for (var i = 0; i < exponent; i++)
                        {
                            iface.Units *= binary ? 2 : 10;
                        }
                    }

                    if (code == 14)
                    {
                        // if_tsoffset, in seconds.
                        if (value.Length != 8)
                        {
                            throw new InvalidDataException("a PCAPNG timestamp offset isn't valid");
                        }

                        iface.Offset = unchecked((long)U64(value, 0, little));
                    }
                });
                interfaces.Add(iface);
                scopes.Add(nextScope++);
            }
            else if (type is 6 or 2 or 3)
            {
                // Enhanced, obsolete and simple packet blocks.
                Require(block, type == 3 ? 16 : 32);
                var id = type == 3 ? 0 : type == 2 ? U16(block, 8, little) : U32(block, 8, little);
                if (id >= interfaces.Count)
                {
                    throw new InvalidDataException("a PCAPNG packet names an interface that isn't described");
                }

                var iface = interfaces[(int)id];
                var original = U32(block, type == 3 ? 8 : 24, little);
                var captured = type == 3 ? Math.Min(original, iface.Snap == 0 ? original : iface.Snap) : U32(block, 20, little);
                var start = type == 3 ? 12 : 28;
                var padded = ((long)captured + 3) & ~3L;
                if (captured > original || captured > MaxBlock || (iface.Snap != 0 && captured > iface.Snap) || start + padded > block.Length - 4)
                {
                    throw new InvalidDataException("a PCAPNG packet length isn't valid");
                }

                var time = DateTime.MinValue;
                if (type == 3)
                {
                    if (start + padded != block.Length - 4)
                    {
                        throw new InvalidDataException("a PCAPNG simple packet's padding isn't valid");
                    }

                    _warn("Packets without a time (PCAPNG simple packets)");
                }
                else
                {
                    var timestamp = ((ulong)U32(block, 12, little) << 32) | U32(block, 16, little);
                    var ticks = decimal.Truncate((decimal)timestamp * TimeSpan.TicksPerSecond / iface.Units) + (decimal)iface.Offset * TimeSpan.TicksPerSecond;
                    if (ticks < -DateTime.UnixEpoch.Ticks || ticks > DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks)
                    {
                        throw new InvalidDataException("a PCAPNG packet's time is out of range");
                    }

                    time = DateTime.UnixEpoch.AddTicks((long)ticks);
                    Options(block, start + (int)padded, little, null);
                }

                var data = new byte[(int)captured];
                Buffer.BlockCopy(block, start, data, 0, data.Length);
                Emit(packet, data, iface.Link, time, scopes[(int)id], captured < original);
            }

            // Statistics, name-resolution and decryption-secret blocks aren't
            // needed for plain HTTP, and unknown blocks are skipped.
            typeBytes = ReadBytes(4, allowEof: true);
        }
    }

    private static void Options(byte[] block, int offset, bool little, Action<ushort, byte[]>? option)
    {
        var end = block.Length - 4;
        while (offset < end)
        {
            if (end - offset < 4)
            {
                throw new InvalidDataException("a PCAPNG option is cut short");
            }

            ushort code = U16(block, offset, little), length = U16(block, offset + 2, little);
            offset += 4;
            if (code == 0)
            {
                if (length != 0)
                {
                    throw new InvalidDataException("a PCAPNG end-of-options isn't valid");
                }

                for (var i = offset; i < end; i++)
                {
                    if (block[i] != 0)
                    {
                        throw new InvalidDataException("a PCAPNG block has data after its end-of-options");
                    }
                }

                return;
            }

            var padded = (length + 3) & ~3;
            if (padded > end - offset)
            {
                throw new InvalidDataException("a PCAPNG option runs past its block");
            }

            option?.Invoke(code, block.AsSpan(offset, length).ToArray());
            offset += padded;
        }
    }

    private static void Require(byte[] data, int minimum)
    {
        if (data.Length < minimum)
        {
            throw new InvalidDataException("a PCAPNG block is too short");
        }
    }

    internal static ushort U16(byte[] b, int p, bool little) =>
        (ushort)(little ? b[p] | b[p + 1] << 8 : b[p] << 8 | b[p + 1]);

    internal static uint U32(byte[] b, int p, bool little) =>
        little
            ? (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24)
            : (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);

    private static ulong U64(byte[] b, int p, bool little) =>
        little
            ? U32(b, p, little: true) | ((ulong)U32(b, p + 4, little: true) << 32)
            : ((ulong)U32(b, p, little: false) << 32) | U32(b, p + 4, little: false);
}
