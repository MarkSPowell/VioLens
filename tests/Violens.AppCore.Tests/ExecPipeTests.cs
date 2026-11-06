using Violens.AppCore.QuickExec;
using Violens.Automation;
using Xunit;

namespace Violens.AppCore.Tests;

public class ExecPipeTests
{
    private static string NewPipeName() => "violens-exec-test-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task SendsACommandWithTheFolderAndGetsTheReply()
    {
        var name = NewPipeName();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var received = new List<(string Folder, string Command)>();
        var server = Task.Run(() => ExecPipe.ServeAsync(
            (folder, command) =>
            {
                received.Add((folder, command));
                return Task.FromResult(command == "fail"
                    ? new ExecReply(false, "it went\nwrong")
                    : new ExecReply(true, $"ran {command}"));
            },
            log: null, stop.Token, name));

        var first = await ExecPipe.SendAsync("save \"my capture.saz\"", @"C:\work", TimeSpan.FromSeconds(10), stop.Token, name);
        var second = await ExecPipe.SendAsync("fail", "/tmp", TimeSpan.FromSeconds(10), stop.Token, name);

        Assert.Equal(new ExecReply(true, "ran save \"my capture.saz\""), first);
        Assert.Equal(new ExecReply(false, "it went wrong"), second);
        Assert.Equal([(@"C:\work", "save \"my capture.saz\""), ("/tmp", "fail")], received);

        await stop.CancelAsync();
        Assert.True(await server);
    }

    [Fact]
    public async Task TimesOutWhenNothingIsListening()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ExecPipe.SendAsync("start", ".", TimeSpan.FromMilliseconds(300), pipeName: NewPipeName()));
    }

    [Theory]
    [InlineData("ok\tSaved 3 session(s)", true, "Saved 3 session(s)")]
    [InlineData("error\tFailed to save SAZ: denied", false, "Failed to save SAZ: denied")]
    [InlineData("ok", true, "")]
    public void ReadsReplies(string line, bool succeeded, string message) =>
        Assert.Equal(new ExecReply(succeeded, message), ExecPipe.ParseReply(line));

    [Fact]
    public void RejectsAReplyItDoesntUnderstand() =>
        Assert.Throws<InvalidDataException>(() => ExecPipe.ParseReply("maybe\tsomething"));

    [Theory]
    [InlineData("dump", QuickExecKind.Dump, "", 0)]
    [InlineData("save capture.har", QuickExecKind.Save, "capture.har", 0)]
    [InlineData("save \"my capture.saz\"", QuickExecKind.Save, "my capture.saz", 0)]
    [InlineData("open C:\\caps\\a.saz", QuickExecKind.Open, "C:\\caps\\a.saz", 0)]
    [InlineData("filter host:example.com status:4xx", QuickExecKind.Filter, "host:example.com status:4xx", 0)]
    [InlineData("filter", QuickExecKind.Filter, "", 0)]
    [InlineData("keep 1000", QuickExecKind.Keep, "1000", 1000)]
    [InlineData("keep all", QuickExecKind.Keep, "all", 0)]
    [InlineData("keep lots", QuickExecKind.Other, "lots", 0)]
    public void ParsesTheAutomationCommands(string line, QuickExecKind kind, string argument, long number)
    {
        var command = QuickExecParser.Parse(line)!;
        Assert.Equal(kind, command.Kind);
        Assert.Equal(argument, command.Argument);
        Assert.Equal(number, command.Number);
    }
}
