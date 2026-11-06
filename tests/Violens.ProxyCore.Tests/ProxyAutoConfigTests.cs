using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Proxy.AutoConfig;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Automatic proxy configuration: loading a PAC script, and the listener choosing routes with it.</summary>
public class ProxyAutoConfigTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static string Routes(IReadOnlyList<UpstreamProxy?> routes) =>
        string.Join("; ", routes.Select(r => r?.ToString() ?? "DIRECT"));

    [Fact]
    public async Task UsesTheScriptsAnswer()
    {
        using var autoConfig = ProxyAutoConfig.FromScript(
            "function FindProxyForURL(url, host) { return dnsDomainIs(host, '.corp.example') ? 'DIRECT' : 'PROXY proxy.corp.example:8080; DIRECT'; }",
            new PacScriptTests.StubPacHost());

        Assert.Equal("proxy.corp.example:8080; DIRECT", Routes(await autoConfig.GetRoutesAsync("https://www.example.com/", "www.example.com", CancellationToken.None)));
        Assert.Equal("DIRECT", Routes(await autoConfig.GetRoutesAsync("https://intranet.corp.example/", "intranet.corp.example", CancellationToken.None)));
        Assert.Contains("using the automatic proxy configuration script", autoConfig.Description);
    }

    [Fact]
    public async Task FallsBackWhenTheScriptCantBeUsed()
    {
        var fallback = new UpstreamProxy("manual.proxy", 3128, ["*.corp.example"]);
        using var autoConfig = ProxyAutoConfig.FromScript("this isn't JavaScript {", new PacScriptTests.StubPacHost(), fallback);

        Assert.Equal("manual.proxy:3128", Routes(await autoConfig.GetRoutesAsync("https://www.example.com/", "www.example.com", CancellationToken.None)));
        Assert.Equal("DIRECT", Routes(await autoConfig.GetRoutesAsync("https://a.corp.example/", "a.corp.example", CancellationToken.None)));
        Assert.Contains("couldn't use the automatic proxy configuration script", autoConfig.Description);
        Assert.Contains("forwarding through manual.proxy:3128", autoConfig.Description);
    }

    [Fact]
    public async Task FallsBackWhenTheScriptFailsForARequest()
    {
        using var autoConfig = ProxyAutoConfig.FromScript(
            "function FindProxyForURL(url, host) { throw new Error('no'); }", new PacScriptTests.StubPacHost());

        Assert.Equal("DIRECT", Routes(await autoConfig.GetRoutesAsync("http://a.example/", "a.example", CancellationToken.None)));
    }

    [Fact]
    public async Task DownloadsTheScriptWithoutAProxy()
    {
        const string script = "function FindProxyForURL(url, host) { return 'PROXY from.script:1'; }";
        await using var server = UpstreamProxyTests.FakeProxy.Start(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\nContent-Length: {script.Length}\r\n\r\n{script}");
        using var autoConfig = ProxyAutoConfig.Create(autoDetect: false, $"http://127.0.0.1:{server.Port}/proxy.pac", fallback: null, new PacScriptTests.StubPacHost());

        Assert.Equal("from.script:1", Routes(await autoConfig.GetRoutesAsync("http://a.example/", "a.example", CancellationToken.None).WaitAsync(TestTimeout)));
        Assert.StartsWith("GET /proxy.pac HTTP/1.1\r\n", await server.Received.Task.WaitAsync(TestTimeout));
        Assert.Equal($"using the automatic proxy configuration script http://127.0.0.1:{server.Port}/proxy.pac", autoConfig.Description);
    }

    [Fact]
    public async Task SaysWhyADownloadFailed()
    {
        await using var server = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
        using var autoConfig = ProxyAutoConfig.Create(autoDetect: false, $"http://127.0.0.1:{server.Port}/proxy.pac", fallback: null, new PacScriptTests.StubPacHost());

        await autoConfig.Loaded.WaitAsync(TestTimeout);

        Assert.Contains("404", autoConfig.Description);
        Assert.Contains("connecting directly", autoConfig.Description);
    }

    [Fact]
    public async Task ReadsAScriptFromAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"violens-{Guid.NewGuid():N}.pac");
        await File.WriteAllTextAsync(path, "function FindProxyForURL(url, host) { return 'PROXY file.proxy:2'; }");
        try
        {
            using var autoConfig = ProxyAutoConfig.Create(autoDetect: false, new Uri(path).AbsoluteUri, fallback: null, new PacScriptTests.StubPacHost());

            Assert.Equal("file.proxy:2", Routes(await autoConfig.GetRoutesAsync("http://a.example/", "a.example", CancellationToken.None).WaitAsync(TestTimeout)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RefusesADownloadedScriptLargerThanTheLimit()
    {
        // No Content-Length, so the limit has to apply as the bytes arrive.
        var script = "//" + new string('a', ProxyAutoConfig.MaxScriptBytes);
        await using var server = UpstreamProxyTests.FakeProxy.Start($"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n{script}");

        var ex = await Assert.ThrowsAsync<PacScriptException>(() =>
            ProxyAutoConfig.DownloadAsync($"http://127.0.0.1:{server.Port}/proxy.pac", discovered: false, CancellationToken.None)
                .WaitAsync(TestTimeout));
        Assert.Contains("larger than", ex.Message);
    }

    [Fact]
    public async Task RefusesAScriptFileLargerThanTheLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"violens-{Guid.NewGuid():N}.pac");
        await File.WriteAllTextAsync(path, "//" + new string('a', ProxyAutoConfig.MaxScriptBytes));
        try
        {
            await Assert.ThrowsAsync<PacScriptException>(() =>
                ProxyAutoConfig.DownloadAsync(new Uri(path).AbsoluteUri, discovered: false, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://attacker.example/share/wpad.dat")]
    [InlineData(@"\\attacker.example\share\wpad.dat")]
    [InlineData("ftp://attacker.example/wpad.dat")]
    public async Task UsesOnlyWebUrlsTheNetworkOffers(string url)
    {
        var ex = await Assert.ThrowsAsync<PacScriptException>(() =>
            ProxyAutoConfig.DownloadAsync(url, discovered: true, CancellationToken.None));
        Assert.Contains("only http and https", ex.Message);
    }

    [Fact]
    public async Task StillReadsAScriptFileTheUserConfigured()
    {
        var path = Path.Combine(Path.GetTempPath(), $"violens-{Guid.NewGuid():N}.pac");
        await File.WriteAllTextAsync(path, "function FindProxyForURL(url, host) { return 'DIRECT'; }");
        try
        {
            Assert.Contains("FindProxyForURL", await ProxyAutoConfig.DownloadAsync(new Uri(path).AbsoluteUri, discovered: false, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheListenerTriesTheNextProxyWhenOneCantBeReached()
    {
        using var root = NewRoot();
        await using var gateway = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        using var autoConfig = ProxyAutoConfig.FromScript(
            $"function FindProxyForURL(url, host) {{ return 'PROXY 127.0.0.1:1; PROXY 127.0.0.1:{gateway.Port}; DIRECT'; }}",
            new PacScriptTests.StubPacHost());
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.AutoConfig = autoConfig;
        try
        {
            var response = await SendPlainAsync(listener.Port, "GET http://example.test/page HTTP/1.1\r\nHost: example.test\r\n\r\n");

            Assert.Equal("ok", Encoding.ASCII.GetString(response.Body));
            Assert.StartsWith("GET http://example.test/page HTTP/1.1\r\n", await gateway.Received.Task.WaitAsync(TestTimeout));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheListenerTunnelsHttpsThroughTheProxyTheScriptChooses()
    {
        using var root = NewRoot();
        await using var gateway = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 502 Bad Gateway\r\n\r\n");
        var seen = new List<string>();
        using var autoConfig = ProxyAutoConfig.FromScript(
            $"function FindProxyForURL(url, host) {{ alert(url); return 'PROXY 127.0.0.1:{gateway.Port}'; }}",
            new RecordingHost(seen));
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.AutoConfig = autoConfig;
        try
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT example.test:8443 HTTP/1.1\r\nHost: example.test:8443\r\n\r\n"), timeout.Token);
            Assert.StartsWith("HTTP/1.1 200", await UpstreamProxyTests.FakeProxy.ReadHeadAsync(stream));

            using var tls = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync("example.test");
            await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /secret/path HTTP/1.1\r\nHost: example.test:8443\r\n\r\n"), timeout.Token);

            Assert.StartsWith("CONNECT example.test:8443 HTTP/1.1\r\n", await gateway.Received.Task.WaitAsync(timeout.Token));

            // Like browsers, the script sees only the scheme, host and port of an HTTPS request.
            lock (seen)
            {
                Assert.Equal(["https://example.test:8443/"], seen);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ThisMachineIsAlwaysReachedDirectly()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nlocal");
        using var autoConfig = ProxyAutoConfig.FromScript("function FindProxyForURL(url, host) { return 'PROXY 127.0.0.1:1'; }", new PacScriptTests.StubPacHost());
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.AutoConfig = autoConfig;
        try
        {
            var response = await SendPlainAsync(listener.Port, $"GET http://127.0.0.1:{origin.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n");

            Assert.Equal("local", Encoding.ASCII.GetString(response.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RecordingHost(List<string> seen) : IPacHost
    {
        public IReadOnlyList<IPAddress> Resolve(string host) => [];

        public IReadOnlyList<IPAddress> LocalAddresses() => [];

        public void Log(string message)
        {
            lock (seen)
            {
                seen.Add(message);
            }
        }
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 NewRoot()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        return CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
    }

    private static async Task<CapturedResponse> SendPlainAsync(int port, string request)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
        Assert.NotNull(response);
        return response!;
    }
}
