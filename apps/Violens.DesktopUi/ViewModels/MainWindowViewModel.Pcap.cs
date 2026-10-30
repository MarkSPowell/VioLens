using Violens.ProxyCore.Sessions;

namespace Violens.DesktopUi.ViewModels;

/// <summary>File &gt; Import Packet Capture (see <see cref="PcapReader"/>).</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Imports a .pcap or .pcapng file's plain HTTP traffic; false (with the
    /// reason on the status line) if it couldn't be read. What was skipped
    /// goes to the console, and the status line says why when nothing was found.
    /// </summary>
    public async Task<bool> ImportPcapAsync(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var result = await PcapReader.ImportAsync(path, _sessionStore);
            foreach (var note in result.Notes)
            {
                Console.WriteLine($"[import] {name}: {note.Text}: {note.Count}");
            }

            var tls = result.Notes.FirstOrDefault(note => note.Text == PcapReader.TlsSkippedNote)?.Count ?? 0;
            StatusText = (result.Imported, tls) switch
            {
                (0, > 0) => $"No plain HTTP found in {name}: its {tls} encrypted (HTTPS) connection(s) can't be read from a packet capture.",
                (0, _) => $"No HTTP/1.x traffic found in {name} ({result.Connections} TCP connection(s); see the console for what was skipped).",
                (_, > 0) => $"Imported {result.Imported} session(s) from {name}; {tls} encrypted (HTTPS) connection(s) skipped.",
                _ when result.Notes.Count > 0 => $"Imported {result.Imported} session(s) from {name} (some traffic skipped -- see the console for details).",
                _ => $"Imported {result.Imported} session(s) from {name}.",
            };
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to import {name}: {ex.Message}";
            return false;
        }
    }
}
