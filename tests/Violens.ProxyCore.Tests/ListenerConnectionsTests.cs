using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Violens.ProxyCore.AutoResponder;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>
/// The listener's connection options: forwarding through an upstream proxy,
/// its own page and certificate for other devices, and listening for remote
/// computers.
/// </summary>
public class ListenerConnectionsTests
{
    private static X509Certificate2 NewRoot()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        return CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
    }

    [Fact]
    public async Task ServesItsHomePageAndCertificateOverPlainHttp()
    {
        using var root = NewRoot();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        try
        {
            var page = await SendPlainAsync(listener.Port, "GET http://violens/ HTTP/1.1\r\nHost: violens\r\n\r\n");
            Assert.Equal(200, page.StatusCode);
            Assert.Contains("text/html", ContentDecoder.FindHeader(page.Headers, "Content-Type"));
            Assert.Contains("ViolensRoot.cer", Encoding.UTF8.GetString(page.Body));

            var certificate = await SendPlainAsync(listener.Port, "GET /ViolensRoot.cer HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            Assert.Equal(200, certificate.StatusCode);
            Assert.Equal(root.Export(X509ContentType.Cert), certificate.Body);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ForwardsPlainHttpAndRecordsIt()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var (store, recorded) = RecordingStore();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        try
        {
            var response = await SendPlainAsync(
                listener.Port,
                $"GET http://127.0.0.1:{origin.Port}/hello?x=1 HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nProxy-Connection: keep-alive\r\n\r\n");

            Assert.Equal(200, response.StatusCode);
            Assert.Equal("hello", Encoding.ASCII.GetString(response.Body));

            // The server gets an ordinary request: path only, no proxy headers.
            var head = await origin.Received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("GET /hello?x=1 HTTP/1.1\r\n", head);
            Assert.DoesNotContain("Proxy-Connection", head);

            var session = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("http", session.Scheme);
            Assert.Equal("127.0.0.1", session.Host);
            Assert.Equal("/hello?x=1", session.Request.Target);
            Assert.Equal($"127.0.0.1:{origin.Port}", ContentDecoder.FindHeader(session.Request.Headers, "Host"));
            Assert.Equal($"http://127.0.0.1:{origin.Port}/hello?x=1", SessionUrl.Of(session));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task OneClientConnectionCanReachDifferentServers()
    {
        using var root = NewRoot();
        await using var first = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nfirst");
        await using var second = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\nsecond");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();

            foreach (var (server, expected) in new[] { (first, "first"), (second, "second") })
            {
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{server.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n"), timeout.Token);
                var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
                Assert.Equal(expected, Encoding.ASCII.GetString(response!.Body));
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RelaysAResponseThatEndsWhenTheServerCloses()
    {
        using var root = NewRoot();
        // No Content-Length and not chunked: the body runs until the server
        // closes the connection (the fake server closes shortly after replying).
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nuntil close");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        try
        {
            var response = await SendPlainAsync(listener.Port, $"GET http://127.0.0.1:{origin.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n");

            Assert.Equal("until close", Encoding.ASCII.GetString(response.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("HTTP/1.1 100 Continue\r\n\r\n")]
    [InlineData("HTTP/1.1 103 Early Hints\r\nLink: </style.css>; rel=preload\r\n\r\n")]
    [InlineData("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 102 Processing\r\n\r\n")]
    public async Task PassesInterimResponsesOnAndThenTheFinalOne(string interim)
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start(interim + "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var (store, recorded) = RecordingStore();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes($"POST http://127.0.0.1:{origin.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nContent-Length: 2\r\n\r\nhi"), timeout.Token);

            // The client sees each interim response, then the real one.
            var interimCount = interim.Split("HTTP/1.1 ").Length - 1;
            for (var i = 0; i < interimCount; i++)
            {
                var interimResponse = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
                Assert.InRange(interimResponse!.StatusCode, 100, 199);
            }

            var final = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
            Assert.Equal(200, final!.StatusCode);
            Assert.Equal("hello", Encoding.ASCII.GetString(final.Body));

            // Recorded once, as the final response.
            var session = await recorded.Task.WaitAsync(timeout.Token);
            Assert.Equal(200, session.Response.StatusCode);
            Assert.Single(store.Snapshot());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task KeepsOnlyTheStartOfABodyOverTheCaptureLimitButRelaysAllOfIt()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\nhello world");
        var (store, recorded) = RecordingStore();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        listener.MaxCapturedBodyBytes = 5;
        try
        {
            var response = await SendPlainAsync(
                listener.Port,
                $"POST http://127.0.0.1:{origin.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nContent-Length: 9\r\n\r\nsome data");

            Assert.Equal("hello world", Encoding.ASCII.GetString(response.Body));

            var session = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("hello", Encoding.ASCII.GetString(session.Response.Body));
            Assert.Equal("some ", Encoding.ASCII.GetString(session.Request.Body));
            Assert.Equal("kept the first 5 of 11 bytes", session.Flags![Session.ResponseBodyTruncatedFlag]);
            Assert.Equal("kept the first 5 of 9 bytes", session.Flags[Session.RequestBodyTruncatedFlag]);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AnswersBadGatewayWhenTheServerCantBeReached()
    {
        using var root = NewRoot();
        var (store, recorded) = RecordingStore();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        try
        {
            // Nothing listens on port 1.
            var response = await SendPlainAsync(listener.Port, "GET http://127.0.0.1:1/ HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n");

            Assert.Equal(502, response.StatusCode);
            Assert.Contains("couldn't connect to 127.0.0.1:1", Encoding.UTF8.GetString(response.Body));

            var session = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(502, session.Response.StatusCode);
            Assert.Equal("http", session.Scheme);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AnswersRequestsItCantForwardInsteadOfDroppingThem()
    {
        using var root = NewRoot();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        try
        {
            var response = await SendPlainAsync(listener.Port, "GET ftp://example.test/file HTTP/1.1\r\nHost: example.test\r\n\r\n");

            Assert.Equal(501, response.StatusCode);
            Assert.Contains("forwards http:// and https:// requests only", Encoding.UTF8.GetString(response.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendsPlainHttpThroughTheGatewayAsAProxyRequest()
    {
        using var root = NewRoot();
        await using var gateway = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.Gateway = new UpstreamProxy("127.0.0.1", gateway.Port);
        try
        {
            var response = await SendPlainAsync(listener.Port, "GET http://example.test/a?b=1 HTTP/1.1\r\nHost: example.test\r\n\r\n");

            Assert.Equal("ok", Encoding.ASCII.GetString(response.Body));
            var head = await gateway.Received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("GET http://example.test/a?b=1 HTTP/1.1\r\n", head);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ForwardsThroughTheGateway()
    {
        using var root = NewRoot();
        await using var gateway = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 502 Bad Gateway\r\n\r\n");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.Gateway = new UpstreamProxy("127.0.0.1", gateway.Port);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"), timeout.Token);
            Assert.StartsWith("HTTP/1.1 200", await UpstreamProxyTests.FakeProxy.ReadHeadAsync(stream));

            using var tls = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync("example.test");
            await tls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n"), timeout.Token);

            // The listener opens its server connection through the gateway.
            var head = await gateway.Received.Task.WaitAsync(timeout.Token);
            Assert.StartsWith("CONNECT example.test:443 HTTP/1.1\r\n", head);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AnAutoResponderUrlIsFetchedTheWayVioLensOwnConnectionsGo()
    {
        // Through the upstream proxy, not the system proxy (which, while
        // capturing, is VioLens itself).
        using var root = NewRoot();
        await using var gateway = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 7\r\n\r\nfetched");
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(new AutoResponderRule { MatchPattern = "original.test", Action = "http://elsewhere.test/mock.json" });
        var store = new SessionStore();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store, autoResponderRules: rules);
        listener.Gateway = new UpstreamProxy("127.0.0.1", gateway.Port);
        try
        {
            var response = await SendPlainAsync(listener.Port, "GET http://original.test/ HTTP/1.1\r\nHost: original.test\r\n\r\n");

            Assert.Equal("fetched", Encoding.ASCII.GetString(response.Body));
            Assert.StartsWith("GET http://elsewhere.test/mock.json HTTP/1.1\r\n", await gateway.Received.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void CanListenForRemoteComputers()
    {
        using var root = NewRoot();
        var local = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        var remote = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore(), allowRemoteClients: true);
        try
        {
            Assert.False(local.AllowsRemoteClients);
            Assert.True(remote.AllowsRemoteClients);
        }
        finally
        {
            local.Stop();
            remote.Stop();
        }
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/ViolensRoot.cer", true)]
    [InlineData("http://violens/", true)]
    [InlineData("http://VIOLENS/ViolensRoot.cer", true)]
    [InlineData("http://127.0.0.1:8888/", true)]
    [InlineData("http://127.0.0.1:9999/", false)]
    [InlineData("http://example.test/", false)]
    public void RecognisesRequestsForItself(string target, bool forProxy)
    {
        Assert.Equal(forProxy, ProxyHomePage.IsRequestForProxy(target, 8888));
    }

    [Fact]
    public async Task TunnelsSkipDecryptionHostsWithoutDecrypting()
    {
        using var root = NewRoot();
        // Not TLS at all: anything VioLens tried to decrypt would fail.
        await using var origin = UpstreamProxyTests.FakeProxy.Start("server bytes\r\n\r\n");
        var (store, recorded) = RecordingStore();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        listener.SkipDecryptionHosts = ["example.test", "127.0.0.*"];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();

            // The client's first bytes come with the CONNECT, as a client that
            // doesn't wait for the 200 sends them.
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\nclient bytes\r\n\r\n"),
                timeout.Token);

            Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", await UpstreamProxyTests.FakeProxy.ReadHeadAsync(stream));
            Assert.Equal("server bytes\r\n\r\n", await UpstreamProxyTests.FakeProxy.ReadHeadAsync(stream));
            Assert.Equal("client bytes\r\n\r\n", await origin.Received.Task.WaitAsync(timeout.Token));

            var session = await recorded.Task.WaitAsync(timeout.Token);
            Assert.Equal("CONNECT", session.Request.Method);
            Assert.Equal(200, session.Response.StatusCode);
            Assert.Equal($"127.0.0.1:{origin.Port}", SessionUrl.Of(session));
            Assert.True(session.Flags!.ContainsKey(InterceptingProxyListener.NoDecryptFlag));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ASkipDecryptionHostThatCantBeReachedGetsA502()
    {
        using var root = NewRoot();
        var (store, recorded) = RecordingStore();
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        listener.SkipDecryptionHosts = ["127.0.0.1"];
        try
        {
            // Nothing listens on port 1.
            var response = await SendPlainAsync(listener.Port, "CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n");

            Assert.Equal(502, response.StatusCode);
            var session = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(502, session.Response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("API.EXAMPLE.COM", true)]
    [InlineData("example.com", false)]
    [InlineData("pinned.test", true)]
    [InlineData("other.test", false)]
    public void SkipDecryptionHostsUseTheBypassListFormat(string host, bool matches) =>
        Assert.Equal(matches, UpstreamProxy.MatchesHostList(host, ["*.example.com", " pinned.test "]));

    private static (SessionStore Store, TaskCompletionSource<Session> Recorded) RecordingStore()
    {
        var store = new SessionStore();
        var recorded = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SessionAdded += session => recorded.TrySetResult(session);
        return (store, recorded);
    }

    private static async Task<CapturedResponse> SendPlainAsync(int port, string request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
        Assert.NotNull(response);
        return response!;
    }
}
