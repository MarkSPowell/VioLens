using System.Text;

namespace Violens.ProxyCore.Http2.Hpack;

/// <summary>A header block couldn't be decoded: a COMPRESSION_ERROR for the whole connection.</summary>
internal sealed class HpackException(string message) : Exception(message);

/// <summary>
/// HPACK works on octets. Header names and values map to strings one octet per
/// character (Latin-1), so every octet round-trips unchanged.
/// </summary>
internal static class HpackText
{
    public static readonly Encoding Encoding = Encoding.Latin1;

    /// <summary>An entry's size in a dynamic table: its name and value octets plus 32 (RFC 7541 section 4.1).</summary>
    public static int EntrySize(string name, string value) => name.Length + value.Length + 32;
}

/// <summary>The static table (RFC 7541 Appendix A), indexes 1 to 61.</summary>
internal static class HpackStaticTable
{
    public static readonly (string Name, string Value)[] Entries =
    [
        (":authority", ""),
        (":method", "GET"),
        (":method", "POST"),
        (":path", "/"),
        (":path", "/index.html"),
        (":scheme", "http"),
        (":scheme", "https"),
        (":status", "200"),
        (":status", "204"),
        (":status", "206"),
        (":status", "304"),
        (":status", "400"),
        (":status", "404"),
        (":status", "500"),
        ("accept-charset", ""),
        ("accept-encoding", "gzip, deflate"),
        ("accept-language", ""),
        ("accept-ranges", ""),
        ("accept", ""),
        ("access-control-allow-origin", ""),
        ("age", ""),
        ("allow", ""),
        ("authorization", ""),
        ("cache-control", ""),
        ("content-disposition", ""),
        ("content-encoding", ""),
        ("content-language", ""),
        ("content-length", ""),
        ("content-location", ""),
        ("content-range", ""),
        ("content-type", ""),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("expect", ""),
        ("expires", ""),
        ("from", ""),
        ("host", ""),
        ("if-match", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("if-range", ""),
        ("if-unmodified-since", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("max-forwards", ""),
        ("proxy-authenticate", ""),
        ("proxy-authorization", ""),
        ("range", ""),
        ("referer", ""),
        ("refresh", ""),
        ("retry-after", ""),
        ("server", ""),
        ("set-cookie", ""),
        ("strict-transport-security", ""),
        ("transfer-encoding", ""),
        ("user-agent", ""),
        ("vary", ""),
        ("via", ""),
        ("www-authenticate", ""),
    ];

    public static int Count => Entries.Length;

    private static readonly Dictionary<(string, string), int> ExactIndex = BuildExactIndex();

    private static readonly Dictionary<string, int> NameIndex = BuildNameIndex();

    /// <summary>The index of an entry with this name and value, or 0.</summary>
    public static int FindExact(string name, string value) => ExactIndex.GetValueOrDefault((name, value));

    /// <summary>The lowest index of an entry with this name, or 0.</summary>
    public static int FindName(string name) => NameIndex.GetValueOrDefault(name);

    private static Dictionary<(string, string), int> BuildExactIndex()
    {
        var index = new Dictionary<(string, string), int>();
        for (var i = 0; i < Entries.Length; i++)
        {
            index.TryAdd(Entries[i], i + 1);
        }

        return index;
    }

    private static Dictionary<string, int> BuildNameIndex()
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Entries.Length; i++)
        {
            index.TryAdd(Entries[i].Name, i + 1);
        }

        return index;
    }
}

/// <summary>
/// One side's dynamic table (RFC 7541 section 2.3.2): newest entry first, at
/// index 62, evicting the oldest entries to stay within <see cref="MaxSize"/>.
/// </summary>
internal sealed class HpackDynamicTable(int maxSize)
{
    private readonly LinkedList<(string Name, string Value)> _entries = new();

    public int MaxSize { get; private set; } = maxSize;

    public int Size { get; private set; }

    public int Count => _entries.Count;

    /// <summary>The entry at <paramref name="index"/>, counting from 1 for the newest.</summary>
    public (string Name, string Value) this[int index]
    {
        get
        {
            if (index < 1 || index > _entries.Count)
            {
                throw new HpackException($"Dynamic table index {index} is out of range.");
            }

            var node = _entries.First!;
            for (var i = 1; i < index; i++)
            {
                node = node.Next!;
            }

            return node.Value;
        }
    }

