namespace Violens.ProxyCore.Http2.Hpack;

/// <summary>
/// Decodes header blocks (RFC 7541 section 6). One per connection direction:
/// its dynamic table follows the peer's encoder, so every block must be
/// decoded, in order, even for streams that are being discarded.
/// </summary>
internal sealed class HpackDecoder(int maxTableSize)
{
    private readonly HpackDynamicTable _table = new(maxTableSize);

    /// <summary>The most the peer's encoder may grow the table to: our SETTINGS_HEADER_TABLE_SIZE.</summary>
    public int MaxTableSize { get; } = maxTableSize;

    public int TableSize => _table.Size;

    /// <summary>
    /// Decodes one complete header block. Throws <see cref="HpackException"/>
    /// when it's malformed, which is a connection error.
    /// </summary>
    public List<(string Name, string Value)> Decode(ReadOnlySpan<byte> block)
    {
        var headers = new List<(string Name, string Value)>();
        var position = 0;
        var sizeUpdateAllowed = true;
        while (position < block.Length)
        {
            var first = block[position];
            if ((first & 0x80) != 0)
            {
                // Indexed header field (6.1).
                var index = HpackPrimitives.ReadInteger(block, ref position, 7);
                headers.Add(Lookup(index));
                sizeUpdateAllowed = false;
            }
            else if ((first & 0xC0) == 0x40)
            {
                // Literal with incremental indexing (6.2.1).
                var (name, value) = ReadLiteral(block, ref position, 6);
                _table.Add(name, value);
                headers.Add((name, value));
                sizeUpdateAllowed = false;
            }
            else if ((first & 0xE0) == 0x20)
            {
                // Dynamic table size update (6.3): only at the start of a block.
                if (!sizeUpdateAllowed)
                {
                    throw new HpackException("A dynamic table size update came after a header field.");
                }

                var size = HpackPrimitives.ReadInteger(block, ref position, 5);
                if (size > MaxTableSize)
                {
                    throw new HpackException($"The dynamic table size update ({size}) is larger than allowed ({MaxTableSize}).");
                }

                _table.SetMaxSize(size);
            }
            else
            {
                // Literal without indexing (0000) or never indexed (0001) (6.2.2, 6.2.3).
                headers.Add(ReadLiteral(block, ref position, 4));
                sizeUpdateAllowed = false;
            }
        }

        return headers;
    }

    private (string Name, string Value) ReadLiteral(ReadOnlySpan<byte> block, ref int position, int prefixBits)
    {
        var nameIndex = HpackPrimitives.ReadInteger(block, ref position, prefixBits);
        var name = nameIndex == 0 ? HpackPrimitives.ReadString(block, ref position) : Lookup(nameIndex).Name;
        var value = HpackPrimitives.ReadString(block, ref position);
        return (name, value);
    }

    private (string Name, string Value) Lookup(int index)
    {
        if (index == 0)
        {
            throw new HpackException("Index 0 isn't valid.");
        }

        return index <= HpackStaticTable.Count
            ? HpackStaticTable.Entries[index - 1]
            : _table[index - HpackStaticTable.Count];
    }
}
