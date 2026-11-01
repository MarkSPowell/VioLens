namespace Violens.Extensibility.Inspection;

/// <summary>
/// What an inspector can return. A closed set, so a host needs one renderer
/// per case even for inspectors it has never seen; adding a case is a
/// breaking change.
/// </summary>
public abstract record InspectorContent;

/// <summary>Text, with an optional hint ("json", "xml", "html") a host may use for highlighting.</summary>
public sealed record TextContent(string Text, string? SyntaxHint = null) : InspectorContent;

/// <summary>One name/value row in a <see cref="KeyValueContent"/> table.</summary>
public sealed record HeaderRow(string Name, string Value);

/// <summary>
/// Name/value rows (Headers, Cookies, WebForms), with an optional line of
/// text to show above them (the Cookies tab's sizes). <see cref="HeaderRow"/>
/// is a record rather than a tuple, because reflection bindings see a
/// tuple's <c>Item1</c>/<c>Item2</c>, not its names, and would show blank rows.
/// </summary>
public sealed record KeyValueContent(IReadOnlyList<HeaderRow> Rows, string? Summary = null) : InspectorContent;

/// <summary>Raw bytes, for a hex/ASCII dump. Formatting them into lines is a rendering concern, not this record's job.</summary>
public sealed record HexContent(byte[] Bytes) : InspectorContent;

/// <summary>The inspector applies but couldn't read this body; shown as a message.</summary>
public sealed record ErrorContent(string Message) : InspectorContent;

/// <summary>
/// An image to draw (the ImageView tab). <see cref="Bytes"/> is the decoded
/// image file; <see cref="MediaType"/> is its Content-Type media type, which
/// may be empty; <see cref="Summary"/> is a line of text to show with it
/// (format, pixel dimensions, size). A host that can't draw the format still
/// shows <see cref="Summary"/>.
/// </summary>
public sealed record ImageContent(byte[] Bytes, string MediaType, string Summary) : InspectorContent;

/// <summary>
/// Structured data shown as a tree (the Tree tab): JSON, CBOR, MessagePack.
/// <see cref="Roots"/> is usually one node; a CBOR sequence has one per
/// item. <see cref="Summary"/> is a line of text to show above it (the
/// format, how many values, whether the tree was cut short).
/// </summary>
public sealed record TreeContent(IReadOnlyList<TreeNode> Roots, string Summary) : InspectorContent;

/// <summary>What a <see cref="TreeNode"/> holds, which decides how it's shown and written out.</summary>
public enum TreeNodeKind
{
    /// <summary>A JSON object, CBOR or MessagePack map: children are its members.</summary>
    Object,

    /// <summary>An array: children are its items, named by index.</summary>
    Array,

    /// <summary>Text; <see cref="TreeNode.Value"/> is the text itself, unescaped.</summary>
    String,

    /// <summary><see cref="TreeNode.Value"/> is the number as written (JSON) or in invariant notation.</summary>
    Number,

    Boolean,

    Null,

    /// <summary>Binary data; <see cref="TreeNode.Value"/> is CBOR diagnostic notation, <c>h'0a1b'</c>.</summary>
    Bytes,

    /// <summary>A CBOR tag: <see cref="TreeNode.Value"/> is the tag number, and the one child is the tagged value.</summary>
    Tagged,

    /// <summary>Anything else (CBOR <c>undefined</c> and simple values); <see cref="TreeNode.Value"/> is written as is.</summary>
    Other,

    /// <summary>Stands in for values left out of a very large tree; <see cref="TreeNode.Value"/> says how many.</summary>
    Omitted,
}

/// <summary>
/// One value in a <see cref="TreeContent"/>. <see cref="Name"/> is the
/// member name, or the index for an array item (<c>[0]</c>), or empty for a
/// root. <see cref="Note"/> is an optional explanation shown after the
/// value, such as the date an epoch time stands for. <see cref="NameIsLiteral"/>
/// marks a map key that isn't text (a CBOR integer key, say): it's written
/// without quotes.
/// </summary>
public sealed record TreeNode(
    string Name,
    TreeNodeKind Kind,
    string Value,
    IReadOnlyList<TreeNode> Children,
    string? Note = null,
    bool NameIsLiteral = false)
{
    private const int MaxLabelValueLength = 200;

    /// <summary>A leaf with no children.</summary>
    public TreeNode(string name, TreeNodeKind kind, string value, string? note = null, bool nameIsLiteral = false)
        : this(name, kind, value, [], note, nameIsLiteral)
    {
    }

    /// <summary>What the tree shows: <c>name: value</c>, <c>name {3}</c> or <c>name [5]</c>, then the note.</summary>
    public string Label
    {
        get
        {
            if (Kind == TreeNodeKind.Omitted)
            {
                return Value;
            }

            var value = Kind switch
            {
                TreeNodeKind.Object => $"{{{Children.Count}}}",
                TreeNodeKind.Array => $"[{Children.Count}]",
                TreeNodeKind.Tagged => $"tag {Value}",
                TreeNodeKind.String => TreeText.Quote(Shorten(Value)),
                _ => Shorten(Value),
            };
            var separator = Kind is TreeNodeKind.Object or TreeNodeKind.Array ? " " : ": ";
            var label = Name.Length == 0 ? value : Name + separator + value;
            return Note is null ? label : $"{label}  ({Note})";
        }
    }

    /// <summary>The value alone, for Copy: a string's text unquoted, a container's text in full.</summary>
    public string CopyText => Kind switch
    {
        TreeNodeKind.String or TreeNodeKind.Number or TreeNodeKind.Boolean or TreeNodeKind.Null or TreeNodeKind.Bytes
            or TreeNodeKind.Other or TreeNodeKind.Omitted => Value,
        _ => TreeText.Write(this),
    };

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= MaxLabelValueLength ? line : string.Concat(line.AsSpan(0, MaxLabelValueLength), "…");
    }
}
