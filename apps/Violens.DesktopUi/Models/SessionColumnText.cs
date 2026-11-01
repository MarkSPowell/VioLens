using System.Globalization;

namespace Violens.DesktopUi.Models;

/// <summary>
/// A session-list cell's text, from its column's binding path, for a column
/// header's Find in This Column and Highlight Repeated Values. Covers the
/// paths the list uses: a <see cref="SessionRow"/> property,
/// <c>ScriptColumns[Title]</c> and <c>Flags[Name]</c>. Anything else (the
/// Waterfall, which has no text) gives null.
/// </summary>
public static class SessionColumnText
{
    public static Func<SessionRow, string?>? For(string? bindingPath)
    {
        switch (bindingPath)
        {
            case null or "":
                return null;
            case nameof(SessionRow.Id):
                return row => row.Id.ToString(CultureInfo.InvariantCulture);
            case nameof(SessionRow.Time):
                return row => row.Time;
            case nameof(SessionRow.StatusCode):
                return row => row.StatusCode.ToString(CultureInfo.InvariantCulture);
            case nameof(SessionRow.Method):
                return row => row.Method;
            case nameof(SessionRow.Url):
                return row => row.Url;
            case nameof(SessionRow.Version):
                return row => row.Version;
            case nameof(SessionRow.RequestSize):
                return row => row.RequestSize;
            case nameof(SessionRow.ResponseSize):
                return row => row.ResponseSize;
            case nameof(SessionRow.ImageBloat):
                return row => row.ImageBloat;
        }

        if (Indexed(bindingPath, "ScriptColumns") is { } title)
        {
            return row => row.ScriptColumns.TryGetValue(title, out var value) ? value : string.Empty;
        }

        if (Indexed(bindingPath, "Flags") is { } flag)
        {
            return row => row.Flags[flag];
        }

        return null;
    }

    /// <summary>"Name[key]" → key, or null when the path isn't that indexer.</summary>
    private static string? Indexed(string path, string name) =>
        path.StartsWith(name + "[", StringComparison.Ordinal) && path.EndsWith(']')
            ? path[(name.Length + 1)..^1]
            : null;
}
