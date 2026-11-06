using System.Collections.Specialized;
using System.IO.Compression;
using System.Text;

namespace Violens.CompatShim;

/// <summary>The request half of a <see cref="Session"/> -- Fiddler's <c>oSession.oRequest</c>.</summary>
public class ClientChatter
{
    internal ClientChatter(HTTPRequestHeaders headers) => this.headers = headers;

    /// <summary>The request headers. Lower-case, as in Fiddler.</summary>
    public HTTPRequestHeaders headers { get; set; }

    /// <summary>Shorthand for <c>headers[name]</c>.</summary>
    public string? this[string sHeader]
    {
        get => headers[sHeader];
        set => headers[sHeader] = value;
    }

    /// <summary>The Host header's value, or an empty string.</summary>
    public string host
    {
        get => headers["Host"] ?? string.Empty;
        set => headers["Host"] = value;
    }
}

/// <summary>The response half of a <see cref="Session"/> -- Fiddler's <c>oSession.oResponse</c>.</summary>
public class ServerChatter
{
    internal ServerChatter(HTTPResponseHeaders headers) => this.headers = headers;

    /// <summary>The response headers. Lower-case, as in Fiddler.</summary>
    public HTTPResponseHeaders headers { get; set; }

    /// <summary>Shorthand for <c>headers[name]</c>.</summary>
    public string? this[string sHeader]
    {
        get => headers[sHeader];
        set => headers[sHeader] = value;
    }

    /// <summary>The Content-Type without parameters, e.g. <c>image/png</c>; empty if there's none.</summary>
    public string MIMEType
    {
        get
        {
            var contentType = headers["Content-Type"];
            if (string.IsNullOrEmpty(contentType))
            {
                return string.Empty;
            }

            var semicolon = contentType.IndexOf(';');
            return (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        }
    }
}

/// <summary>
/// Fiddler's <c>Session</c>, for code ported from Fiddler Classic
/// extensions, following Telerik's published API reference.
/// <see cref="oFlags"/> is a <see cref="StringDictionary"/>, as in Fiddler, so
/// flag names are case-insensitive (and lower-cased when enumerated); the
/// string indexer returns null for a missing flag.
/// </summary>
public class Session
{
    private static int s_lastId;

    private Session(HTTPRequestHeaders requestHeaders, byte[] requestBody, HTTPResponseHeaders responseHeaders, byte[] responseBody, SessionFlags flags)
    {
        id = Interlocked.Increment(ref s_lastId);
        oRequest = new ClientChatter(requestHeaders);
        oResponse = new ServerChatter(responseHeaders);
        requestBodyBytes = requestBody;
        responseBodyBytes = responseBody;
        BitFlags = flags;
    }

    /// <summary>
    /// Builds a finished session from its parts -- Fiddler's documented
    /// factory for importers and tools. With <paramref name="bClone"/> the
    /// headers and bodies are copied; otherwise the session keeps the
    /// objects passed in. A null request or response header object becomes
    /// an empty one; null bodies become empty.
    /// </summary>
    public static Session BuildFromData(
        bool bClone,
        HTTPRequestHeaders headersRequest,
        byte[] arrRequestBody,
        HTTPResponseHeaders headersResponse,
        byte[] arrResponseBody,
        SessionFlags oSF)
    {
        var request = headersRequest ?? new HTTPRequestHeaders();
        var response = headersResponse ?? new HTTPResponseHeaders();
        var requestBody = arrRequestBody ?? Utilities.emptyByteArray;
        var responseBody = arrResponseBody ?? Utilities.emptyByteArray;

        if (bClone)
        {
            request = (HTTPRequestHeaders)request.Clone();
            response = (HTTPResponseHeaders)response.Clone();
            requestBody = (byte[])requestBody.Clone();
            responseBody = (byte[])responseBody.Clone();
        }

        return new Session(request, requestBody, response, responseBody, oSF);
    }

    /// <summary>A sequential number, unique within this process. The host assigns its own id when the session reaches the session list.</summary>
    public int id { get; }

    public ClientChatter oRequest { get; }

    public ServerChatter oResponse { get; }

    /// <summary>The request body. A public field, as in Fiddler.</summary>
    public byte[] requestBodyBytes;

    /// <summary>The response body. A public field, as in Fiddler.</summary>
    public byte[] responseBodyBytes;

    /// <summary>The request body; never null. Setting it doesn't touch the headers (the proxy corrects Content-Length when it sends).</summary>
    public byte[] RequestBody
    {
        get => requestBodyBytes ?? Utilities.emptyByteArray;
        set => requestBodyBytes = value ?? Utilities.emptyByteArray;
    }

    /// <summary>The response body; never null. Setting it doesn't touch the headers (the proxy corrects Content-Length when it sends).</summary>
    public byte[] ResponseBody
    {
        get => responseBodyBytes ?? Utilities.emptyByteArray;
        set => responseBodyBytes = value ?? Utilities.emptyByteArray;
    }

