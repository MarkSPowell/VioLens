using System.Text.RegularExpressions;

namespace Violens.ProxyCore.Certificates;

/// <summary>
/// Firefox keeps its own certificate store rather than the macOS keychain,
/// so trusting VioLens's root in the keychain (see
/// <see cref="MacOSCertificateTrust"/>) isn't enough for it. Its
/// <c>security.enterprise_roots.enabled</c> setting makes it trust what
/// macOS trusts too, so VioLens turns that on in each profile's
/// <c>user.js</c>, which Firefox applies at every start (over the profile's
/// own settings) -- so it takes effect once Firefox restarts. The lines
/// VioLens adds are marked, and removing the root removes only those; a
/// <c>user.js</c> that already sets the setting either way is left alone.
/// </summary>
internal static partial class FirefoxEnterpriseRoots
{
    private const string Marker = "// Added by VioLens: trust the certificates macOS trusts. Removed with VioLens's root certificate.";
    private const string Setting = "user_pref(\"security.enterprise_roots.enabled\", true);";

    /// <summary>
    /// The Firefox profiles under <paramref name="home"/> that have been used
    /// (they have a <c>prefs.js</c>). Profiles kept elsewhere, through
    /// <c>profiles.ini</c>, aren't found.
    /// </summary>
    internal static IReadOnlyList<string> FindMacOSProfiles(string home)
    {
        var folder = Path.Combine(home, "Library", "Application Support", "Firefox", "Profiles");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(folder)
            .Where(profile => File.Exists(Path.Combine(profile, "prefs.js")))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary><paramref name="userJs"/> with VioLens's lines added, unless it already sets the setting.</summary>
    internal static string WithSetting(string userJs)
    {
        if (SettingRegex().IsMatch(userJs))
        {
            return userJs;
        }

        var separator = userJs.Length == 0 || userJs.EndsWith('\n') ? string.Empty : "\n";
        return $"{userJs}{separator}{Marker}\n{Setting}\n";
    }

    /// <summary><paramref name="userJs"/> without VioLens's lines; anything else, the user's own setting included, stays.</summary>
    internal static string WithoutSetting(string userJs) =>
        userJs.Replace($"{Marker}\n{Setting}\n", string.Empty, StringComparison.Ordinal)
              .Replace($"{Marker}\r\n{Setting}\r\n", string.Empty, StringComparison.Ordinal);

    /// <summary>Turns the setting on in every profile; returns how many were changed or already had it. Never throws.</summary>
    public static int Enable(string home, Action<string>? log = null) =>
        Apply(home, WithSetting, "turned on", log);

    /// <summary>Takes VioLens's lines out of every profile; returns how many had them. Never throws.</summary>
    public static int Disable(string home, Action<string>? log = null) =>
        Apply(home, WithoutSetting, "turned off", log, countUnchanged: false);

    private static int Apply(string home, Func<string, string> change, string verb, Action<string>? log, bool countUnchanged = true)
    {
        var count = 0;
        foreach (var profile in FindMacOSProfiles(home))
        {
            var path = Path.Combine(profile, "user.js");
            try
            {
                var before = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                var after = change(before);
                if (after != before)
                {
                    File.WriteAllText(path, after);
                    count++;
                    log?.Invoke($"Firefox profile {Path.GetFileName(profile)}: {verb} trusting the certificates macOS trusts. Restart Firefox for it to take effect.");
                }
                else if (countUnchanged)
                {
                    count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Couldn't change Firefox profile {Path.GetFileName(profile)}: {ex.Message}");
            }
        }

        return count;
    }

    // Any user_pref line for the setting, either quote style, true or false.
    [GeneratedRegex(@"user_pref\(\s*[""']security\.enterprise_roots\.enabled[""']", RegexOptions.None, 1000)]
    private static partial Regex SettingRegex();
}
