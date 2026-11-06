using Violens.Extensibility.Inspection.Html;
using Violens.ProxyCore.Http;

namespace Violens.Extensibility.Inspection.Inspectors;

/// <summary>
/// Fiddler Classic's "WebView", for HTML (Clearinet #17). There's no
/// browser control in the UI toolkit, so instead of drawing the page this
/// gives its title and what it contains, its readable text, and the page
/// itself for a host's locked-down browser preview (see
/// <see cref="HtmlPreview"/>). Offered for <c>text/html</c> and
/// <c>application/xhtml+xml</c> bodies.
/// </summary>
public sealed class HtmlInspector : IInspector
{
    public string Id => "violens.html";

    public string DisplayName => "WebView";

    public int SortOrder => 15;

    public bool CanInspect(InspectorContext context) =>
        context.Body.Length > 0 &&
        ContentDecoder.MediaType(context.FindHeader("Content-Type")) is "text/html" or "application/xhtml+xml";

    public InspectorContent Inspect(InspectorContext context)
    {
        if (!ContentDecoder.TryDecode(context.Body, context.FindHeader("Content-Encoding"), out var decoded, out var error))
        {
            return new ErrorContent(error!);
        }

        var html = ContentDecoder.DecodeText(decoded, context.FindHeader("Content-Type"));
        var session = context.Session;
        var pageUrl = $"{session.Scheme}://{session.Host}{session.Request.Target}";
        return new HtmlContent(html, pageUrl, HtmlText.Summarize(html), HtmlText.ToReadableText(html));
    }
}
