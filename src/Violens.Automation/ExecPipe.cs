using System.IO.Pipes;
using System.Text;

namespace Violens.Automation;

/// <summary>What VioLens said about a command: whether it worked, and its status-line message.</summary>
public sealed record ExecReply(bool Succeeded, string Message);

/// <summary>
/// The pipe <c>violens-exec</c> uses to hand QuickExec commands to a running
/// VioLens, as Fiddler Classic's ExecAction.exe did. One command per
/// connection, as three lines of UTF-8 text:
/// <code>
/// violens-exec 1
/// &lt;the client's current folder, for relative paths&gt;
/// &lt;the command&gt;
/// </code>
/// and one line back: <c>ok</c> or <c>error</c>, a tab, and the message.
///
/// Both ends use <see cref="PipeOptions.CurrentUserOnly"/>: the server takes
/// connections only from the same account, and the client refuses a pipe
/// another account made. Nothing listens on the network.
/// </summary>
public static class ExecPipe
{
    public const string Greeting = "violens-exec 1";

    private const int MaxLineLength = 32 * 1024;

    /// <summary>
    /// The pipe's name, which includes the user name: on Windows pipe names
    /// are shared by every account, and on Linux a pipe is a socket file in
    /// <c>/tmp</c>, which is too. Without it a second account's VioLens
    /// couldn't listen at all.
    /// </summary>
    public static string PipeName =>
        "violens-exec-" + Environment.UserName.Replace('\\', '_').Replace('/', '_');

    /// <summary>
    /// Sends <paramref name="command"/> to the running VioLens and waits for
    /// its reply. Throws <see cref="TimeoutException"/> when nothing answers
    /// within <paramref name="connectTimeout"/> (VioLens isn't running, or
    /// it's busy with another command), and <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the connection fails.
    /// </summary>
    /// <param name="pipeName">Tests only: another pipe than <see cref="PipeName"/>.</param>
    public static async Task<ExecReply> SendAsync(
        string command, string currentDirectory, TimeSpan connectTimeout, CancellationToken cancellationToken = default, string? pipeName = null)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeout, cancellationToken);

        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        await writer.WriteLineAsync(Greeting);
        await writer.WriteLineAsync(OneLine(currentDirectory));
        await writer.WriteLineAsync(OneLine(command));
        await writer.FlushAsync(cancellationToken);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var reply = await reader.ReadLineAsync(cancellationToken)
            ?? throw new IOException("VioLens closed the connection without answering.");
        return ParseReply(reply);
    }

    /// <summary>
    /// Serves commands until <paramref name="cancellationToken"/> is
    /// cancelled, one connection at a time, handing each to
    /// <paramref name="handle"/> (current folder, command). Returns false at
    /// once if the pipe can't be made, usually because another VioLens of
    /// this account already has it.
    /// </summary>
    /// <param name="pipeName">Tests only: another pipe than <see cref="PipeName"/>.</param>
    public static async Task<bool> ServeAsync(
        Func<string, string, Task<ExecReply>> handle, Action<string>? log, CancellationToken cancellationToken, string? pipeName = null)
    {
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName ?? PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException ex) when (first)
            {
                log?.Invoke($"Not taking violens-exec commands: the pipe is in use, probably by another VioLens ({ex.Message}).");
                return false;
            }

            first = false;
            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    await HandleOneAsync(pipe, handle, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return true;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // One client going wrong mustn't stop the next.
                    log?.Invoke($"A violens-exec command failed: {ex.Message}");
                }
            }
        }

        return true;
    }

    private static async Task HandleOneAsync(Stream pipe, Func<string, string, Task<ExecReply>> handle, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        if (await ReadLimitedLineAsync(reader, cancellationToken) != Greeting)
        {
            throw new InvalidDataException("not a violens-exec client, or a different version of it.");
        }

        var currentDirectory = await ReadLimitedLineAsync(reader, cancellationToken) ?? string.Empty;
        var command = await ReadLimitedLineAsync(reader, cancellationToken) ?? string.Empty;

        ExecReply reply;
        try
        {
            reply = await handle(currentDirectory, command);
        }
        catch (Exception ex)
        {
            reply = new ExecReply(false, ex.Message);
        }

        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        await writer.WriteLineAsync(FormatReply(reply));
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<string?> ReadLimitedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        return line is { Length: > MaxLineLength } ? throw new InvalidDataException("a line was too long.") : line;
    }

    internal static string FormatReply(ExecReply reply) => (reply.Succeeded ? "ok" : "error") + "\t" + OneLine(reply.Message);

    internal static ExecReply ParseReply(string line)
    {
        var tab = line.IndexOf('\t');
        var status = tab < 0 ? line : line[..tab];
        var message = tab < 0 ? string.Empty : line[(tab + 1)..];
        return status switch
        {
            "ok" => new ExecReply(true, message),
            "error" => new ExecReply(false, message),
            _ => throw new InvalidDataException($"VioLens sent a reply this violens-exec doesn't understand: {line}"),
        };
    }

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
}
