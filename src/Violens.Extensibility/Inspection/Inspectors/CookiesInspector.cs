namespace Violens.Extensibility.Inspection.Inspectors;

/// <summary>
/// Shows a message's cookies, one per row, parsed per RFC 6265 (and the
/// cookie-prefix rules in its successor draft, RFC 6265bis).
///
/// <list type="bullet">
/// <item><b>Request side</b>: every cookie in the <c>Cookie</c> header(s),
/// as name and value.</item>
/// <item><b>Response side</b>: one row per <c>Set-Cookie</c> header: the
/// cookie's name, then its value followed by its attributes (Path, Domain,
/// Expires, Max-Age, Secure, HttpOnly, SameSite, ...). Settings that
/// browsers reject are called out after the attributes.</item>
/// </list>
///
/// Above the rows, a summary gives the cookies' total size and the largest
/// one, since big cookies slow every request to the site; a cookie whose
/// name and value are over <see cref="MaxCookieBytes"/>, the most browsers
/// store, gets a warning. Sizes are UTF-8 bytes of the name and value,
/// which is what browsers count.
///
/// Appears only on a side that has cookies. VioLens's own feature, built
/// from the RFCs, not from any extension's code (see "Port or build in?" in
/// the Extension Test Targets doc). P3P, which the old Fiddler cookie
/// sample also showed, is obsolete; its header still shows on the Headers
/// tab.
/// </summary>
public sealed class CookiesInspector : IInspector
{
    /// <summary>RFC 6265's minimum, and what Chrome, Firefox and Safari actually allow, for one cookie's name and value.</summary>
    public const int MaxCookieBytes = 4096;

    public string Id => "violens.cookies";

    public string DisplayName => "Cookies";

    public int SortOrder => 25;

    public bool CanInspect(InspectorContext context) => context.Headers.Any(h => IsCookieHeader(context.Side, h.Name));

    public InspectorContent Inspect(InspectorContext context)
    {
        var rows = new List<HeaderRow>();
        var sizes = new List<(string Name, int Bytes)>();
        var headerBytes = 0;
        foreach (var (name, value) in context.Headers)
        {
            if (!IsCookieHeader(context.Side, name))
            {
                continue;
            }

            headerBytes += Utf8Length(value);
            if (context.Side == InspectorSide.Request)
            {
                foreach (var cookie in ParseCookieHeader(value))
                {
                    var bytes = CookieBytes(cookie.Name, cookie.Value);
                    sizes.Add((cookie.Name, bytes));
                    rows.Add(bytes > MaxCookieBytes ? cookie with { Value = cookie.Value + OverLimitWarning(bytes) } : cookie);
                }
            }
            else
            {
                var (cookieName, cookieValue) = SplitPair(value.Split(';')[0]);
                var bytes = CookieBytes(cookieName, cookieValue);
                sizes.Add((cookieName.Length == 0 ? "(no name)" : cookieName, bytes));
                var row = ParseSetCookie(value);
                rows.Add(bytes > MaxCookieBytes ? row with { Value = row.Value + OverLimitWarning(bytes) } : row);
            }
        }

        return new KeyValueContent(rows, Summarize(context.Side, sizes, headerBytes));
    }

    /// <summary>"3 cookies, 1,234 bytes (the Cookie header is 1,250 bytes). Largest: session, 1,100 bytes."</summary>
    internal static string Summarize(InspectorSide side, IReadOnlyList<(string Name, int Bytes)> sizes, int headerBytes)
    {
        var total = sizes.Sum(s => s.Bytes);
        var count = sizes.Count == 1 ? "1 cookie" : $"{sizes.Count:N0} cookies";
        var header = side == InspectorSide.Request
            ? $"the Cookie header is {headerBytes:N0} bytes"
            : $"the Set-Cookie headers are {headerBytes:N0} bytes";
        var summary = $"{count}, {total:N0} bytes of names and values ({header}).";
        if (sizes.Count > 1)
        {
            var largest = sizes.MaxBy(s => s.Bytes);
            summary += $" Largest: {largest.Name}, {largest.Bytes:N0} bytes.";
        }

        return summary;
    }

