using System.IO.Compression;
using System.Text;
using Violens.Compatibility.Extensions;
using Violens.CompatShim;
using Violens.ProxyCore.Preferences;
using Xunit;
using ShimImporter = Violens.CompatShim.ISessionImporter;
using ShimProfferFormat = Violens.CompatShim.ProfferFormatAttribute;
using ShimProgress = Violens.CompatShim.ProgressCallbackEventArgs;
using ShimSession = Violens.CompatShim.Session;

namespace Violens.Compatibility.Tests;

/// <summary>
/// The Fiddler-shaped compatibility layer (<c>Violens.CompatShim</c>) on
/// its own, without any real extension. The end-to-end test against Eric
/// Lawrence's real NetLog importer lives in tests/ExtensionPorts. Runs on
/// both CI legs, like everything else here.
/// </summary>
public class CompatShimHeaderTests
{
    [Fact]
    public void AMissingHeaderReadsAsNullNotEmpty()
    {
        var headers = new HTTPRequestHeaders("/", ["Accept: */*"]);

        Assert.Null(headers["X-Missing"]);
        Assert.Equal("*/*", headers["accept"]);
    }

    [Fact]
    public void AddKeepsRepeatsAndTheIndexerReadsTheFirst()
    {
        var headers = new HTTPResponseHeaders(200, "OK", []);
        headers.Add("Content-Security-Policy-Report-Only", "one");
        headers.Add("Content-Security-Policy-Report-Only", "two");

        Assert.Equal(2, headers.Count());
        Assert.Equal("one", headers["content-security-policy-report-only"]);
        Assert.Equal("one, two", headers.AllValues("Content-Security-Policy-Report-Only"));
    }

    [Fact]
    public void RemoveAndRenameActOnEveryHeaderOfThatName()
    {
        var headers = new HTTPResponseHeaders(200, "OK", ["Content-Encoding: gzip", "Content-Encoding: br", "X-Keep: 1"]);

        Assert.True(headers.RenameHeaderItems("content-encoding", "X-Removed-Content-Encoding"));
        Assert.False(headers.Exists("Content-Encoding"));
        Assert.Equal(2, headers.FindAll("X-Removed-Content-Encoding").Count);

        headers.Remove("X-Removed-Content-Encoding");
        Assert.Equal(1, headers.Count());
        Assert.False(headers.RenameHeaderItems("Not-There", "Whatever"));
    }

    [Fact]
    public void SettingTheIndexerUpdatesOrAddsAndNullRemoves()
    {
        var headers = new HTTPRequestHeaders("/", ["Host: a.test"]);

        headers["Host"] = "b.test";
        headers["X-New"] = "1";
        Assert.Equal("b.test", headers["Host"]);
        Assert.Equal("1", headers["X-New"]);

        headers["X-New"] = null;
        Assert.False(headers.Exists("X-New"));
    }

    [Fact]
    public void GetTokenValueReadsCharset()
    {
        var headers = new HTTPResponseHeaders(200, "OK", ["Content-Type: text/html; charset=\"ISO-8859-1\""]);

        Assert.Equal("ISO-8859-1", headers.GetTokenValue("Content-Type", "charset"));
        Assert.Null(headers.GetTokenValue("Content-Type", "boundary"));
    }

    [Fact]
    public void TheConvenienceConstructorsSetStatusAndDefaults()
    {
        var response = new HTTPResponseHeaders(404, ["Content-Length: 0"]);
        Assert.Equal(404, response.HTTPResponseCode);
        Assert.Equal("Not Found", response.StatusDescription);
        Assert.Equal("404 Not Found", response.HTTPResponseStatus);

        var request = new HTTPRequestHeaders("/file.json", ["Host: IMPORTED"]);
        Assert.Equal("GET", request.HTTPMethod);
        Assert.Equal("HTTP/1.1", request.HTTPVersion);
        Assert.Equal("GET /file.json HTTP/1.1\r\nHost: IMPORTED", request.ToString());
    }
}

public class CompatShimParserTests
{
    [Fact]
    public void ParsesAnAbsoluteFormRequestLikeANetLogCapture()
    {
        var request = Parser.ParseRequest("GET https://example.test/a?b=1 HTTP/1.1\r\nHost: example.test\r\nAccept: */*");

        Assert.NotNull(request);
        Assert.Equal("GET", request.HTTPMethod);
        Assert.Equal("https://example.test/a?b=1", request.RequestPath);
        Assert.Equal("https", request.UriScheme);
        Assert.Equal("example.test", request["Host"]);
        Assert.Equal(2, request.Count());
    }

