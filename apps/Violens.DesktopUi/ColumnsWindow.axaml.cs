using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Violens.DesktopUi.ViewModels;

namespace Violens.DesktopUi;

/// <summary>
/// The column customization dialog (Clearinet #32). Returns the columns in
/// their new order with their new visibility, <see cref="ResetRequested"/>
/// for Reset, or null for Cancel.
/// </summary>
public partial class ColumnsWindow : Window
{
    private readonly ObservableCollection<ColumnChoice> _columns = [];

    public ColumnsWindow()
    {
        InitializeComponent();
        ColumnList.ItemsSource = _columns;
        ColumnList.AddHandler(KeyDownEvent, ColumnList_KeyDown, RoutingStrategies.Tunnel);
    }

    public ColumnsWindow(IEnumerable<ColumnChoice> columns)
        : this()
    {
        foreach (var column in columns)
        {
            _columns.Add(column);
        }

        if (_columns.Count > 0)
        {
            ColumnList.SelectedIndex = 0;
        }
    }

    /// <summary>Stands for Reset in the dialog's result.</summary>
    public static IReadOnlyList<ColumnChoice> ResetRequested { get; } = [];

    private void Move(int step)
    {
        var index = ColumnList.SelectedIndex;
        var target = index + step;
        if (index < 0 || target < 0 || target >= _columns.Count)
        {
            return;
        }

        _columns.Move(index, target);
        ColumnList.SelectedIndex = target;
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object? sender, RoutedEventArgs e) => Move(1);

    private void ColumnList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down)
        {
            Move(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var column in _columns)
        {
            column.IsVisible = true;
        }
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e) => Close(ResetRequested);

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close(_columns.ToList());

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