    /// <summary>
    /// Set by <see cref="utilCreateResponseAndBypassServer"/>: the request
    /// won't be sent to the server, and the response on this session is
    /// returned to the client instead.
    /// </summary>
    internal bool IsBypassingServer { get; private set; }

    /// <summary>
    /// Answers the request without contacting the server: from inside
    /// <c>AutoTamperRequestBefore</c>, this replaces the response with an
    /// empty <c>200 OK</c> (no headers, no body) for the extension to fill
    /// in, and VioLens returns that instead of sending the request.
    /// Calling it from any later hook has no effect on what's sent.
    /// </summary>
    public void utilCreateResponseAndBypassServer()
    {
        var headers = new HTTPResponseHeaders();
        headers.SetStatus(200, "OK");
        oResponse.headers = headers;
        responseBodyBytes = Utilities.emptyByteArray;
        IsBypassingServer = true;
    }

    public SessionTimers Timers { get; set; } = new();

    /// <summary>Per-session string flags (<c>ui-backcolor</c>, <c>X-ProcessInfo</c>, ...). Case-insensitive.</summary>
    public StringDictionary oFlags { get; } = new();

    public SessionFlags BitFlags { get; set; }

    /// <summary>Reads or writes a flag in <see cref="oFlags"/>; null when absent. Setting null removes it.</summary>
    public string? this[string sFlag]
    {
        get => oFlags[sFlag];
        set
        {
            if (value is null)
            {
                oFlags.Remove(sFlag);
            }
            else
            {
                oFlags[sFlag] = value;
            }
        }
    }

    public bool isFlagSet(SessionFlags flagsToTest) => (BitFlags & flagsToTest) == flagsToTest;

    public bool isAnyFlagSet(SessionFlags flagsToTest) => (BitFlags & flagsToTest) != 0;

    public void SetBitFlag(SessionFlags FlagsToSet, bool b) =>
        BitFlags = b ? BitFlags | FlagsToSet : BitFlags & ~FlagsToSet;

    public string RequestMethod
    {
        get => oRequest.headers.HTTPMethod;
        set => oRequest.headers.HTTPMethod = value;
    }

    public int responseCode
    {
        get => oResponse.headers.HTTPResponseCode;
        set => oResponse.headers.SetStatus(value, HTTPResponseHeaders.StandardReasonPhrase(value));
    }

    /// <summary>
    /// The URL's scheme, lower-case: from an absolute-form request target
    /// if there is one, otherwise <c>https</c> when <see cref="SessionFlags.IsHTTPS"/>
    /// is set, otherwise the request headers' <see cref="HTTPRequestHeaders.UriScheme"/>.
    /// </summary>
    private string Scheme
    {
        get
        {
            if (TryGetAbsoluteTarget(out var absolute))
            {
                return absolute.Scheme;
            }

            return isFlagSet(SessionFlags.IsHTTPS) ? "https" : oRequest.headers.UriScheme.ToLowerInvariant();
        }
    }

    public bool isHTTPS => Scheme == "https";

    public bool isFTP => Scheme == "ftp";

    /// <summary>True for a CONNECT tunnel.</summary>
    public bool isTunnel => HTTPMethodIs("CONNECT");

    /// <summary>
    /// The host the request is for, including a non-default port: the Host
    /// header if there is one, otherwise the authority of an absolute-form
    /// request target, otherwise an empty string.
    /// </summary>
    public string host
    {
        get
        {
            var hostHeader = oRequest.headers["Host"];
            if (!string.IsNullOrEmpty(hostHeader))
            {
                return hostHeader;
            }

            if (TryGetAbsoluteTarget(out var absolute))
            {
                return absolute.IsDefaultPort ? absolute.Host : $"{absolute.Host}:{absolute.Port}";
            }

            return string.Empty;
        }
        set => oRequest.headers["Host"] = value;
    }

    /// <summary><see cref="host"/> without any port. IPv6 literals keep their brackets.</summary>
    public string hostname
    {
        get
        {
            var h = host;
            if (h.StartsWith('['))
            {
                var close = h.IndexOf(']');
                return close < 0 ? h : h[..(close + 1)];
            }

            var colon = h.LastIndexOf(':');
            return colon >= 0 && h[(colon + 1)..].All(char.IsAsciiDigit) ? h[..colon] : h;
        }
    }

    /// <summary>The port from <see cref="host"/>, or the scheme's default.</summary>
    public int port
    {
        get
        {
            var h = host;
            var colon = h.LastIndexOf(':');
            if (colon >= 0 && !h.EndsWith(']') && int.TryParse(h[(colon + 1)..], out var explicitPort))
            {
                return explicitPort;
            }

            return Scheme switch
            {
                "https" => 443,
                "ftp" => 21,
                _ => 80,
            };
        }
    }

