using System.IO.Compression;
using System.Text;
using Violens.Extensibility.Inspection;
using Violens.Extensibility.Inspection.Inspectors;
using Violens.ProxyCore;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.Extensibility.Tests;

/// <summary>The JSON, WebForms and ImageView tabs.</summary>
public class BodyInspectorTests
{
    private static InspectorContext Response(IReadOnlyList<(string Name, string Value)> headers, byte[] body) =>
        new(MakeSession(new CapturedRequest("GET", "/", "HTTP/1.1", [], []), new CapturedResponse("HTTP/1.1", 200, "OK", headers, body)),
            InspectorSide.Response);

    private static InspectorContext Request(string target, IReadOnlyList<(string Name, string Value)> headers, byte[] body) =>
        new(MakeSession(new CapturedRequest("POST", target, "HTTP/1.1", headers, body), new CapturedResponse("HTTP/1.1", 200, "OK", [], [])),
            InspectorSide.Request);

    private static Session MakeSession(CapturedRequest request, CapturedResponse response) =>
        new(1, "example.test", DateTimeOffset.UnixEpoch, request, response, SessionState.Done);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ---- JSON ----

    [Fact]
    public void JsonIsPrettyPrintedAndKeepsNonAsciiReadable()
    {
        var context = Response([("Content-Type", "application/json; charset=utf-8")], Utf8("{\"name\":\"Zoë\",\"list\":[1,2]}"));
        var inspector = new JsonInspector();

        Assert.True(inspector.CanInspect(context));
        var text = Assert.IsType<TextContent>(inspector.Inspect(context));

        Assert.Equal("json", text.SyntaxHint);
        Assert.Contains("\"name\": \"Zoë\"", text.Text);
        Assert.Contains("\n", text.Text);
    }

