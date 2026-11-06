using Violens.ProxyCore.Certificates;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>
/// Firefox on macOS keeps its own certificate store, so VioLens turns on its
/// setting to also trust what macOS trusts (<see cref="FirefoxEnterpriseRoots"/>).
/// Run against a made-up home folder, never the real one.
/// </summary>
public sealed class FirefoxEnterpriseRootsTests : IDisposable
{
    private const string Setting = "user_pref(\"security.enterprise_roots.enabled\", true);";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "violens-firefox-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private string Profile(string name)
    {
        var profile = Path.Combine(_home, "Library", "Application Support", "Firefox", "Profiles", name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "prefs.js"), "// Mozilla User Preferences\n");
        return profile;
    }

    private static string UserJs(string profile) =>
        File.Exists(Path.Combine(profile, "user.js")) ? File.ReadAllText(Path.Combine(profile, "user.js")) : string.Empty;

    [Fact]
    public void FindsEveryProfileThatHasBeenUsed()
    {
        var first = Profile("abc123.default-release");
        var second = Profile("xyz789.work");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(first)!, "not-a-profile"));

        Assert.Equal([first, second], FirefoxEnterpriseRoots.FindMacOSProfiles(_home));
    }

    [Fact]
    public void FindsNothingWithoutFirefox()
    {
        Assert.Empty(FirefoxEnterpriseRoots.FindMacOSProfiles(_home));
    }

    [Fact]
    public void TurnsTheSettingOnInEachProfile()
    {
        var first = Profile("abc123.default-release");
        var second = Profile("xyz789.work");
        File.WriteAllText(Path.Combine(second, "user.js"), "user_pref(\"browser.startup.page\", 3);\n");

        Assert.Equal(2, FirefoxEnterpriseRoots.Enable(_home));

        Assert.Contains(Setting, UserJs(first));
        Assert.Contains(Setting, UserJs(second));
        Assert.Contains("user_pref(\"browser.startup.page\", 3);", UserJs(second));
    }

    [Fact]
    public void TurningItOnTwiceAddsItOnce()
    {
        var profile = Profile("abc123.default-release");

        FirefoxEnterpriseRoots.Enable(_home);
        FirefoxEnterpriseRoots.Enable(_home);

        Assert.Single(UserJs(profile).Split('\n'), line => line.Trim() == Setting);
    }

    [Theory]
    [InlineData("user_pref(\"security.enterprise_roots.enabled\", false);\n")]
    [InlineData("user_pref('security.enterprise_roots.enabled', true);\n")]
    public void LeavesTheSettingAloneWhereTheUserSetItThemselves(string userJs) =>
        Assert.Equal(userJs, FirefoxEnterpriseRoots.WithSetting(userJs));

    [Fact]
    public void TurningItOffRemovesOnlyWhatVioLensAdded()
    {
        var mine = "user_pref(\"browser.startup.page\", 3);\n";
        var withSetting = FirefoxEnterpriseRoots.WithSetting(mine);
        Assert.NotEqual(mine, withSetting);

        Assert.Equal(mine, FirefoxEnterpriseRoots.WithoutSetting(withSetting));
    }

    [Fact]
    public void TurningItOffLeavesTheUsersOwnSettingAlone()
    {
        var userJs = "user_pref(\"security.enterprise_roots.enabled\", true);\n";

        Assert.Equal(userJs, FirefoxEnterpriseRoots.WithoutSetting(userJs));
    }

    [Fact]
    public void TurningItOffInEveryProfile()
    {
        var profile = Profile("abc123.default-release");
        FirefoxEnterpriseRoots.Enable(_home);

        Assert.Equal(1, FirefoxEnterpriseRoots.Disable(_home));

        Assert.DoesNotContain(Setting, UserJs(profile));
    }
}
