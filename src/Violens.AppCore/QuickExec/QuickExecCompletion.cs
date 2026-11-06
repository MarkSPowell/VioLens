namespace Violens.AppCore.QuickExec;

/// <summary>
/// One suggestion under the QuickExec box: <see cref="Text"/> is what Tab
/// puts in the box, <see cref="Usage"/> how the command is written and
/// <see cref="Description"/> what it does.
/// </summary>
public sealed record QuickExecSuggestion(string Text, string Usage, string Description);

/// <summary>
/// QuickExec autocomplete (Clearinet #43): as a command is typed, the
/// built-in commands it could be, and for <c>select</c>, <c>allbut</c> and
/// <c>keep</c> the usual arguments. Matches the start of a command word,
/// case-insensitively. Commands a FiddlerScript or extension handles aren't
/// known in advance, so they aren't suggested.
/// </summary>
public static class QuickExecCompletion
{
    public const int MaxSuggestions = 8;

    private static readonly QuickExecSuggestion[] Commands =
    [
        new("?", "?text", "Select sessions whose URL contains the text"),
        new("@", "@host", "Select sessions whose host contains the text"),
        new("=", "=404 or =POST", "Select sessions with that status code or method"),
        new(">", ">40k", "Select sessions whose response body is larger"),
        new("<", "<1k", "Select sessions whose response body is smaller"),
        new("select ", "select type", "Select sessions whose Content-Type contains the text"),
        new("allbut ", "allbut type", "Remove every session whose Content-Type doesn't contain the text"),
        new("keeponly ", "keeponly type", "The same as allbut"),
        new("cls", "cls", "Remove all sessions"),
        new("clear", "clear", "Remove all sessions"),
        new("tail ", "tail 100", "Keep only the newest sessions"),
        new("bpu ", "bpu text", "Pause requests whose URL contains the text (bpu alone stops)"),
        new("bpafter ", "bpafter text", "Pause responses to requests whose URL contains the text"),
        new("bps ", "bps 404", "Pause responses with that status code"),
        new("bpm ", "bpm POST", "Pause requests with that method"),
        new("bpv ", "bpv POST", "The same as bpm"),
        new("go", "go", "Let every paused session continue"),
        new("start", "start", "Start capturing"),
        new("stop", "stop", "Stop capturing"),
        new("dump", "dump", "Save every session to a new .saz file on the Desktop"),
        new("save ", "save file", "Save every session to a .saz or .har file"),
        new("open ", "open file", "Add the sessions in a .saz, .har or packet capture file"),
        new("filter ", "filter text", "Set the session filter (filter alone clears it)"),
        new("keep ", "keep 1000", "How many sessions to keep (keep all for no limit)"),
        new("help", "help", "List the commands and open the User Guide"),
    ];

    private static readonly (string Argument, string Description)[] ContentTypes =
    [
        ("image", "Images"),
        ("json", "JSON"),
        ("html", "HTML pages"),
        ("javascript", "Scripts"),
        ("css", "Style sheets"),
        ("xml", "XML"),
        ("font", "Fonts"),
        ("video", "Video"),
        ("text", "Any text type"),
    ];

    private static readonly (string Argument, string Description)[] KeepCounts =
    [
        ("100", "Keep the newest 100"),
        ("1000", "Keep the newest 1,000"),
        ("10000", "Keep the newest 10,000"),
        ("all", "Keep every session"),
    ];

    /// <summary>Suggestions for <paramref name="text"/> as typed so far; empty when there's nothing useful to add.</summary>
    public static IReadOnlyList<QuickExecSuggestion> Suggest(string text)
    {
        var typed = text.TrimStart();
        if (typed.Length == 0)
        {
            return [];
        }

        var space = typed.IndexOf(' ');
        if (space < 0)
        {
            // Still on the command word. A symbol command already is the
            // whole command once its first character is typed.
            if (typed.Length > 1 && typed[0] is '?' or '@' or '=' or '>' or '<')
            {
                return [];
            }

            // A command already typed in full isn't suggested again, except
            // a symbol, whose suggestion says what to type after it.
            return Commands
                .Where(c => c.Text.TrimEnd() is var word &&
                            word.StartsWith(typed, StringComparison.OrdinalIgnoreCase) &&
                            (word.Length > typed.Length || word.Length == 1))
                .Take(MaxSuggestions)
                .ToList();
        }

        var command = typed[..space].ToLowerInvariant();
        var argument = typed[(space + 1)..].TrimStart();
        var arguments = command switch
        {
            "select" or "allbut" or "keeponly" => ContentTypes,
            "keep" => KeepCounts,
            _ => [],
        };

        return arguments
            .Where(a => a.Argument.StartsWith(argument, StringComparison.OrdinalIgnoreCase) &&
                        !a.Argument.Equals(argument, StringComparison.OrdinalIgnoreCase))
            .Select(a => new QuickExecSuggestion($"{command} {a.Argument}", $"{command} {a.Argument}", a.Description))
            .Take(MaxSuggestions)
            .ToList();
    }
}