    public void SetMaxSize(int maxSize)
    {
        MaxSize = maxSize;
        EvictTo(maxSize);
    }

    /// <summary>
    /// Adds an entry, evicting older ones to make room. An entry larger than
    /// the whole table empties it and isn't added (RFC 7541 section 4.4).
    /// </summary>
    public void Add(string name, string value)
    {
        var size = HpackText.EntrySize(name, value);
        if (size > MaxSize)
        {
            EvictTo(0);
            return;
        }

        EvictTo(MaxSize - size);
        _entries.AddFirst((name, value));
        Size += size;
    }

    /// <summary>The 1-based position of an entry with this name and value (exact), or this name (name only); 0 if none.</summary>
    public (int Exact, int Name) Find(string name, string value)
    {
        var position = 0;
        var nameMatch = 0;
        foreach (var entry in _entries)
        {
            position++;
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                if (string.Equals(entry.Value, value, StringComparison.Ordinal))
                {
                    return (position, position);
                }

                if (nameMatch == 0)
                {
                    nameMatch = position;
                }
            }
        }

        return (0, nameMatch);
    }

    private void EvictTo(int targetSize)
    {
        while (Size > targetSize && _entries.Last is { } oldest)
        {
            Size -= HpackText.EntrySize(oldest.Value.Name, oldest.Value.Value);
            _entries.RemoveLast();
        }
    }
}

/// <summary>HPACK's integer and string representations (RFC 7541 sections 5.1 and 5.2).</summary>
internal static class HpackPrimitives
{
    /// <summary>Writes <paramref name="value"/> with an <paramref name="prefixBits"/>-bit prefix, ORed into <paramref name="firstByteFlags"/>.</summary>
    public static void WriteInteger(int value, int prefixBits, byte firstByteFlags, List<byte> output)
    {
        var max = (1 << prefixBits) - 1;
        if (value < max)
        {
            output.Add((byte)(firstByteFlags | value));
            return;
        }

        output.Add((byte)(firstByteFlags | max));
        value -= max;
        while (value >= 128)
        {
            output.Add((byte)((value % 128) + 128));
            value /= 128;
        }

        output.Add((byte)value);
    }

    /// <summary>Reads an integer with an <paramref name="prefixBits"/>-bit prefix starting at <paramref name="position"/>.</summary>
    public static int ReadInteger(ReadOnlySpan<byte> block, ref int position, int prefixBits)
    {
        if (position >= block.Length)
        {
            throw new HpackException("The header block ends in the middle of an integer.");
        }

        var max = (1 << prefixBits) - 1;
        long value = block[position++] & max;
        if (value < max)
        {
            return (int)value;
        }

        var shift = 0;
        while (true)
        {
            if (position >= block.Length)
            {
                throw new HpackException("The header block ends in the middle of an integer.");
            }

            var b = block[position++];
            value += (long)(b & 0x7F) << shift;
            if (value > int.MaxValue)
            {
                throw new HpackException("An integer in the header block is too large.");
            }

            if ((b & 0x80) == 0)
            {
                return (int)value;
            }

            shift += 7;
        }
    }

    /// <summary>Writes a string literal, Huffman-encoded when that's shorter.</summary>
    public static void WriteString(string value, List<byte> output)
    {
        var bytes = HpackText.Encoding.GetBytes(value);
        var huffmanLength = Huffman.EncodedLength(bytes);
        if (huffmanLength < bytes.Length)
        {
            WriteInteger(huffmanLength, 7, 0x80, output);
            Huffman.Encode(bytes, output);
        }
        else
        {
            WriteInteger(bytes.Length, 7, 0x00, output);
            output.AddRange(bytes);
        }
    }

    public static string ReadString(ReadOnlySpan<byte> block, ref int position)
    {
        if (position >= block.Length)
        {
            throw new HpackException("The header block ends in the middle of a string.");
        }

        var huffman = (block[position] & 0x80) != 0;
        var length = ReadInteger(block, ref position, 7);
        if (length > block.Length - position)
        {
            throw new HpackException("A string runs past the end of the header block.");
        }

        var raw = block.Slice(position, length);
        position += length;
        return HpackText.Encoding.GetString(huffman ? Huffman.Decode(raw) : raw);
    }
}
