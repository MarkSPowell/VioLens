// Violens.ProxyHost: runs VioLens's proxy engine for an app that can't load
// it directly, and talks to that app over a named pipe. Started by
// Violens.ProxyHost.Client's ProxyHostClient.LaunchAsync:
//
//   Violens.ProxyHost --pipe <name> --token <token> [--parent <process id>]
//
// It connects to the app's pipe, says hello with the token so the app knows
// it reached the process it started, and carries out the app's commands until
// the pipe closes or the parent process ends. Either way it stops capturing
// and puts the system proxy back before exiting.

using System.Diagnostics;
using System.IO.Pipes;
using Violens.ProxyHost;

string? pipeName = null;
string? token = null;
int? parentId = null;
for (var i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--pipe":
            pipeName = args[i + 1];
            break;
        case "--token":
            token = args[i + 1];
            break;
        case "--parent" when int.TryParse(args[i + 1], out var id):
            parentId = id;
            break;
    }
}

if (pipeName is null || token is null)
{
    Console.Error.WriteLine("Violens.ProxyHost is started by an app through Violens.ProxyHost.Client, not by hand.");
    Console.Error.WriteLine("Usage: Violens.ProxyHost --pipe <name> --token <token> [--parent <process id>]");
    return 2;
}

// CurrentUserOnly: refuse a pipe of that name made by another account. The
// app takes commands only from this host (the token above proves that to
// it), but this host would otherwise carry out commands from whoever owns
// the pipe: start capturing, trust a root, read and write files.
using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
try
{
    pipe.Connect(30_000);
}
catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Couldn't connect to the app's pipe: {ex.Message}");
    return 3;
}

// If the app ends without closing the pipe cleanly (killed, crashed), close
// it from this side, which ends Run below and puts the system proxy back.
if (parentId is { } parent)
{
    try
    {
        var parentProcess = Process.GetProcessById(parent);
        _ = parentProcess.WaitForExitAsync().ContinueWith(_ => pipe.Dispose(), TaskScheduler.Default);
    }
    catch (ArgumentException)
    {
        // Already gone.
        return 4;
    }
}

new ProxyHostServer(pipe, token).Run();
return 0;
