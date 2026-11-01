using Violens.Extensibility.Inspection.Markdown;
using Violens.ProxyCore.Http;

namespace Violens.Extensibility.Inspection.Inspectors;

/// <summary>
/// A Markdown body, rendered (Clearinet #26): headings, lists, code, tables
/// and links shown as they read, with the source still on the Raw tab.
/// Offered for <c>text/markdown</c> and <c>text/x-markdown</c>, and for a
/// body with no useful Content-Type whose URL ends in <c>.md</c> or
/// <c>.markdown</c>. Links are shown, not followed, and images by their alt
/// text, so rendering never fetches anything. A body over
/// <see cref="MaxChars"/> characters isn't rendered.
/// </summary>
public sealed class MarkdownInspector : IInspector
{
    public const int MaxChars = 512 * 1024;

    public string Id => "violens.markdown";

    public string DisplayName => "Markdown";

    public int SortOrder => 14;

    public bool CanInspect(InspectorContext context)
    {
        if (context.Body.Length == 0)
        {
            return false;
        }

        var mediaType = ContentDecoder.MediaType(context.FindHeader("Content-Type"));
        if (mediaType is "text/markdown" or "text/x-markdown")
        {
            return true;
        }

        if (mediaType is not ("" or "text/plain" or "application/octet-stream"))
        {
            return false;
        }

        var path = context.Session.Request.Target.Split('?', '#')[0];
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    public InspectorContent Inspect(InspectorContext context)
    {
        if (!ContentDecoder.TryDecode(context.Body, context.FindHeader("Content-Encoding"), out var decoded, out var error))
        {
            return new ErrorContent(error!);
        }

        var text = ContentDecoder.DecodeText(decoded, context.FindHeader("Content-Type"));
        if (text.Length > MaxChars)
        {
            return new ErrorContent($"This Markdown is {text.Length:N0} characters, too long to render here. See the Raw tab for the text.");
        }

        return new MarkdownContent(MarkdownParser.Parse(text), text);
    }
}
