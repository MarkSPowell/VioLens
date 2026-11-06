using Violens.ProxyCore.SystemProxy;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>
/// Enable, Disable and RecoverFromCrash change real desktop settings, so
/// only the settings written and the parsing of what's read are tested.
/// </summary>
public class LinuxSystemProxyTests
{
    private static Dictionary<string, string> Gnome(string mode, string url = "''", string httpHost = "''", string httpPort = "0",
        string httpsHost = "''", string httpsPort = "0") => new()
    {
        ["org.gnome.system.proxy mode"] = mode,
        ["org.gnome.system.proxy autoconfig-url"] = url,
        ["org.gnome.system.proxy.http host"] = httpHost,
        ["org.gnome.system.proxy.http port"] = httpPort,
        ["org.gnome.system.proxy.https host"] = httpsHost,
        ["org.gnome.system.proxy.https port"] = httpsPort,
    };

    [Fact]
    public void GnomeSettingsPointAtVioLensAndSetModeLast()
    {
        var settings = LinuxSystemProxy.BuildGnomeEnableSettings(8888);

        Assert.Equal(
            [
                ("org.gnome.system.proxy.http", "host", "'127.0.0.1'"),
                ("org.gnome.system.proxy.http", "port", "8888"),
                ("org.gnome.system.proxy.https", "host", "'127.0.0.1'"),
                ("org.gnome.system.proxy.https", "port", "8888"),
                ("org.gnome.system.proxy", "mode", "'manual'"),
            ],
            settings);
    }

    [Fact]
    public void KdeSettingsPointAtVioLensAndSetTheTypeLast()
    {
        Assert.Equal(
            [("httpProxy", "http://127.0.0.1 8888"), ("httpsProxy", "http://127.0.0.1 8888"), ("ProxyType", "1")],
            LinuxSystemProxy.BuildKdeEnableSettings(8888));
        Assert.Equal(
            ["--file", "kioslaverc", "--group", "Proxy Settings", "--key", "ProxyType", "1"],
            LinuxSystemProxy.BuildKdeWriteArguments("ProxyType", "1"));
        Assert.Equal(
            ["--file", "kioslaverc", "--group", "Proxy Settings", "--key", "httpProxy", "--delete"],
            LinuxSystemProxy.BuildKdeDeleteArguments("httpProxy"));
    }

    [Theory]
    [InlineData("'manual'", "manual")]
    [InlineData("'it\\'s'", "it's")]
    [InlineData("8080", "8080")]
    [InlineData("uint32 8080", "8080")]
    [InlineData("''", "")]
    public void UnquotesGVariantText(string printed, string expected)
    {
        Assert.Equal(expected, LinuxSystemProxy.UnquoteGVariant(printed));
    }

    [Fact]
    public void GnomeManualProxyPrefersHttps()
    {
        var detected = LinuxSystemProxy.FromGnome(
            Gnome("'manual'", httpHost: "'web.corp'", httpPort: "3128", httpsHost: "'secure.corp'", httpsPort: "8443"), ownPort: 8888);

        Assert.Equal("secure.corp", detected!.Proxy!.Host);
        Assert.Equal(8443, detected.Proxy.Port);
    }

    [Fact]
    public void GnomeManualProxyPointingAtVioLensIsIgnored()
    {
        var detected = LinuxSystemProxy.FromGnome(
            Gnome("'manual'", httpHost: "'127.0.0.1'", httpPort: "8888", httpsHost: "'127.0.0.1'", httpsPort: "8888"), ownPort: 8888);

        Assert.Null(detected);
    }

    [Fact]
    public void GnomeAutomaticUsesTheScriptOrDiscovery()
    {
        var script = LinuxSystemProxy.FromGnome(Gnome("'auto'", url: "'http://wpad.corp/proxy.pac'"), ownPort: 8888)!;
        Assert.Equal("http://wpad.corp/proxy.pac", script.ScriptUrl);
        Assert.False(script.AutoDetect);

        var discovery = LinuxSystemProxy.FromGnome(Gnome("'auto'"), ownPort: 8888)!;
        Assert.True(discovery.AutoDetect);
        Assert.Null(discovery.ScriptUrl);
    }

    [Fact]
    public void GnomeNoneMeansDirect()
    {
        Assert.Null(LinuxSystemProxy.FromGnome(Gnome("'none'"), ownPort: 8888));
    }

    [Theory]
    [InlineData("http://proxy.corp 3128", "proxy.corp", 3128)]
    [InlineData("http://proxy.corp:3128", "proxy.corp", 3128)]
    [InlineData("proxy.corp:3128", "proxy.corp", 3128)]
    [InlineData("http://[fd00::1] 8080", "fd00::1", 8080)]
    public void ParsesKdeProxyValues(string value, string host, int port)
    {
        var proxy = LinuxSystemProxy.ParseKdeProxy(value);

        Assert.Equal(host, proxy!.Host);
        Assert.Equal(port, proxy.Port);
    }

    [Fact]
    public void KdeProxyTypes()
    {
        var manual = LinuxSystemProxy.FromKde(new Dictionary<string, string> { ["ProxyType"] = "1", ["httpProxy"] = "http://proxy.corp 3128" }, 8888);
        Assert.Equal("proxy.corp", manual!.Proxy!.Host);

        var script = LinuxSystemProxy.FromKde(
            new Dictionary<string, string> { ["ProxyType"] = "2", ["Proxy Config Script"] = "http://wpad.corp/proxy.pac" }, 8888)!;
        Assert.Equal("http://wpad.corp/proxy.pac", script.ScriptUrl);

        var discovery = LinuxSystemProxy.FromKde(new Dictionary<string, string> { ["ProxyType"] = "3" }, 8888)!;
        Assert.True(discovery.AutoDetect);

        Assert.Null(LinuxSystemProxy.FromKde(new Dictionary<string, string> { ["ProxyType"] = "0" }, 8888));
        Assert.Null(LinuxSystemProxy.FromKde(
            new Dictionary<string, string> { ["ProxyType"] = "1", ["httpProxy"] = "http://127.0.0.1 8888" }, 8888));
    }

    [Fact]
    public void FallsBackToTheEnvironment()
    {
        var variables = new Dictionary<string, string> { ["http_proxy"] = "http://user@proxy.corp:3128/" };

        var detected = LinuxSystemProxy.FromEnvironment(name => variables.GetValueOrDefault(name), ownPort: 8888);

        Assert.Equal("proxy.corp", detected!.Proxy!.Host);
        Assert.Equal(3128, detected.Proxy.Port);
        Assert.Equal("http_proxy", detected.Source);
        Assert.Null(LinuxSystemProxy.FromEnvironment(_ => null, ownPort: 8888));
        Assert.Null(LinuxSystemProxy.FromEnvironment(name => name == "https_proxy" ? "127.0.0.1:8888" : null, ownPort: 8888));
    }
}
