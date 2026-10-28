using System.Diagnostics;
using Violens.AppCore.Diagnostics;
using Violens.AppCore.Updates;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// The update notice: at start-up (unless turned off) VioLens asks GitHub
/// whether a newer release is out, and if so shows a link beside the title.
/// Help &gt; Check for Updates asks straight away.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly HttpClient s_updateClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private bool _checkForUpdatesAtStartup;
    private AvailableUpdate? _availableUpdate;

    /// <summary>Help &gt; Check for Updates at Start-up. Saved.</summary>
    public bool CheckForUpdatesAtStartup
    {
        get => _checkForUpdatesAtStartup;
        set
        {
            if (SetField(ref _checkForUpdatesAtStartup, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.CheckForUpdates, value);
            }
        }
    }

    /// <summary>"VioLens 0.4.0 is available", or empty when there's nothing newer.</summary>
    public string UpdateNotice =>
        _availableUpdate is { } update ? $"VioLens {update.Version} is available" : string.Empty;

    public bool HasUpdate => _availableUpdate is not null;

    public RelayCommand OpenUpdatePageCommand { get; private set; } = null!;

    public RelayCommand CheckForUpdatesNowCommand { get; private set; } = null!;

    private static string RunningVersion => DiagnosticReport.DisplayVersion(typeof(MainWindowViewModel).Assembly);

    private void InitializeUpdates()
    {
        _checkForUpdatesAtStartup = _preferences.GetBoolPref(PreferenceKeys.CheckForUpdates, true);
        OpenUpdatePageCommand = new RelayCommand(OpenUpdatePage, () => HasUpdate);
        CheckForUpdatesNowCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(reportResult: true));
        if (_checkForUpdatesAtStartup)
        {
            _ = CheckForUpdatesAsync(reportResult: false);
        }
    }

    /// <summary>
    /// Asks GitHub. At start-up a failure stays quiet (offline is normal);
    /// when asked from the menu, the status line says what happened.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool reportResult)
    {
        if (!UpdateCheck.ShouldCheck(RunningVersion))
        {
            if (reportResult)
            {
                StatusText = $"This is a local build ({RunningVersion}), so there's no release to compare it with.";
            }

            return;
        }

        try
        {
            var update = await UpdateCheck.CheckAsync(RunningVersion, s_updateClient, CancellationToken.None);
            _availableUpdate = update;
            RaisePropertyChanged(nameof(UpdateNotice));
            RaisePropertyChanged(nameof(HasUpdate));
            OpenUpdatePageCommand.RaiseCanExecuteChanged();
            if (reportResult)
            {
                StatusText = update is null
                    ? $"You're running the latest VioLens ({RunningVersion})."
                    : $"VioLens {update.Version} is available -- click the link beside the title to download it.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Console.WriteLine($"[update] Couldn't check for updates: {ex.Message}");
            if (reportResult)
            {
                StatusText = $"Couldn't check for updates: {ex.Message}";
            }
        }
    }

    /// <summary>Opens the release's page on GitHub, which UpdateCheck builds itself.</summary>
    private void OpenUpdatePage()
    {
        if (_availableUpdate is not { } update)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(update.Page.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open {update.Page}: {ex.Message}";
        }
    }
}
