using System.Text.RegularExpressions;

namespace Violens.Extensibility.Inspection.Html;

/// <summary>
/// Makes a captured page safe to open in a browser for a look (the WebView
/// tab's Preview): a copy with a Content-Security-Policy that blocks every
/// script, frame, plug-in and form submission, and any automatic redirect
/// removed. By default nothing is loaded from the network either, so
/// opening it sends nothing anywhere; with
/// <c>loadImagesAndStyles</c> the page's images, styles and fonts load from
/// its own site (relative addresses resolve against <c>pageUrl</c>), which
/// the browser fetches like any other request.
/// </summary>
public static partial class HtmlPreview
{
    /// <summary>The policy with nothing loaded from the network.</summary>
    public const string OfflinePolicy =
        "default-src 'none'; style-src 'unsafe-inline' data:; img-src data:; font-src data:; media-src data:; form-action 'none'; base-uri 'none'";

    /// <summary>The policy that lets images, styles and fonts load.</summary>
    public const string ResourcesPolicy =
        "default-src 'none'; style-src 'unsafe-inline' data: http: https:; img-src data: http: https:; font-src data: http: https:; media-src data: http: https:; form-action 'none'";

    /// <summary>The page, ready to write to a file: UTF-8, the policy first, and a base address when resources may load.</summary>
    public static string Build(string html, string? pageUrl, bool loadImagesAndStyles)
    {
        var policy = loadImagesAndStyles ? ResourcesPolicy : OfflinePolicy;
        var header = "<meta charset=\"utf-8\">\n" +
                     $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">\n";
        if (loadImagesAndStyles && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && page.Scheme is "http" or "https")
        {
            header += $"<base href=\"{System.Net.WebUtility.HtmlEncode(page.AbsoluteUri)}\">\n";
        }

        // The page's own charset would contradict the UTF-8 this is written
        // in, and a refresh would navigate away from the copy. Every
        // http-equiv goes, not just refresh: a value written with character
        // references (&#114;efresh) is still a refresh to a browser. Two
        // passes, so a tag goes however it's read: as a browser reads it
        // (a ">" inside a quoted value doesn't end it), and up to the first
        // ">" (in case the first pass reads something differently).
        var body = MetaTagRegex().Replace(
            html, tag => HttpEquivWordRegex().IsMatch(tag.Value) ? string.Empty : tag.Value);
        body = MetaUpToFirstCloseRegex().Replace(
            body, tag => HttpEquivWordRegex().IsMatch(tag.Value) ? string.Empty : tag.Value);
        body = CharsetRegex().Replace(body, string.Empty);

        // Ours go first, before anything that could run or load: browsers
        // apply a meta policy only to what comes after it. A doctype stays
        // in front so the page keeps its rendering mode.
        var doctype = DoctypeRegex().Match(body);
        return doctype.Success
            ? body[..(doctype.Index + doctype.Length)] + "\n" + header + body[(doctype.Index + doctype.Length)..]
            : header + body;
    }

    /// <summary>
    /// Writes the preview to a file of its own in the temp folder and returns
    /// its path. Previews more than a day old are deleted first.
    /// </summary>
    public static string WriteFile(string html, string? pageUrl, bool loadImagesAndStyles)
    {
        var folder = Path.Combine(Path.GetTempPath(), "VioLens-preview");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "preview-*.html"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(old);
                }
            }
            catch (IOException)
            {
                // Open in a browser, perhaps; it goes next time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var path = Path.Combine(folder, $"preview-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.html");
        File.WriteAllText(path, Build(html, pageUrl, loadImagesAndStyles), new System.Text.UTF8Encoding(false));
        return path;
    }

    // A <meta> tag read as browsers read it: attribute names, then values
    // that are quoted only when the quote comes straight after "=" (so
    // content="a>b" is one value, but in lang=a" the quote is just part of
    // an unquoted value). Atomic groups, so a tag that doesn't close fails
    // quickly instead of trying every way to split it.
    [GeneratedRegex(@"<meta\b(?>\s+|/|[^\s/>=]+(?>\s*=\s*(?>""[^""]*""|'[^']*'|[^\s>]*))?)*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex MetaUpToFirstCloseRegex();

    [GeneratedRegex(@"\bhttp-equiv\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex HttpEquivWordRegex();

    [GeneratedRegex(@"<meta\b[^>]*\bcharset\s*=[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex CharsetRegex();

    [GeneratedRegex(@"^\s*<!doctype[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex DoctypeRegex();
}
