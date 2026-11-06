using System.Globalization;
using System.Text;

namespace Violens.ProxyCore.Http;

/// <summary>
/// Writes a request or response. Content-Length is rebuilt from the body and
/// Transfer-Encoding dropped, since the body is already decoded and may have
/// been edited. The preamble methods write headers unchanged, for bodies
/// relayed with their original framing.
/// </summary>
internal static class HttpMessageWriter
{
    public static void WriteRequest(Stream destination, CapturedRequest request) =>
        destination.Write(BuildRequestBytes(request));

    public static void WriteResponse(Stream destination, CapturedResponse response) =>
        destination.Write(BuildResponseBytes(response));

    public static Task WriteRequestAsync(Stream destination, CapturedRequest request, CancellationToken cancellationToken) =>
        destination.WriteAsync(BuildRequestBytes(request), cancellationToken).AsTask();

    public static Task WriteResponseAsync(Stream destination, CapturedResponse response, CancellationToken cancellationToken) =>
        destination.WriteAsync(BuildResponseBytes(response), cancellationToken).AsTask();

    /// <summary>
    /// Writes the request line and headers unchanged. The body that follows
    /// must use the framing they declare.
    /// </summary>
    public static Task WriteRequestPreambleAsync(Stream destination, CapturedRequest request, CancellationToken cancellationToken) =>
        WritePreambleAsync(destination, $"{request.Method} {request.Target} {request.HttpVersion}", request.Headers, cancellationToken);

    /// <summary>Writes the status line and headers unchanged.</summary>
    public static Task WriteResponsePreambleAsync(Stream destination, CapturedResponse response, CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrEmpty(response.ReasonPhrase) ? string.Empty : $" {response.ReasonPhrase}";
        return WritePreambleAsync(destination, $"{response.HttpVersion} {response.StatusCode}{reason}", response.Headers, cancellationToken);
    }

    public static byte[] BuildRequestBytes(CapturedRequest request) =>
        BuildMessageBytes($"{request.Method} {request.Target} {request.HttpVersion}", request.Headers, request.Body, frameEmptyBody: false);

    public static byte[] BuildResponseBytes(CapturedResponse response)
    {
        var reason = string.IsNullOrEmpty(response.ReasonPhrase) ? string.Empty : $" {response.ReasonPhrase}";
        // A response without Content-Length runs until the connection
        // closes (RFC 9112 section 6.3), so even an empty one gets
        // "Content-Length: 0" -- except the statuses that never have a body.
        var frameEmptyBody = response.StatusCode is not (>= 100 and < 200 or 204 or 304);
        return BuildMessageBytes($"{response.HttpVersion} {response.StatusCode}{reason}", response.Headers, response.Body, frameEmptyBody);
    }

    private static byte[] BuildMessageBytes(
        string startLine, IReadOnlyList<(string Name, string Value)> headers, byte[] body, bool frameEmptyBody)
    {
        var text = new StringBuilder();
        text.Append(CheckStartLine(startLine)).Append("\r\n");

        var sawContentLength = false;
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                sawContentLength = true;
                text.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                continue;
            }

            text.Append(Clean(name)).Append(": ").Append(Clean(value)).Append("\r\n");
        }

        if (!sawContentLength && (body.Length > 0 || frameEmptyBody))
        {
            text.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        text.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(text.ToString());
        var result = new byte[headerBytes.Length + body.Length];
        headerBytes.CopyTo(result, 0);
        body.CopyTo(result, headerBytes.Length);
        return result;
    }

    private static Task WritePreambleAsync(
        Stream destination, string startLine, IReadOnlyList<(string Name, string Value)> headers, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.Append(CheckStartLine(startLine)).Append("\r\n");
        foreach (var (name, value) in headers)
        {
            text.Append(Clean(name)).Append(": ").Append(Clean(value)).Append("\r\n");
        }

        text.Append("\r\n");
        return destination.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), cancellationToken).AsTask();
    }

    private static readonly System.Buffers.SearchValues<char> LineBreaksAndNul = System.Buffers.SearchValues.Create([(char)0, (char)13, (char)10]);

    /// <summary>
    /// Refuses a start line with a line break or NUL in it, which would end
    /// the message early and start another (request splitting). Whatever
    /// built the message should already have refused it; this is the last
    /// place it can be caught.
    /// </summary>
    private static string CheckStartLine(string startLine) =>
        startLine.AsSpan().IndexOfAny(LineBreaksAndNul) < 0
            ? startLine
            : throw new InvalidDataException("A request or status line has a line break or NUL in it.");

    /// <summary>
    /// A header name or value with any line break or NUL replaced by a
    /// space, as RFC 9110 section 5.5 allows, so it can't end the header
    /// early.
    /// </summary>
    private static string Clean(string text)
    {
        if (text.AsSpan().IndexOfAny(LineBreaksAndNul) < 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is (char)0 or (char)13 or (char)10)
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }
}
