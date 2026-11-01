using System.Text.RegularExpressions;
using Avalonia.Threading;
using Violens.DesktopUi.Models;
using Violens.ProxyCore.Sessions;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// Edit &gt; Find Sessions (Ctrl+F), Fiddler Classic's Find: highlights every
/// captured session that contains some text, in its URL, headers or
/// (decompressed) bodies, and selects the first one. F3 moves to the next.
/// Unlike the filter box, it doesn't hide anything. See <see cref="SessionSearch"/>.
///
/// Right-clicking a column header can scope Find to that column (its text
/// as the list shows it), or highlight every session whose value in the
/// column is shared with another session (Highlight Repeated Values).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool _isFindBarVisible;
    private string _findText = string.Empty;
    private int _findTargetIndex;
    private int _findPartsIndex;
    private bool _findMatchCase;
    private bool _findUseRegex;
    private string _findStatus = string.Empty;
    private bool _isFinding;
    private RelayCommand? _showFindCommand;
    private RelayCommand? _findCommand;
    private RelayCommand? _findNextCommand;
    private RelayCommand? _closeFindCommand;
    private RelayCommand? _clearFindColumnCommand;
    private string _findColumnTitle = string.Empty;
    private Func<SessionRow, string?>? _findColumnText;

    /// <summary>Raised when the Find bar should take keyboard focus.</summary>
    public event Action? FindFocusRequested;

    /// <summary>Raised when which rows are highlighted changed, so the session list redraws them.</summary>
    public event Action? FindResultsChanged;

    public bool IsFindBarVisible
    {
        get => _isFindBarVisible;
        private set => SetField(ref _isFindBarVisible, value);
    }

    public string FindText
    {
        get => _findText;
        set => SetField(ref _findText, value ?? string.Empty);
    }

    /// <summary>0: requests and responses, 1: requests only, 2: responses only.</summary>
    public int FindTargetIndex
    {
        get => _findTargetIndex;
        set => SetField(ref _findTargetIndex, value);
    }

    /// <summary>0: headers and bodies, 1: headers only, 2: bodies only.</summary>
    public int FindPartsIndex
    {
        get => _findPartsIndex;
        set => SetField(ref _findPartsIndex, value);
    }

    public bool FindMatchCase
    {
        get => _findMatchCase;
        set => SetField(ref _findMatchCase, value);
    }

    public bool FindUseRegex
    {
        get => _findUseRegex;
        set => SetField(ref _findUseRegex, value);
    }

    /// <summary>The column Find is limited to, or empty for whole sessions.</summary>
    public string FindColumnTitle
    {
        get => _findColumnTitle;
        private set
        {
            if (SetField(ref _findColumnTitle, value))
            {
                RaisePropertyChanged(nameof(HasFindColumn));
            }
        }
    }

    public bool HasFindColumn => FindColumnTitle.Length > 0;

    /// <summary>Back to searching whole sessions.</summary>
    public RelayCommand ClearFindColumnCommand => _clearFindColumnCommand ??= new RelayCommand(() =>
    {
        _findColumnText = null;
        FindColumnTitle = string.Empty;
    });

    /// <summary>Opens the Find bar limited to one column; <paramref name="cellText"/> gives a row's text in it.</summary>
    public void ScopeFindToColumn(string title, Func<SessionRow, string?> cellText)
    {
        _findColumnText = cellText;
        FindColumnTitle = title;
        IsFindBarVisible = true;
        FindFocusRequested?.Invoke();
    }

    /// <summary>
    /// Highlights every session whose value in a column (not empty) is the
    /// same as another session's, like a spreadsheet's duplicate
    /// highlighting; F3 then steps through them.
    /// </summary>
    public void HighlightRepeatedValues(string title, Func<SessionRow, string?> cellText)
    {
        IsFindBarVisible = true;
        var rows = Sessions.ToList();
        var counts = rows
            .Select(cellText)
            .Where(text => !string.IsNullOrEmpty(text))
            .GroupBy(text => text!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var matches = rows.Where(row => cellText(row) is { } text && counts.Contains(text)).ToHashSet();
        ShowMatches(matches, matches.Count switch
        {
            0 => $"No two sessions share a value in the {title} column.",
            _ => $"{matches.Count} sessions share a value in the {title} column ({counts.Count} repeated {(counts.Count == 1 ? "value" : "values")}).",
        });
    }

    /// <summary>"3 sessions match", or why nothing could be searched for.</summary>
    public string FindStatus
    {
        get => _findStatus;
        private set => SetField(ref _findStatus, value);
    }

    public RelayCommand ShowFindCommand => _showFindCommand ??= new RelayCommand(() =>
    {
        IsFindBarVisible = true;
        FindFocusRequested?.Invoke();
    });

    public RelayCommand FindCommand => _findCommand ??= new RelayCommand(() => _ = FindAsync());

    public RelayCommand FindNextCommand => _findNextCommand ??= new RelayCommand(FindNext);

    public RelayCommand CloseFindCommand => _closeFindCommand ??= new RelayCommand(CloseFind);

    /// <summary>
    /// Searches every captured session (in the background: bodies can be
    /// large), highlights the matches and selects the first one that the
    /// filter isn't hiding.
    /// </summary>
    private async Task FindAsync()
    {
        IsFindBarVisible = true;
        if (_isFinding)
        {
            return;
        }

        if (_findColumnText is { } cellText)
        {
            FindInColumn(cellText);
            return;
        }

        var options = new SessionSearchOptions(
            FindText,
            (SessionSearchTarget)Math.Clamp(FindTargetIndex, 0, 2),
            (SessionSearchParts)Math.Clamp(FindPartsIndex, 0, 2),
            FindMatchCase,
            FindUseRegex);
        if (!SessionSearch.TryCreate(options, out var search, out var error))
        {
            FindStatus = error ?? string.Empty;
            return;
        }

        _isFinding = true;
        FindStatus = "Searching…";
        try
        {
            var rows = Sessions.ToList();
            var matches = await Task.Run(() => rows.Where(row => search!.Matches(row.Session)).ToHashSet());
            ShowMatches(matches, MatchCount(matches.Count));
        }
        finally
        {
            _isFinding = false;
        }
    }

    /// <summary>Find limited to a column: the text as the list shows it, so it's quick enough for the UI thread.</summary>
    private void FindInColumn(Func<SessionRow, string?> cellText)
    {
        if (FindText.Length == 0)
        {
            FindStatus = "Type something to find.";
            return;
        }

        Func<string, bool> matches;
        if (FindUseRegex)
        {
            Regex regex;
            try
            {
                regex = new Regex(FindText, FindMatchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                FindStatus = $"That isn't a valid regular expression: {ex.Message}";
                return;
            }

            matches = text =>
            {
                try
                {
                    return regex.IsMatch(text);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            };
        }
        else
        {
            var comparison = FindMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            matches = text => text.Contains(FindText, comparison);
        }

        var found = Sessions.Where(row => cellText(row) is { } text && matches(text)).ToHashSet();
        ShowMatches(found, MatchCount(found.Count) + $" Searched the {FindColumnTitle} column only.");
    }

    private static string MatchCount(int count) => count switch
    {
        0 => "No sessions match.",
        1 => "1 session matches.",
        _ => $"{count} sessions match.",
    };

    /// <summary>Highlights <paramref name="matches"/>, reports them, and selects the first the filter shows.</summary>
    private void ShowMatches(HashSet<SessionRow> matches, string status)
    {
        foreach (var row in Sessions)
        {
            row.IsFindMatch = matches.Contains(row);
        }

        var visible = FilteredSessions.ToHashSet();
        var hidden = matches.Count(row => !visible.Contains(row));
        FindStatus = status + (hidden > 0 ? $" {hidden} hidden by the filter." : string.Empty);

        FindResultsChanged?.Invoke();
        if (FilteredSessions.FirstOrDefault(row => row.IsFindMatch) is { } first)
        {
            SelectedSessionRow = first;
        }
    }

    /// <summary>Selects the next highlighted session after the selected one, wrapping round.</summary>
    private void FindNext()
    {
        if (FilteredSessions.Count == 0)
        {
            return;
        }

        var start = SelectedSessionRow is { } selected ? FilteredSessions.IndexOf(selected) : -1;
        for (var step = 1; step <= FilteredSessions.Count; step++)
        {
            var row = FilteredSessions[(start + step + FilteredSessions.Count) % FilteredSessions.Count];
            if (row.IsFindMatch)
            {
                SelectedSessionRow = row;
                return;
            }
        }

        if (!string.IsNullOrEmpty(FindText))
        {
            _ = FindAsync();
        }
    }

    /// <summary>Hides the Find bar and removes the highlighting.</summary>
    private void CloseFind()
    {
        IsFindBarVisible = false;
        FindStatus = string.Empty;
        var changed = false;
        foreach (var row in Sessions)
        {
            changed |= row.IsFindMatch;
            row.IsFindMatch = false;
        }

        if (changed)
        {
            Dispatcher.UIThread.Post(() => FindResultsChanged?.Invoke());
        }
    }
}
