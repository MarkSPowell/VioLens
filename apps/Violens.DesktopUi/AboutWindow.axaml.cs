using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Violens.AppCore.Diagnostics;

namespace Violens.DesktopUi;

/// <summary>Help > About VioLens: shows which build is running.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = $"Version {DiagnosticReport.DisplayVersion(Assembly.GetExecutingAssembly())}";
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
