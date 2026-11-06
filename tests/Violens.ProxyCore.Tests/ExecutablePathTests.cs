using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Finding the helper programs VioLens runs by their full path (<see cref="ExecutablePath"/>).</summary>
public sealed class ExecutablePathTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("violens-exe-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void FindsTheProgramInTheFirstPathFolderThatHasIt()
    {
        var first = Directory.CreateDirectory(Path.Combine(_folder, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_folder, "second")).FullName;
        File.WriteAllText(Path.Combine(second, "certutil"), string.Empty);

        var found = ExecutablePath.Find("certutil", string.Join(Path.PathSeparator, first, second), isMacOS: false, isWindows: false);

        Assert.Equal(Path.Combine(second, "certutil"), found);
    }

    [Fact]
    public void IgnoresRelativeAndEmptyPathEntries()
    {
        // "." or an empty entry means the current folder, where anyone who
        // can write there could leave a program of the same name. A relative
        // entry that leads to the program is still skipped.
        File.WriteAllText(Path.Combine(_folder, "gsettings"), string.Empty);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, _folder);
        Assert.False(Path.IsPathFullyQualified(relative));

        Assert.Null(ExecutablePath.Find("gsettings", $".{Path.PathSeparator}{Path.PathSeparator}{relative}", isMacOS: false, isWindows: false));
    }

    [Fact]
    public void IsNullWhenTheProgramIsntInstalled() =>
        Assert.Null(ExecutablePath.Find("no-such-program-violens", _folder, isMacOS: false, isWindows: false));

    [Theory]
    [InlineData("security", "/usr/bin/security")]
    [InlineData("networksetup", "/usr/sbin/networksetup")]
    public void UsesMacOSsOwnToolsWherePathSays(string program, string expected) =>
        Assert.Equal(expected, ExecutablePath.Find(program, _folder, isMacOS: true, isWindows: false));

    [Fact]
    public void FindsAWindowsProgramWithItsExtension()
    {
        File.WriteAllText(Path.Combine(_folder, "dotnet.exe"), string.Empty);

        Assert.Equal(Path.Combine(_folder, "dotnet.exe"), ExecutablePath.Find("dotnet", _folder, isMacOS: false, isWindows: true));
    }
}
