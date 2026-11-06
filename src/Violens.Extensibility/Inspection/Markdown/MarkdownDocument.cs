namespace Violens.Extensibility.Inspection.Markdown;

/// <summary>A parsed Markdown document: blocks, each holding inlines or more blocks. See <see cref="MarkdownParser"/>.</summary>
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks);

public abstract record MarkdownBlock;

/// <summary><c># Title</c> (levels 1 to 6), or a line underlined with <c>===</c> or <c>---</c>.</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>A fenced or indented code block; <see cref="Language"/> is the fence's info word, or empty.</summary>
public sealed record MarkdownCodeBlock(string Language, string Code) : MarkdownBlock;

public sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

/// <summary>A bulleted or numbered list; each item is a list of blocks. <see cref="Start"/> is the first number.</summary>
public sealed record MarkdownList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MarkdownBlock>> Items) : MarkdownBlock;

/// <summary><c>---</c>, <c>***</c> or <c>___</c> on a line of its own.</summary>
public sealed record MarkdownRule : MarkdownBlock;

/// <summary>A GitHub-style table. <see cref="Alignments"/> has one entry per column: "left", "center", "right" or "".</summary>
public sealed record MarkdownTable(
    IReadOnlyList<IReadOnlyList<MarkdownInline>> Header,
    IReadOnlyList<string> Alignments,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> Rows) : MarkdownBlock;

public abstract record MarkdownInline;

public sealed record MarkdownText(string Text) : MarkdownInline;

public sealed record MarkdownEmphasis(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownStrong(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownStrikethrough(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

public sealed record MarkdownCode(string Code) : MarkdownInline;

/// <summary>A link; a host shows it rather than following it, since the address comes from captured traffic.</summary>
public sealed record MarkdownLink(IReadOnlyList<MarkdownInline> Inlines, string Url) : MarkdownInline;

/// <summary>An image, shown by its alt text: loading it would mean fetching from the network.</summary>
public sealed record MarkdownImage(string AltText, string Url) : MarkdownInline;

public sealed record MarkdownLineBreak : MarkdownInline;
