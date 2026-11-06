using System.Net;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Proxy.AutoConfig;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Running PAC scripts (<see cref="PacScript"/>) and reading their answers (<see cref="PacResult"/>).</summary>
public class PacScriptTests
{
    /// <summary>A network with a few known names, for scripts that look things up.</summary>
    internal sealed class StubPacHost : IPacHost
    {
        public Dictionary<string, IPAddress[]> Names { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["intranet.corp.example"] = [IPAddress.Parse("10.1.2.3")],
            ["www.example.com"] = [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:2800:220:1::1")],
        };

        public List<string> Logged { get; } = [];

        public IReadOnlyList<IPAddress> Resolve(string host) =>
            IPAddress.TryParse(host, out var literal) ? [literal] : Names.TryGetValue(host, out var found) ? found : [];

        public IReadOnlyList<IPAddress> LocalAddresses() => [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fd00::20")];

        public void Log(string message) => Logged.Add(message);
    }

    private static string Run(string expression, string url = "http://www.example.com/a", string host = "www.example.com") =>
        new PacScript($"function FindProxyForURL(url, host) {{ return String({expression}); }}", new StubPacHost())
            .FindProxyForUrl(url, host);

    [Fact]
    public void RunsFindProxyForUrl()
    {
        var script = new PacScript(
            """
            function FindProxyForURL(url, host) {
              if (isPlainHostName(host) || dnsDomainIs(host, ".corp.example")) return "DIRECT";
              if (shExpMatch(url, "http://*/downloads/*")) return "PROXY downloads.proxy:3128";
              return "PROXY proxy.corp.example:8080; DIRECT";
            }
            """,
            new StubPacHost());

        Assert.Equal("DIRECT", script.FindProxyForUrl("http://intranet/", "intranet"));
        Assert.Equal("DIRECT", script.FindProxyForUrl("https://intranet.corp.example/", "intranet.corp.example"));
        Assert.Equal("PROXY downloads.proxy:3128", script.FindProxyForUrl("http://cdn.example.com/downloads/x.zip", "cdn.example.com"));
        Assert.Equal("PROXY proxy.corp.example:8080; DIRECT", script.FindProxyForUrl("https://www.example.com/", "www.example.com"));
    }

    [Theory]
    [InlineData("isPlainHostName('intranet')", "true")]
    [InlineData("isPlainHostName('www.example.com')", "false")]
    [InlineData("dnsDomainIs('www.Example.com', '.example.com')", "true")]
    [InlineData("dnsDomainIs('www.example.org', '.example.com')", "false")]
    [InlineData("localHostOrDomainIs('www', 'www.example.com')", "true")]
    [InlineData("localHostOrDomainIs('www.example.com', 'www.example.com')", "true")]
    [InlineData("localHostOrDomainIs('www.example.org', 'www.example.com')", "false")]
    [InlineData("dnsDomainLevels('a.b.example.com')", "3")]
    [InlineData("shExpMatch('http://a.example.com/x', '*.example.com/*')", "true")]
    [InlineData("shExpMatch('abc', 'a?c')", "true")]
    [InlineData("shExpMatch('abc', 'a.c')", "false")]
    [InlineData("dnsResolve('intranet.corp.example')", "10.1.2.3")]
    [InlineData("dnsResolve('www.example.com')", "93.184.216.34")]
    [InlineData("dnsResolve('nowhere.invalid')", "null")]
    [InlineData("isResolvable('nowhere.invalid')", "false")]
    [InlineData("isResolvable('www.example.com')", "true")]
    [InlineData("isInNet('intranet.corp.example', '10.0.0.0', '255.0.0.0')", "true")]
    [InlineData("isInNet('10.1.2.3', '10.1.3.0', '255.255.255.0')", "false")]
    [InlineData("isInNet('nowhere.invalid', '10.0.0.0', '255.0.0.0')", "false")]
    [InlineData("myIpAddress()", "192.168.1.20")]
    [InlineData("myIpAddressEx()", "192.168.1.20;fd00::20")]
    [InlineData("dnsResolveEx('www.example.com')", "93.184.216.34;2606:2800:220:1::1")]
    [InlineData("isInNetEx('2606:2800:220:1::1', '2606:2800::/32')", "true")]
    [InlineData("isInNetEx('10.1.2.3', '10.0.0.0/8')", "true")]
    [InlineData("isInNetEx('11.1.2.3', '10.0.0.0/8')", "false")]
    [InlineData("convert_addr('10.0.0.1')", "167772161")]
    [InlineData("weekdayRange('SUN', 'SAT')", "true")]
    [InlineData("weekdayRange('MON', 'SUN', 'GMT')", "true")]
    [InlineData("dateRange('JAN', 'DEC')", "true")]
    [InlineData("dateRange(1, 31)", "true")]
    [InlineData("dateRange(1995, 2999)", "true")]
    [InlineData("dateRange(1, 'JAN', 1995, 31, 'DEC', 2999)", "true")]
    [InlineData("dateRange(1990, 1991)", "false")]
    [InlineData("timeRange(0, 23)", "true")]
    [InlineData("timeRange(0, 0, 0, 23, 59, 59, 'GMT')", "true")]
    [InlineData("weekdayRange('NOTADAY')", "false")]
    public void HasTheStandardHelpers(string expression, string expected)
    {
        Assert.Equal(expected, Run(expression));
    }

    [Fact]
    public void PrefersFindProxyForUrlEx()
    {
        var script = new PacScript(
            """
            function FindProxyForURL(url, host) { return "PROXY old:1"; }
            function FindProxyForURLEx(url, host) { return "PROXY new:2"; }
            """,
            new StubPacHost());

        Assert.Equal("PROXY new:2", script.FindProxyForUrl("http://a/", "a"));
    }

    [Fact]
    public void AlertGoesToTheLog()
    {
        var host = new StubPacHost();
        var script = new PacScript("function FindProxyForURL(url, host) { alert('checking ' + host); return 'DIRECT'; }", host);

        script.FindProxyForUrl("http://a/", "a");

        Assert.Equal(["checking a"], host.Logged);
    }

    [Fact]
    public void AScriptCantReachDotNet()
    {
        Assert.Equal("undefined undefined", Run("typeof System + ' ' + typeof importNamespace"));
    }

    [Theory]
    [InlineData("function NotIt() {}", "doesn't define FindProxyForURL")]
    [InlineData("function FindProxyForURL(url, host) {", "couldn't be run")]
    public void RefusesAScriptItCantUse(string source, string expected)
    {
        var ex = Assert.Throws<PacScriptException>(() => new PacScript(source, new StubPacHost()));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void AScriptThatFailsReportsIt()
    {
        var script = new PacScript("function FindProxyForURL(url, host) { throw new Error('boom'); }", new StubPacHost());

        var ex = Assert.Throws<PacScriptException>(() => script.FindProxyForUrl("http://a/", "a"));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void AScriptThatNeverReturnsIsStopped()
    {
        var script = new PacScript("function FindProxyForURL(url, host) { while (true) {} }", new StubPacHost(), TimeSpan.FromMilliseconds(200));

        Assert.Throws<PacScriptException>(() => script.FindProxyForUrl("http://a/", "a"));
    }

    [Fact]
    public void AScriptThatEatsMemoryIsStopped()
    {
        var script = new PacScript(
            "function FindProxyForURL(url, host) { var all = []; while (true) { all.push(new Array(10000).join('x')); } }",
            new StubPacHost(),
            TimeSpan.FromSeconds(60));

        var thrown = Assert.Throws<PacScriptException>(() => script.FindProxyForUrl("http://a/", "a"));
        Assert.IsType<Jint.Runtime.MemoryLimitExceededException>(thrown.InnerException);
    }

    [Fact]
    public void ReadsTheRoutesInOrder()
    {
        var routes = PacResult.Parse("PROXY a.proxy:8080; HTTP b.proxy:3128;DIRECT; PROXY a.proxy:8080", out var unsupported);

        Assert.Equal(["a.proxy:8080", "b.proxy:3128", null], routes.Select(r => r?.ToString()));
        Assert.Empty(unsupported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("SOCKS5 s:1080")]
    [InlineData("PROXY not-a-proxy")]
    public void AnswersWithNothingUsableMeanDirect(string? answer)
    {
        Assert.Equal([null], PacResult.Parse(answer, out _));
    }

    [Fact]
    public void ListsWhatItSkipped()
    {
        var routes = PacResult.Parse("SOCKS s:1080; HTTPS secure.proxy:443; PROXY p:8080", out var unsupported);

        Assert.Equal(["p:8080"], routes.Select(r => r?.ToString()));
        Assert.Equal(["SOCKS s:1080", "HTTPS secure.proxy:443"], unsupported);
    }

    [Theory]
    [InlineData("a.b.example.com", new[] { "wpad.a.b.example.com", "wpad.b.example.com", "wpad.example.com" })]
    [InlineData("corp.example.", new[] { "wpad.corp.example" })]
    [InlineData("corp", new string[0])]
    [InlineData(null, new string[0])]
    [InlineData("corp.example.co.uk", new[] { "wpad.corp.example.co.uk", "wpad.example.co.uk" })]
    [InlineData("branch.example.com.au", new[] { "wpad.branch.example.com.au", "wpad.example.com.au" })]
    [InlineData("office.example.ac.jp", new[] { "wpad.office.example.ac.jp", "wpad.example.ac.jp" })]
    [InlineData("example.co.uk", new[] { "wpad.example.co.uk" })]
    [InlineData("a.example.io", new[] { "wpad.a.example.io", "wpad.example.io" })]
    public void WpadLooksInEachParentDomainButNotPastTheLastTwo(string? domain, string[] expected)
    {
        Assert.Equal(expected, WpadDiscovery.CandidateHosts(domain));
    }
}