    private static int CookieBytes(string name, string value) =>
        Utf8Length(name == "(no name)" ? string.Empty : name) + Utf8Length(value);

    private static int Utf8Length(string text) => System.Text.Encoding.UTF8.GetByteCount(text);

    private static string OverLimitWarning(int bytes) =>
        $"   Warning: {bytes:N0} bytes, over the {MaxCookieBytes:N0} browsers store; they drop this cookie.";

    private static (string Name, string Value) SplitPair(string pair)
    {
        pair = pair.Trim();
        var equals = pair.IndexOf('=');
        return equals < 0 ? (string.Empty, pair) : (pair[..equals].Trim(), pair[(equals + 1)..].Trim());
    }

    private static bool IsCookieHeader(InspectorSide side, string headerName) =>
        string.Equals(headerName, side == InspectorSide.Request ? "Cookie" : "Set-Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Cookie: a=1; b=2</c> → one row per pair. A piece with no <c>=</c> is a value with an empty name.</summary>
    internal static IEnumerable<HeaderRow> ParseCookieHeader(string header)
    {
        foreach (var piece in header.Split(';'))
        {
            var pair = piece.Trim();
            if (pair.Length == 0)
            {
                continue;
            }

            var equals = pair.IndexOf('=');
            yield return equals < 0
                ? new HeaderRow("(no name)", pair)
                : new HeaderRow(pair[..equals].Trim(), pair[(equals + 1)..].Trim());
        }
    }

    /// <summary>One <c>Set-Cookie</c> value → name, and value with attributes and any warnings.</summary>
    internal static HeaderRow ParseSetCookie(string header)
    {
        var parts = header.Split(';');
        var pair = parts[0].Trim();
        var equals = pair.IndexOf('=');
        var name = equals < 0 ? string.Empty : pair[..equals].Trim();
        var value = equals < 0 ? pair : pair[(equals + 1)..].Trim();

        var attributes = new List<string>();
        var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? sameSite = null;
        string? path = null;
        foreach (var raw in parts.Skip(1))
        {
            var attribute = raw.Trim();
            if (attribute.Length == 0)
            {
                continue;
            }

            attributes.Add(attribute);
            var attributeEquals = attribute.IndexOf('=');
            var attributeName = (attributeEquals < 0 ? attribute : attribute[..attributeEquals]).Trim();
            var attributeValue = attributeEquals < 0 ? string.Empty : attribute[(attributeEquals + 1)..].Trim();
            attributeNames.Add(attributeName);

            if (attributeName.Equals("SameSite", StringComparison.OrdinalIgnoreCase))
            {
                sameSite = attributeValue;
            }
            else if (attributeName.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                path = attributeValue;
            }
        }

        var secure = attributeNames.Contains("Secure");
        var warnings = new List<string>();
        if (equals < 0)
        {
            warnings.Add("no '=' in the name-value pair; RFC 6265 says to ignore this cookie, newer browsers treat it as a value with no name");
        }

        if (string.Equals(sameSite, "None", StringComparison.OrdinalIgnoreCase) && !secure)
        {
            warnings.Add("SameSite=None without Secure; browsers reject this cookie");
        }

        if (name.StartsWith("__Secure-", StringComparison.Ordinal) && !secure)
        {
            warnings.Add("the __Secure- prefix requires Secure; browsers reject this cookie");
        }

        if (name.StartsWith("__Host-", StringComparison.Ordinal) && (!secure || path != "/" || attributeNames.Contains("Domain")))
        {
            warnings.Add("the __Host- prefix requires Secure, Path=/ and no Domain; browsers reject this cookie");
        }

        var text = value;
        if (attributes.Count > 0)
        {
            text += "   [" + string.Join("; ", attributes) + "]";
        }

        foreach (var warning in warnings)
        {
            text += "   Warning: " + warning + ".";
        }

        return new HeaderRow(name.Length == 0 ? "(no name)" : name, text);
    }
}
