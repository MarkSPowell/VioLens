using System.IO.Compression;
using System.Text;
using Violens.Extensibility.Inspection;
using Violens.Extensibility.Inspection.Inspectors;
using Violens.ProxyCore;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.Extensibility.Tests;

/// <summary>
/// The Tree tab: JSON, CBOR and MessagePack read into a tree, and written
/// back out for Copy Subtree. The CBOR cases are from RFC 8949, appendix A.
/// </summary>
public class TreeInspectorTests
{
    private static InspectorContext Response(string contentType, byte[] body, string? contentEncoding = null)
    {
        var headers = new List<(string, string)> { ("Content-Type", contentType) };
        if (contentEncoding is not null)
        {
            headers.Add(("Content-Encoding", contentEncoding));
        }

        var session = new Session(1, "example.test", DateTimeOffset.UnixEpoch,
            new CapturedRequest("GET", "/", "HTTP/1.1", [], []),
            new CapturedResponse("HTTP/1.1", 200, "OK", headers, body),
            SessionState.Done);
        return new InspectorContext(session, InspectorSide.Response);
    }

    private static TreeContent Inspect(string contentType, byte[] body, int maxNodes = 10_000) =>
        Assert.IsType<TreeContent>(new TreeInspector(maxNodes).Inspect(Response(contentType, body)));

    private static TreeNode Single(string contentType, string hex) =>
        Assert.Single(Inspect(contentType, Convert.FromHexString(hex)).Roots);

    private static string Cbor(string hex) => TreeText.Write(Single("application/cbor", hex));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ---- When it's offered ----

    [Theory]
    [InlineData("application/json", "{}", true)]
    [InlineData("application/problem+json", "{}", true)]
    [InlineData("text/plain", "[1, 2]", true)]
    [InlineData("text/plain", "hello", false)]
    [InlineData("text/html", "{}", false)]
    [InlineData("application/cbor", " ", true)]
    [InlineData("application/cose+cbor", " ", true)]
    [InlineData("application/cbor-seq", " ", true)]
    [InlineData("application/msgpack", "\u0080", true)]
    [InlineData("application/x-msgpack", "\u0080", true)]
    [InlineData("application/vnd.msgpack", "\u0080", true)]
    [InlineData("application/octet-stream", " ", false)]
    public void OfferedForJsonCborAndMessagePack(string contentType, string body, bool expected)
    {
        Assert.Equal(expected, new TreeInspector().CanInspect(Response(contentType, Encoding.Latin1.GetBytes(body))));
    }

    [Fact]
    public void ComesRightAfterTheJsonTab()
    {
        var context = Response("application/json", Utf8("{\"a\":1}"));

        var names = InspectorRegistry.CreateDefault().GetApplicable(context).Select(i => i.DisplayName).ToList();

        Assert.Equal(names.IndexOf("JSON") + 1, names.IndexOf("Tree"));
    }

    // ---- JSON ----

    [Fact]
    public void JsonBecomesATreeWithLabels()
    {
        var content = Inspect("application/json", Utf8("{\"name\":\"Zoë\",\"list\":[1,2.50,true,null],\"empty\":{}}"));

        var root = Assert.Single(content.Roots);
        Assert.Equal(TreeNodeKind.Object, root.Kind);
        Assert.Equal("{3}", root.Label);
        Assert.Equal(["name: \"Zoë\"", "list [4]", "empty {0}"], root.Children.Select(c => c.Label));
        Assert.Equal(["[0]: 1", "[1]: 2.50", "[2]: true", "[3]: null"], root.Children[1].Children.Select(c => c.Label));
        Assert.Equal("JSON, 8 values.", content.Summary);
    }

