namespace Violens.DesktopUi.ViewModels;

/// <summary>One session-list column in the Customize Columns dialog: its key (see MainWindowViewModel.Columns.cs), title and whether it shows.</summary>
public sealed class ColumnChoice(string key, string title, bool isVisible) : ViewModelBase
{
    private bool _isVisible = isVisible || MainWindowViewModel.IsColumnLocked(key);

    public string Key { get; } = key;

    public string Title { get; } = title;

    /// <summary># and URL, which always show.</summary>
    public bool IsLocked => MainWindowViewModel.IsColumnLocked(Key);

    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value || IsLocked);
    }
}
