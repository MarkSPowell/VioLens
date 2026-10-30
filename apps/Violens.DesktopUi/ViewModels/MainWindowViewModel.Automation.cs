using Avalonia.Threading;
using Violens.Automation;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// Commands from violens-exec (see <see cref="ExecPipe"/>): each is run as
/// if typed into QuickExec, on the UI thread, and its status-line message
/// goes back. Tools &gt; Accept Commands from violens-exec turns it off.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private bool _acceptExecCommands;
    private CancellationTokenSource? _automation;

    /// <summary>Whether violens-exec may send commands. On unless turned off; saved.</summary>
    public bool AcceptExecCommands
    {
        get => _acceptExecCommands;
        set
        {
            if (SetField(ref _acceptExecCommands, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.AcceptExecCommands, value);
                if (value)
                {
                    StartAutomation();
                }
                else
                {
                    StopAutomation();
                }
            }
        }
    }

    private void InitializeAutomation()
    {
        _acceptExecCommands = _preferences.GetBoolPref(PreferenceKeys.AcceptExecCommands, true);
        if (_acceptExecCommands)
        {
            StartAutomation();
        }
    }

    private void StartAutomation()
    {
        if (_automation is not null)
        {
            return;
        }

        var stop = new CancellationTokenSource();
        _automation = stop;
        _ = Task.Run(() => ExecPipe.ServeAsync(
            (currentDirectory, command) => Dispatcher.UIThread.InvokeAsync(() => RunQuickExecAsync(command, currentDirectory)),
            message => Console.WriteLine($"[violens-exec] {message}"),
            stop.Token));
    }

    private void StopAutomation()
    {
        _automation?.Cancel();
        _automation?.Dispose();
        _automation = null;
    }
}
