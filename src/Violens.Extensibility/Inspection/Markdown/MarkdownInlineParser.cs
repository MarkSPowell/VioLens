using System.Text;

namespace Violens.Extensibility.Inspection.Markdown;

/// <summary>
/// The inline half of <see cref="MarkdownParser"/>: emphasis (<c>*a*</c>,
/// <c>_a_</c>), strong (<c>**a**</c>), both (<c>***a***</c>),
/// strikethrough (<c>~~a~~</c>), code spans, links, images, autolinks
/// (<c>&lt;https://…&gt;</c>), backslash escapes and hard line breaks (two
/// trailing spaces, or a backslash). A delimiter with no partner stays as
/// text. An underscore inside a word (<c>snake_case</c>) is never emphasis.
/// </summary>
internal sealed class MarkdownInlineParser
{
    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>
    /// How far ahead a link's closing bracket or an emphasis's closing
    /// delimiter is looked for. Real ones are close by; the limit keeps text
    /// full of unmatched brackets or asterisks from taking quadratic time.
    /// </summary>
    private const int MaxSearch = 2_000;

    private readonly string _text;
    private readonly int _depth;

    private MarkdownInlineParser(string text, int depth)
    {
        _text = text;
        _depth = depth;
    }

    public static List<MarkdownInline> Parse(string text, int depth = 0) =>
        new MarkdownInlineParser(text, depth).ParseRange(0, text.Length);

