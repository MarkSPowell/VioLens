using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace Violens.Extensibility.Inspection;

/// <summary>
/// Reads CBOR (RFC 8949) into <see cref="TreeNode"/>s: every major type,
/// definite and indefinite lengths, half, single and double floats. Common
/// tags get a note: an epoch time's date, a bignum's decimal value, a UUID.
/// A body holding several items one after another (a CBOR sequence,
/// <c>application/cbor-seq</c>) gives one root per item.
/// </summary>
internal sealed class CborTree(byte[] data, TreeBudget budget) : BinaryTreeReader(data, budget)
{
    private const byte Break = 0xFF;

    protected override TreeNode? ReadValueCore(string name, bool keep)
    {
        var initial = ReadByte();
        var major = initial >> 5;
        var info = initial & 0x1F;
        if (initial == Break)
        {
            throw new TreeFormatException("A \"break\" byte (0xFF) appears outside an indefinite-length item.");
        }

        if (major == 7)
        {
            return ReadSimpleOrFloat(name, info, keep);
        }

        var indefinite = info == 31;
        if (indefinite && major is 0 or 1 or 6)
        {
            throw new TreeFormatException($"Major type {major} can't have an indefinite length.");
        }

        var argument = indefinite ? 0 : ReadArgument(info);
        switch (major)
        {
            case 0:
                return keep ? new TreeNode(name, TreeNodeKind.Number, argument.ToString(CultureInfo.InvariantCulture)) : null;

            case 1:
                return keep ? new TreeNode(name, TreeNodeKind.Number, (-BigInteger.One - argument).ToString(CultureInfo.InvariantCulture)) : null;

            case 2:
            {
                var bytes = indefinite ? ReadChunks(major: 2) : ReadSpan(argument).ToArray();
                return keep ? new TreeNode(name, TreeNodeKind.Bytes, Hex(bytes)) : null;
            }

            case 3:
            {
                var bytes = indefinite ? ReadChunks(major: 3) : ReadSpan(argument).ToArray();
                var text = Text(bytes);
                return keep ? new TreeNode(name, TreeNodeKind.String, text) : null;
            }

            case 4 or 5:
            {
                var children = ReadChildren(indefinite ? null : argument, isMap: major == 5, keep, AtBreak);
                return keep ? new TreeNode(name, major == 5 ? TreeNodeKind.Object : TreeNodeKind.Array, "", children!) : null;
            }

            default: // 6, a tag
            {
                var tagged = ReadValue("", keep);
                if (!keep)
                {
                    return null;
                }

                var tag = argument.ToString(CultureInfo.InvariantCulture);
                return new TreeNode(name, TreeNodeKind.Tagged, tag, [tagged!], TagNote(argument, tagged!));
            }
        }
    }

    private ulong ReadArgument(int info)
    {
        switch (info)
        {
            case < 24:
                return (ulong)info;
            case 24:
                return ReadByte();
            case 25:
                return BinaryPrimitives.ReadUInt16BigEndian(ReadSpan(2));
            case 26:
                return BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(4));
            case 27:
                return BinaryPrimitives.ReadUInt64BigEndian(ReadSpan(8));
            default:
                throw new TreeFormatException($"Additional information {info} is reserved.");
        }
    }

    /// <summary>An indefinite-length byte or text string: definite chunks of the same type, up to a break.</summary>
    private byte[] ReadChunks(int major)
    {
        var joined = new List<byte>();
        while (!AtBreak())
        {
            var initial = ReadByte();
            if (initial >> 5 != major || (initial & 0x1F) == 31)
            {
                throw new TreeFormatException("An indefinite-length string holds something other than definite-length chunks of its own type.");
            }

            joined.AddRange(ReadSpan(ReadArgument(initial & 0x1F)));
        }

        return [.. joined];
    }

    /// <summary>True, and the break consumed, when the next byte ends an indefinite-length item.</summary>
    private bool AtBreak()
    {
        Require(1);
        if (Data[Position] != Break)
        {
            return false;
        }

        Position++;
        return true;
    }

    private TreeNode? ReadSimpleOrFloat(string name, int info, bool keep)
    {
        TreeNode Node(TreeNodeKind kind, string value) => new(name, kind, value);
        switch (info)
        {
            case 20:
                return keep ? Node(TreeNodeKind.Boolean, "false") : null;
            case 21:
                return keep ? Node(TreeNodeKind.Boolean, "true") : null;
            case 22:
                return keep ? Node(TreeNodeKind.Null, "null") : null;
            case 23:
                return keep ? Node(TreeNodeKind.Other, "undefined") : null;
            case < 20:
                return keep ? Node(TreeNodeKind.Other, $"simple({info})") : null;
            case 24:
            {
                var value = ReadByte();
                return keep ? Node(TreeNodeKind.Other, $"simple({value})") : null;
            }

            case 25:
            {
                var half = BinaryPrimitives.ReadHalfBigEndian(ReadSpan(2));
                return keep ? Node(TreeNodeKind.Number, Number((double)half)) : null;
            }

            case 26:
            {
                var single = BinaryPrimitives.ReadSingleBigEndian(ReadSpan(4));
                return keep ? Node(TreeNodeKind.Number, Number(single)) : null;
            }

            case 27:
            {
                var value = BinaryPrimitives.ReadDoubleBigEndian(ReadSpan(8));
                return keep ? Node(TreeNodeKind.Number, Number(value)) : null;
            }

            default:
                throw new TreeFormatException($"Additional information {info} is reserved for major type 7.");
        }
    }

    /// <summary>What a well-known tag means, with the value it stands for where that's useful.</summary>
    private static string? TagNote(ulong tag, TreeNode tagged)
    {
        switch (tag)
        {
            case 0:
                return "date/time";
            case 1 when tagged.Kind == TreeNodeKind.Number &&
                        double.TryParse(tagged.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds):
                return EpochNote(seconds) is { } date ? $"epoch time, {date}" : "epoch time";
            case 1:
                return "epoch time";
            case 2 or 3 when tagged.Kind == TreeNodeKind.Bytes:
            {
                var magnitude = new BigInteger(Convert.FromHexString(tagged.Value[2..^1]), isUnsigned: true, isBigEndian: true);
                var value = tag == 2 ? magnitude : -BigInteger.One - magnitude;
                return $"bignum, {value.ToString(CultureInfo.InvariantCulture)}";
            }

            case 2 or 3:
                return "bignum";
            case 4:
                return "decimal fraction";
            case 5:
                return "bigfloat";
            case 21:
                return "expected as base64url";
            case 22:
                return "expected as base64";
            case 23:
                return "expected as hex";
            case 24:
                return "embedded CBOR";
            case 32:
                return "URI";
            case 33:
                return "base64url text";
            case 34:
                return "base64 text";
            case 36:
                return "MIME message";
            case 37 when tagged.Kind == TreeNodeKind.Bytes && tagged.Value.Length == 2 + 32 + 1:
                return $"UUID {new Guid(Convert.FromHexString(tagged.Value[2..^1]), bigEndian: true)}";
            case 37:
                return "UUID";
            case 55799:
                return "self-described CBOR";
            default:
                return null;
        }
    }
}
