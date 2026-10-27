using System.Globalization;
using System.Text;

namespace Violens.ProxyCore.Http;

/// <summary>
/// Reads one HTTP/1.1 request or response from a stream.
/// <see cref="ReadBodyAsync"/> buffers the whole body, which a breakpoint
/// needs. <see cref="RelayBodyAsync"/> forwards bytes as they arrive while
/// capturing them, so streamed responses (a chat reply arriving token by
/// token) reach the client live.
/// </summary>
internal static class Http1MessageReader
{
    private const int MaxStartLineOrHeaderLength = 16 * 1024;

    // Content-Length bodies relay in pieces this size, so large downloads
    // stream rather than buffer.
    private const int RelayBufferSize = 64 * 1024;

    /// <summary>Reads one request, body included. Null if the connection closed cleanly between requests.</summary>
    public static async Task<CapturedRequest?> ReadRequestAsync(Stream source, CancellationToken cancellationToken)
    {
        var preamble = await ReadRequestPreambleAsync(source, cancellationToken);
        if (preamble is null)
        {
            return null;
        }

        var body = await ReadBodyAsync(source, preamble.Headers, cancellationToken);
        return preamble with { Body = body };
    }

    /// <summary>Reads one response, body included. Null if the connection closed before a status line.</summary>
    public static async Task<CapturedResponse?> ReadResponseAsync(
        Stream source, bool isResponseToHeadRequest, CancellationToken cancellationToken)
    {
        var preamble = await ReadResponsePreambleAsync(source, cancellationToken);
        if (preamble is null)
        {
            return null;
        }

        var body = ResponseHasNoBody(preamble.StatusCode, isResponseToHeadRequest)
            ? Array.Empty<byte>()
            : await ReadResponseBodyAsync(source, preamble.Headers, cancellationToken);
        return preamble with { Body = body };
    }

    /// <summary>
    /// Reads only the request line and headers, so the caller can decide
    /// whether to buffer or relay the body.
    /// </summary>
    public static async Task<CapturedRequest?> ReadRequestPreambleAsync(Stream source, CancellationToken cancellationToken)
    {
        var startLine = await ReadLineAsync(source, allowImmediateEof: true, cancellationToken);
        if (startLine is null)
        {
            return null;
        }

        var parts = startLine.Split(' ', 3);
        if (parts.Length != 3)
        {
            throw new InvalidDataException($"Malformed request line: '{startLine}'");
        }

        var headers = await ReadHeadersAsync(source, cancellationToken);
        return new CapturedRequest(parts[0], parts[1], parts[2], headers, []);
    }

