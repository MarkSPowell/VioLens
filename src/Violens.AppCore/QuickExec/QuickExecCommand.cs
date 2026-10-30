using System.Globalization;
using System.Text;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;

namespace Violens.AppCore.QuickExec;

/// <summary>What a QuickExec command does.</summary>
public enum QuickExecKind
{
    /// <summary><c>?text</c>: select sessions whose URL contains the text.</summary>
    SelectUrl,

    /// <summary><c>@host</c>: select sessions whose host contains the text.</summary>
    SelectHost,

    /// <summary><c>=404</c>: select sessions with that status code.</summary>
    SelectStatus,

    /// <summary><c>=POST</c>: select sessions with that method.</summary>
    SelectMethod,

    /// <summary><c>&gt;40k</c>: select sessions whose response body is larger.</summary>
    SelectLarger,

    /// <summary><c>&lt;1k</c>: select sessions whose response body is smaller.</summary>
    SelectSmaller,

    /// <summary><c>select image</c>: select sessions whose response Content-Type contains the text.</summary>
    SelectContentType,

    /// <summary><c>allbut image</c> (or <c>keeponly</c>): remove every session whose response Content-Type doesn't contain the text.</summary>
    KeepOnlyContentType,

    /// <summary><c>cls</c> (or <c>clear</c>): remove every session.</summary>
    Clear,

    /// <summary><c>tail 100</c>: keep only the newest sessions.</summary>
    Tail,

    /// <summary><c>bpu text</c>: pause requests whose URL contains the text (<c>bpu</c> alone stops).</summary>
    BreakOnRequestUrl,

    /// <summary><c>bpafter text</c>: pause responses to requests whose URL contains the text.</summary>
    BreakOnResponseUrl,

    /// <summary><c>bps 404</c>: pause responses with that status code.</summary>
    BreakOnStatus,

    /// <summary><c>bpm POST</c> (or <c>bpv</c>): pause requests with that method.</summary>
    BreakOnMethod,

    /// <summary><c>go</c>: let every paused session continue.</summary>
    ResumeAll,

    /// <summary><c>start</c>: start capturing.</summary>
    Start,

    /// <summary><c>stop</c>: stop capturing.</summary>
    Stop,

    /// <summary><c>help</c>: open the User Guide.</summary>
    Help,

    /// <summary><c>dump</c>: save every session to a new SAZ file in the usual place, as Fiddler's <c>dump</c> does.</summary>
    Dump,

    /// <summary><c>save path</c>: save every session to that file, as HAR when it ends in .har and SAZ otherwise.</summary>
    Save,

    /// <summary><c>open path</c>: add the sessions in a SAZ or HAR file.</summary>
    Open,

    /// <summary><c>filter text</c>: set the session filter (<c>filter</c> alone clears it).</summary>
    Filter,

    /// <summary><c>keep 1000</c> or <c>keep all</c>: how many sessions to keep.</summary>
    Keep,

    /// <summary>Anything else: offered to FiddlerScript's OnExecAction and extensions' IHandleExecAction.</summary>
    Other,
}

/// <summary>
/// A parsed QuickExec command: what to do, its argument, and the words the
/// line was split into (what FiddlerScript and extensions receive).
/// </summary>
public sealed record QuickExecCommand(QuickExecKind Kind, string Argument, IReadOnlyList<string> Words, long Number = 0)
{
    /// <summary>True for commands that pick sessions out of the list.</summary>
    public bool IsSelection => Kind is QuickExecKind.SelectUrl or QuickExecKind.SelectHost or QuickExecKind.SelectStatus
        or QuickExecKind.SelectMethod or QuickExecKind.SelectLarger or QuickExecKind.SelectSmaller or QuickExecKind.SelectContentType;
}

/// <summary>
/// QuickExec, the command box under the session list (Alt+Q), with the
/// commands Fiddler Classic users type there: <c>?text</c>, <c>@host</c>,
/// <c>=404</c>, <c>&gt;40k</c>, <c>select</c>, <c>allbut</c>, <c>cls</c>,
/// <c>tail</c>, the <c>bpu</c>/<c>bpafter</c>/<c>bps</c>/<c>bpm</c>
/// breakpoints, <c>go</c>, <c>start</c>, <c>stop</c>, <c>dump</c> and
/// <c>help</c>, plus VioLens's own <c>save</c>, <c>open</c>, <c>filter</c>
/// and <c>keep</c> for scripts driving it through violens-exec. Anything
/// else goes to FiddlerScript and extensions.
/// </summary>
public static class QuickExecParser
{
    /// <summary>The command on <paramref name="line"/>, or null for a blank line.</summary>
    public static QuickExecCommand? Parse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var words = SplitArguments(text);
        var rest = text.Length > 1 ? text[1..].Trim() : string.Empty;
        switch (text[0])
        {
            case '?':
                return new QuickExecCommand(QuickExecKind.SelectUrl, rest, words);
            case '@':
                return new QuickExecCommand(QuickExecKind.SelectHost, rest, words);
            case '=':
                return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
                    ? new QuickExecCommand(QuickExecKind.SelectStatus, rest, words, status)
                    : new QuickExecCommand(QuickExecKind.SelectMethod, rest, words);
            case '>' when TryParseSize(rest, out var larger):
                return new QuickExecCommand(QuickExecKind.SelectLarger, rest, words, larger);
            case '<' when TryParseSize(rest, out var smaller):
                return new QuickExecCommand(QuickExecKind.SelectSmaller, rest, words, smaller);
        }

