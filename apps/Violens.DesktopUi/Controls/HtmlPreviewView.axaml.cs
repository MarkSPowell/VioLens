using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Violens.Extensibility.Inspection;
using Violens.Extensibility.Inspection.Html;

namespace Violens.DesktopUi.Controls;

/// <summary>Shows an <see cref="HtmlContent"/> (the WebView tab), and opens its preview in the browser.</summary>
public partial class HtmlPreviewView : UserControl
{
    public HtmlPreviewView()
    {
        InitializeComponent();
    }

    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HtmlContent content)
        {
            return;
        }

        try
        {
            var path = HtmlPreview.WriteFile(content.Html, content.PageUrl, LoadResourcesBox.IsChecked == true);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            ShowStatus("Opened in your browser. The copy is in the temp folder, and is deleted after a day.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't open the preview: {ex.Message}");
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = true;
    }
}
