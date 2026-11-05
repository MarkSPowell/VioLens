using System.Net;
using System.Net.Sockets;
using Violens.ProxyCore.Proxy;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>Which account a loopback connection to VioLens comes from (<see cref="LoopbackConnectionOwner"/>).</summary>
public class LoopbackConnectionOwnerTests
{
    // Where the lookup isn't available (macOS), the answer is "don't know".
    private static bool? Expected(bool value) => OperatingSystem.IsMacOS() ? null : value;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task AConnectionFromThisProcessIsThisUsers(string address)
    {
        var loopback = IPAddress.Parse(address);
        var listener = new TcpListener(loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient(loopback.AddressFamily);
            await client.ConnectAsync(loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptTcpClientAsync();

            // As the listener sees it: the client's end, then its own.
            var owner = LoopbackConnectionOwner.IsCurrentUser(
                (IPEndPoint)accepted.Client.RemoteEndPoint!, (IPEndPoint)accepted.Client.LocalEndPoint!);

            Assert.Equal(Expected(true), owner);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ADualStackClientsIPv4ConnectionIsFound()
    {
        // TcpClient's default socket is IPv6 carrying IPv4, which Linux lists
        // in /proc/net/tcp6 as ::ffff:127.0.0.1, not in /proc/net/tcp.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptTcpClientAsync();

            Assert.Equal(Expected(true), LoopbackConnectionOwner.IsCurrentUser(
                (IPEndPoint)accepted.Client.RemoteEndPoint!, (IPEndPoint)accepted.Client.LocalEndPoint!));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AConnectionFromAnotherProgramOfThisUserIsThisUsers()
    {
        // Another process, so the owner is found through its account, not
        // by recognising VioLens's own process. curl ships with Windows 10
        // and later and nearly every Linux; without it there's nothing to test.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        System.Diagnostics.Process? curl = null;
        try
        {
            try
            {
                curl = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "curl", $"-s -m 20 http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return;
            }

            using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(Expected(true), LoopbackConnectionOwner.IsCurrentUser(
                (IPEndPoint)accepted.Client.RemoteEndPoint!, (IPEndPoint)accepted.Client.LocalEndPoint!));
        }
        finally
        {
            listener.Stop();
            if (curl is { HasExited: false })
            {
                curl.Kill();
            }

            curl?.Dispose();
        }
    }

    [Theory]
    [InlineData("0100007F:1F90", "127.0.0.1", 8080)]
    [InlineData("0101A8C0:0050", "192.168.1.1", 80)]
    [InlineData("00000000000000000000000001000000:C350", "::1", 50000)]
    [InlineData("0000000000000000FFFF00000100007F:01BB", "::ffff:127.0.0.1", 443)]
    public void ReadsLinuxConnectionTableAddresses(string field, string address, int port)
    {
        // The kernel prints each 32-bit word in its own byte order; these are
        // as a little-endian machine prints them.
        if (!BitConverter.IsLittleEndian)
        {
            return;
        }

        Assert.Equal(new IPEndPoint(IPAddress.Parse(address), port), LoopbackConnectionOwner.ParseLinuxEndPoint(field));
    }

    [Fact]
    public void AConnectionThatDoesntExistIsNobodys()
    {
        var owner = LoopbackConnectionOwner.IsCurrentUser(
            new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2));

        Assert.Equal(Expected(false), owner);
    }

    [Fact]
    public async Task AnIPv4ClientSeenAsAnIPv6AddressIsFound()
    {
        // A dual-mode (IPv6) listener sees an IPv4 client as ::ffff:127.0.0.1.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var clientEnd = (IPEndPoint)accepted.Client.RemoteEndPoint!;
            var serverEnd = (IPEndPoint)accepted.Client.LocalEndPoint!;

            Assert.Equal(Expected(true), LoopbackConnectionOwner.IsCurrentUser(
                new IPEndPoint(clientEnd.Address.MapToIPv6(), clientEnd.Port), new IPEndPoint(serverEnd.Address.MapToIPv6(), serverEnd.Port)));
        }
        finally
        {
            listener.Stop();
        }
    }
}
