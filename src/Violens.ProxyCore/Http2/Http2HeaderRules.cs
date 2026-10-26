using System.Globalization;

namespace Violens.ProxyCore.Http2;

/// <summary>
/// What makes a received header block malformed (RFC 9113 section 8.2 and
/// 8.3). A malformed block is a stream error of type PROTOCOL_ERROR.
/// </summary>
internal static class Http2HeaderRules
{
    // Connection-specific fields have no meaning in HTTP/2 (section 8.2.2).
    private static readonly HashSet<string> ConnectionSpecific = new(StringComparer.Ordinal)
    {
        "connection",
        "keep-alive",
        "proxy-connection",
        "transfer-encoding",
        "upgrade",
    };

    private static readonly HashSet<string> RequestPseudoHeaders = new(StringComparer.Ordinal)
    {
        ":method",
        ":scheme",
        ":authority",
        ":path",
        ":protocol",
    };

    /// <summary>Why a request's header block is malformed, or null if it isn't.</summary>
    public static string? CheckRequest(IReadOnlyList<(string Name, string Value)> headers)
    {
        if (CheckFields(headers, allowPseudo: true) is { } problem)
        {
            return problem;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in headers)
        {
            if (!name.StartsWith(':'))
            {
                break;
            }

            if (!RequestPseudoHeaders.Contains(name))
            {
                return $"'{name}' isn't a request pseudo-header.";
            }

            if (!seen.Add(name))
            {
                return $"'{name}' appears more than once.";
            }

            if (name == ":path" && value.Length == 0)
            {
                return "':path' is empty.";
            }
        }

        if (!seen.Contains(":method"))
        {
            return "':method' is missing.";
        }

        var isConnect = string.Equals(Find(headers, ":method"), "CONNECT", StringComparison.Ordinal);
        if (isConnect && !seen.Contains(":protocol"))
        {
            return seen.Contains(":scheme") || seen.Contains(":path")
                ? "A CONNECT request has ':scheme' or ':path'."
                : seen.Contains(":authority") ? null : "A CONNECT request has no ':authority'.";
        }

        return seen.Contains(":scheme") && seen.Contains(":path") ? null : "':scheme' or ':path' is missing.";
    }

    /// <summary>Why a response's header block is malformed, or null if it isn't.</summary>
    public static string? CheckResponse(IReadOnlyList<(string Name, string Value)> headers)
    {
        if (CheckFields(headers, allowPseudo: true) is { } problem)
        {
            return problem;
        }

        var statusCount = 0;
        foreach (var (name, value) in headers)
        {
            if (!name.StartsWith(':'))
            {
                break;
            }

            if (name != ":status")
            {
                return $"'{name}' isn't a response pseudo-header.";
            }

            if (++statusCount > 1)
            {
                return "':status' appears more than once.";
            }

            if (value.Length != 3 || !value.All(char.IsAsciiDigit))
            {
                return $"':status' isn't a three-digit code: '{value}'.";
            }
        }

        return statusCount == 1 ? null : "':status' is missing.";
    }

    /// <summary>Why a trailer block is malformed, or null if it isn't.</summary>
    public static string? CheckTrailers(IReadOnlyList<(string Name, string Value)> headers) =>
        CheckFields(headers, allowPseudo: false);

    /// <summary>The response's status code; call only on a block <see cref="CheckResponse"/> accepted.</summary>
    public static int Status(IReadOnlyList<(string Name, string Value)> headers) =>
        int.Parse(Find(headers, ":status")!, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>
    /// The Content-Length the block declares, or null. False when it's
    /// unreadable, or given more than once with different values.
    /// </summary>
    public static bool TryGetContentLength(IReadOnlyList<(string Name, string Value)> headers, out long? contentLength)
    {
        contentLength = null;
        foreach (var (name, value) in headers)
        {
            if (name != "content-length")
            {
                continue;
            }

            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                (contentLength is { } previous && previous != parsed))
            {
                return false;
            }

            contentLength = parsed;
        }

        return true;
    }

    private static string? CheckFields(IReadOnlyList<(string Name, string Value)> headers, bool allowPseudo)
    {
        var regularSeen = false;
        foreach (var (name, value) in headers)
        {
            if (name.Length == 0)
            {
                return "A field has an empty name.";
            }

            if (name[0] == ':')
            {
                if (!allowPseudo)
                {
                    return $"Pseudo-header '{name}' isn't allowed here.";
                }

                if (regularSeen)
                {
                    return $"Pseudo-header '{name}' comes after a regular field.";
                }

                continue;
            }

            regularSeen = true;
            foreach (var c in name)
            {
                if (c is >= 'A' and <= 'Z')
                {
                    return $"Field name '{name}' has upper-case letters.";
                }

                if (c <= ' ' || c >= 0x7F || c == ':')
                {
                    return $"Field name '{name}' has a character that isn't allowed.";
                }
            }

            if (ConnectionSpecific.Contains(name))
            {
                return $"'{name}' is connection-specific and isn't allowed in HTTP/2.";
            }

            if (name == "te" && !string.Equals(value, "trailers", StringComparison.Ordinal))
            {
                return "'te' may only be 'trailers'.";
            }

            if (value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
            {
                return $"The value of '{name}' has NUL, CR or LF.";
            }
        }

        return null;
    }

    private static string? Find(IReadOnlyList<(string Name, string Value)> headers, string name)
    {
        foreach (var header in headers)
        {
            if (header.Name == name)
            {
                return header.Value;
            }
        }

        return null;
    }
}
