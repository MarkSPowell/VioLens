using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Violens.Extensibility.Inspection;

namespace Violens.DesktopUi.Controls;

/// <summary>
/// Shows a <see cref="TreeContent"/> (the Tree tab). The top level opens
/// expanded. Copy takes the selected node's value; Copy Subtree and Send to
/// TextWizard take the node and everything under it as text (see
/// <see cref="TreeText"/>). Expand All and Collapse All work below the
/// selected node, or on the whole tree when nothing is selected.
/// </summary>
public partial class TreeInspectorView : UserControl
{
    public TreeInspectorView()
    {
        InitializeComponent();
    }

    private TreeNode? SelectedNode => Tree.SelectedItem as TreeNode;

    private void Tree_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // Only the roots' containers come through here; nested items belong to their parent.
        if (e.Container is TreeViewItem item)
        {
            item.IsExpanded = true;
        }
    }

    private void ContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasNode = SelectedNode is { Kind: not TreeNodeKind.Omitted };
        CopyMenuItem.IsEnabled = hasNode;
        CopySubtreeMenuItem.IsEnabled = hasNode;
        TextWizardMenuItem.IsEnabled = hasNode;
    }

    private async void Tree_KeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+C, or Cmd+C on a Mac.
        if (e.Key == Key.C && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            await CopyAsync(SelectedNode?.CopyText);
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e) => await CopyAsync(SelectedNode?.CopyText);

    private async void CopySubtree_Click(object? sender, RoutedEventArgs e) =>
        await CopyAsync(SelectedNode is { } node ? TreeText.Write(node) : null);

    private void SendToTextWizard_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } node && TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.OpenTextWizard(TreeText.Write(node));
        }
    }

    private void ExpandAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in TargetItems())
        {
            Tree.ExpandSubTree(item);
        }
    }

    private void CollapseAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in TargetItems())
        {
            Tree.CollapseSubTree(item);
        }
    }

    /// <summary>The selected node's container, or every root's when nothing is selected.</summary>
    private IEnumerable<TreeViewItem> TargetItems()
    {
        if (SelectedNode is { } node && Tree.TreeContainerFromItem(node) is TreeViewItem selected)
        {
            return [selected];
        }

        return Tree.GetRealizedContainers().OfType<TreeViewItem>().Where(item => item.Level == 0).ToList();
    }

    private async Task CopyAsync(string? text)
    {
        if (text is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
