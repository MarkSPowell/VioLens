using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Violens.CompatShim;
using Violens.DesktopUi.ViewModels;

namespace Violens.DesktopUi;

public partial class App : Application
{
    // No startup work to wait on; just long enough to register as a splash.
    private static readonly TimeSpan SplashDuration = TimeSpan.FromMilliseconds(1600);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            splash.Show();

            // Not awaited: this method has to return before Avalonia's message loop
            // runs and the splash can paint.
            _ = ShowMainWindowAfterSplashAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowMainWindowAfterSplashAsync(
        IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        await Task.Delay(SplashDuration);

        // Declared before the view model, which needs callbacks that own dialogs
        // by the main window. They only run after the user clicks Start, by
        // which time this is set.
        MainWindow? mainWindow = null;

        var viewModel = new MainWindowViewModel(
            // Awaited, never blocked on: the dialog needs the UI thread.
            confirmCertificateTrust: () =>
                new TrustConfirmationWindow().ShowDialog<bool>(mainWindow!),
            // Import/Export via Extension's format picker.
            chooseFormat: (heading, choices, rememberedKey) =>
                new FormatPickerWindow(heading, choices, rememberedKey)
                    .ShowDialog<Violens.Compatibility.Extensions.FormatChoice?>(mainWindow!));

        mainWindow = new MainWindow { DataContext = viewModel };

        // Compatibility-layer hooks that need a window: asking for a file
        // (Utilities.ObtainOpenFilename) and notifying the user
        // (FiddlerApplication.DoNotifyUser).
        var owner = mainWindow;
        CompatShimHost.PromptForOpenFile = (title, filter) => PromptForOpenFile(owner, title, filter);
        CompatShimHost.NotifyUser = (message, title) =>
            Dispatcher.UIThread.Post(() => viewModel.ShowExtensionNotice(title, message));

        // The listener's socket needs an explicit Stop().
        desktop.ShutdownRequested += (_, _) => viewModel.Dispose();

        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();
    }

    /// <summary>
    /// Shows Avalonia's own file picker (native on both Windows and macOS)
    /// for an extension that asked for a file, and blocks the calling thread
    /// until the user picks or cancels. That's safe only because extension
    /// imports run on a background thread (see
    /// MainWindowViewModel.ImportViaExtension); if it's ever called on the UI
    /// thread, blocking would deadlock, so it declines instead.
    /// </summary>
    private static string? PromptForOpenFile(Window owner, string title, string filter)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Console.WriteLine("[Extension] An extension asked for a file from the UI thread; that would deadlock, so no file was chosen.");
            return null;
        }

        var chosen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var fileTypes = Violens.CompatShim.Utilities.ParseFileDialogFilter(filter)
                    .Select(f => new FilePickerFileType(f.Description) { Patterns = f.Patterns })
                    .Append(FilePickerFileTypes.All)
                    .ToList();

                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = fileTypes,
                });

                chosen.SetResult(files.Count > 0 ? files[0].Path.LocalPath : null);
            }
            catch (Exception ex)
            {
                // Never let a picker failure escape an async-void callback on
                // the UI thread; the extension just sees "no file".
                Console.WriteLine($"[Extension] File picker failed: {ex.Message}");
                chosen.TrySetResult(null);
            }
        });

        return chosen.Task.GetAwaiter().GetResult();
    }
}
