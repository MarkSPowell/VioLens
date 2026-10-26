using Violens.ProxyCore.Http2.Hpack;
using Xunit;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary>HPACK against the examples in RFC 7541 Appendix C, and its error cases.</summary>
public class HpackTests
{
    [Theory]
    [InlineData("www.example.com", "f1e3c2e5f23a6ba0ab90f4ff")]
    [InlineData("no-cache", "a8eb10649cbf")]
    [InlineData("custom-key", "25a849e95ba97d7f")]
    [InlineData("custom-value", "25a849e95bb8e8b4bf")]
    [InlineData("302", "6402")]
    [InlineData("private", "aec3771a4b")]
    public void HuffmanMatchesTheRfcExamples(string text, string hex)
    {
        var encoded = new List<byte>();
        Huffman.Encode(HpackText.Encoding.GetBytes(text), encoded);

        Assert.Equal(hex, Convert.ToHexStringLower([.. encoded]));
        Assert.Equal(text, HpackText.Encoding.GetString(Huffman.Decode(Convert.FromHexString(hex))));
    }

    [Fact]
    public void HuffmanRoundTripsEveryOctet()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var encoded = new List<byte>();
        Huffman.Encode(all, encoded);

        Assert.Equal(all, Huffman.Decode([.. encoded]));
        Assert.Equal(encoded.Count, Huffman.EncodedLength(all));
    }

    [Theory]
    [InlineData("ffffffff")] // EOS, then more than 7 bits of padding
    [InlineData("fe")] // '!' is 10 bits, so this is a partial code that isn't all ones
    [InlineData("f1e3c2e5f23a6ba0ab90f4fe")] // padding with a zero bit
    public void HuffmanRejectsBadInput(string hex)
    {
        Assert.Throws<HpackException>(() => Huffman.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void DecodesTheRfcLiteralExamples()
    {
        // C.2.1 to C.2.4, each with a fresh decoder.
        Assert.Equal([("custom-key", "custom-header")], Decode("400a637573746f6d2d6b65790d637573746f6d2d686561646572"));
        Assert.Equal([(":path", "/sample/path")], Decode("040c2f73616d706c652f70617468"));
        Assert.Equal([("password", "secret")], Decode("100870617373776f726406736563726574"));
        Assert.Equal([(":method", "GET")], Decode("82"));
    }

    [Fact]
    public void DecodesTheRfcRequestsWithoutHuffman()
    {
        // C.3: three requests on one connection, sharing the dynamic table.
        var decoder = new HpackDecoder(4096);
        Assert.Equal(FirstRequest, decoder.Decode(Convert.FromHexString("828684410f7777772e6578616d706c652e636f6d")));
        Assert.Equal(57, decoder.TableSize);
        Assert.Equal(SecondRequest, decoder.Decode(Convert.FromHexString("828684be58086e6f2d6361636865")));
        Assert.Equal(110, decoder.TableSize);
        Assert.Equal(ThirdRequest, decoder.Decode(Convert.FromHexString("828785bf400a637573746f6d2d6b65790c637573746f6d2d76616c7565")));
        Assert.Equal(164, decoder.TableSize);
    }

    [Fact]
    public void DecodesAndEncodesTheRfcRequestsWithHuffman()
    {
        // C.4: the same requests, Huffman-encoded. The encoder makes the same
        // choices the RFC's example does, so it reproduces every octet.
        string[] blocks =
        [
            "828684418cf1e3c2e5f23a6ba0ab90f4ff",
            "828684be5886a8eb10649cbf",
            "828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf",
        ];
        var decoder = new HpackDecoder(4096);
        var encoder = new HpackEncoder();
        var requests = new[] { FirstRequest, SecondRequest, ThirdRequest };
        for (var i = 0; i < blocks.Length; i++)
        {
            Assert.Equal(requests[i], decoder.Decode(Convert.FromHexString(blocks[i])));
            Assert.Equal(blocks[i], Convert.ToHexStringLower(encoder.Encode(requests[i])));
        }
    }

    [Fact]
    public void DecodesTheRfcResponsesWithEviction()
    {
        // C.5.1 and C.5.2: a 256-octet table, so the second response evicts the first's :status.
        var decoder = new HpackDecoder(4096);
        decoder.Decode(Convert.FromHexString("3fe101")); // size update to 256, as the example assumes
        Assert.Equal(
            [(":status", "302"), ("cache-control", "private"), ("date", "Mon, 21 Oct 2013 20:13:21 GMT"), ("location", "https://www.example.com")],
            decoder.Decode(Convert.FromHexString(
                "4803333032580770726976617465611d4d6f6e2c203231204f637420323031332032303a31333a323120474d546e1768747470733a2f2f7777772e6578616d706c652e636f6d")));
        Assert.Equal(222, decoder.TableSize);
        Assert.Equal(
            [(":status", "307"), ("cache-control", "private"), ("date", "Mon, 21 Oct 2013 20:13:21 GMT"), ("location", "https://www.example.com")],
            decoder.Decode(Convert.FromHexString("4803333037c1c0bf")));
        Assert.Equal(222, decoder.TableSize);
    }

    [Fact]
    public void DecodesTheRfcResponsesWithHuffman()
    {
        // C.6.1 and C.6.2.
        var decoder = new HpackDecoder(4096);
        decoder.Decode(Convert.FromHexString("3fe101"));
        Assert.Equal(
            [(":status", "302"), ("cache-control", "private"), ("date", "Mon, 21 Oct 2013 20:13:21 GMT"), ("location", "https://www.example.com")],
            decoder.Decode(Convert.FromHexString(
                "488264025885aec3771a4b6196d07abe941054d444a8200595040b8166e082a62d1bff6e919d29ad171863c78f0b97c8e9ae82ae43d3")));
        Assert.Equal(
            [(":status", "307"), ("cache-control", "private"), ("date", "Mon, 21 Oct 2013 20:13:21 GMT"), ("location", "https://www.example.com")],
            decoder.Decode(Convert.FromHexString("4883640effc1c0bf")));
    }

    [Fact]
    public void EncoderAndDecoderStayInStepOverManyBlocks()
    {
        var encoder = new HpackEncoder();
        var decoder = new HpackDecoder(4096);
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var headers = new List<(string, string)>
            {
                (":method", i % 3 == 0 ? "POST" : "GET"),
                (":scheme", "https"),
                (":path", $"/item/{random.Next(1000)}"),
                (":authority", $"host{random.Next(5)}.test"),
                ("x-request", $"value-{random.Next(50)}"),
                ("authorization", "Bearer secret"),
                ("x-big", new string('x', random.Next(0, 3000))),
            };
            Assert.Equal(headers, decoder.Decode(encoder.Encode(headers)));
        }
    }

    [Fact]
    public void EncoderAnnouncesASmallerTable()
    {
        var encoder = new HpackEncoder();
        var decoder = new HpackDecoder(4096);
        decoder.Decode(encoder.Encode([("x-one", "1"), ("x-two", "2")]));

        encoder.SetPeerMaxTableSize(0);
        var block = encoder.Encode([("x-one", "1")]);

        Assert.Equal(0x20, block[0]); // size update to 0 first
        Assert.Equal([("x-one", "1")], decoder.Decode(block));
        Assert.Equal(0, decoder.TableSize);
    }

    [Fact]
    public void SensitiveFieldsAreNeverIndexed()
    {
        var block = new HpackEncoder().Encode([("authorization", "Bearer secret")]);

        Assert.Equal(0x10, block[0] & 0xF0);
    }

    [Theory]
    [InlineData("be")] // dynamic index 62 with an empty table
    [InlineData("80")] // index 0
    [InlineData("3fe21f")] // size update larger than allowed
    [InlineData("823f")] // size update after a field (and truncated)
    [InlineData("82203f")] // size update after a field
    [InlineData("400a6375")] // string runs past the end
    [InlineData("ff8080808008")] // integer too large
    public void RejectsMalformedBlocks(string hex)
    {
        Assert.Throws<HpackException>(() => new HpackDecoder(4096).Decode(Convert.FromHexString(hex)));
    }

    private static readonly List<(string, string)> FirstRequest =
        [(":method", "GET"), (":scheme", "http"), (":path", "/"), (":authority", "www.example.com")];

    private static readonly List<(string, string)> SecondRequest =
        [(":method", "GET"), (":scheme", "http"), (":path", "/"), (":authority", "www.example.com"), ("cache-control", "no-cache")];

    private static readonly List<(string, string)> ThirdRequest =
        [(":method", "GET"), (":scheme", "https"), (":path", "/index.html"), (":authority", "www.example.com"), ("custom-key", "custom-value")];

    private static List<(string Name, string Value)> Decode(string hex) =>
        new HpackDecoder(4096).Decode(Convert.FromHexString(hex));
}
