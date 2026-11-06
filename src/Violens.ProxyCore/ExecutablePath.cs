namespace Violens.ProxyCore;

/// <summary>
/// The full path of a helper program VioLens runs (certutil, gsettings,
/// security, networksetup, ...), so it's started by that path rather than
/// by name. Given a bare name, .NET looks in the app's own folder and the
/// current folder before PATH, so a program of the same name left in either
/// would run in the helper's place.
/// </summary>
internal static class ExecutablePath
{
    // macOS's own tools, which are always here (and protected by the
    // system), so PATH isn't consulted for them at all.
    private static readonly Dictionary<string, string> MacOSSystemTools = new(StringComparer.Ordinal)
    {
        ["security"] = "/usr/bin/security",
        ["networksetup"] = "/usr/sbin/networksetup",
    };

    /// <summary>
    /// The program's full path: a known system location, or the first PATH
    /// folder that has it. Only absolute PATH entries count (an empty or
    /// relative one would mean the current folder). Null if it isn't found.
    /// </summary>
    public static string? Find(string program) =>
        Find(program, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

    /// <summary>Like <see cref="Find(string)"/>, with the search given. Internal for tests.</summary>
    internal static string? Find(string program, string? path, bool isMacOS, bool isWindows)
    {
        if (Path.IsPathFullyQualified(program))
        {
            return program;
        }

        if (isMacOS && MacOSSystemTools.TryGetValue(program, out var systemTool))
        {
            return systemTool;
        }

        var names = isWindows && !Path.HasExtension(program) ? new[] { program + ".exe", program } : [program];
        foreach (var folder in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = folder.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = Path.Combine(trimmed, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
