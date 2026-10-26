namespace Violens.ProxyCore.Http2.Hpack;

/// <summary>
/// Encodes header blocks (RFC 7541). One per connection direction; blocks
/// must be sent in the order they're encoded, since each may change the
/// dynamic table the peer's decoder mirrors.
/// </summary>
internal sealed class HpackEncoder
{
    /// <summary>The most this encoder grows its table to, whatever the peer allows.</summary>
    public const int PreferredTableSize = 4096;

    // Sent never-indexed, so intermediaries don't store them (RFC 7541 section 7.1.3).
    private static readonly HashSet<string> Sensitive = new(StringComparer.Ordinal)
    {
        "authorization",
        "proxy-authorization",
    };

    // Values that differ on nearly every message, so indexing them only evicts useful entries.
    private static readonly HashSet<string> NotWorthIndexing = new(StringComparer.Ordinal)
    {
        ":path",
        "content-length",
        "date",
        "etag",
        "if-modified-since",
        "if-none-match",
        "last-modified",
        "location",
        "set-cookie",
    };

    private readonly HpackDynamicTable _table = new(PreferredTableSize);
    private int? _pendingSizeUpdate;

    /// <summary>
    /// Applies the peer's SETTINGS_HEADER_TABLE_SIZE. The next block starts with
    /// a size update when the table's size has to change.
    /// </summary>
    public void SetPeerMaxTableSize(int peerMaxTableSize)
    {
        var size = Math.Min(peerMaxTableSize, PreferredTableSize);
        if (size != _table.MaxSize)
        {
            _table.SetMaxSize(size);
            _pendingSizeUpdate = size;
        }
    }

    public byte[] Encode(IReadOnlyList<(string Name, string Value)> headers)
    {
        var output = new List<byte>(headers.Count * 16);
        if (_pendingSizeUpdate is { } size)
        {
            HpackPrimitives.WriteInteger(size, 5, 0x20, output);
            _pendingSizeUpdate = null;
        }

        foreach (var (name, value) in headers)
        {
            EncodeField(name, value, output);
        }

        return output.ToArray();
    }

    private void EncodeField(string name, string value, List<byte> output)
    {
        var staticExact = HpackStaticTable.FindExact(name, value);
        if (staticExact != 0)
        {
            HpackPrimitives.WriteInteger(staticExact, 7, 0x80, output);
            return;
        }

        var (dynamicExact, dynamicName) = _table.Find(name, value);
        if (dynamicExact != 0)
        {
            HpackPrimitives.WriteInteger(HpackStaticTable.Count + dynamicExact, 7, 0x80, output);
            return;
        }

        var nameIndex = HpackStaticTable.FindName(name);
        if (nameIndex == 0 && dynamicName != 0)
        {
            nameIndex = HpackStaticTable.Count + dynamicName;
        }

        if (Sensitive.Contains(name))
        {
            WriteLiteral(name, value, nameIndex, prefixBits: 4, flags: 0x10, output);
        }
        else if (NotWorthIndexing.Contains(name) || HpackText.EntrySize(name, value) > _table.MaxSize / 2)
        {
            WriteLiteral(name, value, nameIndex, prefixBits: 4, flags: 0x00, output);
        }
        else
        {
            WriteLiteral(name, value, nameIndex, prefixBits: 6, flags: 0x40, output);
            _table.Add(name, value);
        }
    }

    private static void WriteLiteral(string name, string value, int nameIndex, int prefixBits, byte flags, List<byte> output)
    {
        HpackPrimitives.WriteInteger(nameIndex, prefixBits, flags, output);
        if (nameIndex == 0)
        {
            HpackPrimitives.WriteString(name, output);
        }

        HpackPrimitives.WriteString(value, output);
    }
}
