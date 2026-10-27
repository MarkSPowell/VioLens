using System.Globalization;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Http2;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// Converts between HTTP/2 header blocks and the protocol-neutral messages
/// the rest of the proxy works with (RFC 9113 section 8.3).
/// </summary>
internal static class Http2Messages
{
    public const string Version = "HTTP/2";

    // Connection-specific fields mean nothing in HTTP/2 and mustn't be sent there (section 8.2.2).
    private static readonly HashSet<string> ConnectionSpecific = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "proxy-connection",
        "transfer-encoding",
        "upgrade",
    };

    /// <summary>
    /// A request's header block as a stored request: <c>:method</c> and
    /// <c>:path</c> become the request line, <c>:authority</c> the Host header,
    /// and cookies split across several fields are joined again (section
    /// 8.2.3), as an HTTP/1.1 server expects. False for CONNECT, which this
    /// proxy doesn't carry over HTTP/2.
    /// </summary>
    public static bool TryToRequest(IReadOnlyList<(string Name, string Value)> block, out CapturedRequest request)
    {
        string? method = null, path = null, authority = null;
        var headers = new List<(string Name, string Value)>();
        var cookies = new List<string>();
        foreach (var (name, value) in block)
        {
            switch (name)
            {
                case ":method":
                    method = value;
                    break;
                case ":path":
                    path = value;
                    break;
                case ":authority":
                    authority = value;
                    break;
                case "cookie":
                    cookies.Add(value);
                    break;
                default:
                    if (!name.StartsWith(':'))
                    {
                        headers.Add((name, value));
                    }

                    break;
            }
        }

        if (method is null || path is null || string.Equals(method, "CONNECT", StringComparison.Ordinal))
        {
            request = null!;
            return false;
        }

        if (authority is not null && !headers.Any(h => string.Equals(h.Name, "host", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Insert(0, ("Host", authority));
        }

        if (cookies.Count > 0)
        {
            headers.Add(("cookie", string.Join("; ", cookies)));
        }

        request = new CapturedRequest(method, path, Version, headers, []);
        return true;
    }

    /// <summary>
    /// A response's status and headers as an HTTP/2 header block: names
    /// lower-cased, connection-specific fields (and any the Connection
    /// header names) left out. <paramref name="contentLength"/>, when given,
    /// replaces the Content-Length.
    /// </summary>
    public static List<(string Name, string Value)> ToHeaderBlock(CapturedResponse response, long? contentLength = null)
    {
        var alsoConnectionSpecific = response.Headers
            .Where(h => string.Equals(h.Name, "Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var block = new List<(string Name, string Value)>
        {
            (":status", response.StatusCode.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var (name, value) in response.Headers)
        {
            if (ConnectionSpecific.Contains(name) || alsoConnectionSpecific.Contains(name) ||
                (contentLength is not null && string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            block.Add((name.ToLowerInvariant(), value));
        }

        if (contentLength is { } length)
        {
            block.Add(("content-length", length.ToString(CultureInfo.InvariantCulture)));
        }

        return block;
    }

    /// <summary>
    /// A request as an HTTP/2 header block for the server: the request line
    /// and Host become <c>:method</c>, <c>:scheme</c>, <c>:authority</c> and
    /// <c>:path</c>; names are lower-cased; connection-specific fields are left
    /// out, and <c>te</c> unless it's <c>trailers</c> (which gRPC sends).
    /// <paramref name="contentLength"/>, when given, replaces the Content-Length.
    /// </summary>
    public static List<(string Name, string Value)> ToRequestBlock(CapturedRequest request, string defaultAuthority, long? contentLength = null)
    {
        var alsoConnectionSpecific = request.Headers
            .Where(h => string.Equals(h.Name, "Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var authority = request.Headers
            .FirstOrDefault(h => string.Equals(h.Name, "Host", StringComparison.OrdinalIgnoreCase)).Value ?? defaultAuthority;

        var block = new List<(string Name, string Value)>
        {
            (":method", request.Method),
            (":scheme", "https"),
            (":authority", authority),
            (":path", request.Target.Length == 0 ? "/" : request.Target),
        };
        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
                ConnectionSpecific.Contains(name) || alsoConnectionSpecific.Contains(name) ||
                (contentLength is not null && string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase) && !string.Equals(value.Trim(), "trailers", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            block.Add((name.ToLowerInvariant(), value));
        }

        if (contentLength is { } length)
        {
            block.Add(("content-length", length.ToString(CultureInfo.InvariantCulture)));
        }

        return block;
    }

    /// <summary>
    /// A response's HTTP/2 header block as a stored response. HTTP/2 has no
    /// reason phrase, so it's empty.
    /// </summary>
    public static CapturedResponse ToResponse(IReadOnlyList<(string Name, string Value)> block)
    {
        var status = int.Parse(block.First(h => h.Name == ":status").Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var headers = block.Where(h => !h.Name.StartsWith(':')).ToList();
        return new CapturedResponse(Version, status, string.Empty, headers, []);
    }

    /// <summary>Trailers ready to send over HTTP/2: lower-case names, no pseudo-headers. Null when there are none.</summary>
    public static List<(string Name, string Value)>? ToTrailerBlock(IReadOnlyList<(string Name, string Value)>? trailers)
    {
        var block = trailers?
            .Where(t => !t.Name.StartsWith(':') && !ConnectionSpecific.Contains(t.Name))
            .Select(t => (t.Name.ToLowerInvariant(), t.Value))
            .ToList();
        return block is { Count: > 0 } ? block : null;
    }
}

/// <summary>
/// The client's side of one exchange on an HTTP/2 stream. Bodies go out as
/// DATA frames, relayed as they arrive or all at once.
/// </summary>
internal sealed class Http2ClientExchange(Http2Stream stream, CapturedRequest request) : IClientExchange, IClientBodyStream
{
    private readonly bool _isHeadRequest = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);

    public bool RequestHasBody => !stream.HeadersEndedStream;

    /// <summary>Whether the response has been sent in full, or the stream reset.</summary>
    public bool IsFinished => stream.IsLocalSideEnded;

    public Task<byte[]> ReadRequestBodyAsync(CapturedRequest preamble, CancellationToken cancellationToken) =>
        stream.ReadAllDataAsync(cancellationToken);

    public ValueTask<int> ReadRequestBodyAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        stream.ReadDataAsync(buffer, cancellationToken);

    public async Task WriteResponseAsync(CapturedResponse response, CancellationToken cancellationToken)
    {
        // The body may have been edited, so the length comes from it, except
        // where a response has no body but keeps its length (HEAD, 304).
        var noBody = HasNoBody(response.StatusCode);
        var trailers = noBody ? null : Http2Messages.ToTrailerBlock(response.Trailers);
        var block = Http2Messages.ToHeaderBlock(response, noBody ? null : response.Body.Length);
        await stream.WriteHeadersAsync(block, endStream: noBody || (response.Body.Length == 0 && trailers is null), cancellationToken);
        if (!noBody && response.Body.Length > 0)
        {
            await stream.WriteDataAsync(response.Body, endStream: trailers is null, cancellationToken);
        }

        if (trailers is not null)
        {
            await stream.WriteTrailersAsync(trailers, cancellationToken);
        }
    }

    public Task WriteInterimResponseAsync(CapturedResponse response, CancellationToken cancellationToken) =>
        stream.WriteHeadersAsync(Http2Messages.ToHeaderBlock(response), endStream: false, cancellationToken);

    public Task WriteResponsePreambleAsync(CapturedResponse response, CancellationToken cancellationToken) =>
        stream.WriteHeadersAsync(Http2Messages.ToHeaderBlock(response), endStream: HasNoBody(response.StatusCode), cancellationToken);

    public Task WriteResponseBodyAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        stream.WriteDataAsync(data, endStream: false, cancellationToken);

    public Task EndResponseAsync(IReadOnlyList<(string Name, string Value)>? trailers, CancellationToken cancellationToken)
    {
        if (stream.IsLocalSideEnded)
        {
            return Task.CompletedTask;
        }

        return Http2Messages.ToTrailerBlock(trailers) is { } block
            ? stream.WriteTrailersAsync(block, cancellationToken)
            : stream.WriteDataAsync(ReadOnlyMemory<byte>.Empty, endStream: true, cancellationToken);
    }

    public void Reset() => _ = stream.ResetAsync(Http2ErrorCode.Cancel);

    private bool HasNoBody(int statusCode) => Http1MessageReader.ResponseHasNoBody(statusCode, _isHeadRequest);
}