    [Fact]
    public void AcceptsBareLfATrailingBlankLineAndIgnoresABody()
    {
        var request = Parser.ParseRequest("POST /submit HTTP/1.1\nHost: a.test\n\nthis is the body: not a header");

        Assert.NotNull(request);
        Assert.Equal("/submit", request.RequestPath);
        Assert.Equal(1, request.Count());
    }

    [Fact]
    public void SplitsPseudoHeadersAtTheSecondColon()
    {
        var request = Parser.ParseRequest("GET / HTTP/1.1\r\n:authority: a.test\r\nx: y");

        Assert.NotNull(request);
        Assert.Equal("a.test", request[":authority"]);
    }

    [Fact]
    public void ParsesAStatusLineWithOrWithoutADescription()
    {
        var withText = Parser.ParseResponse("HTTP/1.1 200 OK\r\nContent-Type: text/plain");
        Assert.NotNull(withText);
        Assert.Equal(200, withText.HTTPResponseCode);
        Assert.Equal("OK", withText.StatusDescription);
        Assert.Equal("text/plain", withText["Content-Type"]);

        var withoutText = Parser.ParseResponse("HTTP/2 204");
        Assert.NotNull(withoutText);
        Assert.Equal(204, withoutText.HTTPResponseCode);
        Assert.Equal("HTTP/2", withoutText.HTTPVersion);
        Assert.Equal("No Content", withoutText.StatusDescription);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GARBAGE")]
    public void UnparseableInputReturnsNull(string text)
    {
        Assert.Null(Parser.ParseRequest(text));
        Assert.Null(Parser.ParseResponse(text));
    }

    [Fact]
    public void ANonNumericStatusIsNotAResponse()
    {
        Assert.Null(Parser.ParseResponse("HTTP/1.1 OK fine"));
    }
}

public class CompatShimSessionTests
{
    private static ShimSession Build(string requestText, string responseText = "HTTP/1.1 200 OK", byte[]? responseBody = null, SessionFlags flags = SessionFlags.None) =>
        ShimSession.BuildFromData(false, Parser.ParseRequest(requestText)!, Utilities.emptyByteArray, Parser.ParseResponse(responseText)!, responseBody ?? Utilities.emptyByteArray, flags);

    [Fact]
    public void UrlMembersForAnOriginFormRequest()
    {
        var session = Build("GET /path/page?x=1#frag HTTP/1.1\r\nHost: www.example.test:8080");

        Assert.Equal("www.example.test:8080", session.host);
        Assert.Equal("www.example.test", session.hostname);
        Assert.Equal(8080, session.port);
        Assert.Equal("/path/page?x=1", session.PathAndQuery);
        Assert.Equal("www.example.test:8080/path/page?x=1", session.url);
        Assert.Equal("http://www.example.test:8080/path/page?x=1", session.fullUrl);
        Assert.False(session.isHTTPS);
        Assert.True(session.HostnameIs("WWW.EXAMPLE.TEST"));
    }

    [Fact]
    public void TheIsHttpsFlagChangesTheScheme()
    {
        var session = Build("GET / HTTP/1.1\r\nHost: secure.test", flags: SessionFlags.IsHTTPS);

        Assert.True(session.isHTTPS);
        Assert.Equal(443, session.port);
        Assert.Equal("https://secure.test/", session.fullUrl);
    }

    [Fact]
    public void AnAbsoluteFormRequestWithNoHostHeaderTakesItsHostFromTheUrl()
    {
        var session = Build("POST https://api.example.test/submit?q=1#section HTTP/1.1\r\ncontent-type: application/json");

        Assert.Equal("api.example.test", session.host);
        Assert.Equal("/submit?q=1", session.PathAndQuery);
        Assert.Equal("https://api.example.test/submit?q=1", session.fullUrl);
        Assert.True(session.isHTTPS);
        Assert.True(session.HTTPMethodIs("post"));
    }

    [Fact]
    public void IPv6HostsKeepTheirBrackets()
    {
        var session = Build("GET / HTTP/1.1\r\nHost: [::1]:8888");

        Assert.Equal("[::1]", session.hostname);
        Assert.Equal(8888, session.port);
    }

