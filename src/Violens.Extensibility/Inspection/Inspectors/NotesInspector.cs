namespace Violens.Extensibility.Inspection.Inspectors;

/// <summary>
/// Shows a session's notes: the Fiddler-style per-session string flags
/// (<c>Session.Flags</c>) that an importer or extension attached, such as
/// the body size the browser reported for a NetLog capture that didn't
/// include bodies. Fiddler Classic shows these under a session's
/// Properties.
///
/// Notes belong to the whole session, not to one side, so this appears
/// only on the request side (the first pane people look at), and only for
/// sessions that have notes. Live traffic doesn't have any today, so the tab
/// stays out of the way until an import or extension adds some.
///
/// Notes whose meaning is known get a plain-English explanation after the
/// value. Everything else is shown as-is, sorted by name.
/// </summary>
public sealed class NotesInspector : IInspector
{
    /// <summary>
    /// Plain-English meanings for notes VioLens knows about, keyed
    /// case-insensitively (Fiddler lower-cases flag names). Mostly the ones
    /// Eric Lawrence's NetLog importer sets, taken from its public source.
    /// </summary>
    private static readonly Dictionary<string, string> KnownNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-responsebodytransferlength"] = "response body size the browser reported; the capture didn't include the body itself",
        ["x-requestbodylength"] = "request body size the browser reported; NetLog captures never include request bodies",
        ["x-netlog-urlrequest-id"] = "this request's ID inside the NetLog capture",
        ["x-netlog-urlrequest-url"] = "the full URL as the browser recorded it, including any #fragment",
        ["x-netlog-traffic_annotation"] = "Chromium's label for why the browser made this request",
        ["x-netlog-original-content-length"] = "the Content-Length the server sent, before the importer corrected it for the decoded body",
        ["x-processinfo"] = "the program that made the request",
        ["x-transport"] = "the protocol the browser used",
        ["x-client-certificate-requested"] = "the server asked for a client certificate (mutual TLS) from these issuers",
        ["x-client-certificate-sent"] = "the client certificate VioLens sent (Tools > Connections)",
        ["x-no-decrypt"] = "this host is in the skip-decryption list, so its traffic passed through encrypted",
        ["x-request-body-truncated"] = "the request body was too large to keep in full; it was sent on in full, but only its start was captured",
        ["x-response-body-truncated"] = "the response body was too large to keep in full; it was passed on in full, but only its start was captured",
        ["ui-comments"] = "a comment attached by the importer or an extension",
        ["ui-backcolor"] = "the row color the importer or an extension asked for",
        ["ui-color"] = "the text color the importer or an extension asked for",
    };

    public string Id => "violens.notes";

    public string DisplayName => "Notes";

    public int SortOrder => 30;

    public bool CanInspect(InspectorContext context) =>
        context.Side == InspectorSide.Request && context.Session.Flags is { Count: > 0 };

    public InspectorContent Inspect(InspectorContext context)
    {
        var flags = context.Session.Flags;
        if (flags is null || flags.Count == 0)
        {
            return new KeyValueContent([]);
        }

        var rows = flags
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new HeaderRow(
                pair.Key,
                KnownNotes.TryGetValue(pair.Key, out var meaning) ? $"{pair.Value}  ({meaning})" : pair.Value))
            .ToList();

        return new KeyValueContent(rows);
    }
}
