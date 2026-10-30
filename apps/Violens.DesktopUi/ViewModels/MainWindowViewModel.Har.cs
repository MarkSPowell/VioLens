using Violens.ProxyCore.Sessions;

namespace Violens.DesktopUi.ViewModels;

/// <summary>File &gt; Import HAR and Export HAR (see <see cref="HarReader"/> and <see cref="HarWriter"/>).</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Whether there's anything to export, for greying out Export HAR.</summary>
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>Imports a .har file; false (with the reason on the status line) if it couldn't be read.</summary>
    public async Task<bool> ImportHarAsync(string path)
    {
        try
        {
            var result = await HarReader.ImportAsync(path, _sessionStore);
            StatusText = result.Skipped.Count == 0
                ? $"Imported {result.Imported} session(s) from {Path.GetFileName(path)}."
                : $"Imported {result.Imported} session(s) from {Path.GetFileName(path)} " +
                  $"({result.Skipped.Count} skipped -- see the console for details).";

            foreach (var reason in result.Skipped)
            {
                Console.WriteLine($"[import] Skipped {reason}");
            }

            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to import {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Saves every session as HAR; false (with the reason on the status line) if it couldn't be written.</summary>
    public async Task<bool> ExportHarAsync(string path)
    {
        var sessions = _sessionStore.Snapshot();
        try
        {
            await Task.Run(() => HarWriter.Write(path, sessions));
            StatusText = $"Saved {sessions.Count} session(s) to {path}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to save HAR: {ex.Message}";
            return false;
        }
    }
}
