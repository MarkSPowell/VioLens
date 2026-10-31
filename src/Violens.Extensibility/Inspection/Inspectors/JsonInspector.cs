using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Violens.ProxyCore.Http;

namespace Violens.Extensibility.Inspection.Inspectors;

/// <summary>
/// Fiddler Classic's "JSON" inspector, as indented text rather than a tree:
/// a JSON body, decompressed and pretty-printed, so an API response can be
/// read without copying it somewhere else first.
///
/// Offered for a body whose Content-Type is JSON (<c>application/json</c>,
/// <c>text/json</c>, or any <c>+json</c> type such as
/// <c>application/problem+json</c>), and for a body with no useful
/// Content-Type (none, <c>text/plain</c>, <c>application/octet-stream</c>)
/// that parses as a JSON object or array. A leading anti-hijacking prefix
/// such as <c>)]}'</c> is skipped. Invalid JSON with a JSON Content-Type gets
/// an explanation instead of the tab disappearing.
/// </summary>
public sealed class JsonInspector : IInspector
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        // Shows non-ASCII text as itself rather than \uXXXX escapes: this is
        // for reading, never for sending anywhere.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 256,
    };

    public string Id => "violens.json";

    public string DisplayName => "JSON";

    public int SortOrder => 12;

    public bool CanInspect(InspectorContext context)
    {
        if (context.Body.Length == 0)
        {
            return false;
        }

        var mediaType = ContentDecoder.MediaType(context.FindHeader("Content-Type"));
        if (IsJsonMediaType(mediaType))
        {
            return true;
        }

        if (mediaType is not ("" or "text/plain" or "application/octet-stream"))
        {
            return false;
        }

        var parsed = TryParse(context, out var document, out _);
        document?.Dispose();
        return parsed;
    }

    public InspectorContent Inspect(InspectorContext context)
    {
        if (!TryParse(context, out var document, out var error))
        {
            return new ErrorContent(error!);
        }

        using (document)
        {
            return new TextContent(Format(document!.RootElement), SyntaxHint: "json");
        }
    }

    internal static bool IsJsonMediaType(string mediaType) =>
        mediaType is "application/json" or "text/json" or "application/x-json" ||
        mediaType.EndsWith("+json", StringComparison.Ordinal);

    internal static string Format(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static bool TryParse(InspectorContext context, out JsonDocument? document, out string? error)
    {
        document = null;
        if (!ContentDecoder.TryDecode(context.Body, context.FindHeader("Content-Encoding"), out var decoded, out error))
        {
            return false;
        }

        var text = StripPrefix(ContentDecoder.DecodeText(decoded, context.FindHeader("Content-Type")));
        var first = text.TrimStart();
        if (first.Length == 0 || (first[0] != '{' && first[0] != '['))
        {
            error = "The body isn't a JSON object or array. See the Raw tab for the text as sent.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(text, ParseOptions);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"The body isn't valid JSON: {ex.Message} See the Raw tab for the text as sent.";
            return false;
        }
    }

    /// <summary>
    /// Drops an anti-JSON-hijacking prefix some APIs put before the JSON:
    /// <c>)]}'</c>, <c>)]}',</c>, <c>while(1);</c> or <c>for(;;);</c>, each
    /// optionally followed by a newline.
    /// </summary>
    internal static string StripPrefix(string text)
    {
        foreach (var prefix in new[] { ")]}',", ")]}'", "while(1);", "for(;;);" })
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..];
            }
        }

        return text;
    }
}