        var name = words[0].ToLowerInvariant();
        var space = text.IndexOfAny([' ', '\t']);
        var argument = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        if (argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"')
        {
            argument = argument[1..^1];
        }

        return name switch
        {
            "select" => new QuickExecCommand(QuickExecKind.SelectContentType, argument, words),
            "allbut" or "keeponly" => new QuickExecCommand(QuickExecKind.KeepOnlyContentType, argument, words),
            "cls" or "clear" => new QuickExecCommand(QuickExecKind.Clear, argument, words),
            "tail" when long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var count) =>
                new QuickExecCommand(QuickExecKind.Tail, argument, words, count),
            "bpu" => new QuickExecCommand(QuickExecKind.BreakOnRequestUrl, argument, words),
            "bpafter" => new QuickExecCommand(QuickExecKind.BreakOnResponseUrl, argument, words),
            "bps" => int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                ? new QuickExecCommand(QuickExecKind.BreakOnStatus, argument, words, code)
                : new QuickExecCommand(QuickExecKind.BreakOnStatus, string.Empty, words),
            "bpm" or "bpv" => new QuickExecCommand(QuickExecKind.BreakOnMethod, argument.ToUpperInvariant(), words),
            "go" => new QuickExecCommand(QuickExecKind.ResumeAll, argument, words),
            "start" => new QuickExecCommand(QuickExecKind.Start, argument, words),
            "stop" => new QuickExecCommand(QuickExecKind.Stop, argument, words),
            "help" => new QuickExecCommand(QuickExecKind.Help, argument, words),
            "dump" => new QuickExecCommand(QuickExecKind.Dump, argument, words),
            "save" => new QuickExecCommand(QuickExecKind.Save, argument, words),
            "open" => new QuickExecCommand(QuickExecKind.Open, argument, words),
            "filter" => new QuickExecCommand(QuickExecKind.Filter, argument, words),
            "keep" when string.Equals(argument, "all", StringComparison.OrdinalIgnoreCase) =>
                new QuickExecCommand(QuickExecKind.Keep, argument, words, 0),
            "keep" when long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var keep) && keep is > 0 and <= int.MaxValue =>
                new QuickExecCommand(QuickExecKind.Keep, argument, words, keep),
            _ => new QuickExecCommand(QuickExecKind.Other, argument, words),
        };
    }

    /// <summary>Whether <paramref name="session"/> is picked by a selection command (or kept by <c>allbut</c>).</summary>
    public static bool Matches(QuickExecCommand command, Session session) => command.Kind switch
    {
        QuickExecKind.SelectUrl => SessionUrl.Of(session).Contains(command.Argument, StringComparison.OrdinalIgnoreCase),
        QuickExecKind.SelectHost => HostOf(session).Contains(command.Argument, StringComparison.OrdinalIgnoreCase),
        QuickExecKind.SelectStatus => session.Response.StatusCode == command.Number,
        QuickExecKind.SelectMethod => string.Equals(session.Request.Method, command.Argument, StringComparison.OrdinalIgnoreCase),
        QuickExecKind.SelectLarger => session.Response.Body.Length > command.Number,
        QuickExecKind.SelectSmaller => session.Response.Body.Length < command.Number,
        QuickExecKind.SelectContentType or QuickExecKind.KeepOnlyContentType =>
            (ContentDecoder.FindHeader(session.Response.Headers, "Content-Type") ?? string.Empty)
                .Contains(command.Argument, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>A size such as <c>40000</c>, <c>40k</c>, <c>40kb</c> or <c>2mb</c>, in bytes.</summary>
    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim().ToLowerInvariant();
        long multiplier = 1;
        foreach (var (suffix, factor) in new[] { ("kb", 1024L), ("mb", 1024L * 1024), ("k", 1024L), ("m", 1024L * 1024), ("b", 1L) })
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                trimmed = trimmed[..^suffix.Length].Trim();
                multiplier = factor;
                break;
            }
        }

        if (!double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        bytes = (long)Math.Round(number * multiplier);
        return true;
    }

    /// <summary>
    /// Splits a command line into words at spaces, keeping "quoted text"
    /// together (without the quotes), as Fiddler hands commands to scripts
    /// and extensions.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var hadQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                hadQuotes = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (word.Length > 0 || hadQuotes)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    hadQuotes = false;
                }
            }
            else
            {
                word.Append(c);
            }
        }

        if (word.Length > 0 || hadQuotes)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static string HostOf(Session session) =>
        ContentDecoder.FindHeader(session.Request.Headers, "Host") ?? session.Host;
}