    /// <summary>The path and query, e.g. <c>/path?q=1</c>, never including a fragment.</summary>
    public string PathAndQuery
    {
        get
        {
            if (TryGetAbsoluteTarget(out var absolute))
            {
                return absolute.PathAndQuery;
            }

            var path = oRequest.headers.RequestPath;
            var hash = path.IndexOf('#');
            path = hash < 0 ? path : path[..hash];
            return path.Length == 0 ? "/" : path;
        }
        set => oRequest.headers.RequestPath = value;
    }

    /// <summary>Host plus path and query, without the scheme, e.g. <c>www.host.com/path?q</c>.</summary>
    public string url => host + PathAndQuery;

    /// <summary>
    /// The complete URL including scheme, e.g. <c>https://www.host.com/path?q</c>.
    /// An absolute-form request target is returned as given, minus any
    /// fragment; otherwise it's built from the scheme, <see cref="host"/> and
    /// the request path.
    /// </summary>
    public string fullUrl
    {
        get
        {
            var path = oRequest.headers.RequestPath;
            if (IsAbsolute(path))
            {
                var hash = path.IndexOf('#');
                return hash < 0 ? path : path[..hash];
            }

            return $"{Scheme}://{host}{PathAndQuery}";
        }
    }

    /// <summary>Case-insensitive comparison against <see cref="hostname"/> (no port).</summary>
    public bool HostnameIs(string sTestHost) => string.Equals(hostname, sTestHost, StringComparison.OrdinalIgnoreCase);

    public bool HTTPMethodIs(string sTestFor) => string.Equals(RequestMethod, sTestFor, StringComparison.OrdinalIgnoreCase);

    /// <summary>The request body as text: decompressed per Content-Encoding (gzip, deflate, br), decoded per the Content-Type charset (UTF-8 if none).</summary>
    public string GetRequestBodyAsString() => DecodeBody(requestBodyBytes, oRequest.headers);

    /// <summary>The response body as text, decoded the same way as <see cref="GetRequestBodyAsString"/>.</summary>
    public string GetResponseBodyAsString() => DecodeBody(responseBodyBytes, oResponse.headers);

    /// <summary>
    /// Replaces the response body with <paramref name="sString"/>, encoded
    /// per the response's Content-Type charset (UTF-8 if none). Removes
    /// Content-Encoding and Transfer-Encoding, since the new body is neither,
    /// and sets Content-Length.
    /// </summary>
    public void utilSetResponseBody(string sString)
    {
        responseBodyBytes = EncodingFor(oResponse.headers).GetBytes(sString ?? string.Empty);
        oResponse.headers.Remove("Content-Encoding");
        oResponse.headers.Remove("Transfer-Encoding");
        oResponse.headers["Content-Length"] = responseBodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>As <see cref="utilSetResponseBody"/>, for the request.</summary>
    public void utilSetRequestBody(string sString)
    {
        requestBodyBytes = EncodingFor(oRequest.headers).GetBytes(sString ?? string.Empty);
        oRequest.headers.Remove("Content-Encoding");
        oRequest.headers.Remove("Transfer-Encoding");
        oRequest.headers["Content-Length"] = requestBodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public override string ToString() => $"Session #{id}, {RequestMethod} {fullUrl}";

    private bool TryGetAbsoluteTarget(out Uri absolute)
    {
        var path = oRequest.headers.RequestPath;
        if (IsAbsolute(path) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            absolute = uri;
            return true;
        }

        absolute = null!;
        return false;
    }

    private static bool IsAbsolute(string target)
    {
        var schemeEnd = target.IndexOf("://", StringComparison.Ordinal);
        return schemeEnd > 0 && target[..schemeEnd].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    private static Encoding EncodingFor(HTTPHeaders headers)
    {
        var charset = headers.GetTokenValue("Content-Type", "charset");
        if (!string.IsNullOrEmpty(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Unknown or unsupported charset label (utf-7, for one):
                // fall back to UTF-8, as a browser would.
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static string DecodeBody(byte[]? body, HTTPHeaders headers)
    {
        if (body is null || body.Length == 0)
        {
            return string.Empty;
        }

        var bytes = body;
        var encoding = headers["Content-Encoding"];
        if (!string.IsNullOrEmpty(encoding))
        {
            try
            {
                foreach (var coding in encoding.Split(',').Select(c => c.Trim().ToLowerInvariant()).Reverse())
                {
                    bytes = coding switch
                    {
                        "gzip" or "x-gzip" => Decompress(bytes, s => new GZipStream(s, CompressionMode.Decompress)),
                        "deflate" => Decompress(bytes, s => new ZLibStream(s, CompressionMode.Decompress)),
                        "br" => Decompress(bytes, s => new BrotliStream(s, CompressionMode.Decompress)),
                        _ => bytes,
                    };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                bytes = body;
            }
        }

        return EncodingFor(headers).GetString(bytes);
    }

    private static byte[] Decompress(byte[] input, Func<Stream, Stream> wrap)
    {
        using var source = new MemoryStream(input);
        using var decompressor = wrap(source);
        return Violens.ProxyCore.Http.ReadLimit.ReadAll(decompressor, Violens.ProxyCore.Http.ContentDecoder.MaxDecodedBytes);
    }
}
