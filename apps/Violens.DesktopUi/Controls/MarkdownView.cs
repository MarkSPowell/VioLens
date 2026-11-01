using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Violens.Extensibility.Inspection;
using Violens.Extensibility.Inspection.Markdown;

namespace Violens.DesktopUi.Controls;

/// <summary>
/// Draws a <see cref="MarkdownContent"/> (the Markdown tab) with plain
/// Avalonia controls: selectable text blocks for paragraphs and headings,
/// bordered blocks for code and quotes, grids for lists and tables. Links
/// are underlined and show their address as a tooltip, but aren't followed;
/// images show their alt text.
/// </summary>
public sealed class MarkdownView : UserControl
{
    private static readonly FontFamily Monospace = new("Cascadia Mono,Consolas,monospace");

    private static readonly double[] HeadingSizes = [26, 21, 18, 16, 14, 13];

    public MarkdownView()
    {
        DataContextChanged += (_, _) => Render();
    }

    private void Render()
    {
        if (DataContext is not MarkdownContent content)
        {
            Content = null;
            return;
        }

        var page = new StackPanel { Spacing = 10, Margin = new Thickness(16, 12), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        AddBlocks(page, content.Document.Blocks);
        Content = new ScrollViewer
        {
            Content = page,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    private void AddBlocks(Panel panel, IEnumerable<MarkdownBlock> blocks)
    {
        foreach (var block in blocks)
        {
            panel.Children.Add(BuildBlock(block));
        }
    }

    private Control BuildBlock(MarkdownBlock block) => block switch
    {
        MarkdownHeading heading => Text(heading.Inlines, fontSize: HeadingSizes[Math.Clamp(heading.Level, 1, 6) - 1], bold: true),
        MarkdownParagraph paragraph => Text(paragraph.Inlines),
        MarkdownCodeBlock code => CodeBlock(code),
        MarkdownQuote quote => Quote(quote),
        MarkdownList list => List(list),
        MarkdownTable table => Table(table),
        MarkdownRule => new Border { Height = 1, Background = Brush("AppBorderBrush"), Margin = new Thickness(0, 4) },
        _ => new TextBlock(),
    };

    private SelectableTextBlock Text(IReadOnlyList<MarkdownInline> inlines, double? fontSize = null, bool bold = false)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (fontSize is { } size)
        {
            text.FontSize = size;
        }

        if (bold)
        {
            text.FontWeight = FontWeight.Bold;
        }

        text.Inlines ??= [];
        foreach (var inline in inlines)
        {
            text.Inlines.Add(BuildInline(inline));
        }

        return text;
    }

    private Inline BuildInline(MarkdownInline inline)
    {
        switch (inline)
        {
            case MarkdownText text:
                return new Run(text.Text);
            case MarkdownCode code:
                return new Run(code.Code) { FontFamily = Monospace, Background = Brush("AppPanelBrush") };
            case MarkdownLineBreak:
                return new LineBreak();
            case MarkdownImage image:
                return new Run($"[image: {(image.AltText.Length > 0 ? image.AltText : image.Url)}]") { Foreground = Brush("AppMutedTextBrush") };
            case MarkdownEmphasis emphasis:
                return Span(new Italic(), emphasis.Inlines);
            case MarkdownStrong strong:
                return Span(new Bold(), strong.Inlines);
            case MarkdownStrikethrough strike:
            {
                var span = Span(new Span(), strike.Inlines);
                span.TextDecorations = TextDecorations.Strikethrough;
                return span;
            }

            case MarkdownLink link:
            {
                // Shown, not followed: the address came from captured traffic.
                var span = Span(new Underline(), link.Inlines);
                span.Foreground = Brush("AppAccentBrush");
                if (link.Inlines.Count == 0 || MarkdownLinkText(link) != link.Url)
                {
                    span.Inlines.Add(new Run($" ({link.Url})") { Foreground = Brush("AppMutedTextBrush") });
                }

                return span;
            }

            default:
                return new Run();
        }
    }

    private static string MarkdownLinkText(MarkdownLink link) =>
        string.Concat(link.Inlines.OfType<MarkdownText>().Select(t => t.Text));

    private Span Span(Span span, IEnumerable<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            span.Inlines.Add(BuildInline(inline));
        }

        return span;
    }

    private Control CodeBlock(MarkdownCodeBlock code) => new Border
    {
        Background = Brush("AppPanelBrush"),
        BorderBrush = Brush("AppBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Text = code.Code, FontFamily = Monospace, TextWrapping = TextWrapping.NoWrap },
        },
    };

    private Control Quote(MarkdownQuote quote)
    {
        var inner = new StackPanel { Spacing = 8 };
        AddBlocks(inner, quote.Blocks);
        return new Border
        {
            BorderBrush = Brush("AppBorderBrush"),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Child = inner,
        };
    }

    private Control List(MarkdownList list)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var i = 0; i < list.Items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var marker = new TextBlock
            {
                Text = list.Ordered ? $"{list.Start + i}." : "•",
                Margin = new Thickness(4, 0, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetRow(marker, i);
            grid.Children.Add(marker);

            var item = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
            AddBlocks(item, list.Items[i]);
            Grid.SetRow(item, i);
            Grid.SetColumn(item, 1);
            grid.Children.Add(item);
        }

        return grid;
    }

    private Control Table(MarkdownTable table)
    {
        var grid = new Grid();
        for (var column = 0; column < table.Header.Count; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownInline>>> { table.Header };
        rows.AddRange(table.Rows);
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < table.Header.Count; column++)
            {
                var text = Text(rows[row][column], bold: row == 0);
                text.TextAlignment = table.Alignments[column] switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                var cell = new Border
                {
                    BorderBrush = Brush("AppBorderBrush"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4),
                    Background = row == 0 ? Brush("AppPanelBrush") : null,
                    Child = text,
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new Border
        {
            BorderBrush = Brush("AppBorderBrush"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
    }

    /// <summary>A theme brush from the app's resources, so the page follows light and dark.</summary>
    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
