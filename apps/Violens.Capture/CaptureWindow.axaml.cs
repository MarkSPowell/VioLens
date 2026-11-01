using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Violens.AppCore.Capture;

namespace Violens.Capture;

/// <summary>
/// VioLens Capture's only window: Start/Stop, Save, and a running count.
/// Closing stops the capture (putting the proxy setting back) and, if
/// ticked, removes the certificate this run trusted.
/// </summary>
public partial class CaptureWindow : Window
{
    private static readonly FilePickerFileType SazFileType = new("Capture (.saz)") { Patterns = ["*.saz"] };

    private readonly CaptureSession _capture = new(message => Console.WriteLine($"[capture] {message}"));
    private readonly DispatcherTimer _countTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private bool _closingAfterCleanup;

    public CaptureWindow()
    {
        InitializeComponent();
        _countTimer.Tick += (_, _) => RefreshCount();
        _countTimer.Start();
        Closing += OnClosing;
    }

    private void RefreshCount()
    {
        var sessions = _capture.Sessions.Snapshot();
        CountText.Text = CaptureSession.Describe(sessions);
        SaveButton.IsEnabled = sessions.Count > 0 && !_busy;
    }

    private async void StartStop_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (_capture.IsCapturing)
            {
                StatusText.Text = "Stopping…";
                await RunAsync(_capture.Stop);
                StatusText.Text = "Stopped. Click Save Capture to keep what was recorded, or Start Capture to record more.";
            }
            else
            {
                var decrypt = DecryptBox.IsChecked == true;
                StatusText.Text = decrypt ? "Starting… your computer may ask you to confirm the certificate." : "Starting…";
                StatusText.Text = await RunAsync(() => _capture.Start(decrypt));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't start capturing: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        StartStopButton.IsEnabled = !busy;
        StartStopButton.Content = _capture.IsCapturing ? "Stop Capture" : "Start Capture";
        DecryptBox.IsEnabled = !busy && !_capture.IsCapturing;
        RefreshCount();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Capture",
            SuggestedFileName = $"capture-{DateTime.Now:yyyy-MM-dd-HHmm}.saz",
            DefaultExtension = "saz",
            FileTypeChoices = [SazFileType],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            var count = await Task.Run(() => _capture.Save(path));
            StatusText.Text = $"Saved {count:N0} {(count == 1 ? "request" : "requests")} to {Path.GetFileName(path)}. Send that file to your support contact.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't save the capture: {ex.Message}";
        }
    }

    /// <summary>Puts the proxy setting back, and removes the certificate if asked, before the window goes.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterCleanup)
        {
            return;
        }

        var removeCertificate = RemoveCertificateBox.IsChecked == true && _capture.TrustedRoot;
        if (!_capture.IsCapturing && !removeCertificate)
        {
            return;
        }

        e.Cancel = true;
        SetBusy(true);
        StatusText.Text = "Putting your settings back…";
        try
        {
            await RunAsync(_capture.Stop);
            if (removeCertificate)
            {
                await RunAsync(() => _capture.RemoveCertificate());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[capture] Cleaning up: {ex.Message}");
        }

        _countTimer.Stop();
        _closingAfterCleanup = true;
        Close();
    }

    /// <summary>
    /// macOS and Linux change settings by running commands that may wait on
    /// a password prompt, so the work goes off the UI thread there. On
    /// Windows it stays on it, so the certificate prompt shows in front.
    /// </summary>
    private static Task<T> RunAsync<T>(Func<T> work) => OperatingSystem.IsWindows() ? Task.FromResult(work()) : Task.Run(work);

    private static Task RunAsync(Action work) => RunAsync(() =>
    {
        work();
        return true;
    });
}
