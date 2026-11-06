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
        // Repeated until nothing changes: taking a tag out can join the
        // text either side into a new one (<met<meta ...>a http-equiv=...>).
        // Each round that changes anything makes the text shorter, so it ends.
        var body = html;
        string before;
        do
        {
            before = body;
            body = MetaTagRegex().Replace(
                body, tag => HttpEquivWordRegex().IsMatch(tag.Value) ? string.Empty : tag.Value);
            body = MetaUpToFirstCloseRegex().Replace(
                body, tag => HttpEquivWordRegex().IsMatch(tag.Value) ? string.Empty : tag.Value);
            body = CharsetRegex().Replace(body, string.Empty);
        }
        while (body.Length != before.Length);

        // Ours go first, before anything that could run or load: browsers
        // apply a meta policy only to what comes after it. A doctype stays
        // in front so the page keeps its rendering mode.
        var doctype = DoctypeRegex().Match(body);
        return doctype.Success
            ? body[..(doctype.Index + doctype.Length)] + "\n" + header + body[(doctype.Index + doctype.Length)..]
            : header + body;
    }

    /// <summary>
    /// Writes the preview to a file of its own and returns its path. The
    /// file goes in a folder this run of VioLens creates for itself in the
    /// temp folder, with a random name and, on Linux and macOS, readable
    /// only by this account (<see cref="Directory.CreateTempSubdirectory"/>):
    /// there the temp folder is shared by every account, and a folder with a
    /// fixed name could be made ahead of time by another one, which could
    /// then read or replace the previews. Previews from earlier runs more
    /// than a day old are deleted first.
    /// </summary>
    public static string WriteFile(string html, string? pageUrl, bool loadImagesAndStyles)
    {
        var path = Path.Combine(PreviewFolder.Value, $"preview-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.html");

        // CreateNew: never into a file that's already there.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
        writer.Write(Build(html, pageUrl, loadImagesAndStyles));
        return path;
    }

    private static readonly Lazy<string> PreviewFolder = new(() =>
    {
        DeleteOldPreviews();
        return Directory.CreateTempSubdirectory(PreviewFolderPrefix).FullName;
    });

    private const string PreviewFolderPrefix = "VioLens-preview-";

    /// <summary>
    /// Deletes previews over a day old in earlier runs' folders (and the
    /// single shared folder earlier versions used), then those folders if
    /// they're empty. Only files and empty folders: a folder that's really a
    /// link, or that holds anything else, is left alone.
    /// </summary>
    private static void DeleteOldPreviews()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), "VioLens-preview*"))
            {
                try
                {
                    if (new DirectoryInfo(folder).LinkTarget is not null)
                    {
                        continue;
                    }

                    foreach (var old in Directory.EnumerateFiles(folder, "preview-*.html"))
                    {
                        if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                        {
                            File.Delete(old);
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(folder).Any() &&
                        Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                    {
                        Directory.Delete(folder);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Open in a browser, perhaps, or another account's; it
                    // goes next time, or never.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