    [Fact]
    public void FlagsAreCaseInsensitiveAndMissingOnesAreNull()
    {
        var session = Build("GET / HTTP/1.1\r\nHost: a.test");

        session["X-ProcessInfo"] = "chrome:0";
        Assert.Equal("chrome:0", session["x-processinfo"]);
        Assert.Null(session["ui-backcolor"]);

        session["X-ProcessInfo"] = null;
        Assert.Null(session["X-ProcessInfo"]);
    }

    [Fact]
    public void BitFlagHelpers()
    {
        var session = Build("GET / HTTP/1.1\r\nHost: a.test", flags: SessionFlags.ImportedFromOtherTool | SessionFlags.ServedFromCache);

        Assert.True(session.isFlagSet(SessionFlags.ImportedFromOtherTool));
        Assert.False(session.isFlagSet(SessionFlags.ImportedFromOtherTool | SessionFlags.IsHTTPS));
        Assert.True(session.isAnyFlagSet(SessionFlags.ImportedFromOtherTool | SessionFlags.IsHTTPS));

        session.SetBitFlag(SessionFlags.ServedFromCache, false);
        Assert.False(session.isAnyFlagSet(SessionFlags.ServedFromCache));
    }

    [Fact]
    public void UtilSetResponseBodyFixesUpTheHeaders()
    {
        var session = Build("GET / HTTP/1.1\r\nHost: a.test", "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Encoding: gzip\r\nTransfer-Encoding: chunked");

        session.utilSetResponseBody("héllo");

        Assert.Equal(Encoding.UTF8.GetBytes("héllo"), session.responseBodyBytes);
        Assert.Equal("6", session.oResponse["Content-Length"]);
        Assert.False(session.oResponse.headers.Exists("Content-Encoding"));
        Assert.False(session.oResponse.headers.Exists("Transfer-Encoding"));
    }

    [Fact]
    public void GetResponseBodyAsStringDecompressesAndDecodes()
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes("compressed text"));
        }

        var session = Build("GET / HTTP/1.1\r\nHost: a.test", "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Type: text/plain", compressed.ToArray());

        Assert.Equal("compressed text", session.GetResponseBodyAsString());
        Assert.Equal("text/plain", session.oResponse.MIMEType);
    }

    [Fact]
    public void BuildFromDataWithCloneCopiesAndWithoutCloneShares()
    {
        var request = new HTTPRequestHeaders("/", ["Host: a.test"]);
        var body = new byte[] { 1, 2, 3 };

        var shared = ShimSession.BuildFromData(false, request, body, new HTTPResponseHeaders(), body, SessionFlags.None);
        var cloned = ShimSession.BuildFromData(true, request, body, new HTTPResponseHeaders(), body, SessionFlags.None);
        request["Host"] = "changed.test";

        Assert.Equal("changed.test", shared.host);
        Assert.Equal("a.test", cloned.host);
        Assert.NotSame(body, cloned.responseBodyBytes);
    }

    [Fact]
    public void NullPartsBecomeEmpty()
    {
        var session = ShimSession.BuildFromData(false, new HTTPRequestHeaders("/", ["Host: a.test"]), null!, null!, null!, SessionFlags.None);

        Assert.Empty(session.requestBodyBytes);
        Assert.Empty(session.responseBodyBytes);
        Assert.Equal(0, session.responseCode);
    }
}

public class CompatShimConversionTests
{
    [Fact]
    public void AShimSessionBecomesAnImportedSession()
    {
        var session = ShimSession.BuildFromData(false,
            Parser.ParseRequest("GET https://example.test/hello?x=1 HTTP/1.1\r\nHost: example.test\r\nAccept: a\r\nAccept: b")!,
            Encoding.UTF8.GetBytes("req"),
            Parser.ParseResponse("HTTP/1.1 201 Created\r\nX-Test: 1")!,
            Encoding.UTF8.GetBytes("resp"),
            SessionFlags.ImportedFromOtherTool);
        var started = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        session.Timers.ClientBeginRequest = started;
        session["X-Netlog-URLRequest-ID"] = "7";
        session["ui-backcolor"] = "#FF8080";

        var imported = ShimSessionConverter.ToImportedSession(session, DateTimeOffset.UnixEpoch);

        Assert.Equal("example.test", imported.Host);
        Assert.Equal(new DateTimeOffset(started), imported.StartedAt);
        Assert.Equal("GET", imported.Request.Method);
        Assert.Equal("/hello?x=1", imported.Request.Target);
        Assert.Equal(new (string, string)[] { ("Host", "example.test"), ("Accept", "a"), ("Accept", "b") }, imported.Request.Headers.Select(h => (h.Name, h.Value)).ToArray());
        Assert.Equal("req", Encoding.UTF8.GetString(imported.Request.Body));
        Assert.Equal(201, imported.Response.StatusCode);
        Assert.Equal("Created", imported.Response.ReasonPhrase);
        Assert.Equal("resp", Encoding.UTF8.GetString(imported.Response.Body));
        Assert.NotNull(imported.Flags);
        Assert.Equal("7", imported.Flags["x-netlog-urlrequest-id"]);
        Assert.Equal("#FF8080", imported.Flags["UI-BACKCOLOR"]);
    }

