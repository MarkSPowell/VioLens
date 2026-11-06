using Violens.ProxyCore.Http;

namespace Violens.Extensibility.Inspection.Inspectors;

/// <summary>
/// Fiddler Classic's JSON tree, extended to binary formats: a JSON, CBOR or
/// MessagePack body as a tree to expand, copy from and send to TextWizard.
///
/// Offered for the same JSON bodies as the JSON tab (see
/// <see cref="JsonInspector"/>), and for CBOR (<c>application/cbor</c>,
/// <c>application/cbor-seq</c>, any <c>+cbor</c> type) and MessagePack
/// (<c>application/msgpack</c>, <c>application/x-msgpack</c>,
/// <c>application/vnd.msgpack</c>) by Content-Type: binary formats can't be
/// recognised reliably from their bytes. The tree holds at most
/// <see cref="TreeBudget.DefaultMaxNodes"/> values, and says so when a
/// body has more.
/// </summary>
public sealed class TreeInspector : IInspector
{
    private enum Format
    {
        None,
        Json,
        Cbor,
        MessagePack,
    }

    private readonly int _maxNodes;

    public TreeInspector()
        : this(TreeBudget.DefaultMaxNodes)
    {
    }

    internal TreeInspector(int maxNodes)
    {
        _maxNodes = maxNodes;
    }

    public string Id => "violens.tree";

    public string DisplayName => "Tree";

    public int SortOrder => 13;

    public bool CanInspect(InspectorContext context) =>
        context.Body.Length > 0 && (FormatOf(context) != Format.None || new JsonInspector().CanInspect(context));

    public InspectorContent Inspect(InspectorContext context)
    {
        var budget = new TreeBudget(_maxNodes);
        var format = FormatOf(context);
        if (format is Format.None or Format.Json)
        {
            if (!JsonInspector.TryParse(context, out var document, out var error))
            {
                return new ErrorContent(error!);
            }

            using (document)
            {
                budget.TryTake();
                var root = JsonTree.Build(document!.RootElement, "", budget);
                return new TreeContent([root], Summary("JSON", items: null, budget, "the JSON tab"));
            }
        }

        if (!ContentDecoder.TryDecode(context.Body, context.FindHeader("Content-Encoding"), out var bytes, out var decodeError))
        {
            return new ErrorContent(decodeError!);
        }

        var name = format == Format.Cbor ? "CBOR" : "MessagePack";
        BinaryTreeReader reader = format == Format.Cbor ? new CborTree(bytes, budget) : new MessagePackTree(bytes, budget);
        try
        {
            var roots = reader.ReadAll();
            return new TreeContent(roots, Summary(name, reader.ValueCount, budget, "the Hex tab"));
        }
        catch (TreeFormatException ex)
        {
            return new ErrorContent($"The body isn't valid {name}: {ex.Message} See the Hex tab for the bytes.");
        }
    }

    private static Format FormatOf(InspectorContext context)
    {
        var mediaType = ContentDecoder.MediaType(context.FindHeader("Content-Type"));
        if (JsonInspector.IsJsonMediaType(mediaType))
        {
            return Format.Json;
        }

        if (mediaType is "application/cbor" or "application/cbor-seq" || mediaType.EndsWith("+cbor", StringComparison.Ordinal))
        {
            return Format.Cbor;
        }

        return mediaType is "application/msgpack" or "application/x-msgpack" or "application/vnd.msgpack"
            ? Format.MessagePack
            : Format.None;
    }

    /// <summary>"CBOR sequence of 3 items, 120 values", with a warning instead of the count when the tree was cut short.</summary>
    private static string Summary(string format, int? items, TreeBudget budget, string fullView)
    {
        var summary = items > 1 ? $"{format} sequence of {items:N0} items" : format;
        return budget.Truncated
            ? $"{summary}. Only the first {budget.Shown:N0} values are shown; {fullView} has the whole body."
            : $"{summary}, {budget.Shown:N0} {(budget.Shown == 1 ? "value" : "values")}.";
    }
}
