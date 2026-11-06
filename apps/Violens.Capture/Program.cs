using Avalonia;
using Violens.ProxyCore;

namespace Violens.Capture;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // VioLens's own identity, so the root certificate, data folder and
        // system-proxy crash recovery are the ones VioLens itself uses.
        ProxyEngine.Configure(ProxyHostIdentity.Default, log: entry => Console.WriteLine($"{entry.Time:HH:mm:ss} {entry}"));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia's designer finds BuildAvaloniaApp() by name.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