    /// <summary>Reads only the status line and headers.</summary>
    public static async Task<CapturedResponse?> ReadResponsePreambleAsync(Stream source, CancellationToken cancellationToken)
    {
        var startLine = await ReadLineAsync(source, allowImmediateEof: true, cancellationToken);
        if (startLine is null)
        {
            return null;
        }

        var parts = startLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var statusCode))
        {
            throw new InvalidDataException($"Malformed status line: '{startLine}'");
        }

        var reason = parts.Length > 2 ? parts[2] : string.Empty;
        var headers = await ReadHeadersAsync(source, cancellationToken);
        return new CapturedResponse(parts[0], statusCode, reason, headers, []);
    }

    /// <summary>1xx, 204, 304 and HEAD responses never have a body, whatever the headers say.</summary>
    public static bool ResponseHasNoBody(int statusCode, bool isResponseToHeadRequest) =>
        (statusCode is >= 100 and < 200 or 204 or 304) || isResponseToHeadRequest;

    /// <summary>Reads a body into memory.</summary>
    public static Task<byte[]> ReadBodyAsync(
        Stream source, IReadOnlyList<(string Name, string Value)> headers, CancellationToken cancellationToken)
    {
        var transferEncoding = FindHeader(headers, "Transfer-Encoding");
        if (transferEncoding is not null && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return ReadChunkedBodyAsync(source, cancellationToken);
        }

        var contentLengthText = FindHeader(headers, "Content-Length");
        if (contentLengthText is not null)
        {
            if (!long.TryParse(contentLengthText, out var contentLength) || contentLength < 0)
            {
                throw new InvalidDataException($"Malformed Content-Length: '{contentLengthText}'");
            }

            return contentLength == 0
                ? Task.FromResult(Array.Empty<byte>())
                : ReadExactAsync(source, contentLength, cancellationToken);
        }

        // Neither header: no body.
        return Task.FromResult(Array.Empty<byte>());
    }

    /// <summary>
    /// How much of a relayed body is kept for the session by default:
    /// every byte is still relayed, but only this many are held in memory
    /// (see <see cref="RelayedBody"/>). Keeps a multi-gigabyte download from
    /// exhausting memory, or failing outright past the 2 GB array limit.
    /// </summary>
    public const long DefaultMaxCapturedBodyBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Writes each byte to <paramref name="destination"/> as it arrives and
    /// returns the decoded body, keeping at most
    /// <paramref name="maxCapturedBytes"/> of it.
    /// </summary>
    public static Task<RelayedBody> RelayBodyAsync(
        Stream source,
        Stream destination,
        IReadOnlyList<(string Name, string Value)> headers,
        CancellationToken cancellationToken,
        long maxCapturedBytes = DefaultMaxCapturedBodyBytes)
    {
        var transferEncoding = FindHeader(headers, "Transfer-Encoding");
        if (transferEncoding is not null && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return RelayChunkedBodyAsync(source, destination, new CaptureBuffer(maxCapturedBytes), cancellationToken);
        }

        var contentLengthText = FindHeader(headers, "Content-Length");
        if (contentLengthText is not null)
        {
            if (!long.TryParse(contentLengthText, out var contentLength) || contentLength < 0)
            {
                throw new InvalidDataException($"Malformed Content-Length: '{contentLengthText}'");
            }

            return contentLength == 0
                ? Task.FromResult(RelayedBody.Empty)
                : RelayExactBytesAsync(source, destination, contentLength, new CaptureBuffer(maxCapturedBytes), cancellationToken);
        }

        return Task.FromResult(RelayedBody.Empty);
    }

    /// <summary>
    /// True when a response has neither <c>Transfer-Encoding: chunked</c>
    /// nor <c>Content-Length</c>, so its body runs until the server closes
    /// the connection (RFC 9112 section 6.3) -- common from older servers,
    /// and on plain HTTP. The connection can't carry another exchange after
    /// one of these.
    /// </summary>
    public static bool IsCloseDelimited(IReadOnlyList<(string Name, string Value)> headers)
    {
        var transferEncoding = FindHeader(headers, "Transfer-Encoding");
        return !(transferEncoding is not null && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase)) &&
               FindHeader(headers, "Content-Length") is null;
    }

    /// <summary>
    /// Reads a response body: like <see cref="ReadBodyAsync"/>, except that a
    /// response with no framing (see <see cref="IsCloseDelimited"/>) is read
    /// until the connection closes rather than treated as empty.
    /// </summary>
    public static Task<byte[]> ReadResponseBodyAsync(
        Stream source, IReadOnlyList<(string Name, string Value)> headers, CancellationToken cancellationToken) =>
        IsCloseDelimited(headers) ? ReadToEndAsync(source, cancellationToken) : ReadBodyAsync(source, headers, cancellationToken);

    /// <summary>
    /// Relays a response body: like <see cref="RelayBodyAsync"/>, except that
    /// a response with no framing (see <see cref="IsCloseDelimited"/>) is
    /// relayed until the connection closes rather than treated as empty.
    /// </summary>
    public static Task<RelayedBody> RelayResponseBodyAsync(
        Stream source,
        Stream destination,
        IReadOnlyList<(string Name, string Value)> headers,
        CancellationToken cancellationToken,
        long maxCapturedBytes = DefaultMaxCapturedBodyBytes) =>
        IsCloseDelimited(headers)
            ? RelayToEndAsync(source, destination, new CaptureBuffer(maxCapturedBytes), cancellationToken)
            : RelayBodyAsync(source, destination, headers, cancellationToken, maxCapturedBytes);

    /// <summary>
    /// Relays a response body without its framing: each piece of the decoded
    /// body goes to <paramref name="write"/> as it arrives, for a client on
    /// another protocol (HTTP/2) that frames the body itself. Chunked,
    /// Content-Length and close-delimited bodies are all handled; trailers
    /// are read and dropped.
    /// </summary>
    public static async Task<RelayedBody> RelayDecodedResponseBodyAsync(
        Stream source,
        IReadOnlyList<(string Name, string Value)> headers,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> write,
        CancellationToken cancellationToken,
        long maxCapturedBytes = DefaultMaxCapturedBodyBytes)
    {
        var capture = new CaptureBuffer(maxCapturedBytes);
        var buffer = new byte[RelayBufferSize];
        var transferEncoding = FindHeader(headers, "Transfer-Encoding");
        if (transferEncoding is not null && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var sizeLine = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
                var semicolon = sizeLine!.IndexOf(';');
                var sizeText = semicolon >= 0 ? sizeLine[..semicolon] : sizeLine;
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                {
                    throw new InvalidDataException($"Malformed chunk size: '{sizeLine}'");
                }

                if (size == 0)
                {
                    while ((await ReadLineAsync(source, allowImmediateEof: false, cancellationToken))!.Length != 0)
                    {
                    }

                    return capture.Build();
                }

                await RelayDecodedExactAsync(source, size, buffer, write, capture, cancellationToken);
                if ((await ReadLineAsync(source, allowImmediateEof: false, cancellationToken))!.Length != 0)
                {
                    throw new InvalidDataException("Malformed chunk terminator.");
                }
            }
        }

        var contentLengthText = FindHeader(headers, "Content-Length");
        if (contentLengthText is not null)
        {
            if (!long.TryParse(contentLengthText, out var contentLength) || contentLength < 0)
            {
                throw new InvalidDataException($"Malformed Content-Length: '{contentLengthText}'");
            }

            await RelayDecodedExactAsync(source, contentLength, buffer, write, capture, cancellationToken);
            return capture.Build();
        }

        // Close-delimited: everything until the server closes.
        while (true)
        {
            var read = await ReadOrEndAsync(source, buffer, cancellationToken);
            if (read == 0)
            {
                return capture.Build();
            }

            await write(buffer.AsMemory(0, read), cancellationToken);
            capture.Append(buffer.AsSpan(0, read));
        }
    }

    private static async Task RelayDecodedExactAsync(
        Stream source,
        long count,
        byte[] buffer,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> write,
        CaptureBuffer capture,
        CancellationToken cancellationToken)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Connection closed before the expected body was fully read.");
            }

            await write(buffer.AsMemory(0, read), cancellationToken);
            capture.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }

    private static async Task<byte[]> ReadToEndAsync(Stream source, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await CopyUntilClosedAsync(source, body, cancellationToken);
        return body.ToArray();
    }

    private static async Task<RelayedBody> RelayToEndAsync(
        Stream source, Stream destination, CaptureBuffer capture, CancellationToken cancellationToken)
    {
        var buffer = new byte[RelayBufferSize];
        while (true)
        {
            var read = await ReadOrEndAsync(source, buffer, cancellationToken);
            if (read == 0)
            {
                return capture.Build();
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            capture.Append(buffer.AsSpan(0, read));
        }
    }

    private static async Task CopyUntilClosedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[RelayBufferSize];
        while (true)
        {
            var read = await ReadOrEndAsync(source, buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// Reads what's available, treating an abrupt close (a TLS server that
    /// closes without a close_notify, or a reset) as the end of the body,
    /// which is how close-delimited bodies end in practice.
    /// </summary>
    private static async Task<int> ReadOrEndAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await source.ReadAsync(buffer.AsMemory(), cancellationToken);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static async Task<List<(string Name, string Value)>> ReadHeadersAsync(
        Stream source, CancellationToken cancellationToken)
    {
        var headers = new List<(string, string)>();
        while (true)
        {
            var line = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
            if (line!.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                throw new InvalidDataException($"Malformed header line: '{line}'");
            }

            headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        return headers;
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(Stream source, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
            var semicolon = sizeLine!.IndexOf(';');
            var sizeText = semicolon >= 0 ? sizeLine[..semicolon] : sizeLine;
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new InvalidDataException($"Malformed chunk size: '{sizeLine}'");
            }

            if (size == 0)
            {
                // Trailers, then the final blank line. Nothing uses trailers, so
                // they're discarded.
                while (true)
                {
                    var trailerLine = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
                    if (trailerLine!.Length == 0)
                    {
                        break;
                    }
                }

                break;
            }

            var chunk = await ReadExactAsync(source, size, cancellationToken);
            body.Write(chunk);

            var terminator = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
            if (terminator!.Length != 0)
            {
                throw new InvalidDataException("Malformed chunk terminator.");
            }
        }

        return body.ToArray();
    }

    /// <summary>Relays a chunked body as it arrives, framing included.</summary>
    private static async Task<RelayedBody> RelayChunkedBodyAsync(
        Stream source, Stream destination, CaptureBuffer capture, CancellationToken cancellationToken)
    {
        while (true)
        {
            var sizeLine = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
            await WriteLineAsync(destination, sizeLine!, cancellationToken);

            var semicolon = sizeLine!.IndexOf(';');
            var sizeText = semicolon >= 0 ? sizeLine[..semicolon] : sizeLine;
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new InvalidDataException($"Malformed chunk size: '{sizeLine}'");
            }

            if (size == 0)
            {
                while (true)
                {
                    var trailerLine = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
                    await WriteLineAsync(destination, trailerLine!, cancellationToken);
                    if (trailerLine!.Length == 0)
                    {
                        break;
                    }
                }

                break;
            }

            // In pieces, like a Content-Length body: a single chunk can be
            // as large as the server likes.
            await RelayExactBytesIntoAsync(source, destination, size, capture, cancellationToken);

            var terminator = await ReadLineAsync(source, allowImmediateEof: false, cancellationToken);
            await WriteLineAsync(destination, terminator ?? string.Empty, cancellationToken);
            if (terminator!.Length != 0)
            {
                throw new InvalidDataException("Malformed chunk terminator.");
            }
        }

        return capture.Build();
    }

    /// <summary>Relays a Content-Length body in pieces.</summary>
    private static async Task<RelayedBody> RelayExactBytesAsync(
        Stream source, Stream destination, long count, CaptureBuffer capture, CancellationToken cancellationToken)
    {
        await RelayExactBytesIntoAsync(source, destination, count, capture, cancellationToken);
        return capture.Build();
    }

    private static async Task RelayExactBytesIntoAsync(
        Stream source, Stream destination, long count, CaptureBuffer capture, CancellationToken cancellationToken)
    {
        var buffer = new byte[(int)Math.Min(count, RelayBufferSize)];
        var remaining = count;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Connection closed before the expected body was fully read.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            capture.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }

    /// <summary>
    /// A relayed body's bytes as far as they're kept, which for a body over
    /// the capture limit is only its start. Every byte of it was relayed
    /// either way.
    /// </summary>
    /// <param name="Captured">The decoded body, or its first bytes when <see cref="Truncated"/>.</param>
    /// <param name="Length">How many bytes the whole decoded body had.</param>
    public readonly record struct RelayedBody(byte[] Captured, long Length)
    {
        public static RelayedBody Empty { get; } = new([], 0);

        public bool Truncated => Captured.Length < Length;
    }

    /// <summary>Keeps at most <c>limit</c> bytes of what's appended, while counting all of it.</summary>
    private sealed class CaptureBuffer(long limit)
    {
        private readonly MemoryStream _kept = new();
        private long _length;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            _length += bytes.Length;
            var room = limit - _kept.Length;
            if (room > 0)
            {
                _kept.Write(bytes[..(int)Math.Min(room, bytes.Length)]);
            }
        }

        public RelayedBody Build() => new(_kept.ToArray(), _length);
    }

    private static ValueTask WriteLineAsync(Stream destination, string line, CancellationToken cancellationToken) =>
        destination.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), cancellationToken);

    private static string? FindHeader(IReadOnlyList<(string Name, string Value)> headers, string name)
    {
        foreach (var (headerName, value) in headers)
        {
            if (string.Equals(headerName, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static async Task<byte[]> ReadExactAsync(Stream source, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await source.ReadAsync(buffer.AsMemory(offset, (int)(count - offset)), cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException("Connection closed before the expected body was fully read.");
            }

            offset += read;
        }

        return buffer;
    }

    // A byte at a time: these lines are short, and the stream underneath is
    // buffered. allowImmediateEof: whether EOF before the first byte is a
    // clean end (null) rather than a truncated message (throws).
    private static async Task<string?> ReadLineAsync(
        Stream source, bool allowImmediateEof, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];

        while (true)
        {
            var read = await source.ReadAsync(single.AsMemory(), cancellationToken);
            if (read == 0)
            {
                if (bytes.Count == 0 && allowImmediateEof)
                {
                    return null;
                }

                throw new InvalidDataException("Connection closed unexpectedly while reading a line.");
            }

            if (single[0] == (byte)'\n')
            {
                break;
            }

            if (single[0] != (byte)'\r')
            {
                bytes.Add(single[0]);
                if (bytes.Count > MaxStartLineOrHeaderLength)
                {
                    throw new InvalidDataException("A line exceeded the maximum allowed length.");
                }
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
