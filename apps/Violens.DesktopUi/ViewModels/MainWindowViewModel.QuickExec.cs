using Violens.DesktopUi.Models;
using Violens.AppCore.QuickExec;
using Violens.Automation;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// QuickExec, the command box under the session list (Alt+Q), as in Fiddler
/// Classic. Built-in commands run here (see <see cref="QuickExecParser"/>);
/// anything else is offered to the loaded FiddlerScript's
/// <c>OnExecAction</c>, then to extensions' <c>IHandleExecAction</c>.
/// Up and Down go through earlier commands. As a command is typed,
/// suggestions appear above the box (see <see cref="QuickExecCompletion"/>):
/// Tab takes the highlighted one, and Up and Down move between them. The
/// same commands arrive from violens-exec (see MainWindowViewModel.Automation.cs).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const int QuickExecHistoryLimit = 50;

    private readonly List<string> _quickExecHistory = [];
    private int _quickExecHistoryPosition;
    private string _quickExecText = string.Empty;
    private RelayCommand? _runQuickExecCommand;
    private IReadOnlyList<QuickExecSuggestion> _quickExecSuggestions = [];
    private int _quickExecSuggestionIndex;
    private bool _browsingQuickExecHistory;

    /// <summary>Raised with the sessions a selection command picked, for the window to select in the list.</summary>
    public event Action<IReadOnlyList<SessionRow>>? QuickExecSelectionRequested;

    public string QuickExecText
    {
        get => _quickExecText;
        set
        {
            if (SetField(ref _quickExecText, value ?? string.Empty))
            {
                // A command brought back from history is run as it is, not completed.
                QuickExecSuggestions = _browsingQuickExecHistory ? [] : QuickExecCompletion.Suggest(_quickExecText);
            }
        }
    }

    /// <summary>What the typed text could become; empty hides the suggestions.</summary>
    public IReadOnlyList<QuickExecSuggestion> QuickExecSuggestions
    {
        get => _quickExecSuggestions;
        private set
        {
            if (SetField(ref _quickExecSuggestions, value))
            {
                QuickExecSuggestionIndex = value.Count > 0 ? 0 : -1;
                RaisePropertyChanged(nameof(HasQuickExecSuggestions));
            }
        }
    }

    public bool HasQuickExecSuggestions => _quickExecSuggestions.Count > 0;

    /// <summary>The highlighted suggestion, which Tab takes.</summary>
    public int QuickExecSuggestionIndex
    {
        get => _quickExecSuggestionIndex;
        set => SetField(ref _quickExecSuggestionIndex, value);
    }

    /// <summary>Up (-1) or Down (+1) among the suggestions, wrapping round.</summary>
    public void MoveQuickExecSuggestion(int step)
    {
        var count = _quickExecSuggestions.Count;
        if (count > 0)
        {
            QuickExecSuggestionIndex = ((QuickExecSuggestionIndex + step) % count + count) % count;
        }
    }

    /// <summary>Puts the highlighted suggestion in the box; false when there's none.</summary>
    public bool AcceptQuickExecSuggestion()
    {
        if (QuickExecSuggestionIndex < 0 || QuickExecSuggestionIndex >= _quickExecSuggestions.Count)
        {
            return false;
        }

        QuickExecText = _quickExecSuggestions[QuickExecSuggestionIndex].Text;
        return true;
    }

    public void DismissQuickExecSuggestions() => QuickExecSuggestions = [];

    /// <summary>Runs <see cref="QuickExecText"/> (Enter in the box).</summary>
    public RelayCommand RunQuickExecCommand => _runQuickExecCommand ??= new RelayCommand(() =>
    {
        var line = QuickExecText.Trim();
        if (line.Length == 0)
        {
            return;
        }

        _quickExecHistory.Remove(line);
        _quickExecHistory.Add(line);
        if (_quickExecHistory.Count > QuickExecHistoryLimit)
        {
            _quickExecHistory.RemoveAt(0);
        }

        _quickExecHistoryPosition = _quickExecHistory.Count;
        QuickExecText = string.Empty;
        _ = RunQuickExecAsync(line);
    });

    /// <summary>Up (-1) or Down (+1) in the box: the previous or next command typed.</summary>
    public void BrowseQuickExecHistory(int step)
    {
        if (_quickExecHistory.Count == 0)
        {
            return;
        }

        _quickExecHistoryPosition = Math.Clamp(_quickExecHistoryPosition + step, 0, _quickExecHistory.Count);
        _browsingQuickExecHistory = true;
        try
        {
            QuickExecText = _quickExecHistoryPosition < _quickExecHistory.Count ? _quickExecHistory[_quickExecHistoryPosition] : string.Empty;
        }
        finally
        {
            _browsingQuickExecHistory = false;
        }
    }

    /// <summary>
    /// Runs one QuickExec command, reporting what it did on the status line,
    /// and returns that report. Call on the UI thread. A relative path in
    /// <c>save</c> or <c>open</c> is taken from <paramref name="baseDirectory"/>
    /// (violens-exec's current folder), or the Desktop when typed in the box.
    /// </summary>
    public async Task<ExecReply> RunQuickExecAsync(string line, string? baseDirectory = null)
    {
        ExecReply Report(string message, bool succeeded = true)
        {
            StatusText = message;
            return new ExecReply(succeeded, message);
        }

        ExecReply Status(bool succeeded) => new(succeeded, StatusText);

        if (QuickExecParser.Parse(line) is not { } command)
        {
            return Report("QuickExec: there was no command.", succeeded: false);
        }

        if (command.IsSelection)
        {
            var picked = FilteredSessions.Where(row => QuickExecParser.Matches(command, row.Session)).ToList();
            QuickExecSelectionRequested?.Invoke(picked);
            return Report(picked.Count == 0
                ? $"QuickExec: no sessions in the list match {line}."
                : $"QuickExec: selected {picked.Count} {(picked.Count == 1 ? "session" : "sessions")} matching {line}.");
        }

        switch (command.Kind)
        {
            case QuickExecKind.KeepOnlyContentType:
            {
                if (command.Argument.Length == 0)
                {
                    return Report("QuickExec: allbut needs a Content-Type to keep, such as allbut image or allbut json.", succeeded: false);
                }

                var remove = Sessions.Where(row => !QuickExecParser.Matches(command, row.Session)).Select(row => row.Id).ToList();
                var removed = _sessionStore.Remove(remove);
                return Report($"QuickExec: removed {removed} {(removed == 1 ? "session" : "sessions")} whose Content-Type doesn't contain \"{command.Argument}\".");
            }

            case QuickExecKind.Clear:
                _sessionStore.Clear();
                return Report("QuickExec: removed all sessions.");

            case QuickExecKind.Tail:
            {
                var remove = Sessions.OrderBy(row => row.Id).Select(row => row.Id).SkipLast((int)Math.Min(command.Number, int.MaxValue)).ToList();
                var removed = _sessionStore.Remove(remove);
                return Report($"QuickExec: kept the newest {command.Number}, removed {removed}.");
            }

            case QuickExecKind.BreakOnRequestUrl:
                RequestUrlContains = NullIfEmpty(command.Argument);
                return Report(DescribeBreakpoint(command.Argument, $"requests whose URL contains \"{command.Argument}\"", "bpu"));

            case QuickExecKind.BreakOnResponseUrl:
                ResponseUrlContains = NullIfEmpty(command.Argument);
                return Report(DescribeBreakpoint(command.Argument, $"responses to URLs containing \"{command.Argument}\"", "bpafter"));

            case QuickExecKind.BreakOnStatus:
                ResponseStatusCodeEquals = command.Argument.Length == 0 ? null : command.Number;
                return Report(DescribeBreakpoint(command.Argument, $"responses with status {command.Argument}", "bps"));

            case QuickExecKind.BreakOnMethod:
                RequestMethodEquals = NullIfEmpty(command.Argument);
                return Report(DescribeBreakpoint(command.Argument, $"{command.Argument} requests", "bpm"));

            case QuickExecKind.ResumeAll:
            {
                var paused = PendingBreakpoints.ToList();
                foreach (var pending in paused)
                {
                    if (pending.ResumeCommand.CanExecute(null))
                    {
                        pending.ResumeCommand.Execute(null);
                    }
                }

                return Report(paused.Count == 0 ? "QuickExec: nothing is paused." : $"QuickExec: let {paused.Count} paused {(paused.Count == 1 ? "session" : "sessions")} continue.");
            }

            case QuickExecKind.Start:
                if (!StartCommand.CanExecute(null))
                {
                    return Report(IsRunning ? "QuickExec: already capturing." : "QuickExec: can't start; check the port setting.", succeeded: IsRunning);
                }

                await StartAsync();
                return Status(IsRunning);

            case QuickExecKind.Stop:
                if (!StopCommand.CanExecute(null))
                {
                    return Report("QuickExec: not capturing.");
                }

                await StopAsync();
                return Status(!IsRunning);

            case QuickExecKind.Dump:
                return Status(SaveSazTo(DefaultSazPath()));

            case QuickExecKind.Save:
            {
                if (command.Argument.Length == 0)
                {
                    return Report("QuickExec: save needs a file name, such as save capture.saz or save capture.har.", succeeded: false);
                }

                var path = ResolvePath(command.Argument, baseDirectory);
                var saved = string.Equals(Path.GetExtension(path), ".har", StringComparison.OrdinalIgnoreCase)
                    ? await ExportHarAsync(path)
                    : SaveSazTo(path);
                return Status(saved);
            }

            case QuickExecKind.Open:
            {
                if (command.Argument.Length == 0)
                {
                    return Report("QuickExec: open needs a .saz, .har or packet capture (.pcap, .pcapng) file.", succeeded: false);
                }

                var path = ResolvePath(command.Argument, baseDirectory);
                var opened = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".har" => await ImportHarAsync(path),
                    ".pcap" or ".pcapng" or ".cap" => await ImportPcapAsync(path),
                    _ => await ImportSazAsync(path),
                };
                return Status(opened);
            }

            case QuickExecKind.Filter:
                FilterText = command.Argument;
                return Report(command.Argument.Length == 0
                    ? "QuickExec: filter cleared."
                    : $"QuickExec: showing {FilteredSessions.Count} of {Sessions.Count} sessions matching {command.Argument}.");

            case QuickExecKind.Keep:
                SessionsToKeep = (int)command.Number;
                return Report(command.Number == 0 ? "QuickExec: keeping all sessions." : $"QuickExec: keeping the newest {command.Number:N0} sessions.");

            case QuickExecKind.Help:
                OpenDocumentationCommand.Execute(null);
                return Report("QuickExec commands: ?text  @host  =404  =POST  >40k  <1k  select type  allbut type  cls  tail N  " +
                              "bpu text  bpafter text  bps 404  bpm POST  go  start  stop  dump  save file  open file  filter text  keep N. " +
                              "The User Guide's QuickExec section has more.");
        }

        // Not built in: the script's OnExecAction, then extensions.
        if (_fiddlerScriptRunner.InvokeOnExecAction(command.Words))
        {
            return Report($"QuickExec: FiddlerScript ran {command.Words[0]}.");
        }

        foreach (var handler in _extensionHost.ExecActionHandlers)
        {
            try
            {
                if (handler.OnExecAction(line))
                {
                    return Report($"QuickExec: {handler.GetType().Name} ran {command.Words[0]}.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QuickExec] {handler.GetType().FullName} failed on '{line}': {ex.Message}");
            }
        }

        return Report($"QuickExec: \"{command.Words[0]}\" isn't a command VioLens, the loaded FiddlerScript or an extension knows. Type help for the list.", succeeded: false);
    }

    /// <summary>A path as given, or relative to <paramref name="baseDirectory"/> (the Desktop when there's none).</summary>
    private static string ResolvePath(string path, string? baseDirectory) =>
        Path.GetFullPath(path, string.IsNullOrWhiteSpace(baseDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : baseDirectory);

    private string DescribeBreakpoint(string argument, string what, string command)
    {
        if (argument.Length == 0)
        {
            return $"QuickExec: {command} breakpoint cleared.";
        }

        ShowAlsoBreakOnRow = true;
        return $"QuickExec: VioLens will pause {what}. Type {command} on its own to stop.";
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
