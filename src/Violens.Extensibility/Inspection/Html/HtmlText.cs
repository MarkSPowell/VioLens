using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Violens.Extensibility.Inspection.Html;

/// <summary>
/// What the WebView tab says about a page without rendering it: its title,
/// counts of what it would load or run, and its text as a reader sees it.
/// Regular expressions, not a parser: this is a summary for a person, and a
/// page that defeats it still has its markup on the Raw tab.
/// </summary>
public static partial class HtmlText
{
    /// <summary>More than this much markup is summarised from its start only.</summary>
    public const int MaxHtmlChars = 2 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>The page's &lt;title&gt;, decoded, or empty.</summary>
    public static string Title(string html)
    {
        var match = TitleRegex().Match(Limit(html));
        return match.Success ? Collapse(WebUtility.HtmlDecode(match.Groups[1].Value)) : string.Empty;
    }

    /// <summary>"Title — 12,345 characters; 3 scripts, 2 style sheets, 14 images, 1 form".</summary>
    public static string Summarize(string html)
    {
        var limited = Limit(html);
        var parts = new List<string>();
        var counts = new (Regex Pattern, string One, string Many)[]
        {
            (ScriptRegex(), "script", "scripts"),
            (StyleRegex(), "style sheet", "style sheets"),
            (ImageRegex(), "image", "images"),
            (FormRegex(), "form", "forms"),
            (FrameRegex(), "frame", "frames"),
        };
        foreach (var (pattern, one, many) in counts)
        {
            var count = SafeCount(pattern, limited);
            if (count > 0)
            {
                parts.Add($"{count:N0} {(count == 1 ? one : many)}");
            }
        }

        var title = Title(html);
        var head = title.Length > 0 ? $"\"{title}\", {html.Length:N0} characters" : $"{html.Length:N0} characters";
        return parts.Count == 0 ? head : $"{head}; {string.Join(", ", parts)}";
    }

    /// <summary>
    /// The text a reader would see: no scripts, styles, head or comments;
    /// a line break for each block element and &lt;br&gt;; "• " before list
    /// items; entities decoded; runs of spaces and blank lines collapsed.
    /// </summary>
    public static string ToReadableText(string html)
    {
        try
        {
            var text = Limit(html);
            text = HiddenRegex().Replace(text, " ");
            text = ListItemRegex().Replace(text, "\n• ");
            text = BlockRegex().Replace(text, "\n");
            text = TagRegex().Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text);

            var lines = text.Split('\n').Select(Collapse);
            var result = new StringBuilder();
            var blank = true;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    if (!blank)
                    {
                        result.Append('\n');
                    }

                    blank = true;
                    continue;
                }

                result.Append(line).Append('\n');
                blank = false;
            }

            return result.ToString().Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return "This page's markup is too complicated to summarise quickly. See the Raw tab.";
        }
    }

    private static string Limit(string html) => html.Length <= MaxHtmlChars ? html : html[..MaxHtmlChars];

    private static string Collapse(string text) => SpacesRegex().Replace(text, " ").Trim();

    private static int SafeCount(Regex pattern, string text)
    {
        try
        {
            return pattern.Count(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return 0;
        }
    }

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<script\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style\b|<link\b[^>]*\bstylesheet\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<img\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"<form\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex FormRegex();

    [GeneratedRegex(@"<i?frame\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"<!--.*?-->|<(script|style|head|noscript|template|svg)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex HiddenRegex();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<br\s*/?>|</?(p|div|h[1-6]|ul|ol|tr|table|section|article|header|footer|nav|main|aside|blockquote|pre|dl|dt|dd|figure|figcaption|form|fieldset|address|hr)\b[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 2000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\f\v ]+", RegexOptions.None, 2000)]
    private static partial Regex SpacesRegex();
}
