using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using Violens.AppCore.Diagnostics;
using Violens.AppCore.Migration;
using Violens.Compatibility.Extensions;
using Violens.Compatibility.FiddlerScript;
using Violens.DesktopUi.Models;
using Violens.Extensibility.Inspection;
using Violens.ProxyCore.AutoResponder;
using Violens.ProxyCore.Breakpoints;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Extensions;
using Violens.ProxyCore.Preferences;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Violens.ProxyCore.SystemProxy;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// Owns the proxy core (root CA, session store, listener) and mirrors the
/// session store into collections the UI binds to.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int DefaultPreferredPort = 8888;

    private readonly SessionStore _sessionStore = new();
    private readonly InspectorRegistry _inspectorRegistry;
    private readonly BreakpointManager _breakpointManager = new();
    private readonly Dictionary<PendingBreakpoint, PendingBreakpointViewModel> _pendingBreakpointViewModels = [];
    private readonly AutoResponderRules _autoResponderRules = new();

    /// <summary>Created once and handed to each listener on Start; scripts can be (re)loaded while running.</summary>
    private readonly FiddlerScriptRunner _fiddlerScriptRunner;

    /// <summary>
    /// Loaded once at startup and never reloaded, unlike FiddlerScript (see
    /// <see cref="ExtensionHost"/>).
    /// </summary>
    private readonly ExtensionHost _extensionHost;

    /// <summary>
    /// Lives for the whole run so <see cref="Stop"/> only closes a legacy host
    /// this app launched, never one the user started.
    /// </summary>
    private readonly LegacyExtensionHostLauncher _legacyExtensionHostLauncher = new();

    /// <summary>
    /// Asks the user before trusting the root on macOS, where the
    /// <c>security</c> command has no confirmation of its own. Must be awaited,
    /// not blocked on: it needs the UI thread. Null means always decline.
    /// </summary>
    private readonly Func<Task<bool>>? _confirmCertificateTrust;

    /// <summary>Stops a second Start/Stop from beginning while one is still in progress.</summary>
    private bool _starting;

    /// <summary>
    /// Writes are batched on a background timer, so setters can save on every
    /// change without waiting on disk.
    /// </summary>
    private readonly PreferenceStore _preferences;

    /// <summary>
    /// Shows the format picker: (heading, choices, choice to highlight) to the
    /// chosen one, or null if cancelled. Null means no picker.
    /// </summary>
    private readonly Func<string, IReadOnlyList<FormatChoice>, string?, Task<FormatChoice?>>? _chooseFormat;

    private CertificateAuthority? _authority;
    private bool _registeredAsSystemProxy;
    private InterceptingProxyListener? _proxy;
    private string _statusText;
    private decimal? _preferredPort = DefaultPreferredPort;
    private bool _isRunning;
    private SessionRow? _selectedSessionRow;
    private PendingBreakpointViewModel? _selectedPendingBreakpoint;
    private AutoResponderRuleViewModel? _selectedAutoResponderRule;
    private SessionQuery _query = SessionQuery.MatchAll;
    private string _filterText = string.Empty;
    private bool _useAutomaticPort = true;
    private bool _isCaptureFlashing;
    private string _fiddlerScriptPath = string.Empty;
    private string _fiddlerScriptStatus = "No script loaded.";
    private string _extensionStatus = "Not scanned yet.";
    private bool _autoLaunchLegacyHost;
    private string _legacyExtensionHostStatus = "Auto-launch is off -- Tools -> Legacy Extension Host to turn it on, then Start.";
    private bool _showFiddlerScriptPanel;
    private bool _showExtensionsPanel;
    private bool _showLegacyExtensionHostPanel;
    private bool _showAlsoBreakOnRow;

    /// <summary>Restarted on every capture, so the dot stays lit through a burst.</summary>
    private readonly DispatcherTimer _captureFlashTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>
    /// How long new sessions wait so a burst reaches the lists as one update.
    /// </summary>
    private static readonly TimeSpan SessionArrivalInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Captured sessions not yet in <see cref="Sessions"/>; added in batches by <see cref="AddSessionRows"/>.</summary>
    private readonly CoalescingQueue<Session> _sessionArrivals;

    /// <summary>
    /// Every session captured this run, ignoring the filter. Saving exports
    /// these, so a filter never silently drops sessions from a SAZ.
    /// </summary>
    public ObservableCollection<SessionRow> Sessions { get; } = [];

    /// <summary>Tabs added by extensions; filled while they load.</summary>
    public ObservableCollection<ExtensionTabViewModel> ExtensionTabs { get; } = [];

    /// <summary>
    /// What the grid shows. Avalonia's DataGrid has no live filtered view, so
    /// this is kept in sync by hand.
    /// </summary>
    public ObservableCollection<SessionRow> FilteredSessions { get; } = [];

    /// <summary>
    /// Filter text (see <see cref="SessionQuery"/>). Rescans every session per
    /// keystroke, which is fast enough at real capture sizes.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value))
            {
                _query = SessionQuery.Parse(value);
                ApplyFilter();
                _preferences.SetStringPref(PreferenceKeys.FilterText, value ?? string.Empty);
            }
        }
    }

    /// <summary>
    /// Sessions paused at a breakpoint, oldest first. Breakpoint events arrive
    /// on connection threads, so handlers marshal to the UI thread.
    /// </summary>
    public ObservableCollection<PendingBreakpointViewModel> PendingBreakpoints { get; } = [];

    /// <summary>
    /// Shown once any breakpoint rule is armed, so the panel is there before
    /// the first hit, and while anything is paused, so a paused connection can
    /// always be resumed.
    /// </summary>
    public bool IsBreakpointsPanelVisible =>
        _breakpointManager.Rules.AnyActive || PendingBreakpoints.Count > 0;

    public PendingBreakpointViewModel? SelectedPendingBreakpoint
    {
        get => _selectedPendingBreakpoint;
        set => SetField(ref _selectedPendingBreakpoint, value);
    }

    /// <summary>Fiddler's "Rules -&gt; Automatic Breakpoints -&gt; Before Requests".</summary>
    public bool BreakOnAllRequests
    {
        get => _breakpointManager.Rules.BreakOnAllRequests;
        set
        {
            if (_breakpointManager.Rules.BreakOnAllRequests == value)
            {
                return;
            }

            _breakpointManager.Rules.BreakOnAllRequests = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        }
    }

    /// <summary>Fiddler's "Rules -&gt; Automatic Breakpoints -&gt; After Responses".</summary>
    public bool BreakOnAllResponses
    {
        get => _breakpointManager.Rules.BreakOnAllResponses;
        set
        {
            if (_breakpointManager.Rules.BreakOnAllResponses == value)
            {
                return;
            }

            _breakpointManager.Rules.BreakOnAllResponses = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        }
    }

    /// <summary>Fiddler's <c>bpu</c>: pause a request whose target contains this text.</summary>
    public string? RequestUrlContains
    {
        get => _breakpointManager.Rules.RequestUrlContains;
        set
        {
            if (_breakpointManager.Rules.RequestUrlContains == value)
            {
                return;
            }

            _breakpointManager.Rules.RequestUrlContains = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        }
    }

    /// <summary>Fiddler's <c>bpafter</c>: pause a response whose request target contains this text.</summary>
    public string? ResponseUrlContains
    {
        get => _breakpointManager.Rules.ResponseUrlContains;
        set
        {
            if (_breakpointManager.Rules.ResponseUrlContains == value)
            {
                return;
            }

            _breakpointManager.Rules.ResponseUrlContains = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        }
    }

    /// <summary>Fiddler's <c>bpm</c>: pause a request with this HTTP method.</summary>
    public string? RequestMethodEquals
    {
        get => _breakpointManager.Rules.RequestMethodEquals;
        set
        {
            if (_breakpointManager.Rules.RequestMethodEquals == value)
            {
                return;
            }

            _breakpointManager.Rules.RequestMethodEquals = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        }
    }

    /// <summary>
    /// Fiddler's <c>bps</c>: pause a response with this status code. Decimal
    /// because that's what <c>NumericUpDown</c> binds to.
    /// </summary>
    public decimal? ResponseStatusCodeEquals
    {
        get => _breakpointManager.Rules.ResponseStatusCodeEquals;
        set
        {
            var statusCode = value.HasValue ? (int?)value.Value : null;
            if (_breakpointManager.Rules.ResponseStatusCodeEquals == statusCode)
            {
                return;
            }

            _breakpointManager.Rules.ResponseStatusCodeEquals = statusCode;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        }
    }

    /// <summary>
    /// Shows or hides the "Also break on" row. Hiding it doesn't clear the
    /// rules, which keep working.
    /// </summary>
    public bool ShowAlsoBreakOnRow
    {
        get => _showAlsoBreakOnRow;
        set
        {
            if (SetField(ref _showAlsoBreakOnRow, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.ShowAlsoBreakOnRow, value);
            }
        }
    }

    /// <summary>
    /// One row per <see cref="AutoResponderRules.Rules"/> entry, kept in the
    /// same order by hand.
    /// </summary>
    public ObservableCollection<AutoResponderRuleViewModel> AutoResponderRuleRows { get; } = [];

    /// <summary>Fiddler Classic's "Enable rules" checkbox.</summary>
    public bool AutoResponderEnabled
    {
        get => _autoResponderRules.IsEnabled;
        set
        {
            if (_autoResponderRules.IsEnabled == value)
            {
                return;
            }

            _autoResponderRules.IsEnabled = value;
            RaisePropertyChanged();
        }
    }

    public AutoResponderRuleViewModel? SelectedAutoResponderRule
    {
        get => _selectedAutoResponderRule;
        set
        {
            if (SetField(ref _selectedAutoResponderRule, value))
            {
                RemoveAutoResponderRuleCommand.RaiseCanExecuteChanged();
                MoveAutoResponderRuleUpCommand.RaiseCanExecuteChanged();
                MoveAutoResponderRuleDownCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand AddAutoResponderRuleCommand { get; }
    public RelayCommand RemoveAutoResponderRuleCommand { get; }
    public RelayCommand MoveAutoResponderRuleUpCommand { get; }
    public RelayCommand MoveAutoResponderRuleDownCommand { get; }

    /// <summary>The path typed into the FiddlerScript panel, which may not be loaded yet.</summary>
    public string FiddlerScriptPath
    {
        get => _fiddlerScriptPath;
        set
        {
            if (SetField(ref _fiddlerScriptPath, value))
            {
                LoadFiddlerScriptCommand.RaiseCanExecuteChanged();
                _preferences.SetStringPref(PreferenceKeys.FiddlerScriptPath, value ?? string.Empty);
            }
        }
    }

    public string FiddlerScriptStatus
    {
        get => _fiddlerScriptStatus;
        private set => SetField(ref _fiddlerScriptStatus, value);
    }

    /// <summary>
    /// The folders scanned, what loaded and any errors. Shown in the UI because
    /// a WinExe's console output often isn't visible.
    /// </summary>
    public string ExtensionStatus
    {
        get => _extensionStatus;
        private set => SetField(ref _extensionStatus, value);
    }

    /// <summary>
    /// Whether Start launches the legacy extension host. Off by default; takes
    /// effect on the next Start or Stop.
    /// </summary>
    public bool AutoLaunchLegacyHost
    {
        get => _autoLaunchLegacyHost;
        set
        {
            if (SetField(ref _autoLaunchLegacyHost, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.AutoLaunchLegacyHost, value);
            }
        }
    }

    public string LegacyExtensionHostStatus
    {
        get => _legacyExtensionHostStatus;
        private set => SetField(ref _legacyExtensionHostStatus, value);
    }

    public RelayCommand LoadFiddlerScriptCommand { get; }
    public RelayCommand ReloadFiddlerScriptCommand { get; }

    public bool ShowFiddlerScriptPanel
    {
        get => _showFiddlerScriptPanel;
        set
        {
            if (SetField(ref _showFiddlerScriptPanel, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.ShowFiddlerScriptPanel, value);
            }
        }
    }

    public bool ShowExtensionsPanel
    {
        get => _showExtensionsPanel;
        set
        {
            if (SetField(ref _showExtensionsPanel, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.ShowExtensionsPanel, value);
            }
        }
    }

    public bool ShowLegacyExtensionHostPanel
    {
        get => _showLegacyExtensionHostPanel;
        set
        {
            if (SetField(ref _showLegacyExtensionHostPanel, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.ShowLegacyExtensionHostPanel, value);
            }
        }
    }

    /// <summary>The Rules menu, rebuilt whenever a script loads.</summary>
    public ObservableCollection<RulesMenuEntryViewModel> RulesMenuEntries { get; } = [];

    /// <summary>
    /// Lets the Rules menu grey out when empty. Raised by hand in
    /// <see cref="RefreshScriptMenus"/>.
    /// </summary>
    public bool HasRulesMenuEntries => RulesMenuEntries.Count > 0;

    /// <summary>
    /// The script's <c>ToolsAction</c>s, shown as buttons in the FiddlerScript
    /// panel rather than in the Tools menu.
    /// </summary>
    public ObservableCollection<ActionMenuEntryViewModel> ToolsMenuEntries { get; } = [];

    public bool HasToolsMenuEntries => ToolsMenuEntries.Count > 0;

    /// <summary>The session list's right-click menu, from the script's <c>ContextAction</c>s.</summary>
    public ObservableCollection<ActionMenuEntryViewModel> ContextActionEntries { get; } = [];

    /// <summary>Read by the window to rebuild script-defined grid columns after a script loads.</summary>
    public FiddlerScriptDirectives ScriptDirectives => _fiddlerScriptRunner.Directives;

    /// <summary>Rebuilt on each selection change; captured sessions don't change afterwards.</summary>
    public ObservableCollection<InspectorTabViewModel> RequestInspectors { get; } = [];

    public ObservableCollection<InspectorTabViewModel> ResponseInspectors { get; } = [];

    public SessionRow? SelectedSessionRow
    {
        get => _selectedSessionRow;
        set
        {
            if (SetField(ref _selectedSessionRow, value))
            {
                RefreshInspectors();
                RefreshTiming();
                RefreshWebSocket();
                RemoveSelectedSessionCommand?.RaiseCanExecuteChanged();
                RaiseComposerCanExecuteChanged();
            }
        }
    }

    /// <summary>Interception needs a trust store and system proxy, which VioLens handles on Windows, macOS and Linux.</summary>
    public bool IsSupported { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>
    /// Decimal to bind straight to <c>NumericUpDown</c>. Null (cleared while
    /// editing) means the default. Ignored in Auto mode.
    /// </summary>
    public decimal? PreferredPort
    {
        get => _preferredPort;
        set
        {
            if (SetField(ref _preferredPort, value))
            {
                StartCommand.RaiseCanExecuteChanged();

                // A cleared field mid-edit keeps the last good value.
                if (value is >= 1 and <= 65535)
                {
                    _preferences.SetInt32Pref(PreferenceKeys.Port, (int)value.Value);
                }
            }
        }
    }

    /// <summary>
    /// Auto lets the OS pick a free port. <see cref="PreferredPort"/> keeps its
    /// value so switching to Specify starts from something sensible.
    /// </summary>
    public bool UseAutomaticPort
    {
        get => _useAutomaticPort;
        set
        {
            if (SetField(ref _useAutomaticPort, value))
            {
                RaisePropertyChanged(nameof(UseSpecificPort));
                StartCommand.RaiseCanExecuteChanged();
                _preferences.SetBoolPref(PreferenceKeys.PortAutomatic, value);
            }
        }
    }

    /// <summary>
    /// The inverse of <see cref="UseAutomaticPort"/>. Each RadioButton needs its
    /// own two-way property; binding to <c>!UseAutomaticPort</c> starts
    /// unchecked whatever the value.
    /// </summary>
    public bool UseSpecificPort
    {
        get => !_useAutomaticPort;
        set => UseAutomaticPort = !value;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(ShowSteadyRunningIndicator));
                RaiseComposerCanExecuteChanged();
            }
        }
    }

    /// <summary>True briefly after each capture, so the status dot flashes.</summary>
    public bool IsCaptureFlashing
    {
        get => _isCaptureFlashing;
        private set
        {
            if (SetField(ref _isCaptureFlashing, value))
            {
                RaisePropertyChanged(nameof(ShowSteadyRunningIndicator));
            }
        }
    }

    /// <summary>
    /// Running with no recent traffic. With <see cref="IsCaptureFlashing"/> and
    /// <c>!IsRunning</c>, exactly one status dot is visible.
    /// </summary>
    public bool ShowSteadyRunningIndicator => IsRunning && !IsCaptureFlashing;

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SaveSazCommand { get; }
    public RelayCommand ImportViaExtensionCommand { get; }
    public RelayCommand ExportViaExtensionCommand { get; }
    public RelayCommand OpenDocumentationCommand { get; }

    /// <summary>Tools > Remove Root Certificate.</summary>
    public RelayCommand RemoveRootCertificateCommand { get; }

    /// <summary>Edit > Remove Selected Session (also the Delete key in the session list).</summary>
    public RelayCommand RemoveSelectedSessionCommand { get; }

    public RelayCommand RemoveAllSessionsCommand { get; }

    /// <param name="confirmCertificateTrust">Asks before trusting the root on macOS and Linux; null means always decline.</param>
    /// <param name="preferences">Null means the per-user file. Disposed with this view model.</param>
    /// <param name="chooseFormat">Null means no format picker.</param>
    public MainWindowViewModel(
        Func<Task<bool>>? confirmCertificateTrust = null,
        PreferenceStore? preferences = null,
        Func<string, IReadOnlyList<FormatChoice>, string?, Task<FormatChoice?>>? chooseFormat = null)
    {
        _confirmCertificateTrust = confirmCertificateTrust;
        _chooseFormat = chooseFormat;

        // Loaded into the backing fields, not the setters, so loading doesn't
        // write straight back out.
        _preferences = preferences ?? CreateDefaultPreferences();
        LoadPreferences();
        InitializeTimingViews();

        // Ported extensions read preferences through the compatibility layer.
        // Set before extensions load, since OnLoad may read them. App.axaml.cs
        // sets the hooks that need the window.
        Violens.CompatShim.CompatShimHost.Preferences = _preferences;
        Violens.CompatShim.CompatShimHost.Log = message => Console.WriteLine($"[Extension] {message}");

        // A leftover backup file means the last run never stopped cleanly and
        // may have left the system proxy pointing at a dead port.
        SystemProxyController.RecoverFromCrash();

        _extensionHost = new ExtensionHost(
            // The user's own folder first, so their copy wins over a bundled one.
            [ExtensionHost.DefaultExtensionsFolder, ExtensionHost.BundledExtensionsFolder],
            log: message => Console.WriteLine($"[Extension] {message}"));
        // Extensions add their tabs from OnLoad, inside Load() below.
        ExtensionUi.SetTabHost((title, view) => ExtensionTabs.Add(new ExtensionTabViewModel(title, view)));
        _extensionHost.Load();
        foreach (var loadError in _extensionHost.LoadErrors)
        {
            Console.WriteLine($"[Extension] {loadError}");
        }
        ExtensionStatus = BuildExtensionStatus();

        // Built after Load(), so extension inspectors join the built-in ones.
        _inspectorRegistry = InspectorRegistry.CreateDefault(
            _extensionHost.RequestInspectors.Select(i => (IInspector)Inspector2Adapter.ForRequest(i))
                .Concat(_extensionHost.ResponseInspectors.Select(i => (IInspector)Inspector2Adapter.ForResponse(i))));

        StartCommand = new RelayCommand(
            Start, () => IsSupported && !IsRunning && !_starting && (UseAutomaticPort || PreferredPort is >= 1 and <= 65535));
        StopCommand = new RelayCommand(Stop, () => IsRunning && !_starting);
        SaveSazCommand = new RelayCommand(SaveSaz, () => Sessions.Count > 0);
        // Extensions don't change mid-run, so only the session count needs
        // RaiseCanExecuteChanged.
        ImportViaExtensionCommand = new RelayCommand(ImportViaExtension, () => _extensionHost.Importers.Count > 0);
        ExportViaExtensionCommand = new RelayCommand(ExportViaExtension, () => _extensionHost.Exporters.Count > 0 && Sessions.Count > 0);
        OpenDocumentationCommand = new RelayCommand(OpenDocumentation);
        RemoveRootCertificateCommand = new RelayCommand(RemoveRootCertificate);
        RemoveSelectedSessionCommand = new RelayCommand(
            () => { if (SelectedSessionRow is { } row) { _sessionStore.Remove([row.Id]); } },
            () => SelectedSessionRow is not null);
        RemoveAllSessionsCommand = new RelayCommand(() => _sessionStore.Clear(), () => Sessions.Count > 0);
        ExecuteComposerCommand = new RelayCommand(ExecuteComposer, () => CanSend);
        ReplaySelectedSessionCommand = new RelayCommand(ReplaySelectedSession, () => CanSend && SelectedSessionRow is not null);
        EditInComposerCommand = new RelayCommand(EditInComposer, () => SelectedSessionRow is not null);

        AddAutoResponderRuleCommand = new RelayCommand(AddAutoResponderRule);
        RemoveAutoResponderRuleCommand = new RelayCommand(RemoveSelectedAutoResponderRule, () => SelectedAutoResponderRule is not null);
        MoveAutoResponderRuleUpCommand = new RelayCommand(() => MoveSelectedAutoResponderRule(-1), () => CanMoveSelectedAutoResponderRule(-1));
        MoveAutoResponderRuleDownCommand = new RelayCommand(() => MoveSelectedAutoResponderRule(1), () => CanMoveSelectedAutoResponderRule(1));

        // Script alerts and handler errors go to the console, like the rest of
        // the proxy's diagnostics.
        _fiddlerScriptRunner = new FiddlerScriptRunner(log: message => Console.WriteLine($"[FiddlerScript] {message}"));
        LoadFiddlerScriptCommand = new RelayCommand(LoadFiddlerScript, () => !string.IsNullOrWhiteSpace(FiddlerScriptPath));
        ReloadFiddlerScriptCommand = new RelayCommand(ReloadFiddlerScript, () => _fiddlerScriptRunner.IsLoaded);

        // DispatcherTimer ticks on the UI thread, so no marshalling is needed.
        _captureFlashTimer.Tick += (_, _) =>
        {
            _captureFlashTimer.Stop();
            IsCaptureFlashing = false;
        };

        // Must come after SaveSazCommand is assigned, or nullable analysis
        // reports CS8602.
        //
        // Sessions reach the lists in batches, at most one UI update per
        // SessionArrivalInterval, so heavy traffic doesn't flood the UI
        // thread. Only the delivery is posted to it.
        _sessionArrivals = new CoalescingQueue<Session>(
            deliver => _ = Task.Delay(SessionArrivalInterval).ContinueWith(
                _ => Dispatcher.UIThread.Post(deliver), TaskScheduler.Default),
            AddSessionRows);
        _sessionStore.SessionAdded += _sessionArrivals.Add;

        // Removed sessions leave both lists; the selection goes with them.
        // Batched too: once the Keep limit is reached, every new session
        // evicts an old one.
        var sessionRemovals = new CoalescingQueue<IReadOnlyList<int>>(deliver => Dispatcher.UIThread.Post(deliver), batches =>
        {
            // Anything still waiting to be added goes in first, so a session
            // removed before its batch arrived can't reappear afterwards.
            _sessionArrivals.Flush();
            var removed = new HashSet<int>(batches.SelectMany(ids => ids));
            for (var i = Sessions.Count - 1; i >= 0; i--)
            {
                if (removed.Contains(Sessions[i].Id))
                {
                    Sessions.RemoveAt(i);
                }
            }

            for (var i = FilteredSessions.Count - 1; i >= 0; i--)
            {
                if (removed.Contains(FilteredSessions[i].Id))
                {
                    FilteredSessions.RemoveAt(i);
                }
            }

            if (SelectedSessionRow is { } selected && removed.Contains(selected.Id))
            {
                SelectedSessionRow = null;
            }

            SaveSazCommand.RaiseCanExecuteChanged();
            ExportViaExtensionCommand.RaiseCanExecuteChanged();
            RemoveAllSessionsCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(HasSessions));
        });
        _sessionStore.SessionsRemoved += sessionRemovals.Add;

        _breakpointManager.BreakpointHit += pending => Dispatcher.UIThread.Post(() =>
        {
            var vm = new PendingBreakpointViewModel(pending);
            _pendingBreakpointViewModels[pending] = vm;
            PendingBreakpoints.Add(vm);
            SelectedPendingBreakpoint ??= vm;
            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        });

        _breakpointManager.BreakpointResolved += pending => Dispatcher.UIThread.Post(() =>
        {
            if (!_pendingBreakpointViewModels.Remove(pending, out var vm))
            {
                return;
            }

            PendingBreakpoints.Remove(vm);
            if (ReferenceEquals(SelectedPendingBreakpoint, vm))
            {
                SelectedPendingBreakpoint = PendingBreakpoints.Count > 0 ? PendingBreakpoints[0] : null;
            }

            RaisePropertyChanged(nameof(IsBreakpointsPanelVisible));
        });

        _statusText = IsSupported
            ? "Not started. Pick a port and click Start."
            : "Capturing isn't supported on this operating system: VioLens can trust its certificate and set the " +
              "system proxy on Windows, macOS and Linux only.";

        InitializeUpdates();
        InitializeAutomation();
    }

    /// <summary>
    /// Shows an extension's message in the status line rather than a dialog,
    /// since an import may report several in a row. Call on the UI thread.
    /// </summary>
    internal void ShowExtensionNotice(string title, string message)
    {
        Console.WriteLine($"[Extension] {title}: {message}");
        StatusText = $"{title}: {message.ReplaceLineEndings(" ")}";
    }

    private static PreferenceStore CreateDefaultPreferences()
    {
        try
        {
            return PreferenceStore.CreateDefault(log: message => Console.WriteLine($"[Preferences] {message}"));
        }
        catch (InvalidOperationException ex)
        {
            // No app-data folder: keep preferences in memory for this run.
            Console.WriteLine($"[Preferences] {ex.Message} Settings won't be saved this run.");
            return PreferenceStore.CreateInMemory();
        }
    }

    /// <summary>Defaults match the app's behaviour before preferences existed.</summary>
    private void LoadPreferences()
    {
        _useAutomaticPort = _preferences.GetBoolPref(PreferenceKeys.PortAutomatic, true);

        var port = _preferences.GetInt32Pref(PreferenceKeys.Port, DefaultPreferredPort);
        _preferredPort = port is >= 1 and <= 65535 ? port : DefaultPreferredPort;

        _showFiddlerScriptPanel = _preferences.GetBoolPref(PreferenceKeys.ShowFiddlerScriptPanel, false);
        _showExtensionsPanel = _preferences.GetBoolPref(PreferenceKeys.ShowExtensionsPanel, false);
        _showLegacyExtensionHostPanel = _preferences.GetBoolPref(PreferenceKeys.ShowLegacyExtensionHostPanel, false);
        _showAlsoBreakOnRow = _preferences.GetBoolPref(PreferenceKeys.ShowAlsoBreakOnRow, false);

        _filterText = _preferences.GetStringPref(PreferenceKeys.FilterText, string.Empty);
        _query = SessionQuery.Parse(_filterText);

        _fiddlerScriptPath = _preferences.GetStringPref(PreferenceKeys.FiddlerScriptPath, string.Empty);

        _autoLaunchLegacyHost = _preferences.GetBoolPref(PreferenceKeys.AutoLaunchLegacyHost, false);
        LoadNetworkPreferences();
        LoadRetentionPreferences();
        LoadAutoResponderRules();
        if (_autoLaunchLegacyHost)
        {
            _legacyExtensionHostStatus = "Auto-launch is on -- it will launch the next time you click Start.";
        }
    }

    /// <summary>
    /// Shows the port actually bound without saving it; only the user's own
    /// edits change the saved port.
    /// </summary>
    private void ShowBoundPort(int port)
    {
        if (SetField(ref _preferredPort, (decimal?)port, nameof(PreferredPort)))
        {
            StartCommand.RaiseCanExecuteChanged();
        }
    }

    private async void Start() => await StartAsync();

    /// <summary>Start, awaitable (for QuickExec's start through violens-exec).</summary>
    private async Task StartAsync()
    {
        if (_starting)
        {
            return;
        }

        SetStarting(true);
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            SetStarting(false);
        }
    }

    private void SetStarting(bool starting)
    {
        _starting = starting;
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// On macOS and Linux, certificate and system proxy changes run commands
    /// (<c>networksetup</c>, which may wait on a password prompt;
    /// <c>certutil</c>, <c>gsettings</c>), so they run off the UI thread. On
    /// Windows they're quick registry calls, and the certificate prompt needs
    /// the UI thread.
    /// </summary>
    private static Task<T> RunOffUiThreadUnlessWindowsAsync<T>(Func<T> work) =>
        OperatingSystem.IsWindows() ? Task.FromResult(work()) : Task.Run(work);

    private static Task RunOffUiThreadUnlessWindowsAsync(Action work) =>
        RunOffUiThreadUnlessWindowsAsync(() =>
        {
            work();
            return true;
        });

    private async Task StartCoreAsync()
    {
        try
        {
            // Created once per run and reused across Start/Stop; each Start gets a
            // fresh leaf provider. Stays null if the macOS or Linux trust prompt is
            // declined, so the next Start asks again.
            if (_authority is null)
            {
                if (!OperatingSystem.IsWindows())
                {
                    var confirmed = _confirmCertificateTrust is not null
                        && await _confirmCertificateTrust();
                    if (!confirmed)
                    {
                        StatusText =
                            "Not started -- installing the trusted root certificate was declined. VioLens can't " +
                            "decrypt HTTPS traffic without it, so there's no way to proceed without installing it.";
                        return;
                    }

                    StatusText = OperatingSystem.IsMacOS()
                        ? "Installing the root certificate -- macOS may ask for your password..."
                        : "Installing the root certificate in your browsers' certificate databases...";
                }

                // On Windows this stays on the UI thread so the OS's own "install this
                // certificate?" dialog appears in front of the app.
                _authority = await RunOffUiThreadUnlessWindowsAsync(CreateAuthority);
            }

            var leafProvider = new LeafCertificateProvider(_authority.RootCertificate);
            // Port 0 lets the OS pick.
            var requestedPort = UseAutomaticPort ? 0 : (int)(PreferredPort ?? DefaultPreferredPort);

            // Before the probe below, so a freshly launched host is picked up by
            // this Start. EnsureRunning waits for its pipe.
            if (AutoLaunchLegacyHost)
            {
                var launchResult = _legacyExtensionHostLauncher.EnsureRunning(
                    log: message => Console.WriteLine($"[Extension] {message}"));
                LegacyExtensionHostStatus = launchResult.Message;
            }
            else
            {
                LegacyExtensionHostStatus = "Auto-launch is off -- Tools -> Legacy Extension Host to turn it on.";
            }

            // Probed on every Start, so a legacy host started after VioLens is
            // picked up without a restart. Times out quickly and never throws.
            var legacyBridge = LegacyExtensionHostBridgeClient.Probe(
                log: message => Console.WriteLine($"[Extension] {message}"));

            _proxy = InterceptingProxyListener.StartOnAvailablePort(
                requestedPort, leafProvider, _sessionStore, _breakpointManager, _autoResponderRules, _fiddlerScriptRunner,
                new CompositeExtensionAutoTamperHost([_extensionHost.CreateAutoTamperHost(), legacyBridge]),
                _extensionHost.CreateSessionHost(),
                allowRemoteClients: AllowRemoteClients);
            _proxy.Http2Enabled = Http2Enabled;
            _proxy.SkipDecryptionHosts = SkipDecryptionHosts;
            ApplyClientCertificate();
            IsRunning = true;
            ShowBoundPort(_proxy.Port);

            // Before registering as the system proxy, while the system setting
            // still names the network's own proxy (if any).
            if (OperatingSystem.IsMacOS())
            {
                StatusText = $"Listening on port {_proxy.Port} -- registering as the system proxy (macOS may ask for your password)...";
            }

            var proxy = _proxy;
            var detected = await RunOffUiThreadUnlessWindowsAsync(() => SystemProxyController.DetectUpstream(proxy.Port));
            var gatewayNote = StartGateway(proxy, detected);
            var listeningOn = proxy.AllowsRemoteClients
                ? $"all network interfaces, port {proxy.Port}"
                : $"127.0.0.1:{proxy.Port}";

            // Only Specify mode can have its port taken.
            var portNote = UseAutomaticPort || proxy.Port == requestedPort
                ? string.Empty
                : $" (port {requestedPort} was already taken -- probably another VioLens process still running)";

            // Failing to become the system proxy leaves the listener running; the
            // browser can still be pointed at it by hand.
            try
            {
                await RunOffUiThreadUnlessWindowsAsync(() => SystemProxyController.Enable(proxy.Port));
                _registeredAsSystemProxy = true;
                StatusText =
                    $"Listening on {listeningOn}{portNote} and registered as the system proxy -- " +
                    "browsers and most other apps on this machine will route through here automatically " +
                    $"until you click Stop. Outgoing: {gatewayNote}.";
            }
            catch (Exception ex)
            {
                StatusText =
                    $"Listening on {listeningOn}{portNote}, but couldn't register as the system " +
                    $"proxy ({ex.Message}) -- point a browser's proxy settings here manually instead. Outgoing: {gatewayNote}.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to start the proxy: {ex.Message}";
        }
    }

    private static CertificateAuthority CreateAuthority()
    {
        var authority = new CertificateAuthority();

        // CLeARINET's old root, now that VioLens's own is trusted. Only on
        // a computer that had CLeARINET; see FormerRoots.
        if (Program.Migration?.FoundFormerInstall == true)
        {
            FormerRoots.Remove(log: message => Console.WriteLine($"[certificates] {message}"));
        }

        return authority;
    }

    private async void Stop() => await StopAsync();

    /// <summary>Stops capturing and restores the system proxy, off the UI thread on macOS.</summary>
    private async Task StopAsync()
    {
        if (_proxy is null || _starting)
        {
            return;
        }

        _proxy.Stop();
        _proxy = null;
        _registeredAsSystemProxy = false;
        SetStarting(true);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                StatusText = "Stopping -- putting the system proxy settings back...";
            }

            await RunOffUiThreadUnlessWindowsAsync(SystemProxyController.Disable);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[system-proxy] Couldn't put the system proxy settings back: {ex.Message}");
        }
        finally
        {
            SetStarting(false);
        }

        IsRunning = false;
        StopGateway();

        // Only stops a host this app launched.
        _legacyExtensionHostLauncher.StopIfLaunchedByUs(
            log: message => Console.WriteLine($"[Extension] {message}"));

        // Add any sessions still waiting, so the count below includes them.
        _sessionArrivals.Flush();
        _captureFlashTimer.Stop();
        IsCaptureFlashing = false;

        StatusText = $"Stopped and released as the system proxy. {Sessions.Count} session(s) captured this run -- " +
                      "Start again to keep going, or File > Save SAZ to export what's here.";
    }

    /// <summary>
    /// Stops capturing (HTTPS can't be decrypted without the root) and removes
    /// every VioLens root and its key. The next Start makes a new one.
    /// </summary>
    private async void RemoveRootCertificate()
    {
        if (_starting)
        {
            StatusText = "Wait for Start or Stop to finish, then remove the root certificate.";
            return;
        }

        await StopAsync();
        _authority = null;

        var name = Violens.ProxyCore.ProxyEngine.Identity.RootCertificateName;
        void Log(string message) => Console.WriteLine($"[certificates] {message}");
        var removed = await RunOffUiThreadUnlessWindowsAsync(() => CertificateAuthority.RemoveRoots(name, Log));

        StatusText = removed > 0
            ? $"Removed {removed} root certificate(s). Start again to create and trust a new one."
            : "No root certificate was removed (none was trusted, or the removal was declined).";
    }

    /// <summary>Lights the dot and restarts the countdown, so it stays lit through a burst.</summary>
    private void FlashCaptureIndicator()
    {
        IsCaptureFlashing = true;
        _captureFlashTimer.Stop();
        _captureFlashTimer.Start();
    }

    /// <summary>
    /// Adds an empty rule and selects it, like Fiddler's "Add Rule". It starts
    /// disabled because an empty rule matches everything.
    /// </summary>
    private void AddAutoResponderRule()
    {
        var rule = new AutoResponderRule { IsEnabled = false };
        _autoResponderRules.Rules.Add(rule);
        var vm = CreateAutoResponderRow(rule);
        AutoResponderRuleRows.Add(vm);
        SelectedAutoResponderRule = vm;
        ScheduleAutoResponderSave();
    }

    /// <summary>
    /// Loads the script at <see cref="FiddlerScriptPath"/>. Unreadable files
    /// and script errors both show in the status line.
    /// </summary>
    private void LoadFiddlerScript()
    {
        try
        {
            _fiddlerScriptRunner.LoadFromFile(FiddlerScriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FiddlerScriptStatus = $"Couldn't read '{FiddlerScriptPath}': {ex.Message}";
            ReloadFiddlerScriptCommand.RaiseCanExecuteChanged();
            return;
        }

        RefreshFiddlerScriptStatus();
        ReloadFiddlerScriptCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Also reached from a script calling <c>FiddlerObject.ReloadScript()</c>.</summary>
    private void ReloadFiddlerScript()
    {
        _fiddlerScriptRunner.Reload();
        RefreshFiddlerScriptStatus();
    }

    private void RefreshFiddlerScriptStatus()
    {
        FiddlerScriptStatus = _fiddlerScriptRunner.LoadError is { } error
            ? $"Script error -- still running the last-good script, if any: {error}"
            : $"Loaded '{_fiddlerScriptRunner.LoadedPath}'. " +
              $"OnBeforeRequest: {(_fiddlerScriptRunner.HasOnBeforeRequest ? "yes" : "no")}, " +
              $"OnBeforeResponse: {(_fiddlerScriptRunner.HasOnBeforeResponse ? "yes" : "no")}.";

        RefreshScriptMenus();
    }

    /// <summary>Rebuilds the script menus from scratch; reloads are rare, so no diffing.</summary>
    private void RefreshScriptMenus()
    {
        RulesMenuEntries.Clear();
        foreach (var entry in BuildRulesMenuEntries())
        {
            RulesMenuEntries.Add(entry);
        }

        RaisePropertyChanged(nameof(HasRulesMenuEntries));

        ToolsMenuEntries.Clear();
        foreach (var descriptor in _fiddlerScriptRunner.Directives.ToolsActions)
        {
            ToolsMenuEntries.Add(new ActionMenuEntryViewModel(
                descriptor.MenuText, () => _fiddlerScriptRunner.InvokeToolsAction(descriptor.MethodName)));
        }

        RaisePropertyChanged(nameof(HasToolsMenuEntries));

        ContextActionEntries.Clear();
        foreach (var descriptor in _fiddlerScriptRunner.Directives.ContextActions)
        {
            ContextActionEntries.Add(new ActionMenuEntryViewModel(
                descriptor.MenuText, () => InvokeContextAction(descriptor.MethodName)));
        }
    }

    /// <summary>Runs a <c>ContextAction</c> on the selected session; does nothing if none is selected.</summary>
    private void InvokeContextAction(string methodName)
    {
        if (SelectedSessionRow is { } selected)
        {
            _fiddlerScriptRunner.InvokeContextAction(methodName, [selected.Session]);
        }
    }

    /// <summary>
    /// One entry per Rules option and string choice. Radio groups and string
    /// choices update their siblings' script fields, not just the checkmarks.
    /// </summary>
    private List<RulesMenuEntryViewModel> BuildRulesMenuEntries()
    {
        var entries = new List<RulesMenuEntryViewModel>();
        var radioGroups = new Dictionary<string, List<(RulesMenuOption Option, RulesMenuEntryViewModel Entry)>>();

        foreach (var option in _fiddlerScriptRunner.Directives.RulesMenuOptions)
        {
            var header = option.SubmenuName is null ? option.MenuText : $"{option.SubmenuName}: {option.MenuText}";
            var initial = _fiddlerScriptRunner.GetRulesOptionValue(option.FieldName);

            List<(RulesMenuOption Option, RulesMenuEntryViewModel Entry)>? group = null;
            if (option.IsRadio && option.SubmenuName is not null)
            {
                if (!radioGroups.TryGetValue(option.SubmenuName, out group))
                {
                    group = [];
                    radioGroups[option.SubmenuName] = group;
                }
            }

            var entry = new RulesMenuEntryViewModel(header, initial, isChecked =>
            {
                _fiddlerScriptRunner.SetRulesOptionValue(option.FieldName, isChecked);

                // Clear the siblings' fields too, so only one stays true in the script.
                if (isChecked && group is not null)
                {
                    foreach (var (siblingOption, siblingEntry) in group)
                    {
                        if (!string.Equals(siblingOption.FieldName, option.FieldName, StringComparison.Ordinal))
                        {
                            _fiddlerScriptRunner.SetRulesOptionValue(siblingOption.FieldName, false);
                            siblingEntry.SetCheckedWithoutNotifying(false);
                        }
                    }
                }
            });

            entries.Add(entry);
            group?.Add((option, entry));
        }

        foreach (var stringOption in _fiddlerScriptRunner.Directives.RulesMenuStringOptions)
        {
            var currentValue = _fiddlerScriptRunner.GetRulesStringValue(stringOption.FieldName);
            var group = new List<RulesMenuEntryViewModel>();

            foreach (var choice in stringOption.Choices)
            {
                var header = $"{stringOption.SubmenuName}: {choice.Name}";
                var initial = string.Equals(currentValue, choice.Value, StringComparison.Ordinal);

                RulesMenuEntryViewModel? entry = null;
                entry = new RulesMenuEntryViewModel(header, initial, isChecked =>
                {
                    if (!isChecked)
                    {
                        // Unchecking a choice isn't meaningful; picking another one is. Keep
                        // this one checked.
                        entry!.SetCheckedWithoutNotifying(true);
                        return;
                    }

                    _fiddlerScriptRunner.SetRulesStringValue(stringOption.FieldName, choice.Value);
                    foreach (var sibling in group)
                    {
                        if (!ReferenceEquals(sibling, entry))
                        {
                            sibling.SetCheckedWithoutNotifying(false);
                        }
                    }
                });

                entries.Add(entry);
                group.Add(entry);
            }
        }

        return entries;
    }

    private int LoadedExtensionCount =>
        _extensionHost.AutoTampers.Count
        + _extensionHost.RequestInspectors.Count
        + _extensionHost.ResponseInspectors.Count
        + _extensionHost.Importers.Count
        + _extensionHost.Exporters.Count
        + _extensionHost.ExecActionHandlers.Count
        + _extensionHost.ShimExtensions.Count;

    /// <summary>Lists every folder scanned, so it's clear where extensions were looked for.</summary>
    private string BuildExtensionStatus()
    {
        var lines = new List<string>();

        foreach (var (folder, existed, dllFilesFound) in _extensionHost.ScanResults)
        {
            lines.Add(existed
                ? $"Scanned '{folder}': found {dllFilesFound} .dll file(s)."
                : $"Folder not found, nothing scanned: '{folder}'.");
        }

        var loadedCount = LoadedExtensionCount;
        lines.Add(
            $"Loaded: {_extensionHost.AutoTampers.Count} AutoTamper, " +
            $"{_extensionHost.RequestInspectors.Count} request inspector, " +
            $"{_extensionHost.ResponseInspectors.Count} response inspector, " +
            $"{_extensionHost.Importers.Count} importer, " +
            $"{_extensionHost.Exporters.Count} exporter, " +
            $"{_extensionHost.ExecActionHandlers.Count} exec-action handler, " +
            $"{_extensionHost.ShimExtensions.Count} ported Fiddler extension " +
            $"(total {loadedCount}).");

        foreach (var loadError in _extensionHost.LoadErrors)
        {
            lines.Add($"Error: {loadError}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void RemoveSelectedAutoResponderRule()
    {
        var selected = SelectedAutoResponderRule;
        if (selected is null)
        {
            return;
        }

        var index = AutoResponderRuleRows.IndexOf(selected);
        _autoResponderRules.Rules.Remove(selected.Rule);
        AutoResponderRuleRows.Remove(selected);
        selected.PropertyChanged -= OnAutoResponderRowChanged;
        ScheduleAutoResponderSave();

        SelectedAutoResponderRule = AutoResponderRuleRows.Count == 0
            ? null
            : AutoResponderRuleRows[Math.Min(index, AutoResponderRuleRows.Count - 1)];
    }

    private bool CanMoveSelectedAutoResponderRule(int direction)
    {
        if (SelectedAutoResponderRule is null)
        {
            return false;
        }

        var index = AutoResponderRuleRows.IndexOf(SelectedAutoResponderRule);
        var newIndex = index + direction;
        return newIndex >= 0 && newIndex < AutoResponderRuleRows.Count;
    }

    /// <summary>
    /// Moves the rule in both lists; <see cref="AutoResponderRules.Evaluate"/>
    /// uses the order of its own list.
    /// </summary>
    private void MoveSelectedAutoResponderRule(int direction)
    {
        if (SelectedAutoResponderRule is not { } selected || !CanMoveSelectedAutoResponderRule(direction))
        {
            return;
        }

        var index = AutoResponderRuleRows.IndexOf(selected);
        var newIndex = index + direction;

        AutoResponderRuleRows.Move(index, newIndex);
        _autoResponderRules.Rules.RemoveAt(index);
        _autoResponderRules.Rules.Insert(newIndex, selected.Rule);
        ScheduleAutoResponderSave();

        MoveAutoResponderRuleUpCommand.RaiseCanExecuteChanged();
        MoveAutoResponderRuleDownCommand.RaiseCanExecuteChanged();
    }

    private void SaveSaz() => SaveSazTo(DefaultSazPath());

    /// <summary>Where Save SAZ (and QuickExec's dump) writes: a new, dated file on the Desktop.</summary>
    private static string DefaultSazPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"violens-capture-{DateTime.Now:yyyyMMdd-HHmmss}.saz");

    /// <summary>Saves every session as SAZ; false (with the reason on the status line) if it couldn't be written.</summary>
    private bool SaveSazTo(string path)
    {
        var sessions = _sessionStore.Snapshot();
        try
        {
            SazWriter.Write(path, sessions);
            StatusText = $"Saved {sessions.Count} session(s) to {path}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to save SAZ: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Imports a .saz file. Sessions go through <see cref="SessionStore.Add"/>,
    /// so they arrive like live captures. Awaited rather than blocked on, since
    /// <see cref="SazReader"/> doesn't use <c>ConfigureAwait(false)</c>.
    /// </summary>
    public async Task<bool> ImportSazAsync(string path)
    {
        try
        {
            var result = await SazReader.ImportAsync(path, _sessionStore);
            StatusText = result.Skipped.Count == 0
                ? $"Imported {result.Imported} session(s) from {Path.GetFileName(path)}."
                : $"Imported {result.Imported} session(s) from {Path.GetFileName(path)} " +
                  $"({result.Skipped.Count} skipped -- see the console for details).";

            foreach (var reason in result.Skipped)
            {
                Console.WriteLine($"[import] Skipped {reason}");
            }

            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to import {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Adds a batch of new sessions, checking only them against the filter,
    /// and updates the commands and the capture flash once for the batch.
    /// </summary>
    private void AddSessionRows(IReadOnlyList<Session> arrived)
    {
        foreach (var session in arrived)
        {
            var row = SessionRow.From(session, _fiddlerScriptRunner);
            Sessions.Add(row);
            // ui-hide sessions are captured and saved but not listed.
            if (!row.Style.Hidden && _query.Matches(session))
            {
                FilteredSessions.Add(row);
            }
        }

        SaveSazCommand.RaiseCanExecuteChanged();
        ExportViaExtensionCommand.RaiseCanExecuteChanged();
        RemoveAllSessionsCommand.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(HasSessions));
        FlashCaptureIndicator();
    }

    /// <summary>Full rescan; only needed when the query changes.</summary>
    private void ApplyFilter()
    {
        FilteredSessions.Clear();
        foreach (var row in Sessions)
        {
            if (!row.Style.Hidden && _query.Matches(row.Session))
            {
                FilteredSessions.Add(row);
            }
        }
    }

    /// <summary>Rebuilds both tab lists, running every inspector up front; they're cheap.</summary>
    private void RefreshInspectors()
    {
        RequestInspectors.Clear();
        ResponseInspectors.Clear();

        var session = _selectedSessionRow?.Session;
        if (session is null)
        {
            return;
        }

        AddInspectorTabs(RequestInspectors, new InspectorContext(session, InspectorSide.Request));
        AddInspectorTabs(ResponseInspectors, new InspectorContext(session, InspectorSide.Response));
    }

    private void AddInspectorTabs(ObservableCollection<InspectorTabViewModel> target, InspectorContext context)
    {
        foreach (var inspector in _inspectorRegistry.GetApplicable(context))
        {
            InspectorContent content;
            try
            {
                content = inspector.Inspect(context);
            }
            catch (Exception ex)
            {
                // A failing inspector shows its error in its own tab.
                content = new ErrorContent($"{inspector.DisplayName} inspector failed: {ex.Message}");
            }

            target.Add(new InspectorTabViewModel(inspector.DisplayName, content));
        }
    }

    public void Dispose()
    {
        StopAutomation();

        // OnBeforeUnload is owed even if the proxy never started.
        _extensionHost.Unload();
        ExtensionUi.SetTabHost(null);

        // Even if never started: EnsureRunning can succeed when the listener
        // then fails, which would otherwise orphan the legacy host.
        _legacyExtensionHostLauncher.StopIfLaunchedByUs(
            log: message => Console.WriteLine($"[Extension] {message}"));

        // After extensions unload, since they may save settings in
        // OnBeforeUnload.
        SaveAutoResponderRulesNow();
        _preferences.Dispose();

        if (_proxy is null)
        {
            return;
        }

        _proxy.Stop();
        _proxy = null;
        SystemProxyController.Disable();
    }

    /// <summary>
    /// File > Import via Extension. Runs the importer on a background thread.
    /// No options are passed; an importer that needs a file asks for one
    /// itself, as in Fiddler.
    /// </summary>
    private async void ImportViaExtension()
    {
        try
        {
            var choice = await PickFormatAsync(
                ProfferedFormats.ChoicesFrom(_extensionHost.Importers),
                PreferenceKeys.LastImportFormat,
                "Import sessions using which format?");
            if (choice?.Handler is not ISessionImporter importer)
            {
                return;
            }

            var formatName = choice.Format.Name;
            StatusText = $"Importing via {choice.DisplayName}...";
            var imported = await Task.Run(() => importer.ImportSessions(formatName, new Dictionary<string, object>(), progress: null));

            foreach (var session in imported)
            {
                _sessionStore.Add(session.Host, session.StartedAt, session.Request, session.Response, session.Flags);
            }

            StatusText = $"Imported {imported.Count} session(s) via {choice.DisplayName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Extension import failed: {ex.Message}";
        }
    }

    /// <summary>File > Export via Extension, for every captured session.</summary>
    private async void ExportViaExtension()
    {
        try
        {
            var choice = await PickFormatAsync(
                ProfferedFormats.ChoicesFrom(_extensionHost.Exporters),
                PreferenceKeys.LastExportFormat,
                "Export sessions using which format?");
            if (choice?.Handler is not ISessionExporter exporter)
            {
                return;
            }

            var sessions = _sessionStore.Snapshot();
            var formatName = choice.Format.Name;
            StatusText = $"Exporting via {choice.DisplayName}...";
            var succeeded = await Task.Run(() => exporter.ExportSessions(formatName, sessions, new Dictionary<string, object>(), progress: null));

            StatusText = succeeded
                ? $"Exported {sessions.Count} session(s) via {choice.DisplayName}."
                : "Extension export reported failure -- see the console for anything it logged.";
        }
        catch (Exception ex)
        {
            StatusText = $"Extension export failed: {ex.Message}";
        }
    }

    /// <summary>
    /// No dialog for a single choice; otherwise the picker, starting on the
    /// last choice. Without a picker, the remembered or first choice.
    /// </summary>
    private async Task<FormatChoice?> PickFormatAsync(IReadOnlyList<FormatChoice> choices, string preferenceKey, string heading)
    {
        if (choices.Count <= 1)
        {
            return choices.Count == 1 ? choices[0] : null;
        }

        var remembered = _preferences.GetStringPref(preferenceKey, string.Empty);
        if (_chooseFormat is null)
        {
            return ProfferedFormats.Preselect(choices, remembered);
        }

        var chosen = await _chooseFormat(heading, choices, remembered);
        if (chosen is not null)
        {
            _preferences.SetStringPref(preferenceKey, chosen.Key);
        }

        return chosen;
    }

    /// <summary>Help > Copy Diagnostic Info: puts <see cref="BuildDiagnosticInfo"/> on the clipboard.</summary>
    public async Task CopyDiagnosticInfoAsync(Func<string, Task> setClipboardText)
    {
        try
        {
            await setClipboardText(BuildDiagnosticInfo());
            StatusText = "Diagnostic info copied -- paste it into the bug report's Diagnostic info box.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't copy the diagnostic info: {ex.Message}";
        }
    }

    /// <summary>The text a bug report asks for (see DiagnosticReport).</summary>
    public string BuildDiagnosticInfo() => DiagnosticReport.Format(new DiagnosticSnapshot
    {
        Version = DiagnosticReport.DisplayVersion(typeof(MainWindowViewModel).Assembly),
        Port = _proxy?.Port,
        ListensOnAllInterfaces = _proxy?.AllowsRemoteClients == true,
        RegisteredAsSystemProxy = _proxy is null ? null : _registeredAsSystemProxy,
        Upstream = GatewayStatus,
        Http2Enabled = Http2Enabled,
        RootCertificate = _authority?.RootCertificate,
        SessionCount = Sessions.Count,
        ExtensionCount = LoadedExtensionCount,
        FiddlerScriptLoaded = _fiddlerScriptRunner.IsLoaded,
        LastStatus = StatusText,
    });

    /// <summary>
    /// Help > Documentation: opens the bundled User Guide, which works offline
    /// and matches the running build.
    /// </summary>
    private void OpenDocumentation()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Documentation", "User Guide.md");
            if (!File.Exists(path))
            {
                // In the macOS bundle, documentation lives in Contents/Resources; only
                // code may go in Contents/MacOS, or the signature won't verify.
                var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Documentation", "User Guide.md"));
                if (File.Exists(bundled))
                {
                    path = bundled;
                }
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the documentation: {ex.Message}";
        }
    }
}
