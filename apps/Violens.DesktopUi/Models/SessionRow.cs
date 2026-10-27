using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Violens.Compatibility.FiddlerScript;
using Violens.Extensibility.Inspection;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;

namespace Violens.DesktopUi.Models;

/// <summary>
/// A session formatted for the session list. Keeps the <see cref="Session"/>
/// itself for the inspectors.
/// </summary>
public sealed class SessionRow
{
    private static readonly IReadOnlyDictionary<string, string> NoScriptColumns = new Dictionary<string, string>();

    public required int Id { get; init; }
    public required string Time { get; init; }
    public required int StatusCode { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }

    /// <summary>The HTTP version with the client, and with the server where it differs: "HTTP/2 → HTTP/1.1".</summary>
    public required string Version { get; init; }
    public required string RequestSize { get; init; }
    public required string ResponseSize { get; init; }
    public required Session Session { get; init; }

    /// <summary>
    /// Values for script-defined columns, keyed by title. Computed once when the
    /// row is built; reloading a script doesn't update existing rows.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ScriptColumns { get; init; }

    /// <summary>
    /// The Image Bloat column: "bloat / total bytes (percent)" for a PNG,
    /// JPEG, GIF or WebP response, empty otherwise. See <see cref="ImageBloatAnalyzer"/>.
    /// </summary>
    public string ImageBloat { get; private init; } = string.Empty;

    /// <summary>
    /// The session's flags by name (case-insensitive), for session-list
    /// columns an extension binds to a flag (<c>{Binding Flags[X-Privacy]}</c>);
    /// a missing flag reads as an empty string.
    /// </summary>
    public FlagLookup Flags { get; private init; } = FlagLookup.Empty;

    /// <summary>
    /// Whether the last Find (Edit &gt; Find Sessions) matched this session;
    /// the session list highlights it. Changed only on the UI thread.
    /// </summary>
    public bool IsFindMatch { get; set; }

    /// <summary>How the session list draws this row, from its <c>ui-*</c> flags. See <see cref="SessionRowStyle"/>.</summary>
    public SessionRowStyle Style { get; private init; } = SessionRowStyle.Plain;

    /// <summary>Background for a row whose image is mostly bloat (see <see cref="ImageBloatReport.IsHeavy"/>), unless flags set one.</summary>
    private static readonly IBrush HeavyBloatBackground = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0xB0));

    private static readonly IBrush HeavyBloatForeground = new ImmutableSolidColorBrush(Colors.Black);

    /// <param name="scriptRunner">
    /// When the loaded script has <c>[BindUIColumn]</c> methods, they're run
    /// against this session to fill <see cref="ScriptColumns"/>.
    /// </param>
    public static SessionRow From(Session session, FiddlerScriptRunner? scriptRunner = null)
    {
        var bloat = ImageBloatAnalyzer.AnalyzeResponse(session.Response);
        var style = SessionRowStyle.From(session.Flags);
        if (bloat is { IsHeavy: true } && style.Background is null)
        {
            style = style with { Background = HeavyBloatBackground, Foreground = style.Foreground ?? HeavyBloatForeground };
        }

        return new SessionRow
        {
            Id = session.Id,
            Time = session.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            StatusCode = session.Response.StatusCode,
            Method = session.Request.Method,
            Url = $"{session.Scheme}://{session.Host}{session.Request.Target}",
            Version = HttpVersions.Describe(session.Request.HttpVersion, session.Response.StatusCode == 0 ? null : session.Response.HttpVersion),
            RequestSize = FormatBytes(session.Request.Body.Length),
            ResponseSize = FormatBytes(session.Response.Body.Length),
            Session = session,
            ScriptColumns = BuildScriptColumns(session, scriptRunner),
            Flags = new FlagLookup(session.Flags),
            Style = style,
            ImageBloat = bloat?.ColumnText ?? string.Empty,
        };
    }

    private static string FormatBytes(int bytes) => bytes < 1024
        ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
        : (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";

    private static IReadOnlyDictionary<string, string> BuildScriptColumns(Session session, FiddlerScriptRunner? scriptRunner)
    {
        if (scriptRunner is null || scriptRunner.Directives.UIColumns.Count == 0)
        {
            return NoScriptColumns;
        }

        var columns = new Dictionary<string, string>();
        foreach (var column in scriptRunner.Directives.UIColumns)
        {
            columns[column.ColumnTitle] = scriptRunner.ComputeUIColumnValue(column.MethodName, session);
        }

        return columns;
    }
}

/// <summary>A session's flags, readable by name from a binding path; a missing one is an empty string.</summary>
public sealed class FlagLookup
{
    public static readonly FlagLookup Empty = new(null);

    private readonly IReadOnlyDictionary<string, string>? _flags;

    public FlagLookup(IReadOnlyDictionary<string, string>? flags) => _flags = flags;

    public string this[string name]
    {
        get
        {
            if (_flags is null)
            {
                return string.Empty;
            }

            if (_flags.TryGetValue(name, out var value))
            {
                return value;
            }

            // Flags from older sources may not use a case-insensitive
            // dictionary; Fiddler flag names never depend on case.
            foreach (var (key, candidate) in _flags)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }
    }
}

/// <summary>
/// How a row looks, from the Fiddler Classic flags an importer, extension or
/// script set on its session:
/// <list type="bullet">
/// <item><c>ui-backcolor</c> / <c>ui-color</c>: background / text colour,
/// as a name (<c>Lime</c>) or <c>#RRGGBB</c>. A background without a text
/// colour gets black or white text, whichever reads better on it, so the
/// row stays readable in both light and dark themes.</item>
/// <item><c>ui-bold</c>, <c>ui-italic</c>, <c>ui-strikeout</c>: present
/// (any value) to turn on.</item>
/// <item><c>ui-hide</c>: present to leave the row out of the list.</item>
/// </list>
/// An unrecognised colour is ignored.
/// </summary>
public sealed record SessionRowStyle(
    IBrush? Background,
    IBrush? Foreground,
    bool Bold,
    bool Italic,
    bool Strikeout,
    bool Hidden)
{
    public static readonly SessionRowStyle Plain = new(null, null, false, false, false, false);

    public bool IsPlain => this == Plain;

    public static SessionRowStyle From(IReadOnlyDictionary<string, string>? flags)
    {
        if (flags is null || flags.Count == 0)
        {
            return Plain;
        }

        var lookup = new FlagLookup(flags);
        Color? back = ParseColor(lookup["ui-backcolor"]);
        Color? fore = ParseColor(lookup["ui-color"]);
        if (back is { } background && fore is null)
        {
            fore = ReadableTextOn(background);
        }

        var style = new SessionRowStyle(
            back is { } b ? new ImmutableSolidColorBrush(b) : null,
            fore is { } f ? new ImmutableSolidColorBrush(f) : null,
            Has(flags, "ui-bold"),
            Has(flags, "ui-italic"),
            Has(flags, "ui-strikeout"),
            Has(flags, "ui-hide"));

        return style == Plain ? Plain : style;
    }

    private static bool Has(IReadOnlyDictionary<string, string> flags, string name) =>
        flags.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static Color? ParseColor(string text) =>
        !string.IsNullOrWhiteSpace(text) && Color.TryParse(text.Trim(), out var color) ? color : null;

    /// <summary>Black or white, whichever contrasts more with <paramref name="background"/> (WCAG relative luminance).</summary>
    private static Color ReadableTextOn(Color background)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var luminance = (0.2126 * Linear(background.R)) + (0.7152 * Linear(background.G)) + (0.0722 * Linear(background.B));
        return luminance > 0.179 ? Colors.Black : Colors.White;
    }
}
