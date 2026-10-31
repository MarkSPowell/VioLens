using System.Text;

namespace Violens.Extensibility.Inspection;

/// <summary>
/// Writes a <see cref="TreeNode"/> and everything under it as text, for Copy
/// Subtree and Send to TextWizard. A tree read from JSON comes out as
/// indented JSON. One read from CBOR or MessagePack comes out in CBOR
/// diagnostic notation (RFC 8949, section 8), which is JSON plus
/// <c>h'…'</c> for bytes, <c>tag(value)</c>, <c>undefined</c> and non-text
/// map keys. Values the tree left out are written as a
/// <c>/* N more not shown */</c> comment.
/// </summary>
public static class TreeText
{
    private const string IndentUnit = "  ";

    public static string Write(TreeNode node)
    {
        var text = new StringBuilder();
        WriteValue(text, node, depth: 0, indented: true);
        return text.ToString();
    }

    /// <summary>On one line, for a map key that isn't text.</summary>
    internal static string WriteCompact(TreeNode node)
    {
        var text = new StringBuilder();
        WriteValue(text, node, depth: 0, indented: false);
        return text.ToString();
    }

    /// <summary>
    /// A JSON string literal. Only what JSON requires is escaped (quotes,
    /// backslashes, control characters) plus unpaired surrogates, so all
    /// other text, emoji included, stays readable.
    /// </summary>
    internal static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '\n':
                    quoted.Append("\\n");
                    break;
                case '\r':
                    quoted.Append("\\r");
                    break;
                case '\t':
                    quoted.Append("\\t");
                    break;
                case '\b':
                    quoted.Append("\\b");
                    break;
                case '\f':
                    quoted.Append("\\f");
                    break;
                case < ' ':
                    quoted.Append($"\\u{(int)c:x4}");
                    break;
                case >= '\uD800' and <= '\uDBFF' when i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]):
                    quoted.Append(c).Append(text[++i]);
                    break;
                case >= '\uD800' and <= '\uDFFF':
                    quoted.Append($"\\u{(int)c:x4}");
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    private static void WriteValue(StringBuilder text, TreeNode node, int depth, bool indented)
    {
        switch (node.Kind)
        {
            case TreeNodeKind.Object or TreeNodeKind.Array:
                WriteContainer(text, node, depth, indented);
                break;
            case TreeNodeKind.String:
                text.Append(Quote(node.Value));
                break;
            case TreeNodeKind.Tagged:
                text.Append(node.Value).Append('(');
                if (node.Children.Count > 0)
                {
                    WriteValue(text, node.Children[0], depth, indented);
                }

                text.Append(')');
                break;
            case TreeNodeKind.Omitted:
                text.Append("/* ").Append(node.Value).Append(" */");
                break;
            default:
                text.Append(node.Value);
                break;
        }
    }

    private static void WriteContainer(StringBuilder text, TreeNode node, int depth, bool indented)
    {
        var isObject = node.Kind == TreeNodeKind.Object;
        var (open, close) = isObject ? ('{', '}') : ('[', ']');
        if (node.Children.Count == 0)
        {
            text.Append(open).Append(close);
            return;
        }

        text.Append(open);
        // A comma goes after every value except the last real one, so an
        // Omitted comment at the end doesn't leave a trailing comma.
        var lastValue = node.Children.Count - 1;
        while (lastValue > 0 && node.Children[lastValue].Kind == TreeNodeKind.Omitted)
        {
            lastValue--;
        }

        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (indented)
            {
                text.Append('\n').Insert(text.Length, IndentUnit, depth + 1);
            }
            else if (i > 0)
            {
                text.Append(' ');
            }

            if (isObject && child.Kind != TreeNodeKind.Omitted)
            {
                text.Append(child.NameIsLiteral ? child.Name : Quote(child.Name)).Append(": ");
            }

            WriteValue(text, child, depth + 1, indented);
            if (i < lastValue)
            {
                text.Append(',');
            }
        }

        if (indented)
        {
            text.Append('\n').Insert(text.Length, IndentUnit, depth);
        }

        text.Append(close);
    }
}
