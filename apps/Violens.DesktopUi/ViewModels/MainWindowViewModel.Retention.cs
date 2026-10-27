namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// Fiddler Classic's "Keep: N sessions": a long capture would otherwise grow
/// without limit, since the session list never trims itself (see
/// <see cref="Violens.ProxyCore.Sessions.SessionStore.MaxSessions"/>).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly int[] StandardSessionsToKeep = [0, 100, 500, 1000, 5000, 10000];

    private int _sessionsToKeep;
    private readonly List<int> _sessionsToKeepChoices = [.. StandardSessionsToKeep];

    /// <summary>The Keep drop-down's entries, matching <see cref="SessionsToKeepIndex"/>.</summary>
    public IReadOnlyList<string> SessionsToKeepLabels =>
        _sessionsToKeepChoices.Select(n => n == 0 ? "Keep all sessions" : $"Keep newest {n:N0}").ToList();

    public int SessionsToKeepIndex
    {
        get => _sessionsToKeepChoices.IndexOf(_sessionsToKeep);
        set
        {
            if (value >= 0 && value < _sessionsToKeepChoices.Count)
            {
                SessionsToKeep = _sessionsToKeepChoices[value];
            }
        }
    }

    /// <summary>How many sessions to keep, newest first; zero keeps them all. Applies immediately.</summary>
    public int SessionsToKeep
    {
        get => _sessionsToKeep;
        set
        {
            if (SetField(ref _sessionsToKeep, Math.Max(0, value)))
            {
                _preferences.SetInt32Pref(PreferenceKeys.SessionsToKeep, _sessionsToKeep);
                _sessionStore.MaxSessions = _sessionsToKeep;
                RaisePropertyChanged(nameof(SessionsToKeepIndex));
            }
        }
    }

    private void LoadRetentionPreferences()
    {
        _sessionsToKeep = Math.Max(0, _preferences.GetInt32Pref(PreferenceKeys.SessionsToKeep, 0));

        // A number set in the preferences file gets its own entry.
        if (!_sessionsToKeepChoices.Contains(_sessionsToKeep))
        {
            _sessionsToKeepChoices.Add(_sessionsToKeep);
            _sessionsToKeepChoices.Sort();
        }

        _sessionStore.MaxSessions = _sessionsToKeep;
    }
}
