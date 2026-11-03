using System.Diagnostics;
using System.Text;
using Violens.Extensibility.Inspection;
using Violens.Extensibility.Inspection.Html;
using Violens.Extensibility.Inspection.Inspectors;
using Violens.Extensibility.Inspection.Markdown;
using Violens.ProxyCore;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.Extensibility.Tests;

/// <summary>The Markdown and WebView tabs.</summary>
public class MarkdownAndHtmlTests
{
    private static InspectorContext Response(string target, string contentType, string body) =>
        new(new Session(1, "example.test", DateTimeOffset.UnixEpoch,
                new CapturedRequest("GET", target, "HTTP/1.1", [], []),
                new CapturedResponse("HTTP/1.1", 200, "OK", [("Content-Type", contentType)], Encoding.UTF8.GetBytes(body)),
                SessionState.Done),
            InspectorSide.Response);

    private static IReadOnlyList<MarkdownBlock> Blocks(string markdown) => MarkdownParser.Parse(markdown).Blocks;

    private static IReadOnlyList<MarkdownInline> Inlines(string markdown) =>
        Assert.IsType<MarkdownParagraph>(Assert.Single(Blocks(markdown))).Inlines;

    /// <summary>Inlines written out, since records compare their lists by reference.</summary>
    private static string Show(IEnumerable<MarkdownInline> inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        MarkdownText t => t.Text,
        MarkdownEmphasis e => $"<em>{Show(e.Inlines)}</em>",
        MarkdownStrong s => $"<strong>{Show(s.Inlines)}</strong>",
        MarkdownStrikethrough s => $"<del>{Show(s.Inlines)}</del>",
        MarkdownCode c => $"<code>{c.Code}</code>",
        MarkdownLink l => $"<a {l.Url}>{Show(l.Inlines)}</a>",
        MarkdownImage i => $"<img {i.Url} {i.AltText}>",
        MarkdownLineBreak => "<br>",
        _ => "?",
    }));

    private static string Show(string markdown) => Show(Inlines(markdown));

    // ---- Markdown blocks ----

    [Fact]
    public void HeadingsParagraphsAndRules()
    {
        var blocks = Blocks("# Title #\n\nSome text\nwrapped.\n\nSub\n---\n\n***\n###### Six");

        Assert.Equal(1, Assert.IsType<MarkdownHeading>(blocks[0]).Level);
        Assert.Equal([new MarkdownText("Title")], ((MarkdownHeading)blocks[0]).Inlines);
        Assert.Equal([new MarkdownText("Some text wrapped.")], Assert.IsType<MarkdownParagraph>(blocks[1]).Inlines);
        Assert.Equal(2, Assert.IsType<MarkdownHeading>(blocks[2]).Level);
        Assert.IsType<MarkdownRule>(blocks[3]);
        Assert.Equal(6, Assert.IsType<MarkdownHeading>(blocks[4]).Level);
        Assert.IsType<MarkdownParagraph>(Assert.Single(Blocks("#hashtag, not a heading")));
    }

    [Fact]
    public void CodeBlocks()
    {
        var fenced = Assert.IsType<MarkdownCodeBlock>(Assert.Single(Blocks("```json\n{ \"a\": 1 }\n\n  indented\n```")));
        Assert.Equal("json", fenced.Language);
        Assert.Equal("{ \"a\": 1 }\n\n  indented", fenced.Code);

        var indented = Assert.IsType<MarkdownCodeBlock>(Assert.Single(Blocks("    line 1\n\n    line 2\n")));
        Assert.Equal("line 1\n\nline 2", indented.Code);

        var unclosed = Assert.IsType<MarkdownCodeBlock>(Assert.Single(Blocks("~~~\nno end")));
        Assert.Equal("no end", unclosed.Code);
    }

    [Fact]
    public void ListsNestAndNumbersStartWhereTheyStart()
    {
        var list = Assert.IsType<MarkdownList>(Assert.Single(Blocks("3. three\n4. four\n   - nested\n   - again\n5. five")));

        Assert.True(list.Ordered);
        Assert.Equal(3, list.Start);
        Assert.Equal(3, list.Items.Count);
        var nested = Assert.IsType<MarkdownList>(list.Items[1][1]);
        Assert.False(nested.Ordered);
        Assert.Equal(2, nested.Items.Count);
    }

    [Fact]
    public void ListItemsCanHoldSeveralParagraphs()
    {
        var list = Assert.IsType<MarkdownList>(Assert.Single(Blocks("- first\n\n  more of first\n- second\nlazy line")));

        Assert.Equal(2, list.Items[0].Count);
        Assert.Equal([new MarkdownText("second lazy line")], Assert.IsType<MarkdownParagraph>(list.Items[1][0]).Inlines);
    }

    [Fact]
    public void QuotesHoldBlocks()
    {
        var quote = Assert.IsType<MarkdownQuote>(Assert.Single(Blocks("> # Quoted\n> text\n>\n> - item")));

        Assert.IsType<MarkdownHeading>(quote.Blocks[0]);
        Assert.IsType<MarkdownParagraph>(quote.Blocks[1]);
        Assert.IsType<MarkdownList>(quote.Blocks[2]);
    }

    [Fact]
    public void Tables()
    {
        var table = Assert.IsType<MarkdownTable>(Assert.Single(Blocks("| Name | Size |\n|:-----|-----:|\n| a \\| b | 1 |\n| c |")));

        Assert.Equal(["left", "right"], table.Alignments);
        Assert.Equal([new MarkdownText("Name")], table.Header[0]);
        Assert.Equal([new MarkdownText("a | b")], table.Rows[0][0]);
        Assert.Empty(table.Rows[1][1]);
    }

    [Fact]
    public void DeepNestingIsKeptAsText()
    {
        var text = new string('>', 200) + " deep";

        var blocks = Blocks(text);

        Assert.Single(blocks);
    }

    // ---- Markdown inlines ----

    [Fact]
    public void EmphasisStrongAndCode()
    {
        Assert.Equal(
            "a <em>b</em> <strong>c</strong> <strong><em>d</em></strong> <code>e *f*</code> <del>g</del>",
            Show("a *b* __c__ ***d*** `e *f*` ~~g~~"));
    }

    [Fact]
    public void NestedEmphasis()
    {
        Assert.Equal("<em>a <strong>b</strong> c</em>", Show("*a **b** c*"));
    }

    [Theory]
    [InlineData("snake_case_name")]
    [InlineData("2 * 3 * 4")]
    [InlineData("an *unclosed star")]
    [InlineData("a \\*literal\\* star")]
    [InlineData("``")]
    public void TextThatIsntEmphasisStaysText(string markdown)
    {
        var inlines = Inlines(markdown);

        Assert.All(inlines, inline => Assert.IsType<MarkdownText>(inline));
    }

    [Fact]
    public void LinksImagesAndAutolinks()
    {
        Assert.Equal(
            "See <a https://example.test/a_(b)>the <em>docs</em></a>, <img /logo.png logo> and <a https://example.test>https://example.test</a>.",
            Show("See [the *docs*](https://example.test/a_(b) \"Title\"), ![logo](/logo.png) and <https://example.test>."));
        Assert.Equal("<a mailto:a@example.test>a@example.test</a>", Show("<a@example.test>"));
        Assert.IsType<MarkdownText>(Assert.Single(Inlines("[not a link] (gap)")));
    }

    [Fact]
    public void HardLineBreaks()
    {
        Assert.Equal("one<br>two<br>three", Show("one  \ntwo\\\nthree"));
    }

    [Fact]
    public void PathologicalInputStaysFast()
    {
        var text = new string('[', 50_000) + new string('*', 50_000) + new string('`', 20_000);
        var timer = Stopwatch.StartNew();

        MarkdownParser.Parse(text);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    // ---- Markdown inspector ----

    [Theory]
    [InlineData("/readme", "text/markdown; charset=utf-8", true)]
    [InlineData("/README.md?raw=1", "text/plain", true)]
    [InlineData("/notes.markdown", "", true)]
    [InlineData("/page", "text/plain", false)]
    [InlineData("/README.md", "text/html", false)]
    public void MarkdownIsOfferedByTypeOrFileName(string target, string contentType, bool expected)
    {
        Assert.Equal(expected, new MarkdownInspector().CanInspect(Response(target, contentType, "# Hi")));
    }

    [Fact]
    public void MarkdownInspectorKeepsTheSource()
    {
        var content = Assert.IsType<MarkdownContent>(new MarkdownInspector().Inspect(Response("/a.md", "text/markdown", "# Hi\n\ntext")));

        Assert.Equal("# Hi\n\ntext", content.Source);
        Assert.Equal(2, content.Document.Blocks.Count);
    }

    // ---- HTML ----

    private const string Page = """
        <!DOCTYPE html>
        <html><head><title>Example &amp; Co</title>
        <meta charset="iso-8859-1">
        <meta http-equiv="refresh" content="0; url=https://elsewhere.test/">
        <link rel="stylesheet" href="/site.css"><style>p { color: red }</style>
        <script>alert(1)</script></head>
        <body><!-- hidden comment --><h1>Welcome</h1><p>First&nbsp;paragraph <b>bold</b>.</p>
        <ul><li>One</li><li>Two</li></ul><img src="/a.png"><form action="/go"></form>
        <script src="/app.js"></script></body></html>
        """;

    [Fact]
    public void SummarisesThePage()
    {
        Assert.Equal("Example & Co", HtmlText.Title(Page));
        Assert.Equal($"\"Example & Co\", {Page.Length:N0} characters; 2 scripts, 2 style sheets, 1 image, 1 form", HtmlText.Summarize(Page));
    }

    [Fact]
    public void ReadableTextLeavesOutScriptsStylesAndMarkup()
    {
        Assert.Equal("Welcome\n\nFirst paragraph bold.\n\n• One\n• Two", HtmlText.ToReadableText(Page));
    }

    [Fact]
    public void ThePreviewBlocksScriptsAndTheNetworkFirst()
    {
        var preview = HtmlPreview.Build(Page, "https://example.test/dir/page", loadImagesAndStyles: false);

        Assert.StartsWith("<!DOCTYPE html>\n<meta charset=\"utf-8\">\n<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none';", preview);
        Assert.Contains("form-action 'none'", preview);
        Assert.DoesNotContain("refresh", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iso-8859-1", preview);
        Assert.DoesNotContain("<base", preview);
        Assert.Contains("<h1>Welcome</h1>", preview);
    }

    [Theory]
    [InlineData("<meta content=\"a>b\" http-equiv=\"refresh\" content=\"0; url=https://elsewhere.test/\">")]
    [InlineData("<meta content='0; url=https://elsewhere.test/?a>b' http-equiv=refresh>")]
    [InlineData("<META HTTP-EQUIV=\"&#114;efresh\" CONTENT=\"0; url=https://elsewhere.test/\">")]
    public void ThePreviewRemovesRefreshesWrittenToSlipPastIt(string meta)
    {
        var preview = HtmlPreview.Build($"<!DOCTYPE html><head>{meta}</head><p>hi</p>", null, loadImagesAndStyles: false);

        Assert.DoesNotContain("elsewhere.test", preview);
        Assert.Contains("<p>hi</p>", preview);
    }

    [Fact]
    public void ThePreviewCanLoadImagesAndStylesFromTheSite()
    {
        var preview = HtmlPreview.Build("<p>hi</p>", "https://example.test/dir/page?q=1&r=2", loadImagesAndStyles: true);

        Assert.StartsWith("<meta charset=\"utf-8\">", preview);
        Assert.Contains("img-src data: http: https:", preview);
        Assert.DoesNotContain("script-src", preview);
        Assert.Contains("<base href=\"https://example.test/dir/page?q=1&amp;r=2\">", preview);
    }

    [Fact]
    public void WebViewIsOfferedForHtml()
    {
        var inspector = new HtmlInspector();

        Assert.True(inspector.CanInspect(Response("/", "text/html; charset=utf-8", Page)));
        Assert.True(inspector.CanInspect(Response("/", "application/xhtml+xml", Page)));
        Assert.False(inspector.CanInspect(Response("/", "text/plain", Page)));

        var content = Assert.IsType<HtmlContent>(inspector.Inspect(Response("/dir/page", "text/html", Page)));
        Assert.Equal("https://example.test/dir/page", content.PageUrl);
        Assert.StartsWith("Welcome", content.Text);
    }

    [Fact]
    public void WritesThePreviewToItsOwnFile()
    {
        var path = HtmlPreview.WriteFile("<p>hi</p>", null, loadImagesAndStyles: false);
        try
        {
            Assert.StartsWith("preview-", Path.GetFileName(path));
            Assert.Contains("Content-Security-Policy", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
