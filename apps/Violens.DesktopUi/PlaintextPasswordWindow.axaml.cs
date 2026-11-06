using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Violens.DesktopUi;

/// <summary>
/// Asks before the proxy password from Tools &gt; Connections goes to a proxy
/// that only offers Basic sign-in, which carries it readably. VioLens can't
/// tell which proxies on a network are meant to get it (one named by an
/// automatic configuration script could be anyone's), so the user decides.
/// </summary>
public partial class PlaintextPasswordWindow : Window
{
    // For the XAML previewer.
    public PlaintextPasswordWindow()
        : this("proxy.example:8080")
    {
    }

    public PlaintextPasswordWindow(string proxy)
    {
        InitializeComponent();
        ExplanationText.Text =
            $"The proxy {proxy} asked for your password in plain text (Basic sign-in). Anyone who can see the " +
            "traffic between this computer and that proxy could read it. Send it only if you know this proxy is " +
            "your network's own. VioLens asks again for this proxy only if you change the password.";
    }

    private void DontSendButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void SendButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
