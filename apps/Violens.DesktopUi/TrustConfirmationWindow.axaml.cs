using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Violens.DesktopUi;

/// <summary>
/// Asks before trusting the root certificate on macOS and Linux, because
/// <c>security add-trusted-cert</c> and <c>certutil</c> have no prompt of
/// their own (Windows shows one).
/// </summary>
public partial class TrustConfirmationWindow : Window
{
    private const string MacOSExplanation =
        "VioLens needs to add a certificate to your login keychain so it can decrypt this account's HTTPS traffic " +
        "for inspection. It will only be trusted for your user account -- not system-wide -- and can be removed " +
        "later with Tools > Remove Root Certificate, or from Keychain Access.";

    private const string LinuxExplanation =
        "VioLens needs to add a certificate to your browsers' certificate databases (Chrome and other NSS apps, and " +
        "each Firefox profile) so it can decrypt this account's HTTPS traffic for inspection. It will only be trusted " +
        "for your user account -- not system-wide -- and can be removed later with Tools > Remove Root Certificate. " +
        "Restart Firefox afterwards if it's open.";

    public TrustConfirmationWindow()
    {
        InitializeComponent();
        ExplanationText.Text = OperatingSystem.IsLinux() ? LinuxExplanation : MacOSExplanation;
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void InstallButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