    private List<MarkdownInline> ParseRange(int start, int end)
    {
        var result = new List<MarkdownInline>();
        if (_depth > MarkdownParser.MaxDepth)
        {
            result.Add(new MarkdownText(_text[start..end]));
            return result;
        }

        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length > 0)
            {
                result.Add(new MarkdownText(buffer.ToString()));
                buffer.Clear();
            }
        }

        var i = start;
        while (i < end)
        {
            var c = _text[i];
            switch (c)
            {
                case '\\' when i + 1 < end && _text[i + 1] == '\n':
                    Flush();
                    result.Add(new MarkdownLineBreak());
                    i += 2;
                    continue;

                case '\\' when i + 1 < end && Punctuation.Contains(_text[i + 1]):
                    buffer.Append(_text[i + 1]);
                    i += 2;
                    continue;

                case '`':
                {
                    var run = RunLength(i, end, '`');
                    var close = FindBacktickRun(i + run, end, run);
                    if (close < 0)
                    {
                        buffer.Append(_text, i, run);
                        i += run;
                        continue;
                    }

                    Flush();
                    result.Add(new MarkdownCode(CodeSpanText(_text[(i + run)..close])));
                    i = close + run;
                    continue;
                }

                case '!' when i + 1 < end && _text[i + 1] == '[':
                    if (TryLink(i + 1, end, out var altEnd, out var imageUrl, out var imageNext))
                    {
                        Flush();
                        result.Add(new MarkdownImage(PlainText(Parse(_text[(i + 2)..altEnd], _depth + 1)), imageUrl));
                        i = imageNext;
                        continue;
                    }

                    break;

                case '[':
                    if (TryLink(i, end, out var textEnd, out var url, out var next))
                    {
                        Flush();
                        result.Add(new MarkdownLink(new MarkdownInlineParser(_text, _depth + 1).ParseRange(i + 1, textEnd), url));
                        i = next;
                        continue;
                    }

                    break;

                case '<':
                {
                    var close = _text.IndexOf('>', i + 1, end - i - 1);
                    if (close > i + 1)
                    {
                        var inner = _text[(i + 1)..close];
                        if (!inner.Any(char.IsWhiteSpace) && (inner.Contains("://", StringComparison.Ordinal) || IsEmail(inner)))
                        {
                            Flush();
                            var target = IsEmail(inner) && !inner.Contains(':') ? "mailto:" + inner : inner;
                            result.Add(new MarkdownLink([new MarkdownText(inner)], target));
                            i = close + 1;
                            continue;
                        }
                    }

                    break;
                }

                case '*' or '_' or '~':
                    if (TryDelimited(i, end, out var inline, out var after))
                    {
                        Flush();
                        result.Add(inline);
                        i = after;
                        continue;
                    }

                    // An unmatched run stays as text, all of it, so its
                    // characters aren't tried again one at a time.
                    var length = RunLength(i, end, c);
                    buffer.Append(_text, i, length);
                    i += length;
                    continue;

                case '\n':
                {
                    // Two or more trailing spaces make a hard break; otherwise a space.
                    var spaces = 0;
                    while (buffer.Length > spaces && buffer[buffer.Length - 1 - spaces] == ' ')
                    {
                        spaces++;
                    }

                    buffer.Length -= spaces;
                    if (spaces >= 2)
                    {
                        Flush();
                        result.Add(new MarkdownLineBreak());
                    }
                    else
                    {
                        buffer.Append(' ');
                    }

                    i++;
                    continue;
                }
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return result;
    }

    private int RunLength(int i, int end, char c)
    {
        var length = 0;
        while (i + length < end && _text[i + length] == c)
        {
            length++;
        }

        return length;
    }

    /// <summary>The start of the next run of exactly <paramref name="run"/> backticks, or -1.</summary>
    private int FindBacktickRun(int from, int end, int run)
    {
        end = Math.Min(end, from + MaxSearch);
        var i = from;
        while (i < end)
        {
            if (_text[i] != '`')
            {
                i++;
                continue;
            }

            var length = RunLength(i, end, '`');
            if (length == run)
            {
                return i;
            }

            i += length;
        }

        return -1;
    }

    private static string CodeSpanText(string inner)
    {
        inner = inner.Replace('\n', ' ');
        return inner.Length > 2 && inner[0] == ' ' && inner[^1] == ' ' && inner.Trim().Length > 0 ? inner[1..^1] : inner;
    }

    /// <summary>
    /// <c>[text](url "title")</c> starting at <paramref name="open"/>: the
    /// end of the text, the URL and where parsing continues.
    /// </summary>
    private bool TryLink(int open, int end, out int textEnd, out string url, out int next)
    {
        textEnd = -1;
        url = string.Empty;
        next = open;

        var depth = 0;
        var i = open;
        end = Math.Min(end, open + MaxSearch);
        for (; i < end; i++)
        {
            var c = _text[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '`')
            {
                var run = RunLength(i, end, '`');
                var close = FindBacktickRun(i + run, end, run);
                i = close < 0 ? i + run - 1 : close + run - 1;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']' && --depth == 0)
            {
                break;
            }
        }

        if (i >= end || i + 1 >= end || _text[i + 1] != '(')
        {
            return false;
        }

        textEnd = i;
        var p = i + 2;
        while (p < end && _text[p] is ' ' or '\n')
        {
            p++;
        }

        var destination = new StringBuilder();
        if (p < end && _text[p] == '<')
        {
            var close = _text.IndexOf('>', p + 1, end - p - 1);
            if (close < 0)
            {
                return false;
            }

            destination.Append(_text, p + 1, close - p - 1);
            p = close + 1;
        }
        else
        {
            var parens = 0;
            while (p < end && !char.IsWhiteSpace(_text[p]))
            {
                if (_text[p] == '(')
                {
                    parens++;
                }
                else if (_text[p] == ')')
                {
                    if (parens == 0)
                    {
                        break;
                    }

                    parens--;
                }

                destination.Append(_text[p]);
                p++;
            }
        }

        while (p < end && _text[p] is ' ' or '\n')
        {
            p++;
        }

        // An optional title, which isn't shown.
        if (p < end && _text[p] is '"' or '\'' or '(')
        {
            var closer = _text[p] == '(' ? ')' : _text[p];
            var close = _text.IndexOf(closer, p + 1, end - p - 1);
            if (close < 0)
            {
                return false;
            }

            p = close + 1;
            while (p < end && _text[p] is ' ' or '\n')
            {
                p++;
            }
        }

        if (p >= end || _text[p] != ')')
        {
            return false;
        }

        url = destination.ToString();
        next = p + 1;
        return true;
    }

    /// <summary>Emphasis, strong or strikethrough opening at <paramref name="i"/>, if its closing delimiter is there.</summary>
    private bool TryDelimited(int i, int end, out MarkdownInline inline, out int after)
    {
        inline = null!;
        after = i;
        var c = _text[i];
        var run = RunLength(i, end, c);
        if (c == '~')
        {
            if (run != 2)
            {
                return false;
            }
        }
        else if (run > 3)
        {
            return false;
        }

        // An opener is followed by something other than a space; an
        // underscore can't open in the middle of a word.
        var opensAt = i + run;
        if (opensAt >= end || char.IsWhiteSpace(_text[opensAt]) ||
            (c == '_' && i > 0 && char.IsLetterOrDigit(_text[i - 1])))
        {
            return false;
        }

        var close = FindCloser(opensAt, end, c, run);
        if (close < 0)
        {
            return false;
        }

        var inner = new MarkdownInlineParser(_text, _depth + 1).ParseRange(opensAt, close);
        inline = (c, run) switch
        {
            ('~', _) => new MarkdownStrikethrough(inner),
            (_, 1) => new MarkdownEmphasis(inner),
            (_, 2) => new MarkdownStrong(inner),
            _ => new MarkdownStrong([new MarkdownEmphasis(inner)]),
        };
        after = close + run;
        return true;
    }

    /// <summary>The next run of exactly <paramref name="run"/> <paramref name="c"/>s that can close, skipping code spans and escapes.</summary>
    private int FindCloser(int from, int end, char c, int run)
    {
        end = Math.Min(end, from + MaxSearch);
        var i = from;
        while (i < end)
        {
            var ch = _text[i];
            if (ch == '\\')
            {
                i += 2;
                continue;
            }

            if (ch == '`')
            {
                var ticks = RunLength(i, end, '`');
                var close = FindBacktickRun(i + ticks, end, ticks);
                i = close < 0 ? i + ticks : close + ticks;
                continue;
            }

            if (ch != c)
            {
                i++;
                continue;
            }

            var length = RunLength(i, end, c);
            var precededBySpace = char.IsWhiteSpace(_text[i - 1]);
            var followedByWord = i + length < end && char.IsLetterOrDigit(_text[i + length]);
            if (length == run && !precededBySpace && !(c == '_' && followedByWord))
            {
                return i;
            }

            i += length;
        }

        return -1;
    }

    private static bool IsEmail(string text)
    {
        var at = text.IndexOf('@');
        return at > 0 && at < text.Length - 1 && text.IndexOf('.', at) > at;
    }

    /// <summary>The text of some inlines with the formatting dropped, for an image's alt text.</summary>
    internal static string PlainText(IEnumerable<MarkdownInline> inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            text.Append(inline switch
            {
                MarkdownText t => t.Text,
                MarkdownCode code => code.Code,
                MarkdownEmphasis e => PlainText(e.Inlines),
                MarkdownStrong s => PlainText(s.Inlines),
                MarkdownStrikethrough s => PlainText(s.Inlines),
                MarkdownLink link => PlainText(link.Inlines),
                MarkdownImage image => image.AltText,
                MarkdownLineBreak => " ",
                _ => string.Empty,
            });
        }

        return text.ToString();
    }
}
