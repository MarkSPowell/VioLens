using System.Net;
using System.Net.Sockets;
using System.Text;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Signing in to an upstream proxy that answers 407 (<see cref="ProxyAuthenticator"/>).</summary>
public class ProxyAuthenticationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static readonly string BasicAlice = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret"));

    private const string AskForBasic =
        "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp, main\"\r\nContent-Length: 7\r\n\r\nsign in";

    [Fact]
    public void ReadsTheProxysChallenges()
    {
        var challenges = ProxyAuthenticator.ParseChallenges(
        [
            ("Proxy-Authenticate", "Negotiate, NTLM"),
            ("Proxy-Authenticate", "Basic realm=\"corp, main\", charset=\"UTF-8\""),
            ("Content-Length", "0"),
            ("proxy-authenticate", "NTLM TlRMTVNTUAACAAAADAAMADgAAAA="),
        ]);

        Assert.Equal(
            [("Negotiate", null), ("NTLM", null), ("Basic", null), ("NTLM", "TlRMTVNTUAACAAAADAAMADgAAAA=")],
            challenges.Select(c => (c.Scheme, c.Token)));
    }

    [Fact]
    public void SendsBasicWhenThatsAllTheProxyOffersAndAUserNameWasGiven()
    {
        using var authenticator = new ProxyAuthenticator("proxy.test", new NetworkCredential("alice", "s3cret"));

        Assert.Equal(BasicAlice, authenticator.Respond([("Proxy-Authenticate", "Basic realm=\"corp\"")]));

        // Asked again: the password was wrong. There's nothing else to try.
        Assert.Null(authenticator.Respond([("Proxy-Authenticate", "Basic realm=\"corp\"")]));
    }

    [Fact]
    public void CantUseBasicWithoutAUserName()
    {
        using var authenticator = new ProxyAuthenticator("proxy.test", credential: null);

        Assert.Null(authenticator.Respond([("Proxy-Authenticate", "Basic realm=\"corp\"")]));
        Assert.Equal(["Basic"], authenticator.OfferedSchemes);
    }

    [Theory]
    [InlineData("CORP\\alice", "alice", "CORP")]
    [InlineData("alice@corp.example", "alice@corp.example", "")]
    [InlineData("  alice ", "alice", "")]
    public void ReadsAUserName(string typed, string userName, string domain)
    {
        var credential = UpstreamProxy.CredentialFor(typed, "pw")!;

        Assert.Equal(userName, credential.UserName);
        Assert.Equal(domain, credential.Domain);
        Assert.Equal("pw", credential.Password);
    }

    [Fact]
    public void NoUserNameMeansSignInAsYou()
    {
        Assert.Null(UpstreamProxy.CredentialFor("  ", "pw"));
    }

    [Fact]
    public async Task SignsInToOpenATunnel()
    {
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 Connection Established\r\n\r\n");

        using var client = await new UpstreamProxy("127.0.0.1", proxy.Port)
            .ConnectTunnelAsync("example.test", 443, new NetworkCredential("alice", "s3cret"), CancellationToken.None)
            .WaitAsync(TestTimeout);

        var heads = await proxy.Done.WaitAsync(TestTimeout);
        Assert.DoesNotContain("Proxy-Authorization", heads[0]);
        Assert.StartsWith("CONNECT example.test:443 HTTP/1.1\r\n", heads[1]);
        Assert.Contains($"Proxy-Authorization: {BasicAlice}\r\n", heads[1]);
    }

    [Fact]
    public async Task DoesntSendAPlainTextPasswordForATunnelWhenTheUserSaysNo()
    {
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 Connection Established\r\n\r\n");
        var asked = 0;

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new UpstreamProxy("127.0.0.1", proxy.Port)
                .ConnectTunnelAsync(
                    "example.test", 443, new NetworkCredential("alice", "s3cret"), signIn: true,
                    confirmPlaintext: _ => { asked++; return Task.FromResult(false); }, CancellationToken.None)
                .WaitAsync(TestTimeout));

        Assert.Equal(1, asked);
        Assert.Contains("plain text", error.Message);
    }

    [Fact]
    public async Task SendsAPlainTextPasswordForATunnelWhenTheUserSaysYes()
    {
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 Connection Established\r\n\r\n");

        using var client = await new UpstreamProxy("127.0.0.1", proxy.Port)
            .ConnectTunnelAsync(
                "example.test", 443, new NetworkCredential("alice", "s3cret"), signIn: true,
                confirmPlaintext: _ => Task.FromResult(true), CancellationToken.None)
            .WaitAsync(TestTimeout);

        var heads = await proxy.Done.WaitAsync(TestTimeout);
        Assert.Contains($"Proxy-Authorization: {BasicAlice}\r\n", heads[1]);
    }

    [Fact]
    public async Task TheListenerAsksBeforeSendingAPasswordInPlainTextAndPassesThe407WhenTheUserSaysNo()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.Gateway = new UpstreamProxy("127.0.0.1", proxy.Port);
        listener.ProxyCredential = new NetworkCredential("alice", "s3cret");
        var asked = new List<string>();
        listener.ConfirmPlaintextSignIn = (route, _) =>
        {
            lock (asked)
            {
                asked.Add(route.ToString());
            }

            return Task.FromResult(false);
        };
        try
        {
            var response = await SendThroughAsync(listener.Port, "GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n");

            Assert.Equal(407, response.StatusCode);
            Assert.Equal([$"127.0.0.1:{proxy.Port}"], asked);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheListenerAsksOnlyOncePerProxyAndSendsThePasswordWhenTheUserSaysYes()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        const string Ok = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello";
        await using var proxy = ScriptedProxy.Start(AskForBasic, Ok, AskForBasic, Ok);
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.Gateway = new UpstreamProxy("127.0.0.1", proxy.Port);
        listener.ProxyCredential = new NetworkCredential("alice", "s3cret");
        var asked = 0;
        listener.ConfirmPlaintextSignIn = (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(true);
        };
        try
        {
            // Two requests on one client connection, each answered 407 first.
            using var timeout = new CancellationTokenSource(TestTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            for (var i = 0; i < 2; i++)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"), timeout.Token);
                var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
                Assert.Equal(200, response!.StatusCode);
            }

            Assert.Equal(1, asked);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RequestsWaitingOnTheSameProxyShareOneQuestion()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var listener = new InterceptingProxyListener(0, new LeafCertificateProvider(root), new SessionStore());
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        listener.ConfirmPlaintextSignIn = (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return answer.Task;
        };
        var route = new UpstreamProxy("proxy.example", 8080);

        var first = listener.ApprovePlaintextSignInAsync(route, CancellationToken.None);
        var second = listener.ApprovePlaintextSignInAsync(new UpstreamProxy("PROXY.example", 8080), CancellationToken.None);
        answer.SetResult(true);

        Assert.True(await first.WaitAsync(TestTimeout));
        Assert.True(await second.WaitAsync(TestTimeout));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task NoAnswerInTimeCountsAsNo()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var listener = new InterceptingProxyListener(0, new LeafCertificateProvider(root), new SessionStore())
        {
            PlaintextSignInTimeout = TimeSpan.FromMilliseconds(100),
            ConfirmPlaintextSignIn = (_, _) => new TaskCompletionSource<bool>().Task,
        };

        Assert.False(await listener.ApprovePlaintextSignInAsync(new UpstreamProxy("proxy.example", 8080), CancellationToken.None)
            .WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task ANewPasswordIsAskedAboutAgain()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var asked = 0;
        var listener = new InterceptingProxyListener(0, new LeafCertificateProvider(root), new SessionStore())
        {
            ConfirmPlaintextSignIn = (_, _) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult(false);
            },
        };
        var route = new UpstreamProxy("proxy.example", 8080);

        await listener.ApprovePlaintextSignInAsync(route, CancellationToken.None);
        listener.ProxyCredential = new NetworkCredential("alice", "new password");
        await listener.ApprovePlaintextSignInAsync(route, CancellationToken.None);

        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task WithNothingToAskTheSignInGoesAheadAsBefore()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var listener = new InterceptingProxyListener(0, new LeafCertificateProvider(root), new SessionStore());

        Assert.True(await listener.ApprovePlaintextSignInAsync(new UpstreamProxy("proxy.example", 8080), CancellationToken.None));
    }

    private static async Task<CapturedResponse> SendThroughAsync(int port, string request)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
        return response!;
    }

    [Fact]
    public async Task SaysWhenTheProxyRefusesTheSignIn()
    {
        await using var proxy = ScriptedProxy.Start(AskForBasic, AskForBasic);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new UpstreamProxy("127.0.0.1", proxy.Port)
                .ConnectTunnelAsync("example.test", 443, new NetworkCredential("alice", "wrong"), CancellationToken.None)
                .WaitAsync(TestTimeout));

        Assert.Contains("didn't accept the user name and password", error.Message);
    }

    [Fact]
    public async Task TheListenerSignsInForPlainHttp()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var store = new SessionStore();
        var recorded = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SessionAdded += session => recorded.TrySetResult(session);
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), store);
        listener.Gateway = new UpstreamProxy("127.0.0.1", proxy.Port);
        listener.ProxyCredential = new NetworkCredential("alice", "s3cret");
        try
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("POST http://example.test/form HTTP/1.1\r\nHost: example.test\r\nContent-Length: 3\r\n\r\na=1"), timeout.Token);

            var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
            Assert.Equal(200, response!.StatusCode);
            Assert.Equal("hello", Encoding.ASCII.GetString(response.Body));

            // Sent again, whole, with the sign-in; the client never sees the 407.
            var heads = await proxy.Done.WaitAsync(timeout.Token);
            Assert.StartsWith("POST http://example.test/form HTTP/1.1\r\n", heads[1]);
            Assert.Contains($"Proxy-Authorization: {BasicAlice}\r\n", heads[1]);
            Assert.EndsWith("\r\n\r\na=1", heads[1]);

            var session = await recorded.Task.WaitAsync(timeout.Token);
            Assert.Equal(200, session.Response.StatusCode);
            Assert.Null(ContentDecoder.FindHeader(session.Request.Headers, "Proxy-Authorization"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AClientOnAnotherComputerDoesntGetTheUsersProxySignIn()
    {
        // A "remote" client: one connecting from this computer's own network
        // address rather than loopback, which the listener treats the same as
        // another computer. Without such an address there's nothing to test.
        if (NetworkAddress() is not { } address)
        {
            return;
        }

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var listener = InterceptingProxyListener.StartOnAvailablePort(
            0, new LeafCertificateProvider(root), new SessionStore(), allowRemoteClients: true);
        listener.Gateway = new UpstreamProxy("127.0.0.1", proxy.Port);
        listener.ProxyCredential = new NetworkCredential("alice", "s3cret");
        try
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            using var client = new TcpClient(new IPEndPoint(address, 0));
            await client.ConnectAsync(address, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"), timeout.Token);

            // The proxy's 407 reaches the client, as it would from any proxy
            // the client used directly; the user's password isn't sent.
            var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
            Assert.Equal(407, response!.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AProgramOfAnotherAccountOnThisComputerDoesntGetTheUsersProxySignIn()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.Gateway = new UpstreamProxy("127.0.0.1", proxy.Port);
        listener.ProxyCredential = new NetworkCredential("alice", "s3cret");

        // Stands in for the lookup finding another account at the other end.
        listener.LoopbackOwnerCheck = (_, _) => false;
        try
        {
            var response = await SendThroughAsync(listener.Port, "GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n");

            Assert.Equal(407, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WhenTheOwnerCantBeFoundOutTheSignInGoesAheadAsBefore()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        await using var proxy = ScriptedProxy.Start(AskForBasic, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.Gateway = new UpstreamProxy("127.0.0.1", proxy.Port);
        listener.ProxyCredential = new NetworkCredential("alice", "s3cret");
        listener.LoopbackOwnerCheck = (_, _) => null;
        try
        {
            var response = await SendThroughAsync(listener.Port, "GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n");

            Assert.Equal(200, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void AClientOnAnotherComputerDoesntGetTheUsersClientCertificate()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        using var certificate = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var listener = new InterceptingProxyListener(0, new LeafCertificateProvider(root), new SessionStore())
        {
            ClientCertificate = certificate,
        };

        Assert.Same(certificate, listener.ClientCertificateFor("example.test", remoteClient: false));
        Assert.Null(listener.ClientCertificateFor("example.test", remoteClient: true));
    }

    /// <summary>One of this computer's own non-loopback IPv4 addresses, if it has one.</summary>
    private static IPAddress? NetworkAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                                 !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));

    [Fact]
    public async Task TheClientGetsThe407WhenSigningInDoesntWork()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        await using var proxy = ScriptedProxy.Start(AskForBasic);
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        listener.Gateway = new UpstreamProxy("127.0.0.1", proxy.Port);
        try
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"), timeout.Token);

            var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
            Assert.Equal(407, response!.StatusCode);
            Assert.Equal("sign in", Encoding.ASCII.GetString(response.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// A proxy that takes one connection and answers each request on it with
    /// the next scripted reply, recording what it received (head and any
    /// Content-Length body).
    /// </summary>
    internal sealed class ScriptedProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;

        private ScriptedProxy(string[] replies)
        {
            _listener.Start();
            _serve = ServeAsync(replies);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public TaskCompletionSource<List<string>> DoneSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<List<string>> Done => DoneSource.Task;

        public static ScriptedProxy Start(params string[] replies) => new(replies);

        private async Task ServeAsync(string[] replies)
        {
            var received = new List<string>();
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                foreach (var reply in replies)
                {
                    var head = await UpstreamProxyTests.FakeProxy.ReadHeadAsync(stream);
                    var body = string.Empty;
                    var lengthLine = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    if (lengthLine is not null && int.TryParse(lengthLine["Content-Length:".Length..].Trim(), out var length) && length > 0)
                    {
                        var buffer = new byte[length];
                        await stream.ReadExactlyAsync(buffer);
                        body = Encoding.ASCII.GetString(buffer);
                    }

                    received.Add(head + body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                    await stream.FlushAsync();
                }

                DoneSource.TrySetResult(received);
                await Task.Delay(200);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                DoneSource.TrySetException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception)
            {
            }
        }
    }
}