    [Fact]
    public void WithNoTimersTheImportTimeIsUsedAndNoFlagsIsNull()
    {
        var session = ShimSession.BuildFromData(false, new HTTPRequestHeaders("/x", []), [], new HTTPResponseHeaders(200, []), [], SessionFlags.None);
        var importedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var imported = ShimSessionConverter.ToImportedSession(session, importedAt);

        Assert.Equal(importedAt, imported.StartedAt);
        Assert.Equal("(unknown host)", imported.Host);
        Assert.Null(imported.Flags);
    }
}

public class CompatShimImporterAdapterTests
{
    [ShimProfferFormat("Test Format", "A format for tests", ".test;.tst")]
    private sealed class FakeFiddlerImporter : ShimImporter
    {
        public Dictionary<string, object>? LastOptions { get; private set; }

        public bool Disposed { get; private set; }

        public ShimSession[]? ImportSessions(string sImportFormat, Dictionary<string, object> dictOptions, EventHandler<ShimProgress>? evtProgressNotifications)
        {
            LastOptions = dictOptions;
            var progress = new ShimProgress(0.5f, "halfway");
            evtProgressNotifications?.Invoke(null, progress);
            if (progress.Cancel)
            {
                return null;
            }

            return
            [
                ShimSession.BuildFromData(false, new HTTPRequestHeaders("/one", ["Host: a.test"]), [], new HTTPResponseHeaders(200, []), [], SessionFlags.ImportedFromOtherTool),
                ShimSession.BuildFromData(false, new HTTPRequestHeaders("/two", ["Host: b.test"]), [], new HTTPResponseHeaders(404, []), [], SessionFlags.ImportedFromOtherTool),
            ];
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void FormatsComeFromTheShimAttributeIncludingExtensions()
    {
        var adapter = new CompatShimImporterAdapter(new FakeFiddlerImporter());

        var format = Assert.Single(ProfferedFormats.Of(adapter));
        Assert.Equal(new ProfferedFormat("Test Format", "A format for tests", ".test;.tst"), format);
    }

    [Fact]
    public void ImportPassesOptionsThroughAndConvertsSessions()
    {
        var inner = new FakeFiddlerImporter();
        var adapter = new CompatShimImporterAdapter(inner);
        var progress = new List<string>();

        var imported = adapter.ImportSessions(
            "Test Format",
            new Dictionary<string, object> { ["Filename"] = "capture.json" },
            p => progress.Add(p.ProgressText));

        Assert.Equal("capture.json", inner.LastOptions!["Filename"]);
        Assert.Equal(new[] { "halfway" }, progress.ToArray());
        Assert.Equal(new[] { "/one", "/two" }, imported.Select(s => s.Request.Target).ToArray());
        Assert.Equal(new[] { "a.test", "b.test" }, imported.Select(s => s.Host).ToArray());
    }

    [Fact]
    public void CancelFromTheHostReachesThePortedCode()
    {
        var adapter = new CompatShimImporterAdapter(new FakeFiddlerImporter());

        var imported = adapter.ImportSessions("Test Format", new Dictionary<string, object>(), p => p.Cancel = true);

        Assert.Empty(imported);
    }

    [Fact]
    public void DisposeReachesThePortedCode()
    {
        var inner = new FakeFiddlerImporter();

        new CompatShimImporterAdapter(inner).Dispose();

        Assert.True(inner.Disposed);
    }
}

public class CompatShimUtilitiesTests
{
    [Fact]
    public void GzipExpandRoundTripsAndToleratesBadData()
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write([1, 2, 3, 4]);
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Utilities.GzipExpand(compressed.ToArray()));
        Assert.Empty(Utilities.GzipExpand([9, 9, 9]));
        Assert.ThrowsAny<Exception>(() => Utilities.GzipExpand([9, 9, 9], bThrowErrors: true));
    }

