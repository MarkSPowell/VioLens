using System.Globalization;
using System.Text;

namespace Violens.Extensibility.Inspection;

/// <summary>Thrown when a CBOR or MessagePack body can't be read; the message says why, for the tab to show.</summary>
internal sealed class TreeFormatException(string message) : Exception(message);

/// <summary>
/// What the CBOR and MessagePack readers share: a position in the body,
/// bounds-checked reads, a nesting limit and the node budget. A body can
/// hold several values one after another (a CBOR sequence, a MessagePack
/// stream), so <see cref="ReadAll"/> reads until the end.
/// </summary>
internal abstract class BinaryTreeReader(byte[] data, TreeBudget budget)
{
    private const int MaxDepth = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private int _depth;

    protected byte[] Data { get; } = data;

    protected int Position { get; set; }

    protected TreeBudget Budget { get; } = budget;

    /// <summary>How many values the body held, including any the budget left out.</summary>
    public int ValueCount { get; private set; }

    public List<TreeNode> ReadAll()
    {
        var roots = new List<TreeNode>();
        var hidden = 0;
        while (Position < Data.Length)
        {
            var keep = hidden == 0 && Budget.TryTake();
            var node = ReadValue("", keep);
            if (keep)
            {
                roots.Add(node!);
            }
            else
            {
                hidden++;
            }

            ValueCount++;
        }

        if (hidden > 0)
        {
            roots.Add(TreeBudget.Omitted(hidden));
        }

        // Several values are named by position, like array items.
        if (ValueCount > 1)
        {
            for (var i = 0; i < roots.Count; i++)
            {
                if (roots[i].Kind != TreeNodeKind.Omitted)
                {
                    roots[i] = roots[i] with { Name = $"[{i}]" };
                }
            }
        }

        return roots;
    }

    /// <summary>Reads one value; returns null when <paramref name="keep"/> is false (the value is skipped, not kept).</summary>
    protected abstract TreeNode? ReadValueCore(string name, bool keep);

    protected TreeNode? ReadValue(string name, bool keep)
    {
        if (++_depth > MaxDepth)
        {
            throw new TreeFormatException($"Values are nested more than {MaxDepth} deep.");
        }

        try
        {
            return ReadValueCore(name, keep);
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>
    /// Reads <paramref name="count"/> children, or, when it's null, children
    /// until <paramref name="atEnd"/> says the container is over. A map's
    /// are read as key/value pairs, the key becoming the child's name.
    /// Returns null when <paramref name="keep"/> is false.
    /// </summary>
    protected List<TreeNode>? ReadChildren(ulong? count, bool isMap, bool keep, Func<bool>? atEnd = null)
    {
        if (count is { } n && n > (ulong)(Data.Length - Position))
        {
            // Every value takes at least a byte, so this can't be right.
            throw new TreeFormatException($"A {(isMap ? "map" : "array")} claims {n:N0} items, more than the bytes left.");
        }

        var children = keep ? new List<TreeNode>() : null;
        var hidden = 0;
        for (ulong i = 0; count is null ? !atEnd!() : i < count; i++)
        {
            var keepChild = keep && hidden == 0 && Budget.TryTake();
            var name = $"[{i}]";
            var nameIsLiteral = false;
            if (isMap)
            {
                // The key is kept with its value, and needs no budget of its own.
                var key = ReadValue("", keepChild);
                (name, nameIsLiteral) = key is null ? ("", false)
                    : key.Kind == TreeNodeKind.String ? (key.Value, false)
                    : (TreeText.WriteCompact(key), true);
            }

            var child = ReadValue(name, keepChild);
            if (keepChild)
            {
                children!.Add(child! with { NameIsLiteral = nameIsLiteral });
            }
            else if (keep)
            {
                hidden++;
            }
        }

        if (hidden > 0)
        {
            children!.Add(TreeBudget.Omitted(hidden));
        }

        return children;
    }

    protected byte ReadByte()
    {
        Require(1);
        return Data[Position++];
    }

    protected ReadOnlySpan<byte> ReadSpan(ulong length)
    {
        if (length > (ulong)(Data.Length - Position))
        {
            throw new TreeFormatException($"A value claims {length:N0} bytes, more than the {Data.Length - Position:N0} left.");
        }

        var span = Data.AsSpan(Position, (int)length);
        Position += (int)length;
        return span;
    }

    protected void Require(int length)
    {
        if (Data.Length - Position < length)
        {
            throw new TreeFormatException("The body ends in the middle of a value.");
        }
    }

    protected static string Hex(ReadOnlySpan<byte> bytes) => "h'" + Convert.ToHexStringLower(bytes) + "'";

    protected static string Text(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new TreeFormatException("A text string isn't valid UTF-8.");
        }
    }

    /// <summary>A floating-point number as CBOR diagnostic notation writes it: always with a decimal point or exponent.</summary>
    protected static string Number(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>"2024-05-01T12:00:00Z" for seconds since 1970, or null when it's out of range.</summary>
    protected static string? EpochNote(double seconds, long extraNanoseconds = 0)
    {
        // DateTimeOffset's range: years 1 to 9999.
        if (double.IsNaN(seconds) || seconds < -62135596800 || seconds > 253402300799)
        {
            return null;
        }

        var ticks = (long)(seconds * TimeSpan.TicksPerSecond) + extraNanoseconds / 100;
        var time = DateTimeOffset.UnixEpoch.AddTicks(ticks);
        // ".FFFFFFF" drops its own point when the fraction is zero.
        return time.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }
}
