using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Sessions;

/// <summary>
/// Writes sessions as an HTTP Archive (HAR 1.2), the format browsers'
/// developer tools, support teams and many other tools read. The companion
/// to <see cref="HarReader"/>.
///
/// Bodies are written as the application sees them: Content-Encoding
/// (gzip, br, ...) is undone, text is written as text and anything else as
/// base64, as the HAR spec describes (<c>content.encoding: "base64"</c>).
/// Headers are written as captured, so a response still shows the
/// Content-Encoding it was sent with; <c>content.compression</c> records how
/// many bytes that saved. A request body that isn't text is written as
/// base64 with the same <c>encoding</c> field, which HAR only defines for
/// responses; most tools accept it. Timings come from
/// <see cref="Session.Timings"/> (all zero when a session has none); see
/// <see cref="Timings"/> for how they map.
/// </summary>
public static class HarWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(string path, IReadOnlyList<Session> sessions)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(stream, sessions);
    }

    public static void Write(Stream destination, IReadOnlyList<Session> sessions)
    {
        var entries = new JsonArray();
        foreach (var session in sessions)
        {
            entries.Add(Entry(session));
        }

        var har = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["version"] = "1.2",
                ["creator"] = new JsonObject
                {
                    ["name"] = ProxyEngine.Identity.ProductName,
                    ["version"] = ProxyEngine.Identity.ProductVersion,
                },
                ["entries"] = entries,
            },
        };

        using var writer = new Utf8JsonWriter(destination, WriterOptions);
        har.WriteTo(writer);
    }

    /// <summary>
    /// HAR's timings object. HAR counts the TLS handshake inside
    /// <c>connect</c> as well as in <c>ssl</c>, where VioLens keeps them
    /// apart, so <c>connect</c> here is the two added together. -1 means
    /// "doesn't apply" in both. <c>_paused</c> (breakpoint time) is
    /// VioLens's own; HAR allows extra fields that start with "_".
    /// </summary>
    internal static JsonObject Timings(SessionTimings? timings)
    {
        if (timings is null)
        {
            return new JsonObject { ["send"] = 0, ["wait"] = 0, ["receive"] = 0 };
        }

        var connect = timings.Connect < 0
            ? SessionTimings.NotApplicable
            : timings.Connect + Math.Max(0, timings.Ssl);
        var json = new JsonObject
        {
            ["blocked"] = timings.Blocked,
            ["dns"] = timings.Dns,
            ["connect"] = connect,
            ["ssl"] = timings.Ssl,
            ["send"] = Math.Max(0, timings.Send),
            ["wait"] = Math.Max(0, timings.Wait),
            ["receive"] = Math.Max(0, timings.Receive),
        };

        if (timings.Paused > 0)
        {
            json["_paused"] = timings.Paused;
        }

        return json;
    }

    /// <summary>
    /// A WebSocket's messages the way browsers' developer tools export them:
    /// <c>type</c> ("send" or "receive"), <c>time</c> (Unix seconds),
    /// <c>opcode</c>, and <c>data</c> (text, or base64 for binary). Text and
    /// binary messages only, as browsers write.
    /// </summary>
    internal static JsonArray WebSocketMessages(WebSockets.WebSocketLog log)
    {
        var array = new JsonArray();
        foreach (var message in log.Messages)
        {
            if (message.Kind is not (WebSockets.WebSocketMessageKind.Text or WebSockets.WebSocketMessageKind.Binary))
            {
                continue;
            }

            array.Add(new JsonObject
            {
                ["type"] = message.Direction == WebSockets.WebSocketDirection.Sent ? "send" : "receive",
                ["time"] = message.Time.ToUnixTimeMilliseconds() / 1000.0,
                ["opcode"] = (int)message.Kind,
                ["data"] = message.Kind == WebSockets.WebSocketMessageKind.Binary
                    ? Convert.ToBase64String(message.Payload)
                    : message.Text,
            });
        }

        return array;
    }

    private static JsonObject Entry(Session session)
    {
        var entry = new JsonObject
        {
            ["startedDateTime"] = session.StartedAt.ToString("o", CultureInfo.InvariantCulture),
            ["time"] = session.Timings?.Total ?? 0,
            ["request"] = Request(session),
            ["response"] = Response(session.Response),
            ["cache"] = new JsonObject(),
            ["timings"] = Timings(session.Timings),
        };

        if (session.Timings is { ConnectionId: > 0 } timings)
        {
            entry["connection"] = timings.ConnectionId.ToString(CultureInfo.InvariantCulture);
        }

        if (session.Flags is { } flags && flags.TryGetValue("ui-comments", out var comment) && !string.IsNullOrEmpty(comment))
        {
            entry["comment"] = comment;
        }

        if (session.WebSocket is { } webSocket)
        {
            entry["_resourceType"] = "websocket";
            entry["_webSocketMessages"] = WebSocketMessages(webSocket);
        }

        return entry;
    }

    private static JsonObject Request(Session session)
    {
        var request = session.Request;
        var url = SessionUrl.Of(session);
        var harRequest = new JsonObject
        {
            ["method"] = request.Method,
            ["url"] = url,
            ["httpVersion"] = HttpVersions.ForHar(request.HttpVersion),
            ["cookies"] = RequestCookies(request.Headers),
            ["headers"] = Headers(request.Headers),
            ["queryString"] = QueryString(url),
            ["headersSize"] = -1,
            ["bodySize"] = request.Body.Length,
        };

        if (request.Body.Length > 0)
        {
            var mimeType = ContentDecoder.FindHeader(request.Headers, "Content-Type") ?? string.Empty;
            var postData = new JsonObject { ["mimeType"] = mimeType };
            var body = Decode(request.Body, request.Headers);
            if (IsText(mimeType))
            {
                postData["text"] = ContentDecoder.DecodeText(body, mimeType);
            }
            else
            {
                postData["text"] = Convert.ToBase64String(body);
                postData["encoding"] = "base64";
            }

            harRequest["postData"] = postData;
        }

        return harRequest;
    }

    private static JsonObject Response(CapturedResponse response)
    {
        var mimeType = ContentDecoder.FindHeader(response.Headers, "Content-Type") ?? string.Empty;
        var body = Decode(response.Body, response.Headers);
        var content = new JsonObject
        {
            ["size"] = body.Length,
            ["mimeType"] = mimeType,
        };

        if (body.Length != response.Body.Length)
        {
            content["compression"] = body.Length - response.Body.Length;
        }

        if (body.Length > 0)
        {
            if (IsText(mimeType))
            {
                content["text"] = ContentDecoder.DecodeText(body, mimeType);
            }
            else
            {
                content["text"] = Convert.ToBase64String(body);
                content["encoding"] = "base64";
            }
        }

        return new JsonObject
        {
            ["status"] = response.StatusCode,
            ["statusText"] = response.ReasonPhrase,
            ["httpVersion"] = HttpVersions.ForHar(response.HttpVersion),
            ["cookies"] = ResponseCookies(response.Headers),
            ["headers"] = Headers(response.Headers),
            ["content"] = content,
            ["redirectURL"] = ContentDecoder.FindHeader(response.Headers, "Location") ?? string.Empty,
            ["headersSize"] = -1,
            ["bodySize"] = response.Body.Length,
        };
    }

    /// <summary>
    /// Whether a body with this Content-Type is text, so it can be written
    /// as a string. An empty Content-Type counts as binary.
    /// </summary>
    internal static bool IsText(string? contentType)
    {
        var mediaType = ContentDecoder.MediaType(contentType);
        return mediaType.StartsWith("text/", StringComparison.Ordinal) ||
               mediaType.EndsWith("+json", StringComparison.Ordinal) ||
               mediaType.EndsWith("+xml", StringComparison.Ordinal) ||
               mediaType is "application/json" or "application/xml" or "application/javascript" or
                   "application/x-javascript" or "application/ecmascript" or "application/x-www-form-urlencoded" or
                   "application/graphql" or "image/svg+xml";
    }

    private static byte[] Decode(byte[] body, IReadOnlyList<(string Name, string Value)> headers) =>
        ContentDecoder.TryDecode(body, headers, out var decoded, out _) ? decoded : body;

    private static JsonArray Headers(IReadOnlyList<(string Name, string Value)> headers)
    {
        var array = new JsonArray();
        foreach (var (name, value) in headers)
        {
            array.Add(new JsonObject { ["name"] = name, ["value"] = value });
        }

        return array;
    }

    private static JsonArray QueryString(string url)
    {
        var array = new JsonArray();
        var question = url.IndexOf('?');
        if (question < 0)
        {
            return array;
        }

        var query = url[(question + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0)
        {
            query = query[..hash];
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            array.Add(new JsonObject
            {
                ["name"] = Unescape(equals < 0 ? pair : pair[..equals]),
                ["value"] = equals < 0 ? string.Empty : Unescape(pair[(equals + 1)..]),
            });
        }

        return array;
    }

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private static JsonArray RequestCookies(IReadOnlyList<(string Name, string Value)> headers)
    {
        var array = new JsonArray();
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var pair in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                array.Add(new JsonObject
                {
                    ["name"] = equals < 0 ? string.Empty : pair[..equals],
                    ["value"] = equals < 0 ? pair : pair[(equals + 1)..],
                });
            }
        }

        return array;
    }

    private static JsonArray ResponseCookies(IReadOnlyList<(string Name, string Value)> headers)
    {
        var array = new JsonArray();
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = value.Split(';', StringSplitOptions.TrimEntries);
            var equals = attributes[0].IndexOf('=');
            var cookie = new JsonObject
            {
                ["name"] = equals < 0 ? string.Empty : attributes[0][..equals],
                ["value"] = equals < 0 ? attributes[0] : attributes[0][(equals + 1)..],
            };

            foreach (var attribute in attributes.Skip(1))
            {
                var attributeEquals = attribute.IndexOf('=');
                var attributeName = attributeEquals < 0 ? attribute : attribute[..attributeEquals];
                var attributeValue = attributeEquals < 0 ? string.Empty : attribute[(attributeEquals + 1)..];
                switch (attributeName.ToLowerInvariant())
                {
                    case "path":
                        cookie["path"] = attributeValue;
                        break;
                    case "domain":
                        cookie["domain"] = attributeValue;
                        break;
                    case "expires":
                        cookie["expires"] = attributeValue;
                        break;
                    case "httponly":
                        cookie["httpOnly"] = true;
                        break;
                    case "secure":
                        cookie["secure"] = true;
                        break;
                }
            }

            array.Add(cookie);
        }

        return array;
    }
}