    [Fact]
    public void GzipExpandStopsAtTheSameLimitAsVioLensItself()
    {
        // 300 MB of zeros, a few hundred KB compressed.
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 300; i++)
            {
                gzip.Write(zeros);
            }
        }

        var bomb = compressed.ToArray();

        Assert.Empty(Utilities.GzipExpand(bomb));
        Assert.ThrowsAny<IOException>(() => Utilities.GzipExpand(bomb, bThrowErrors: true));
    }

    [Fact]
    public void HexViewShowsBytesAndAscii()
    {
        var view = Utilities.ByteArrayToHexView(Encoding.ASCII.GetBytes("AB\u0001"), 4);

        Assert.Equal("41 42 01     AB.\n", view);
    }

    [Fact]
    public void ParsesAWinFormsFilter()
    {
        var filter = Utilities.ParseFileDialogFilter("NetLog JSON (*.json[.gz], *.zip)|*.json;*.json.gz;*.zip|All|*.*");

        Assert.Equal(2, filter.Count);
        Assert.Equal("NetLog JSON (*.json[.gz], *.zip)", filter[0].Description);
        Assert.Equal(new[] { "*.json", "*.json.gz", "*.zip" }, filter[0].Patterns);
        Assert.Equal(new[] { "*.*" }, filter[1].Patterns);
        Assert.Empty(Utilities.ParseFileDialogFilter(null));
    }

    [Fact]
    public void OicHelpersIgnoreCase()
    {
        Assert.True("EXCLUDE_SAMESITE_LAX, INCLUDE".OICContains("include"));
        Assert.True("Hello".OICStartsWith("he"));
        Assert.True("Hello".OICEndsWith("LO"));
        Assert.True("Hello".OICEquals("hello"));
        Assert.False(((string)null!).OICContains("x"));
    }
}

public class CompatShimTlsDescriptionTests
{
    /// <summary>A ServerHello choosing TLS 1.3, framed the way Eric's NetLog importer frames it: a placeholder 5-byte record header with a made-up length.</summary>
    private static MemoryStream ServerHelloTls13()
    {
        var body = new List<byte>();
        body.AddRange([0x03, 0x03]);                  // legacy_version
        body.AddRange(new byte[32]);                   // random
        body.Add(0);                                   // session id: empty
        body.AddRange([0x13, 0x01]);                   // TLS_AES_128_GCM_SHA256
        body.Add(0);                                   // compression: none
        byte[] extensions = [0x00, 0x2B, 0x00, 0x02, 0x03, 0x04]; // supported_versions: TLS 1.3
        body.AddRange([0x00, (byte)extensions.Length]);
        body.AddRange(extensions);

        var message = new List<byte> { 0x02, 0x00, 0x00, (byte)body.Count }; // ServerHello, 24-bit length
        message.AddRange(body);

        var stream = new MemoryStream();
        stream.Write([0x16, 0x03, 0x03, 0x00, 0x9B]); // record header, length deliberately wrong
        stream.Write(message.ToArray());
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void ATls13ServerHelloIsReportedTheWayTheNetLogImporterLooksForIt()
    {
        var description = Utilities.UNSTABLE_DescribeServerHello(ServerHelloTls13());

        Assert.Contains("supported_versions\tTls1.3", description);
        Assert.Contains("TLS_AES_128_GCM_SHA256", description);

        // The importer skips the first two non-empty lines; they must be the header, not data.
        var lines = description.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.StartsWith("A TLS ServerHello", lines[0]);
        Assert.StartsWith("Version:", lines[2]);
    }

    [Fact]
    public void AClientHelloListsSniAndCiphers()
    {
        var sni = Encoding.ASCII.GetBytes("example.test");
        var serverName = new List<byte> { 0x00, (byte)(sni.Length + 3), 0x00, 0x00, (byte)sni.Length };
        serverName.AddRange(sni);

        var extensions = new List<byte> { 0x00, 0x00, 0x00, (byte)serverName.Count };
        extensions.AddRange(serverName);

        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(new byte[32]);
        body.Add(0);
        body.AddRange([0x00, 0x04, 0x13, 0x01, 0x0A, 0x0A]); // two suites, the second GREASE
        body.AddRange([0x01, 0x00]);                          // compression: null only
        body.AddRange([0x00, (byte)extensions.Count]);
        body.AddRange(extensions);

        var stream = new MemoryStream();
        stream.Write([0x16, 0x03, 0x03, 0x00, 0x9B, 0x01, 0x00, 0x00, (byte)body.Count]);
        stream.Write(body.ToArray());
        stream.Position = 0;

        var description = Utilities.UNSTABLE_DescribeClientHello(stream);

        Assert.Contains("server_name\texample.test", description);
        Assert.Contains("[1301]\tTLS_AES_128_GCM_SHA256", description);
        Assert.Contains("[0A0A]\tgrease", description);
    }

    [Fact]
    public void TruncatedInputIsDescribedNotThrown()
    {
        var stream = new MemoryStream([0x16, 0x03, 0x03, 0x00, 0x9B, 0x02, 0x00, 0x00, 0x40, 0x03]);

        var description = Utilities.UNSTABLE_DescribeServerHello(stream);

        Assert.Contains("truncated or malformed", description);
    }
}

/// <summary>
/// Tests that change <see cref="CompatShimHost"/>'s static hooks. Kept in
/// one class (xUnit runs a class's tests one at a time) and restored after
/// each test, so they can't interfere with each other or anything else.
/// </summary>
public sealed class CompatShimHostTests : IDisposable
{
    private readonly Action<string> _originalLog = CompatShimHost.Log;
    private readonly IPreferenceStore _originalPreferences = CompatShimHost.Preferences;
    private readonly Func<string, string, string?>? _originalPrompt = CompatShimHost.PromptForOpenFile;
    private readonly Action<string, string>? _originalNotify = CompatShimHost.NotifyUser;