    [Fact]
    public void JsonCopySubtreeIsIndentedJson()
    {
        var root = Inspect("application/json", Utf8("{\"a\":[1,{\"b\":\"x\\\"y\"}],\"c\":[]}")).Roots[0];

        Assert.Equal(
            "{\n  \"a\": [\n    1,\n    {\n      \"b\": \"x\\\"y\"\n    }\n  ],\n  \"c\": []\n}",
            TreeText.Write(root));
        Assert.Equal("[\n  1,\n  {\n    \"b\": \"x\\\"y\"\n  }\n]", TreeText.Write(root.Children[0]));
    }

    [Fact]
    public void CopyTakesAStringsTextUnquoted()
    {
        var root = Inspect("application/json", Utf8("{\"url\":\"https://example.test/?a=1\",\"n\":3}")).Roots[0];

        Assert.Equal("https://example.test/?a=1", root.Children[0].CopyText);
        Assert.Equal("3", root.Children[1].CopyText);
        Assert.Equal(TreeText.Write(root), root.CopyText);
    }

    [Fact]
    public void LongStringsAreShortenedInTheLabelOnly()
    {
        var text = new string('x', 500);
        var node = Inspect("application/json", Utf8($"[\"{text}\"]")).Roots[0].Children[0];

        Assert.EndsWith("…\"", node.Label);
        Assert.True(node.Label.Length < 250);
        Assert.Equal(text, node.CopyText);
    }

    [Fact]
    public void InvalidJsonExplainsWhy()
    {
        var content = new TreeInspector().Inspect(Response("application/json", Utf8("{\"a\":")));

        Assert.Contains("isn't valid JSON", Assert.IsType<ErrorContent>(content).Message);
    }

    [Fact]
    public void ALargeTreeIsCutShortAndSaysSo()
    {
        var content = Inspect("application/json", Utf8("[0,1,2,3,4,5,6,7,8,9]"), maxNodes: 5);

        var root = content.Roots[0];
        Assert.Equal(5, root.Children.Count);
        Assert.Equal(TreeNodeKind.Omitted, root.Children[^1].Kind);
        Assert.Equal("6 more not shown", root.Children[^1].Label);
        Assert.Contains("Only the first 5 values are shown; the JSON tab has the whole body.", content.Summary);
        Assert.Equal("[\n  0,\n  1,\n  2,\n  3\n  /* 6 more not shown */\n]", TreeText.Write(root));
    }

    // ---- CBOR ----

    [Theory]
    [InlineData("00", "0")]
    [InlineData("1903e8", "1000")]
    [InlineData("1bffffffffffffffff", "18446744073709551615")]
    [InlineData("3903e7", "-1000")]
    [InlineData("3bffffffffffffffff", "-18446744073709551616")]
    [InlineData("f93c00", "1.0")]
    [InlineData("f98000", "-0.0")]
    [InlineData("f97c00", "Infinity")]
    [InlineData("f97e00", "NaN")]
    [InlineData("fa47c35000", "100000.0")]
    [InlineData("fb3ff199999999999a", "1.1")]
    [InlineData("fb7e37e43c8800759c", "1E+300")]
    [InlineData("f4", "false")]
    [InlineData("f6", "null")]
    [InlineData("f7", "undefined")]
    [InlineData("f0", "simple(16)")]
    [InlineData("f8ff", "simple(255)")]
    [InlineData("4401020304", "h'01020304'")]
    [InlineData("40", "h''")]
    [InlineData("62225c", "\"\\\"\\\\\"")]
    [InlineData("64f0908591", "\"\U00010151\"")]
    [InlineData("80", "[]")]
    [InlineData("a0", "{}")]
    [InlineData("c074323031332d30332d32315432303a30343a30305a", "0(\"2013-03-21T20:04:00Z\")")]
    [InlineData("d82076687474703a2f2f7777772e6578616d706c652e636f6d", "32(\"http://www.example.com\")")]
    public void CborValues(string hex, string expected)
    {
        Assert.Equal(expected, Cbor(hex));
    }