    [Fact]
    public void JsonIsDecompressedFirst()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Utf8("[true]"));
        }

        var context = Response([("Content-Type", "application/json"), ("Content-Encoding", "gzip")], buffer.ToArray());

        var text = Assert.IsType<TextContent>(new JsonInspector().Inspect(context));
        Assert.Contains("true", text.Text);
    }

    [Theory]
    [InlineData("application/problem+json")]
    [InlineData("text/json")]
    public void OtherJsonMediaTypesCount(string contentType)
    {
        Assert.True(new JsonInspector().CanInspect(Response([("Content-Type", contentType)], Utf8("{}"))));
    }

    [Fact]
    public void JsonWithoutAContentTypeIsSniffed()
    {
        Assert.True(new JsonInspector().CanInspect(Response([], Utf8("  {\"a\":1}"))));
        Assert.False(new JsonInspector().CanInspect(Response([], Utf8("hello"))));
        Assert.False(new JsonInspector().CanInspect(Response([("Content-Type", "text/html")], Utf8("{\"a\":1}"))));
    }

    [Fact]
    public void AnAntiHijackingPrefixIsSkipped()
    {
        var text = Assert.IsType<TextContent>(new JsonInspector().Inspect(Response([("Content-Type", "application/json")], Utf8(")]}'\n{\"a\":1}"))));
        Assert.Contains("\"a\": 1", text.Text);
    }

    [Fact]
    public void InvalidJsonGetsAnExplanation()
    {
        var error = Assert.IsType<ErrorContent>(new JsonInspector().Inspect(Response([("Content-Type", "application/json")], Utf8("{\"a\":"))));
        Assert.Contains("isn't valid JSON", error.Message);
    }

    // ---- WebForms ----

    [Fact]
    public void QueryStringParametersAreDecoded()
    {
        var context = Request("/search?q=hello+world&lang=en%2Dgb&flag#top", [], []);
        var inspector = new WebFormsInspector();

        Assert.True(inspector.CanInspect(context));
        var rows = Assert.IsType<KeyValueContent>(inspector.Inspect(context)).Rows;

        Assert.Equal(
            new[] { ("q", "hello world"), ("lang", "en-gb"), ("flag", "") },
            rows.Select(r => (r.Name, r.Value)).ToArray());
    }

    [Fact]
    public void QueryAndFormBodyAreBothShownWithMarkers()
    {
        var context = Request("/login?next=%2Fhome", [("Content-Type", "application/x-www-form-urlencoded")], Utf8("user=z%C3%B6e&pass=x"));

        var rows = Assert.IsType<KeyValueContent>(new WebFormsInspector().Inspect(context)).Rows;

        Assert.Equal(
            new[] { ("[Query string]", ""), ("next", "/home"), ("[Body]", ""), ("user", "zöe"), ("pass", "x") },
            rows.Select(r => (r.Name, r.Value)).ToArray());
    }

    [Fact]
    public void MultipartFieldsAndFilesAreListed()
    {
        const string boundary = "----b0undary";
        var body = Utf8(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nHéllo\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"upload\"; filename=\"a.png\"\r\nContent-Type: image/png\r\n\r\n12345\r\n" +
            $"--{boundary}--\r\n");
        var context = Request("/upload", [("Content-Type", $"multipart/form-data; boundary={boundary}")], body);

        var rows = Assert.IsType<KeyValueContent>(new WebFormsInspector().Inspect(context)).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(("title", "Héllo"), (rows[0].Name, rows[0].Value));
        Assert.Equal(("upload", "(file: a.png, image/png, 5 bytes)"), (rows[1].Name, rows[1].Value));
    }

    [Fact]
    public void WebFormsIsRequestOnlyAndNeedsSomethingToShow()
    {
        var inspector = new WebFormsInspector();
        Assert.False(inspector.CanInspect(Request("/plain", [], [])));
        Assert.False(inspector.CanInspect(Request("/plain", [("Content-Type", "application/json")], Utf8("{}"))));
        Assert.False(inspector.CanInspect(Response([], [])));
    }

    // ---- ImageView ----

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void PngIsRecognisedWithItsDimensions()
    {
        var context = Response([("Content-Type", "image/png")], OnePixelPng);
        var inspector = new ImageInspector();

        Assert.True(inspector.CanInspect(context));
        var image = Assert.IsType<ImageContent>(inspector.Inspect(context));

        Assert.Equal(OnePixelPng, image.Bytes);
        Assert.Contains("PNG", image.Summary);
        Assert.Contains("1 × 1 pixels", image.Summary);
    }

    [Fact]
    public void AnImageIsSniffedEvenWithTheWrongContentType()
    {
        Assert.True(new ImageInspector().CanInspect(Response([("Content-Type", "application/octet-stream")], OnePixelPng)));
    }

    [Fact]
    public void SvgAndTextAreLeftToOtherTabs()
    {
        Assert.False(new ImageInspector().CanInspect(Response([("Content-Type", "image/svg+xml")], Utf8("<svg/>"))));
        Assert.False(new ImageInspector().CanInspect(Response([("Content-Type", "text/plain")], Utf8("hello"))));
    }

    [Fact]
    public void GifAndJpegDimensionsAreRead()
    {
        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x10, 0x00, 0x20, 0x00, 0, 0, 0 };
        Assert.True(ImageInspector.TryGetDimensions(gif, "GIF", out var w, out var h));
        Assert.Equal((16, 32), (w, h));

        // SOI, an APP0 segment of length 4, then SOF0 with height 0x0102 and width 0x0304.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x02, 0x03, 0x04, 0x03 };
        Assert.Equal("JPEG", ImageInspector.DetectFormat(jpeg));
        Assert.True(ImageInspector.TryGetDimensions(jpeg, "JPEG", out w, out h));
        Assert.Equal((0x0304, 0x0102), (w, h));
    }

    // ---- Ordering ----

    [Fact]
    public void NewTabsSortAfterRawAndBeforeHex()
    {
        var context = Response([("Content-Type", "application/json")], Utf8("{}"));

        var names = InspectorRegistry.CreateDefault().GetApplicable(context).Select(i => i.DisplayName).ToArray();

        Assert.Equal(new[] { "Headers", "Raw", "JSON", "Tree", "Hex" }, names);
    }
}
