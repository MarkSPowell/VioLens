using System.Buffers.Binary;
using System.Globalization;

namespace Violens.Extensibility.Inspection;

/// <summary>
/// Reads MessagePack into <see cref="TreeNode"/>s. Binary and extension
/// values are shown as bytes, in CBOR diagnostic notation's <c>h'…'</c>;
/// an extension's type number goes in the note, and the timestamp
/// extension (type -1) also shows its date. A body holding several values
/// one after another (as SignalR's MessagePack protocol sends them) gives
/// one root per value.
/// </summary>
internal sealed class MessagePackTree(byte[] data, TreeBudget budget) : BinaryTreeReader(data, budget)
{
    protected override TreeNode? ReadValueCore(string name, bool keep)
    {
        var format = ReadByte();
        switch (format)
        {
            case <= 0x7F:
                return Integer(name, format, keep);
            case <= 0x8F:
                return Container(name, (ulong)(format & 0x0F), isMap: true, keep);
            case <= 0x9F:
                return Container(name, (ulong)(format & 0x0F), isMap: false, keep);
            case <= 0xBF:
                return String(name, (ulong)(format & 0x1F), keep);
            case 0xC0:
                return keep ? new TreeNode(name, TreeNodeKind.Null, "null") : null;
            case 0xC1:
                throw new TreeFormatException("The byte 0xC1 is never used in MessagePack.");
            case 0xC2 or 0xC3:
                return keep ? new TreeNode(name, TreeNodeKind.Boolean, format == 0xC3 ? "true" : "false") : null;
            case 0xC4 or 0xC5 or 0xC6:
            {
                var bytes = ReadSpan(ReadLength(format - 0xC4));
                return keep ? new TreeNode(name, TreeNodeKind.Bytes, Hex(bytes)) : null;
            }

            case 0xC7 or 0xC8 or 0xC9:
                return Extension(name, ReadLength(format - 0xC7), keep);
            case 0xCA:
            {
                var value = BinaryPrimitives.ReadSingleBigEndian(ReadSpan(4));
                return keep ? new TreeNode(name, TreeNodeKind.Number, Number(value)) : null;
            }

            case 0xCB:
            {
                var value = BinaryPrimitives.ReadDoubleBigEndian(ReadSpan(8));
                return keep ? new TreeNode(name, TreeNodeKind.Number, Number(value)) : null;
            }

            case 0xCC:
                return Integer(name, ReadByte(), keep);
            case 0xCD:
                return Integer(name, BinaryPrimitives.ReadUInt16BigEndian(ReadSpan(2)), keep);
            case 0xCE:
                return Integer(name, BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(4)), keep);
            case 0xCF:
                return Integer(name, BinaryPrimitives.ReadUInt64BigEndian(ReadSpan(8)), keep);
            case 0xD0:
                return Integer(name, (sbyte)ReadByte(), keep);
            case 0xD1:
                return Integer(name, BinaryPrimitives.ReadInt16BigEndian(ReadSpan(2)), keep);
            case 0xD2:
                return Integer(name, BinaryPrimitives.ReadInt32BigEndian(ReadSpan(4)), keep);
            case 0xD3:
                return Integer(name, BinaryPrimitives.ReadInt64BigEndian(ReadSpan(8)), keep);
            case 0xD4 or 0xD5 or 0xD6 or 0xD7 or 0xD8:
                // fixext 1, 2, 4, 8 and 16.
                return Extension(name, 1UL << (format - 0xD4), keep);
            case 0xD9 or 0xDA or 0xDB:
                return String(name, ReadLength(format - 0xD9), keep);
            case 0xDC or 0xDD:
                return Container(name, ReadLength(format - 0xDC + 1), isMap: false, keep);
            case 0xDE or 0xDF:
                return Container(name, ReadLength(format - 0xDE + 1), isMap: true, keep);
            default:
                // 0xE0 to 0xFF: negative fixint.
                return Integer(name, (sbyte)format, keep);
        }
    }

    /// <summary>A big-endian length of 1, 2 or 4 bytes (<paramref name="size"/> 0, 1 or 2).</summary>
    private ulong ReadLength(int size) => size switch
    {
        0 => ReadByte(),
        1 => BinaryPrimitives.ReadUInt16BigEndian(ReadSpan(2)),
        _ => BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(4)),
    };

    private static TreeNode? Integer<T>(string name, T value, bool keep) where T : IFormattable =>
        keep ? new TreeNode(name, TreeNodeKind.Number, value.ToString(null, CultureInfo.InvariantCulture)) : null;

    private TreeNode? String(string name, ulong length, bool keep)
    {
        var text = Text(ReadSpan(length));
        return keep ? new TreeNode(name, TreeNodeKind.String, text) : null;
    }

    private TreeNode? Container(string name, ulong count, bool isMap, bool keep)
    {
        var children = ReadChildren(count, isMap, keep);
        return keep ? new TreeNode(name, isMap ? TreeNodeKind.Object : TreeNodeKind.Array, "", children!) : null;
    }

    private TreeNode? Extension(string name, ulong length, bool keep)
    {
        var type = (sbyte)ReadByte();
        var bytes = ReadSpan(length);
        if (!keep)
        {
            return null;
        }

        var note = type == -1 && TimestampNote(bytes) is { } date
            ? $"timestamp, {date}"
            : $"extension type {type}";
        return new TreeNode(name, TreeNodeKind.Bytes, Hex(bytes), note);
    }

    /// <summary>The timestamp extension's 32-, 64- and 96-bit forms.</summary>
    private static string? TimestampNote(ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
            case 4:
                return EpochNote(BinaryPrimitives.ReadUInt32BigEndian(bytes));
            case 8:
            {
                var value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
                return EpochNote(value & 0x3_FFFF_FFFF, (long)(value >> 34));
            }

            case 12:
                return EpochNote(BinaryPrimitives.ReadInt64BigEndian(bytes[4..]), BinaryPrimitives.ReadUInt32BigEndian(bytes));
            default:
                return null;
        }
    }
}