    [Fact]
    public void CborContainersAndIndefiniteLengths()
    {
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    2,\n    3\n  ]\n}", Cbor("a26161016162820203"));
        Assert.Equal("{\n  \"Fun\": true,\n  \"Amt\": -2\n}", Cbor("bf6346756ef563416d7421ff"));
        Assert.Equal("[\n  1,\n  [\n    2,\n    3\n  ],\n  [\n    4,\n    5\n  ]\n]", Cbor("9f018202039f0405ffff"));
        Assert.Equal("[]", Cbor("9fff"));
        Assert.Equal("h'0102030405'", Cbor("5f42010243030405ff"));
        Assert.Equal("\"streaming\"", Cbor("7f657374726561646d696e67ff"));
    }

    [Fact]
    public void CborNonTextKeysAreWrittenWithoutQuotes()
    {
        var root = Single("application/cbor", "a201020304");

        Assert.Equal(["1: 2", "3: 4"], root.Children.Select(c => c.Label));
        Assert.Equal("{\n  1: 2,\n  3: 4\n}", TreeText.Write(root));
    }

    [Fact]
    public void CborTagsGetNotes()
    {
        var epoch = Single("application/cbor", "c11a514b67b0");
        Assert.Equal("tag 1  (epoch time, 2013-03-21T20:04:00Z)", epoch.Label);
        Assert.Equal("1(1363896240)", TreeText.Write(epoch));

        Assert.Equal("epoch time, 2013-03-21T20:04:00.5Z", Single("application/cbor", "c1fb41d452d9ec200000").Note);
        Assert.Equal("bignum, 18446744073709551616", Single("application/cbor", "c249010000000000000000").Note);
        Assert.Equal("bignum, -18446744073709551617", Single("application/cbor", "c349010000000000000000").Note);
        Assert.Equal("UUID 8c8a8d48-68b6-4ff3-9fc4-9c1b2a3ce5d1",
            Single("application/cbor", "d825508c8a8d4868b64ff39fc49c1b2a3ce5d1").Note);
    }

    [Fact]
    public void ACborSequenceHasOneRootPerItem()
    {
        var content = Inspect("application/cbor-seq", Convert.FromHexString("0161618102"));

        Assert.Equal(["[0]: 1", "[1]: \"a\"", "[2] [1]"], content.Roots.Select(r => r.Label));
        Assert.Equal("CBOR sequence of 3 items, 4 values.", content.Summary);
    }

    [Fact]
    public void CborIsDecompressedFirst()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Convert.FromHexString("a1616101"));
        }

        var content = Assert.IsType<TreeContent>(new TreeInspector().Inspect(Response("application/cbor", buffer.ToArray(), "gzip")));

        Assert.Equal("a: 1", content.Roots[0].Children[0].Label);
    }

    [Fact]
    public void ABinaryTreeCutShortIsStillReadToTheEnd()
    {
        // [0, 1, ..., 9], then 7: the second item must still be found after
        // the first one's hidden values are skipped.
        var content = Inspect("application/cbor-seq", Convert.FromHexString("8a0001020304050607080907"), maxNodes: 3);

        Assert.Equal(2, content.Roots.Count);
        Assert.Equal(["[0]: 0", "[1]: 1", "8 more not shown"], content.Roots[0].Children.Select(c => c.Label));
        Assert.Equal("1 more not shown", content.Roots[1].Label);
        Assert.Contains("CBOR sequence of 2 items. Only the first 3 values are shown; the Hex tab has the whole body.", content.Summary);
    }

    [Theory]
    [InlineData("6261", "more than the 1 left")]
    [InlineData("9bffffffffffffffff", "more than the bytes left")]
    [InlineData("ff", "break")]
    [InlineData("1c", "reserved")]
    [InlineData("1f", "indefinite length")]
    [InlineData("5f61616161ff", "definite-length chunks")]
    [InlineData("62c328", "UTF-8")]
    [InlineData("9f01", "ends in the middle")]
    public void InvalidCborExplainsWhy(string hex, string expected)
    {
        var content = new TreeInspector().Inspect(Response("application/cbor", Convert.FromHexString(hex)));

        var error = Assert.IsType<ErrorContent>(content).Message;
        Assert.StartsWith("The body isn't valid CBOR:", error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void DeeplyNestedCborIsRefused()
    {
        var bytes = Enumerable.Repeat((byte)0x81, 300).Append((byte)0x00).ToArray();

        var content = new TreeInspector().Inspect(Response("application/cbor", bytes));

        Assert.Contains("nested more than 256 deep", Assert.IsType<ErrorContent>(content).Message);
    }

    // ---- MessagePack ----

    [Theory]
    [InlineData("00", "0")]
    [InlineData("7f", "127")]
    [InlineData("ff", "-1")]
    [InlineData("e0", "-32")]
    [InlineData("cc80", "128")]
    [InlineData("cdffff", "65535")]
    [InlineData("cfffffffffffffffff", "18446744073709551615")]
    [InlineData("d080", "-128")]
    [InlineData("d38000000000000000", "-9223372036854775808")]
    [InlineData("ca3fc00000", "1.5")]
    [InlineData("cb3ff199999999999a", "1.1")]
    [InlineData("c0", "null")]
    [InlineData("c2", "false")]
    [InlineData("c3", "true")]
    [InlineData("a3616263", "\"abc\"")]
    [InlineData("d903616263", "\"abc\"")]
    [InlineData("c4020102", "h'0102'")]
    [InlineData("90", "[]")]
    [InlineData("80", "{}")]
    [InlineData("dc0002c3c2", "[\n  true,\n  false\n]")]
    public void MessagePackValues(string hex, string expected)
    {
        Assert.Equal(expected, TreeText.Write(Single("application/msgpack", hex)));
    }

    [Fact]
    public void MessagePackMapsAndArrays()
    {
        var root = Single("application/msgpack", "82a16101a16293c3c0ff");

        Assert.Equal(["a: 1", "b [3]"], root.Children.Select(c => c.Label));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    true,\n    null,\n    -1\n  ]\n}", TreeText.Write(root));
    }

    [Fact]
    public void MessagePackExtensionsShowTheirTypeAndTimestampsTheirDate()
    {
        Assert.Equal("extension type 5", Single("application/msgpack", "d4052a").Note);
        Assert.Equal("h'2a'", Single("application/msgpack", "d4052a").Value);
        Assert.Equal("timestamp, 2013-03-21T20:04:00Z", Single("application/msgpack", "d6ff514b67b0").Note);
        // 64-bit form: 500,000,000 nanoseconds and the same seconds.
        Assert.Equal("timestamp, 2013-03-21T20:04:00.5Z", Single("application/msgpack", "d7ff77359400514b67b0").Note);
        Assert.Equal("timestamp, 2013-03-21T20:04:00Z", Single("application/msgpack", "c70cff0000000000000000514b67b0").Note);
    }

    [Fact]
    public void AMessagePackStreamHasOneRootPerValue()
    {
        var content = Inspect("application/x-msgpack", Convert.FromHexString("0102c3"));

        Assert.Equal(["[0]: 1", "[1]: 2", "[2]: true"], content.Roots.Select(r => r.Label));
    }

    [Theory]
    [InlineData("c1", "never used")]
    [InlineData("a361", "more than the")]
    [InlineData("ddffffffff", "more than the bytes left")]
    public void InvalidMessagePackExplainsWhy(string hex, string expected)
    {
        var content = new TreeInspector().Inspect(Response("application/msgpack", Convert.FromHexString(hex)));

        var error = Assert.IsType<ErrorContent>(content).Message;
        Assert.StartsWith("The body isn't valid MessagePack:", error);
        Assert.Contains(expected, error);
    }
}
