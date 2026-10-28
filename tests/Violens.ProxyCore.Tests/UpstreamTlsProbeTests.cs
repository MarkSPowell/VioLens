using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Noticing a server ask for a client certificate (<see cref="UpstreamTlsProbe"/>).</summary>
public class UpstreamTlsProbeTests
{
    /// <summary>
    /// Runs a TLS server on loopback that does or doesn't ask for a client
    /// certificate, connects to it through a probe, and returns what the
    /// probe saw.
    /// </summary>
    private static async Task<ServerTlsInfo> HandshakeAsync(bool serverAsksForCertificate)
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var leaf = new LeafCertificateProvider(root).GetCertificateFor("localhost");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = leaf,
                        ClientCertificateRequired = serverAsksForCertificate,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true, // asks, but takes no certificate too
                        EnabledSslProtocols = SslProtocols.None,
                    },
                    timeout.Token);
                await tls.WriteAsync(new byte[] { 1 }, timeout.Token);
            });

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var clientTls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            var probe = new UpstreamTlsProbe();
            probe.Attach(clientTls);
            await clientTls.AuthenticateAsClientAsync(probe.CreateOptions("localhost", (_, _, _, _) => true), timeout.Token);

            // Read a byte, so a TLS 1.3 server's request has certainly arrived.
            var one = new byte[1];
            await clientTls.ReadExactlyAsync(one, timeout.Token);
            await serverTask;
            return probe.Snapshot();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task NoticesAServerAskingForAClientCertificate()
    {
        var tls = await HandshakeAsync(serverAsksForCertificate: true);

        Assert.True(tls.ClientCertificateRequested);
    }

    [Fact]
    public async Task SendsTheChosenClientCertificate()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var leaf = new LeafCertificateProvider(root).GetCertificateFor("localhost");
        using var clientCertificate = LeafCertificateProvider.CreateLeaf(root, "client.test", notBefore, notBefore.AddDays(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = leaf,
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                        EnabledSslProtocols = SslProtocols.None,
                    },
                    timeout.Token);
                await tls.WriteAsync(new byte[] { 1 }, timeout.Token);
                return tls.RemoteCertificate?.Subject;
            });

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var clientTls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            string? askedFor = null;
            var probe = new UpstreamTlsProbe(host =>
            {
                askedFor = host;
                return clientCertificate;
            });
            probe.Attach(clientTls);
            await clientTls.AuthenticateAsClientAsync(probe.CreateOptions("localhost", (_, _, _, _) => true), timeout.Token);
            await clientTls.ReadExactlyAsync(new byte[1], timeout.Token);

            Assert.Equal(clientCertificate.Subject, await serverTask);
            Assert.Equal("localhost", askedFor);
            Assert.Equal(clientCertificate.Subject, probe.Snapshot().ClientCertificateSent);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task DescribesAnOrdinaryConnection()
    {
        var tls = await HandshakeAsync(serverAsksForCertificate: false);

        Assert.False(tls.ClientCertificateRequested);
        Assert.Empty(tls.AcceptableIssuers);
        Assert.StartsWith("TLS 1.", tls.Protocol);
        Assert.False(string.IsNullOrEmpty(tls.CipherSuite));
        Assert.Contains("CN=localhost", tls.CertificateSubject);
        Assert.NotNull(tls.CertificateNotAfter);
    }

    [Fact]
    public void BeforeTheHandshakeThereIsNothingToDescribe()
    {
        var tls = new UpstreamTlsProbe().Snapshot();

        Assert.Null(tls.Protocol);
        Assert.False(tls.ClientCertificateRequested);
    }

    [Theory]
    [InlineData(SslProtocols.Tls12, "TLS 1.2")]
    [InlineData(SslProtocols.Tls13, "TLS 1.3")]
    public void NamesProtocols(SslProtocols protocol, string name)
    {
        Assert.Equal(name, UpstreamTlsProbe.DescribeProtocol(protocol));
    }

    [Fact]
    public void DescribesTheAcceptedIssuers()
    {
        Assert.Equal("any issuer", new ServerTlsInfo { ClientCertificateRequested = true }.DescribeAcceptableIssuers());
        Assert.Equal(
            "CN=Corp Root; CN=Partner CA",
            new ServerTlsInfo { AcceptableIssuers = ["CN=Corp Root", "CN=Partner CA"] }.DescribeAcceptableIssuers());
    }
}
