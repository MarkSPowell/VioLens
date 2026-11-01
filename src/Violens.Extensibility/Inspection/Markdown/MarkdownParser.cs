using System.Text;
using System.Text.RegularExpressions;

namespace Violens.Extensibility.Inspection.Markdown;

/// <summary>
/// Reads the Markdown people actually write into a <see cref="MarkdownDocument"/>:
/// CommonMark's headings, paragraphs, emphasis, code spans and blocks
/// (fenced and indented), block quotes, bulleted and numbered lists,
/// rules, links, images and hard line breaks, plus GitHub's tables and
/// strikethrough. Not a complete CommonMark implementation: reference-style
/// links, HTML blocks and some delimiter edge cases come out as plain text,
/// which is the right failure for an inspector, since nothing is lost.
/// Never throws; nesting deeper than <see cref="MaxDepth"/> is kept as text.
/// </summary>
public static partial class MarkdownParser
{
    public const int MaxDepth = 32;

    public static MarkdownDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\t", "    ", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
        return new MarkdownDocument(ParseBlocks(lines, depth: 0));
    }

    private static List<MarkdownBlock> ParseBlocks(List<string> lines, int depth)
    {
        var blocks = new List<MarkdownBlock>();
        if (depth > MaxDepth)
        {
            blocks.Add(new MarkdownParagraph([new MarkdownText(string.Join("\n", lines))]));
            return blocks;
        }

        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                // Lines keep their trailing spaces until here: two of them make a hard break.
                blocks.Add(new MarkdownParagraph(MarkdownInlineParser.Parse(string.Join("\n", paragraph).TrimEnd(), depth)));
                paragraph.Clear();
            }
        }

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                FlushParagraph();
                i++;
                continue;
            }

            var indent = Indent(line);
            if (indent >= 4 && paragraph.Count == 0)
            {
                i = ReadIndentedCode(lines, i, blocks);
                continue;
            }

            if (indent >= 4)
            {
                // A paragraph's continuation line.
                paragraph.Add(line.TrimStart());
                i++;
                continue;
            }

            var content = line.TrimStart();
            if (FenceStart(content, out var fence, out var info))
            {
                FlushParagraph();
                i = ReadFencedCode(lines, i, indent, fence, info, blocks);
                continue;
            }

            if (AtxHeading(content) is { } heading)
            {
                FlushParagraph();
                blocks.Add(new MarkdownHeading(heading.Level, MarkdownInlineParser.Parse(heading.Text, depth)));
                i++;
                continue;
            }

            if (paragraph.Count > 0 && SetextLevel(content) is { } level)
            {
                blocks.Add(new MarkdownHeading(level, MarkdownInlineParser.Parse(string.Join("\n", paragraph).Trim(), depth)));
                paragraph.Clear();
                i++;
                continue;
            }

            if (IsRule(content))
            {
                FlushParagraph();
                blocks.Add(new MarkdownRule());
                i++;
                continue;
            }

            if (content.StartsWith('>'))
            {
                FlushParagraph();
                var quoted = new List<string>();
                while (i < lines.Count && !IsBlank(lines[i]) && Indent(lines[i]) < 4 && lines[i].TrimStart().StartsWith('>'))
                {
                    var rest = lines[i].TrimStart()[1..];
                    quoted.Add(rest.StartsWith(' ') ? rest[1..] : rest);
                    i++;
                }

                blocks.Add(new MarkdownQuote(ParseBlocks(quoted, depth + 1)));
                continue;
            }

            // A list can interrupt a paragraph only with a bullet or "1.", as in CommonMark.
            if (ListMarker(line) is { } marker && (paragraph.Count == 0 || !marker.Ordered || marker.Number == 1))
            {
                FlushParagraph();
                i = ReadList(lines, i, marker, depth, blocks);
                continue;
            }

            if (paragraph.Count == 0 && content.Contains('|') && i + 1 < lines.Count &&
                DelimiterRow(lines[i + 1]) is { } alignments && SplitRow(content).Count == alignments.Count)
            {
                i = ReadTable(lines, i, alignments, depth, blocks);
                continue;
            }

            paragraph.Add(line.TrimStart());
            i++;
        }

        FlushParagraph();
        return blocks;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static int Indent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static int ReadIndentedCode(List<string> lines, int i, List<MarkdownBlock> blocks)
    {
        var code = new List<string>();
        while (i < lines.Count && (IsBlank(lines[i]) || Indent(lines[i]) >= 4))
        {
            code.Add(IsBlank(lines[i]) ? string.Empty : lines[i][4..]);
            i++;
        }

        while (code.Count > 0 && code[^1].Length == 0)
        {
            code.RemoveAt(code.Count - 1);
        }

        blocks.Add(new MarkdownCodeBlock(string.Empty, string.Join("\n", code)));
        return i;
    }

    private static bool FenceStart(string content, out string fence, out string info)
    {
        fence = string.Empty;
        info = string.Empty;
        if (content.Length < 3 || content[0] is not ('`' or '~'))
        {
            return false;
        }

        var length = 0;
        while (length < content.Length && content[length] == content[0])
        {
            length++;
        }

        if (length < 3)
        {
            return false;
        }

        info = content[length..].Trim();
        if (content[0] == '`' && info.Contains('`'))
        {
            return false;
        }

        fence = content[..length];
        info = info.Split(' ', 2)[0];
        return true;
    }

    private static int ReadFencedCode(List<string> lines, int i, int indent, string fence, string info, List<MarkdownBlock> blocks)
    {
        var code = new List<string>();
        i++;
        while (i < lines.Count)
        {
            var trimmed = lines[i].TrimStart();
            if (Indent(lines[i]) < 4 && trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.Trim().All(c => c == fence[0]))
            {
                i++;
                break;
            }

            // The opening fence's indent comes off every line, as far as it goes.
            code.Add(lines[i][Math.Min(indent, Indent(lines[i]))..]);
            i++;
        }

        blocks.Add(new MarkdownCodeBlock(info, string.Join("\n", code)));
        return i;
    }

    private static (int Level, string Text)? AtxHeading(string content)
    {
        var level = 0;
        while (level < content.Length && content[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6 || (level < content.Length && content[level] != ' '))
        {
            return null;
        }

        var text = content[level..].Trim();
        // A closing run of #s, after a space, isn't part of the heading.
        var closing = text.TrimEnd('#');
        if (closing.Length == 0 || closing.EndsWith(' '))
        {
            text = closing.TrimEnd();
        }

        return (level, text);
    }

    private static int? SetextLevel(string content)
    {
        var trimmed = content.TrimEnd();
        if (trimmed.Length > 0 && trimmed.All(c => c == '='))
        {
            return 1;
        }

        return trimmed.Length > 0 && trimmed.All(c => c == '-') ? 2 : null;
    }

    private static bool IsRule(string content)
    {
        var chars = content.Where(c => c != ' ').ToList();
        return chars.Count >= 3 && chars[0] is '-' or '*' or '_' && chars.All(c => c == chars[0]);
    }

    private sealed record Marker(bool Ordered, int Number, char Delimiter, int ContentIndent, string FirstLine);

    private static Marker? ListMarker(string line)
    {
        var match = ListMarkerRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var ordered = match.Groups["number"].Success;
        var number = ordered ? int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        var delimiter = ordered ? match.Groups["delim"].Value[0] : match.Groups["bullet"].Value[0];
        var rest = line[match.Length..];
        var contentIndent = match.Length + (rest.Length == 0 ? 0 : Math.Min(Indent(rest), 4));
        return new Marker(ordered, number, delimiter, contentIndent, rest.TrimStart());
    }

    [GeneratedRegex(@"^ {0,3}(?:(?<bullet>[-*+])|(?<number>\d{1,9})(?<delim>[.)]))(?= |$) ?")]
    private static partial Regex ListMarkerRegex();

    private static int ReadList(List<string> lines, int i, Marker first, int depth, List<MarkdownBlock> blocks)
    {
        var items = new List<IReadOnlyList<MarkdownBlock>>();
        var marker = first;
        while (true)
        {
            var itemLines = new List<string> { marker.FirstLine };
            i++;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (IsBlank(line))
                {
                    var next = i + 1;
                    while (next < lines.Count && IsBlank(lines[next]))
                    {
                        next++;
                    }

                    if (next < lines.Count && Indent(lines[next]) >= marker.ContentIndent)
                    {
                        itemLines.AddRange(Enumerable.Repeat(string.Empty, next - i));
                        i = next;
                        continue;
                    }

                    break;
                }

                if (Indent(line) >= marker.ContentIndent)
                {
                    itemLines.Add(line[marker.ContentIndent..]);
                    i++;
                    continue;
                }

                // A lazy continuation: more of the item's paragraph, unindented.
                var trimmed = line.TrimStart();
                if (ListMarker(line) is null && !IsBlank(itemLines[^1]) && !IsRule(trimmed) && !trimmed.StartsWith('>') &&
                    !trimmed.StartsWith('#') && !FenceStart(trimmed, out _, out _))
                {
                    itemLines.Add(trimmed);
                    i++;
                    continue;
                }

                break;
            }

            items.Add(ParseBlocks(itemLines, depth + 1));

            // The next item, possibly after blank lines, if it's the same kind of list.
            var after = i;
            while (after < lines.Count && IsBlank(lines[after]))
            {
                after++;
            }

            if (after < lines.Count && ListMarker(lines[after]) is { } nextMarker &&
                nextMarker.Ordered == first.Ordered && nextMarker.Delimiter == first.Delimiter)
            {
                i = after;
                marker = nextMarker;
                continue;
            }

            break;
        }

        blocks.Add(new MarkdownList(first.Ordered, first.Number, items));
        return i;
    }

    /// <summary>A table's second line, <c>| --- | :-: |</c>: each column's alignment, or null when the line isn't one.</summary>
    private static List<string>? DelimiterRow(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.Contains('-'))
        {
            return null;
        }

        var cells = SplitRow(trimmed);
        var alignments = new List<string>();
        foreach (var cell in cells)
        {
            var c = cell.Trim();
            if (c.Length == 0 || !c.Trim(':').All(ch => ch == '-') || !c.Contains('-'))
            {
                return null;
            }

            alignments.Add((c.StartsWith(':'), c.EndsWith(':')) switch
            {
                (true, true) => "center",
                (false, true) => "right",
                (true, false) => "left",
                _ => string.Empty,
            });
        }

        return alignments;
    }

    /// <summary>A table row's cells: split on unescaped pipes, without the outer ones.</summary>
    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (trimmed[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(trimmed[i]);
            }
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static int ReadTable(List<string> lines, int i, List<string> alignments, int depth, List<MarkdownBlock> blocks)
    {
        var header = SplitRow(lines[i]).Select(cell => (IReadOnlyList<MarkdownInline>)MarkdownInlineParser.Parse(cell, depth)).ToList();
        var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownInline>>>();
        i += 2;
        while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|'))
        {
            var cells = SplitRow(lines[i]);
            // Short rows are padded and long ones cut, to the header's width.
            rows.Add(Enumerable.Range(0, header.Count)
                .Select(column => (IReadOnlyList<MarkdownInline>)MarkdownInlineParser.Parse(column < cells.Count ? cells[column] : string.Empty, depth))
                .ToList());
            i++;
        }

        blocks.Add(new MarkdownTable(header, alignments, rows));
        return i;
    }
}
