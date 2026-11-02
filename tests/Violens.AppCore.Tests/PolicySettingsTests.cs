using System.Text.Json;
using Violens.AppCore.Preferences;
using Violens.ProxyCore.Preferences;
using Xunit;

namespace Violens.AppCore.Tests;

/// <summary>Machine policy: reading the policy file, and the store that enforces it.</summary>
public class PolicySettingsTests
{
    private static PolicyPreferenceStore Store(PolicySettings policy, PreferenceStore? inner = null, List<string>? log = null) =>
        new(inner ?? PreferenceStore.CreateInMemory(), policy, log is null ? null : log.Add);

    private static PolicySettings Policy(string json) => PolicySettings.Parse(json);

    [Fact]
    public void ParsesLockedAndDefaultSections()
    {
        var policy = Policy("""
            {
              // Comments are allowed, for the administrator's notes.
              "formatVersion": 1,
              "locked": { "violens.proxy.allowremote": false, "violens.proxy.port": 8888, "violens.proxy.upstream": "proxy.corp:3128" },
              "defaults": { "violens.update.check": "False" },
            }
            """);

        Assert.Equal("False", policy.Locked["violens.proxy.allowremote"]);
        Assert.Equal("8888", policy.Locked["VIOLENS.PROXY.PORT"]);
        Assert.Equal("proxy.corp:3128", policy.Locked["violens.proxy.upstream"]);
        Assert.Equal("False", policy.Defaults["violens.update.check"]);
        Assert.False(policy.IsEmpty);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"locked\": [] }")]
    [InlineData("{ \"locked\": { \"a\": null } }")]
    [InlineData("{ \"locked\": { \"a\": { } } }")]
    [InlineData("not json")]
    public void RejectsBadShapes(string json)
    {
        Assert.ThrowsAny<JsonException>(() => Policy(json));
    }

    [Fact]
    public void RejectsInvalidNames()
    {
        Assert.Throws<FormatException>(() => Policy("{ \"locked\": { \"has space\": \"1\" } }"));
    }

    [Fact]
    public void LockedValuesWinAndCantBeChanged()
    {
        var inner = PreferenceStore.CreateInMemory();
        inner.SetBoolPref("violens.proxy.allowremote", true);
        var log = new List<string>();
        var store = Store(Policy("{ \"locked\": { \"violens.proxy.allowremote\": false } }"), inner, log);

        Assert.True(store.IsLocked("Violens.Proxy.AllowRemote"));
        Assert.False(store.GetBoolPref("violens.proxy.allowremote", true));

        store.SetBoolPref("violens.proxy.allowremote", true);
        store.SetPrefs([new("violens.proxy.allowremote", "True"), new("violens.other", "x")]);
        store.RemovePref("violens.proxy.allowremote");

        Assert.False(store.GetBoolPref("violens.proxy.allowremote", true));
        Assert.Equal("x", store["violens.other"]);
        Assert.Equal(3, log.Count);
        Assert.Contains("locked by machine policy", log[0]);
    }

    [Fact]
    public void DefaultsApplyUntilTheUserSetsAValue()
    {
        var store = Store(Policy("{ \"defaults\": { \"violens.update.check\": false } }"));

        Assert.False(store.IsLocked("violens.update.check"));
        Assert.False(store.GetBoolPref("violens.update.check", true));

        store.SetBoolPref("violens.update.check", true);

        Assert.True(store.GetBoolPref("violens.update.check", false));
    }

    [Fact]
    public void GroupPolicyStyleNumbersReadAsSwitches()
    {
        var policy = new PolicySettings(
            new Dictionary<string, string> { ["violens.update.check"] = "0", ["violens.automation.exec"] = "1", ["violens.proxy.port"] = "1" },
            new Dictionary<string, string>());
        var store = Store(policy);

        Assert.False(store.GetBoolPref("violens.update.check", true));
        Assert.True(store.GetBoolPref("violens.automation.exec", false));
        Assert.Equal(1, store.GetInt32Pref("violens.proxy.port", 8888));
    }

    [Fact]
    public void TheUsersOwnOneOrZeroIsNotASwitch()
    {
        // Only policy values get the 1/0 reading; the user's file keeps
        // PreferenceStore's own rules.
        var inner = PreferenceStore.CreateInMemory();
        inner.SetStringPref("violens.update.check", "0");

        Assert.True(Store(PolicySettings.None, inner).GetBoolPref("violens.update.check", true));
    }

    [Fact]
    public void ListsComeFromThePolicyToo()
    {
        var store = Store(Policy("{ \"locked\": { \"violens.config.https.skipdecryption\": \"*.apple.com; login.corp\" } }"));

        Assert.Equal(["*.apple.com", "login.corp"], store.GetListPref("violens.config.https.skipdecryption", []));
    }

    [Fact]
    public void NoPolicyChangesNothing()
    {
        var inner = PreferenceStore.CreateInMemory();
        var store = Store(PolicySettings.None, inner);

        store.SetInt32Pref("violens.proxy.port", 9000);

        Assert.Equal(9000, inner.GetInt32Pref("violens.proxy.port", 0));
        Assert.Null(store["violens.unset"]);
        Assert.True(PolicySettings.None.IsEmpty);
    }

    [Fact]
    public void WatchersSeeTheUsersChanges()
    {
        var store = Store(PolicySettings.None);
        var seen = new List<string>();
        store.AddWatcher("violens.", (_, e) => seen.Add(e.PrefName));

        store.SetStringPref("violens.ui.filter.text", "host:example.test");

        Assert.Equal(["violens.ui.filter.text"], seen);
    }
}
