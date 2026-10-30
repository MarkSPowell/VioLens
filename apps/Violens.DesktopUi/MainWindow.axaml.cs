using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Violens.DesktopUi.ViewModels;
using Violens.AppCore.Preferences;
using Violens.ProxyCore.Preferences;

namespace Violens.DesktopUi;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType SazFileType = new("Session Archive Zip (*.saz)")
    {
        Patterns = ["*.saz"],
    };

    private static readonly FilePickerFileType HarFileType = new("HTTP Archive (*.har)")
    {
        Patterns = ["*.har"],
    };

    private static readonly FilePickerFileType PcapFileType = new("Packet capture (*.pcap, *.pcapng, *.cap)")
    {
        Patterns = ["*.pcap", "*.pcapng", "*.cap"],
    };

    /// <summary>
    /// <c>*.js</c> only, since <c>CustomRules.cs</c> isn't supported yet; "All
    /// files" is offered too.
    /// </summary>
    private static readonly FilePickerFileType FiddlerScriptFileType = new("FiddlerScript (*.js)")
    {
        Patterns = ["*.js"],
    };

    /// <summary>Columns added for <c>[BindUIColumn]</c>, removed again on the next script load.</summary>
    private readonly List<DataGridColumn> _scriptColumns = [];

    /// <summary>
    /// The column-chooser key of each script and extension column (see
    /// MainWindowViewModel.Columns.cs). Built-in columns aren't listed:
    /// their key is their header.
    /// </summary>
    private readonly Dictionary<DataGridColumn, string> _columnKeys = [];

    /// <summary>The built-in columns as MainWindow.axaml declares them, for Reset Columns.</summary>
    private readonly List<(DataGridColumn Column, DataGridLength Width)> _defaultColumns;

    // The saved order and widths are applied once the view model is
    // attached; saving before that would overwrite them with the defaults.
    private bool _columnLayoutApplied;
    private bool _applyingColumnLayout;

    private MainWindowViewModel? _viewModel;

    private bool _extensionTabsAdded;

    private bool _extensionMenusAttached;

    private readonly List<Control> _extensionTopMenus = [];

    private readonly List<Control> _extensionToolsItems = [];

    private bool _placementApplied;

    // The window's last position and size while it was neither maximized
    // nor minimized -- what's saved, so a maximized window comes back
    // maximized and un-maximizes to where it was.
    private PixelPoint _normalPosition;
    private Size _normalSize;

    public MainWindow()
    {
        InitializeComponent();

        // DataContext is set after construction (see App.axaml.cs).
        DataContextChanged += (_, _) => AttachViewModel();
        PositionChanged += (_, _) => RememberNormalBounds();
        Closing += (_, _) =>
        {
            SavePlacement();
            SaveColumnLayout(); // widths aren't saved as they change
        };

        _defaultColumns = SessionsDataGrid.Columns.Select(c => (c, c.Width)).ToList();

        // Right-clicking a column header offers the column chooser instead
        // of the list's own context menu. Tunnel, so this runs before the
        // grid's ContextMenu sees the request.
        SessionsDataGrid.AddHandler(ContextRequestedEvent, SessionsDataGrid_ContextRequested, RoutingStrategies.Tunnel);

        // Tunnel, so Up and Down reach the history before the text box uses them.
        QuickExecBox.AddHandler(KeyDownEvent, QuickExecBox_KeyDown, RoutingStrategies.Tunnel);
        SessionsDataGrid.ColumnReordered += (_, _) =>
        {
            if (!_applyingColumnLayout)
            {
                SaveColumnLayout();
                RefreshColumnsMenu();
            }
        };
    }

    /// <summary>
    /// Puts the window back where it was last time (see
    /// <see cref="MainWindowViewModel.LoadWindowPlacement"/>), as long as
    /// that's still on a connected screen -- a monitor may have been
    /// unplugged since. Runs once, before the window is shown.
    /// </summary>
    private void ApplyPlacement(MainWindowViewModel viewModel)
    {
        if (_placementApplied)
        {
            return;
        }

        _placementApplied = true;
        if (viewModel.LoadWindowPlacement() is not { } placement)
        {
            return;
        }

        Width = Math.Max(placement.Width, 480);
        Height = Math.Max(placement.Height, 320);

        var position = new PixelPoint(placement.X, placement.Y);
        var screens = Screens?.All ?? Array.Empty<Avalonia.Platform.Screen>();
        // Enough of the title bar to grab must be on some screen.
        var visible = screens.Any(screen => screen.WorkingArea.Contains(new PixelPoint(position.X + 40, position.Y + 10)));
        if (visible)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
        }

        _normalPosition = Position;
        _normalSize = new Size(Width, Height);
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty || change.Property == WindowStateProperty)
        {
            RememberNormalBounds();
        }
    }

    private void RememberNormalBounds()
    {
        if (WindowState == WindowState.Normal && IsVisible)
        {
            _normalPosition = Position;
            _normalSize = ClientSize;
        }
    }

    private void SavePlacement()
    {
        RememberNormalBounds();
        if (_viewModel is null || _normalSize.Width <= 0 || _normalSize.Height <= 0)
        {
            return;
        }

        _viewModel.SaveWindowPlacement(new WindowPlacement(
            _normalPosition.X,
            _normalPosition.Y,
            (int)Math.Round(_normalSize.Width),
            (int)Math.Round(_normalSize.Height),
            WindowState == WindowState.Maximized));
    }

    /// <summary>
    /// Subscribes to the view model. Script columns are built here because
    /// <c>DataGrid.Columns</c> can't be bound.
    /// </summary>
    private void AttachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ComposerShowRequested -= ShowComposerTab;
            _viewModel.FindFocusRequested -= FocusFindBox;
            _viewModel.FindResultsChanged -= RefreshFindHighlights;
            _viewModel.QuickExecSelectionRequested -= SelectQuickExecMatches;
        }

        _viewModel = DataContext as MainWindowViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ComposerShowRequested += ShowComposerTab;
            _viewModel.FindFocusRequested += FocusFindBox;
            _viewModel.FindResultsChanged += RefreshFindHighlights;
            _viewModel.QuickExecSelectionRequested += SelectQuickExecMatches;
            ApplyPlacement(_viewModel);
            RefreshScriptColumns();
            AddExtensionTabs(_viewModel);
            AttachExtensionMenusAndColumns();
            ApplyColumnLayout();
            ApplyColumnVisibility();
        }
    }

    /// <summary>
    /// Shows what extensions add through <c>FiddlerApplication.UI</c>, and
    /// keeps showing it as they add more: top-level menus (before Help),
    /// Tools menu items (after a separator), and session-list columns that
    /// show a session flag. Runs once.
    /// </summary>
    private void AttachExtensionMenusAndColumns()
    {
        if (_extensionMenusAttached)
        {
            return;
        }

        _extensionMenusAttached = true;
        var ui = Violens.CompatShim.FiddlerApplication.UI;

        RefreshExtensionMenus();
        ui.mnuMain.MenuItems.CollectionChanged += (_, _) => ExtensionMenus.OnUiThread(RefreshExtensionMenus);
        ui.mnuTools.MenuItems.CollectionChanged += (_, _) => ExtensionMenus.OnUiThread(RefreshExtensionMenus);

        foreach (var column in ui.lvSessions.Columns)
        {
            AddExtensionColumn(column);
        }

        ui.lvSessions.ColumnAdded += (_, column) => ExtensionMenus.OnUiThread(() => AddExtensionColumn(column));
    }

    private void RefreshExtensionMenus()
    {
        var ui = Violens.CompatShim.FiddlerApplication.UI;

        foreach (var control in _extensionTopMenus)
        {
            MainMenuBar.Items.Remove(control);
        }

        _extensionTopMenus.Clear();
        var insertAt = MainMenuBar.Items.IndexOf(HelpMenu);
        foreach (var item in ui.mnuMain.MenuItems)
        {
            var control = ExtensionMenus.Build(item);
            MainMenuBar.Items.Insert(insertAt++, control);
            _extensionTopMenus.Add(control);
        }

        foreach (var control in _extensionToolsItems)
        {
            ToolsMenu.Items.Remove(control);
        }

        _extensionToolsItems.Clear();
        if (ui.mnuTools.MenuItems.Count > 0)
        {
            var separator = new Separator();
            ToolsMenu.Items.Add(separator);
            _extensionToolsItems.Add(separator);
            foreach (var item in ui.mnuTools.MenuItems)
            {
                var control = ExtensionMenus.Build(item);
                ToolsMenu.Items.Add(control);
                _extensionToolsItems.Add(control);
            }
        }
    }

    /// <summary>
    /// A session-list column showing one session flag (<c>lvSessions.AddBoundColumn</c>),
    /// bound to <c>SessionRow.Flags</c>. Placed at the extension's requested
    /// position, or last if that's past the end.
    /// </summary>
    private void AddExtensionColumn(Violens.CompatShim.BoundColumn bound)
    {
        var column = new DataGridTextColumn
        {
            Header = bound.Title,
            Binding = new Binding($"Flags[{bound.FlagName}]"),
            Width = bound.Width > 0 ? new DataGridLength(bound.Width) : DataGridLength.Auto,
        };

        var columns = SessionsDataGrid.Columns;
        var index = bound.DisplayOrder < 0 || bound.DisplayOrder > columns.Count ? columns.Count : bound.DisplayOrder;
        SaveColumnLayout();
        _columnKeys[column] = "flag:" + bound.FlagName;
        columns.Insert(index, column);
        ApplyColumnLayout();
        ApplyColumnVisibility();
    }

    /// <summary>
    /// Adds a tab after Inspectors for each view an extension supplied
    /// through <c>ExtensionUi.AddTab</c>. Extensions only add tabs while
    /// loading, before this window exists, so this runs once. A view that
    /// isn't an Avalonia <see cref="Control"/> gets a tab explaining that,
    /// rather than being dropped silently. The tab strip stays hidden while
    /// Inspectors is the only tab.
    /// </summary>
    private void AddExtensionTabs(MainWindowViewModel viewModel)
    {
        if (_extensionTabsAdded)
        {
            return;
        }

        _extensionTabsAdded = true;
        foreach (var tab in viewModel.ExtensionTabs)
        {
            var content = tab.View as Control ?? new TextBlock
            {
                Text = $"This extension's view is a {tab.View.GetType().FullName}, not an Avalonia control, so VioLens can't show it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8),
            };
            DetailTabs.Items.Add(new TabItem { Header = tab.Title, Content = content });
        }

        DetailTabs.Classes.Set("single", DetailTabs.Items.Count <= 1);
    }

    /// <summary>A change to FiddlerScriptStatus means a script just loaded.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.FiddlerScriptStatus))
        {
            RefreshScriptColumns();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedSessionRow) &&
                 _viewModel?.SelectedSessionRow is { IsFindMatch: true } match)
        {
            // Find and Find Next select a match that may be scrolled out of view.
            SessionsDataGrid.ScrollIntoView(match, null);
        }
    }

    /// <summary>Ctrl+F: puts the cursor in the Find box, with its text selected so typing replaces it.</summary>
    private void FocusFindBox() => Dispatcher.UIThread.Post(() =>
    {
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    });

    /// <summary>
    /// Re-tags the rows on screen after a Find (rows scrolled into view
    /// later get tagged by <see cref="SessionsDataGrid_LoadingRow"/>).
    /// </summary>
    private void RefreshFindHighlights()
    {
        foreach (var row in SessionsDataGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            row.Classes.Set("find-match", row.DataContext is Models.SessionRow { IsFindMatch: true });
        }
    }

    private void ShowComposerTab() => DetailTabs.SelectedItem = ComposerTab;

    /// <summary>The Composer's Browse button: a file to send as the request body.</summary>
    private async void BrowseComposerBodyFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "File to send as the request body",
            AllowMultiple = false,
        });

        if (files.Count > 0)
        {
            _viewModel.ComposerBodyFile = files[0].Path.LocalPath;
        }
    }

    /// <summary>Edit > QuickExec (Alt+Q): puts the cursor in the QuickExec box.</summary>
    private void QuickExecMenuItem_Click(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => QuickExecBox.Focus());

    /// <summary>Up and Down go through earlier commands; Escape clears the box.</summary>
    private void QuickExecBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                _viewModel.BrowseQuickExecHistory(-1);
                break;
            case Key.Down:
                _viewModel.BrowseQuickExecHistory(1);
                break;
            case Key.Escape:
                _viewModel.QuickExecText = string.Empty;
                break;
            default:
                return;
        }

        QuickExecBox.CaretIndex = QuickExecBox.Text?.Length ?? 0;
        e.Handled = true;
    }

    /// <summary>Selects the sessions a QuickExec command picked, and scrolls to the first.</summary>
    private void SelectQuickExecMatches(IReadOnlyList<Models.SessionRow> rows)
    {
        SessionsDataGrid.SelectedItems.Clear();
        foreach (var row in rows)
        {
            SessionsDataGrid.SelectedItems.Add(row);
        }

        if (rows.Count > 0)
        {
            SessionsDataGrid.ScrollIntoView(rows[0], null);
        }
    }

    /// <summary>The Delete key removes the selected session, as Edit > Remove Selected Session does; R replays it, as Edit > Replay Selected Session does.</summary>
    private void SessionsDataGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        // R replays the selected session, as Fiddler Classic's Reissue.
        if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.None &&
            _viewModel?.ReplaySelectedSessionCommand is { } replay && replay.CanExecute(null))
        {
            replay.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None &&
            _viewModel?.RemoveSelectedSessionCommand is { } remove && remove.CanExecute(null))
        {
            remove.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Tags each session row, as it's shown (rows are reused while
    /// scrolling, so this runs again for every reuse), with the classes the
    /// grid's styles in MainWindow.axaml key on: <c>ui-back</c>,
    /// <c>ui-fore</c>, <c>ui-bold</c>, <c>ui-italic</c>, <c>ui-strikeout</c>.
    /// </summary>
    private void SessionsDataGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        var style = (e.Row.DataContext as Models.SessionRow)?.Style ?? Models.SessionRowStyle.Plain;
        e.Row.Classes.Set("ui-back", style.Background is not null);
        e.Row.Classes.Set("ui-fore", style.Foreground is not null);
        e.Row.Classes.Set("ui-bold", style.Bold);
        e.Row.Classes.Set("ui-italic", style.Italic);
        e.Row.Classes.Set("ui-strikeout", style.Strikeout);
        e.Row.Classes.Set("find-match", e.Row.DataContext is Models.SessionRow { IsFindMatch: true });
    }

    /// <summary>
    /// Rebuilds the <c>[BindUIColumn]</c> columns after the built-in ones, in
    /// declaration order (<c>DisplayOrder</c> isn't honoured yet). Each binds to
    /// <c>ScriptColumns[Title]</c>.
    /// </summary>
    private void RefreshScriptColumns()
    {
        if (_viewModel is null)
        {
            return;
        }

        // Remember where these columns were (and their widths) before
        // they go, so the reloaded script's columns come back there.
        SaveColumnLayout();
        foreach (var column in _scriptColumns)
        {
            SessionsDataGrid.Columns.Remove(column);
            _columnKeys.Remove(column);
        }

        _scriptColumns.Clear();

        foreach (var descriptor in _viewModel.ScriptDirectives.UIColumns)
        {
            var column = new DataGridTextColumn
            {
                Header = descriptor.ColumnTitle,
                Binding = new Binding($"ScriptColumns[{descriptor.ColumnTitle}]"),
                Width = descriptor.Width is { } width ? new DataGridLength(width) : DataGridLength.Auto,
            };

            _columnKeys[column] = "script:" + descriptor.ColumnTitle;
            SessionsDataGrid.Columns.Add(column);
            _scriptColumns.Add(column);
        }

        ApplyColumnLayout();
        ApplyColumnVisibility();
    }

    /// <summary>
    /// Puts the columns in their saved order and gives them their saved
    /// widths. A column with no saved place (one the user has never seen
    /// before) stays where it was added. The URL column keeps filling the
    /// space that's left.
    /// </summary>
    private void ApplyColumnLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        _columnLayoutApplied = true;
        var saved = _viewModel.LoadColumnLayout();
        if (saved.Count == 0)
        {
            return;
        }

        _applyingColumnLayout = true;
        try
        {
            var current = SessionsDataGrid.Columns.OrderBy(c => c.DisplayIndex).ToList();
            var order = ColumnLayout.Arrange(
                current.Select(c => ColumnKey(c) ?? string.Empty).ToList(),
                saved.Select(e => e.Key).ToList());
            for (var i = 0; i < order.Count; i++)
            {
                current[order[i]].DisplayIndex = i;
            }

            var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in saved)
            {
                if (entry.Width is { } width)
                {
                    widths.TryAdd(entry.Key, width);
                }
            }

            foreach (var column in current)
            {
                if (!column.Width.IsStar && ColumnKey(column) is { } key && widths.TryGetValue(key, out var width))
                {
                    column.Width = new DataGridLength(Math.Max(width, column.MinWidth));
                }
            }
        }
        finally
        {
            _applyingColumnLayout = false;
        }
    }

    /// <summary>Remembers the columns' order and widths (only widths the user or the column set; not the URL column's fill).</summary>
    private void SaveColumnLayout()
    {
        if (_viewModel is null || !_columnLayoutApplied)
        {
            return;
        }

        var current = SessionsDataGrid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(c => (Column: c, Key: ColumnKey(c)))
            .Where(c => !string.IsNullOrEmpty(c.Key))
            .Select(c => new ColumnLayoutEntry(c.Key!, c.Column.Width.IsAbsolute ? c.Column.Width.Value : null))
            .ToList();
        _viewModel.SaveColumnLayout(current);
    }

    /// <summary>Every column shown, in the order and at the widths MainWindow.axaml gives them.</summary>
    private void ResetColumns()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ShowAllColumns();
        _viewModel.ClearColumnLayout();
        _applyingColumnLayout = true;
        try
        {
            for (var i = 0; i < _defaultColumns.Count; i++)
            {
                var (column, width) = _defaultColumns[i];
                column.Width = width;
                column.DisplayIndex = i;
            }
        }
        finally
        {
            _applyingColumnLayout = false;
        }

        ApplyColumnVisibility();
    }

    /// <summary>The key a column is remembered by: see MainWindowViewModel.Columns.cs.</summary>
    private string? ColumnKey(DataGridColumn column) =>
        _columnKeys.TryGetValue(column, out var key) ? key : column.Header as string;

    /// <summary>Shows or hides every column as the saved choice says, and refreshes Tools > Session List Columns.</summary>
    private void ApplyColumnVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        foreach (var column in SessionsDataGrid.Columns)
        {
            if (ColumnKey(column) is { } key)
            {
                column.IsVisible = _viewModel.IsColumnVisible(key);
            }
        }

        RefreshColumnsMenu();
    }

    private void RefreshColumnsMenu()
    {
        ColumnsMenu.Items.Clear();
        foreach (var item in BuildColumnMenuItems())
        {
            ColumnsMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// One checkable entry per column, in the order they're shown, then
    /// Show All Columns and Reset Columns. <c>#</c> and <c>URL</c> are listed but can't be
    /// unchecked. Built fresh each time: the menu items for Tools and for
    /// the header's context menu can't be shared.
    /// </summary>
    private List<Control> BuildColumnMenuItems()
    {
        var items = new List<Control>();
        if (_viewModel is not { } viewModel)
        {
            return items;
        }

        foreach (var column in SessionsDataGrid.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (ColumnKey(column) is not { } key)
            {
                continue;
            }

            var title = column.Header?.ToString() ?? key;
            var item = new MenuItem
            {
                // A single underscore would be taken as an access key.
                Header = title.Replace("_", "__", StringComparison.Ordinal),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                IsEnabled = !MainWindowViewModel.IsColumnLocked(key),
            };
            var target = column;
            item.Click += (_, _) => SetColumnVisible(target, key, !target.IsVisible);
            items.Add(item);
        }

        items.Add(new Separator());
        var showAll = new MenuItem { Header = "_Show All Columns" };
        showAll.Click += (_, _) =>
        {
            viewModel.ShowAllColumns();
            Dispatcher.UIThread.Post(ApplyColumnVisibility);
        };
        items.Add(showAll);

        var reset = new MenuItem { Header = "_Reset Columns" };
        ToolTip.SetTip(reset, "Shows every column, in the original order and widths.");
        reset.Click += (_, _) => Dispatcher.UIThread.Post(ResetColumns);
        items.Add(reset);
        return items;
    }

    private void SetColumnVisible(DataGridColumn column, string key, bool visible)
    {
        if (_viewModel is null || MainWindowViewModel.IsColumnLocked(key))
        {
            return;
        }

        _viewModel.SetColumnVisible(key, visible);
        column.IsVisible = visible;

        // After the click, not during it: the menu being clicked is rebuilt.
        Dispatcher.UIThread.Post(RefreshColumnsMenu);
    }

    /// <summary>
    /// Right-click on a column header: the column chooser. On a row: selects
    /// it (unless it's already part of the selection, so a right-click on one
    /// of two selected sessions keeps both), then fills the list's own
    /// context menu, which opens once this returns.
    /// </summary>
    private void SessionsDataGrid_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var source = e.Source as Visual;
        if (source?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is { } header)
        {
            e.Handled = true;
            var menu = new ContextMenu { ItemsSource = BuildColumnMenuItems() };
            menu.Open(header);
            return;
        }

        if (source?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: Models.SessionRow clicked } &&
            !SessionsDataGrid.SelectedItems.Contains(clicked))
        {
            SessionsDataGrid.SelectedItem = clicked;
        }

        SessionContextMenu.ItemsSource = BuildSessionMenuItems();
    }

    /// <summary>The session list's right-click menu, for the current selection.</summary>
    private List<Control> BuildSessionMenuItems()
    {
        var items = new List<Control>();
        if (_viewModel is not { } viewModel)
        {
            return items;
        }

        var compare = new MenuItem
        {
            Header = "Co_mpare Sessions",
            InputGesture = new KeyGesture(Key.W, KeyModifiers.Control),
            IsEnabled = SessionsDataGrid.SelectedItems.Count == 2,
        };
        ToolTip.SetTip(compare, "Select two sessions (Ctrl+click the second) to compare them side by side.");
        compare.Click += (_, _) => CompareSelectedSessions();
        items.Add(compare);

        items.Add(new MenuItem { Header = "_Replay", Command = viewModel.ReplaySelectedSessionCommand, InputGesture = new KeyGesture(Key.R) });
        items.Add(new MenuItem { Header = "Edit in _Composer", Command = viewModel.EditInComposerCommand });
        items.Add(new MenuItem { Header = "Re_move", Command = viewModel.RemoveSelectedSessionCommand, InputGesture = new KeyGesture(Key.Delete) });

        if (viewModel.ContextActionEntries.Count > 0)
        {
            items.Add(new Separator());
            foreach (var entry in viewModel.ContextActionEntries)
            {
                items.Add(new MenuItem { Header = entry.Header, Command = entry.InvokeCommand });
            }
        }

        return items;
    }

    /// <summary>
    /// File > Open SAZ. Picking a file needs <see cref="TopLevel.StorageProvider"/>,
    /// so this lives in code-behind.
    /// </summary>
    private async void OpenSazMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var topLevel = GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SAZ",
            AllowMultiple = false,
            FileTypeFilter = [SazFileType],
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null)
        {
            return;
        }

        await viewModel.ImportSazAsync(file.Path.LocalPath);
    }

    private async void ImportHarMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import HAR",
            AllowMultiple = false,
            FileTypeFilter = [HarFileType, FilePickerFileTypes.All],
        });
        if (files.Count > 0)
        {
            await viewModel.ImportHarAsync(files[0].Path.LocalPath);
        }
    }

    private async void ImportPcapMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Packet Capture",
            AllowMultiple = false,
            FileTypeFilter = [PcapFileType, FilePickerFileTypes.All],
        });
        if (files.Count > 0)
        {
            await viewModel.ImportPcapAsync(files[0].Path.LocalPath);
        }
    }

    private async void ExportHarMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export HAR",
            SuggestedFileName = $"violens-capture-{DateTime.Now:yyyyMMdd-HHmmss}.har",
            DefaultExtension = "har",
            FileTypeChoices = [HarFileType],
            ShowOverwritePrompt = true,
        });
        if (file is not null)
        {
            await viewModel.ExportHarAsync(file.Path.LocalPath);
        }
    }

    /// <summary>
    /// Browse for a FiddlerScript file. A button because a read-only TextBox
    /// handles PointerPressed itself, so a click handler on it never fires.
    /// </summary>
    private async void BrowseFiddlerScriptButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var topLevel = GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a FiddlerScript file",
            AllowMultiple = false,
            FileTypeFilter = [FiddlerScriptFileType, FilePickerFileTypes.All],
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null)
        {
            return;
        }

        viewModel.FiddlerScriptPath = file.Path.LocalPath;
    }

    /// <summary>Help > About VioLens, as a dialog owned by this window.</summary>
    private void AboutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        _ = about.ShowDialog(this);
    }

    /// <summary>Help > Copy Diagnostic Info, for pasting into a bug report.</summary>
    private async void CopyDiagnosticInfoMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && Clipboard is { } clipboard)
        {
            await viewModel.CopyDiagnosticInfoAsync(text => clipboard.SetTextAsync(text));
        }
    }

    /// <summary>
    /// Edit > Compare Sessions (Ctrl+W): the two selected sessions side by
    /// side, in a window of their own.
    /// </summary>
    private void CompareSessionsMenuItem_Click(object? sender, RoutedEventArgs e) => CompareSelectedSessions();

    private void CompareSelectedSessions()
    {
        var selected = SessionsDataGrid.SelectedItems.OfType<Models.SessionRow>().ToList();
        if (selected.Count != 2)
        {
            _viewModel?.ShowStatus(selected.Count < 2
                ? "To compare, select two sessions: click one, then Ctrl+click the other (Cmd+click on a Mac)."
                : "Compare works on two sessions; select just two.");
            return;
        }

        new CompareWindow(selected[0].Session, selected[1].Session).Show(this);
    }

    private TextWizardWindow? _textWizard;

    /// <summary>
    /// Tools > TextWizard (Ctrl+E). Text selected in an inspector (or any
    /// text box) becomes the input.
    /// </summary>
    private void TextWizardMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var selectedText = FocusManager?.GetFocusedElement() switch
        {
            TextBox box => box.SelectedText,
            SelectableTextBlock block => block.SelectedText,
            _ => null,
        };

        if (_textWizard is null)
        {
            _textWizard = new TextWizardWindow(
                _viewModel?.TextWizardTransform,
                _viewModel?.TextWizardEncoding,
                (transform, encoding) => _viewModel?.RememberTextWizardChoice(transform, encoding));
            _textWizard.Closed += (_, _) => _textWizard = null;
            _textWizard.Show(this);
        }
        else
        {
            _textWizard.Activate();
        }

        if (!string.IsNullOrEmpty(selectedText))
        {
            _textWizard.SetInput(selectedText);
        }
    }
}
