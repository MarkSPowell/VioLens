// violens-exec: sends one QuickExec command to the running VioLens and
// prints its answer, like Fiddler Classic's ExecAction.exe.
//
//   violens-exec start
//   violens-exec "save capture.saz"
//   violens-exec stop
//
// Exit codes: 0 done, 1 VioLens reported a failure, 2 usage, 3 couldn't
// reach VioLens (not running, busy, or commands from violens-exec turned off).

using Violens.Automation;

var timeout = TimeSpan.FromSeconds(10);
var words = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--timeout" && i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds) && seconds > 0)
    {
        timeout = TimeSpan.FromSeconds(seconds);
        i++;
    }
    else if (args[i] is "-h" or "--help" or "/?")
    {
        words.Clear();
        break;
    }
    else
    {
        words.Add(args[i]);
    }
}

if (words.Count == 0)
{
    Console.Error.WriteLine(
        """
        Usage: violens-exec [--timeout <seconds>] <QuickExec command>

        Sends a command to the running VioLens, as if typed into QuickExec, and
        prints what it did. For example:

          violens-exec start
          violens-exec "filter host:example.com"
          violens-exec save capture.saz
          violens-exec stop

        Relative paths are taken from the current folder. --timeout is how long
        to wait for VioLens to accept the command (10 seconds by default).
        Exit codes: 0 done, 1 VioLens reported a failure, 2 usage, 3 couldn't
        reach VioLens. See the User Guide's "Driving VioLens from scripts".
        """);
    return 2;
}

// The arguments as one command line; one with spaces is quoted again, so
// violens-exec save "my capture.saz" reaches VioLens as typed.
var command = string.Join(' ', words.Select(w => w.Contains(' ') && !w.StartsWith('"') ? $"\"{w}\"" : w));

try
{
    var reply = await ExecPipe.SendAsync(command, Environment.CurrentDirectory, timeout);
    (reply.Succeeded ? Console.Out : Console.Error).WriteLine(reply.Message);
    return reply.Succeeded ? 0 : 1;
}
catch (TimeoutException)
{
    Console.Error.WriteLine(
        $"VioLens didn't answer within {timeout.TotalSeconds:0} seconds. Is it running, with Tools > Accept Commands from violens-exec ticked?");
    return 3;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"Couldn't send the command to VioLens: {ex.Message}");
    return 3;
}