    public void Dispose()
    {
        CompatShimHost.Log = _originalLog;
        CompatShimHost.Preferences = _originalPreferences;
        CompatShimHost.PromptForOpenFile = _originalPrompt;
        CompatShimHost.NotifyUser = _originalNotify;
    }

    [Fact]
    public void PrefsPassThroughToTheHostsPreferenceStore()
    {
        var store = PreferenceStore.CreateInMemory();
        CompatShimHost.Preferences = store;

        FiddlerApplication.Prefs.SetBoolPref("FiddlerCSPExtension.enabled", true);
        FiddlerApplication.Prefs.SetInt32Pref("example.count", 3);

        Assert.True(store.GetBoolPref("FiddlerCSPExtension.enabled", false));
        Assert.Equal(3, FiddlerApplication.Prefs.GetInt32Pref("example.count", 0));
        Assert.Equal("fallback", FiddlerApplication.Prefs.GetStringPref("example.missing", "fallback"));

        FiddlerApplication.Prefs["example.count"] = null;
        Assert.Null(store["example.count"]);
    }

    [Fact]
    public void LogFormatWithNoArgumentsLogsBracesVerbatim()
    {
        var log = new List<string>();
        CompatShimHost.Log = message => { lock (log) { log.Add(message); } };

        FiddlerApplication.Log.LogFormat("text with {braces} and {0}");
        FiddlerApplication.Log.LogFormat("value={0}", 42);

        // Filtered: other test classes run in parallel and may log through the
        // same static hook while this one has it swapped in.
        Assert.Equal(
            new[] { "text with {braces} and {0}", "value=42" },
            log.Where(l => l.StartsWith("text with", StringComparison.Ordinal) || l.StartsWith("value=", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void NotifyAndFilePromptGoThroughTheHostOrFallBack()
    {
        var log = new List<string>();
        CompatShimHost.Log = message => { lock (log) { log.Add(message); } };
        CompatShimHost.NotifyUser = null;
        CompatShimHost.PromptForOpenFile = null;

        FiddlerApplication.DoNotifyUser("message", "Title");
        Assert.Null(Utilities.ObtainOpenFilename("Pick", "All|*.*"));
        Assert.Contains("Title: message", log);

        var notices = new List<string>();
        CompatShimHost.NotifyUser = (message, title) => notices.Add($"{title}/{message}");
        CompatShimHost.PromptForOpenFile = (title, filter) => $"{title}|{filter}";

        FiddlerApplication.DoNotifyUser("m", "t");
        Assert.Equal(new[] { "t/m" }, notices.ToArray());
        Assert.Equal("Pick|All|*.*", Utilities.ObtainOpenFilename("Pick", "All|*.*"));
    }
}
