using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// Another HTTP proxy that VioLens forwards its own outgoing connections
/// through -- Fiddler Classic's "gateway". On a network that only reaches the
/// internet through a proxy, the system proxy setting points at that proxy
/// until VioLens registers itself in its place; without chaining to it,
/// everything VioLens forwards would try to connect directly and fail.
///
/// Only plain HTTP proxies are supported, reached with a <c>CONNECT</c>
/// tunnel for HTTPS. Automatic configuration (PAC scripts and WPAD) is
/// handled by <see cref="AutoConfig.ProxyAutoConfig"/>, which picks one of
/// these per request. A proxy that asks for a sign-in (407) gets one: see
/// <see cref="ProxyAuthenticator"/>. Not supported yet: SOCKS.
/// </summary>
/// <param name="Host">The proxy's host name or address.</param>
/// <param name="Port">The proxy's port.</param>
/// <param name="BypassList">
/// Hosts to connect to directly instead, in the Windows proxy-override
/// format: exact names, <c>*</c> wildcards (<c>*.corp.example</c>,
/// <c>10.*</c>), and <c>&lt;local&gt;</c> for names without a dot. Loopback
/// targets are always connected to directly.
/// </param>
public sealed record UpstreamProxy(string Host, int Port, IReadOnlyList<string> BypassList)
{
    public UpstreamProxy(string host, int port)
        : this(host, port, [])
    {
    }

    public override string ToString() => $"{FormatHost(Host)}:{Port}";

    /// <summary>
    /// Parses "host:port" or "http://host:port" (a trailing "/" is ignored).
    /// Returns null for anything else, including an https:// or socks://
    /// proxy, which aren't supported.
    /// </summary>
    public static UpstreamProxy? Parse(string? text, IReadOnlyList<string>? bypassList = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimEnd('/');
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["http://".Length..];
        }
        else if (value.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        string host;
        string portText;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0 || close + 1 >= value.Length || value[close + 1] != ':')
            {
                return null;
            }

            host = value[1..close];
            portText = value[(close + 2)..];
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || value.IndexOf(':') != colon)
            {
                return null;
            }

            host = value[..colon];
            portText = value[(colon + 1)..];
        }

        return int.TryParse(portText, out var port) && port is >= 1 and <= 65535 && host.Length > 0
            ? new UpstreamProxy(host, port, bypassList ?? Array.Empty<string>())
            : null;
    }

    /// <summary>
    /// Reads Windows' own proxy settings (the Internet Settings
    /// <c>ProxyServer</c> and <c>ProxyOverride</c> values). ProxyServer is
    /// either one "host:port" for every protocol or a per-protocol list such
    /// as "http=h:80;https=h:443"; the https entry is preferred (VioLens's
    /// outgoing connections are HTTPS), then http, then a bare entry. A list
    /// with only socks or ftp entries gives null.
    /// </summary>
    public static UpstreamProxy? FromWindowsSettings(string? proxyServer, string? proxyOverride)
    {
        if (string.IsNullOrWhiteSpace(proxyServer))
        {
            return null;
        }

        var bypass = (proxyOverride ?? string.Empty)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (!proxyServer.Contains('='))
        {
            return Parse(proxyServer, bypass);
        }

        string? http = null;
        string? https = null;
        foreach (var entry in proxyServer.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var scheme = entry[..equals].Trim();
            var address = entry[(equals + 1)..].Trim();
            if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                https = address;
            }
            else if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                http = address;
            }
        }

        return Parse(https ?? http, bypass);
    }

    /// <summary>True when <paramref name="host"/> should be connected to directly rather than through this proxy.</summary>
    public bool ShouldBypass(string host)
    {
        return IsLoopback(host) || MatchesHostList(host, BypassList);
    }

    /// <summary>
    /// Whether <paramref name="host"/> matches an entry in a host list in the
    /// Windows proxy-override format (see <see cref="BypassList"/>). Also
    /// used for <see cref="InterceptingProxyListener.SkipDecryptionHosts"/>.
    /// </summary>
    internal static bool MatchesHostList(string host, IReadOnlyList<string> hostList)
    {
        foreach (var entry in hostList)
        {
            if (string.Equals(entry.Trim(), "<local>", StringComparison.OrdinalIgnoreCase))
            {
                if (!host.Contains('.') && !host.Contains(':'))
                {
                    return true;
                }

                continue;
            }

            var pattern = "^" + Regex.Escape(entry.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (Regex.IsMatch(host, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when this proxy is VioLens itself listening on <paramref name="ownPort"/> (a loopback address on that port).</summary>
    public bool IsSelf(int ownPort) => Port == ownPort && IsLoopback(Host);

    /// <summary>
    /// Connects to this proxy and asks it for a tunnel to
    /// <paramref name="targetHost"/>:<paramref name="targetPort"/>. Returns
    /// the connected client; its stream then carries the tunnelled
    /// connection (TLS to the target goes over it). Throws
    /// <see cref="IOException"/> when the proxy refuses.
    /// </summary>
    public Task<TcpClient> ConnectTunnelAsync(string targetHost, int targetPort, CancellationToken cancellationToken) =>
        ConnectTunnelAsync(targetHost, targetPort, credential: null, cancellationToken);

    /// <summary>
    /// As <see cref="ConnectTunnelAsync(string, int, CancellationToken)"/>,
    /// signing in when the proxy asks (407) -- see
    /// <see cref="ProxyAuthenticator"/>: as the signed-in Windows account,
    /// or with <paramref name="credential"/> when given.
    /// </summary>
    public async Task<TcpClient> ConnectTunnelAsync(
        string targetHost, int targetPort, NetworkCredential? credential, CancellationToken cancellationToken)
    {
        using var authenticator = new ProxyAuthenticator(Host, credential);
        string? authorization = null;

        // A proxy may close the connection after a 407; the sign-in then
        // carries on over a new one.
        for (var connection = 0; connection < 3; connection++)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(Host, Port, cancellationToken);
                var stream = client.GetStream();
                while (true)
                {
                    var reply = await SendConnectAsync(stream, targetHost, targetPort, authorization, ToString(), cancellationToken);
                    if (reply.StatusCode is >= 200 and <= 299)
                    {
                        return client;
                    }

                    if (reply.StatusCode != 407)
                    {
                        throw Refused(reply, targetHost, targetPort, ToString());
                    }

                    authorization = authenticator.Respond(reply.Headers)
                        ?? throw new IOException(SignInFailedMessage(authenticator, credential));

                    if (KeepsConnection(reply))
                    {
                        await Http1MessageReader.ReadBodyAsync(stream, reply.Headers, cancellationToken);
                        continue;
                    }

                    break; // reconnect and carry on
                }
            }
            catch
            {
                client.Dispose();
                throw;
            }

            client.Dispose();
        }

        throw new IOException(SignInFailedMessage(authenticator, credential));
    }

    /// <summary>
    /// Sends <c>CONNECT</c> for <paramref name="targetHost"/>:<paramref name="targetPort"/>
    /// on <paramref name="stream"/> (already connected to a proxy) and reads
    /// the proxy's answer. Returns once the tunnel is open; throws
    /// <see cref="IOException"/> otherwise. Used by the Composer, which
    /// sends through VioLens's own listener.
    /// </summary>
    public static async Task EstablishTunnelAsync(
        Stream stream, string targetHost, int targetPort, string proxyName, CancellationToken cancellationToken)
    {
        var reply = await SendConnectAsync(stream, targetHost, targetPort, authorization: null, proxyName, cancellationToken);
        if (reply.StatusCode is < 200 or > 299)
        {
            throw Refused(reply, targetHost, targetPort, proxyName);
        }
    }

    /// <summary>
    /// A user name and password as typed: "DOMAIN\user" or "user@domain"
    /// keep their domain. Null when <paramref name="userName"/> is empty.
    /// </summary>
    public static NetworkCredential? CredentialFor(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var name = userName.Trim();
        var backslash = name.IndexOf('\\');
        return backslash > 0
            ? new NetworkCredential(name[(backslash + 1)..], password ?? string.Empty, name[..backslash])
            : new NetworkCredential(name, password ?? string.Empty);
    }

    internal static string SignInFailedMessage(ProxyAuthenticator authenticator, NetworkCredential? credential)
    {
        var offered = authenticator.OfferedSchemes.Count == 0 ? "no sign-in method" : string.Join(", ", authenticator.OfferedSchemes);
        return credential is null
            ? $"The proxy asked for a sign-in (it offers {offered}) and signing in as you didn't work. " +
              "Enter a user name and password for the proxy under Tools > Connections."
            : $"The proxy didn't accept the user name and password from Tools > Connections (it offers {offered}).";
    }

    private static async Task<CapturedResponse> SendConnectAsync(
        Stream stream, string targetHost, int targetPort, string? authorization, string proxyName, CancellationToken cancellationToken)
    {
        var authority = $"{FormatHost(targetHost)}:{targetPort}";
        var connect = new StringBuilder($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: keep-alive\r\n");
        if (authorization is not null)
        {
            connect.Append("Proxy-Authorization: ").Append(authorization).Append("\r\n");
        }

        connect.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(connect.ToString()), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        try
        {
            return await Http1MessageReader.ReadResponsePreambleAsync(stream, cancellationToken)
                ?? throw new IOException($"Proxy {proxyName} closed the connection before answering CONNECT.");
        }
        catch (InvalidDataException ex)
        {
            throw new IOException($"Proxy {proxyName} sent an unreadable answer to CONNECT {authority}: {ex.Message}", ex);
        }
    }

    private static IOException Refused(CapturedResponse reply, string targetHost, int targetPort, string proxyName) =>
        new($"Proxy {proxyName} refused CONNECT {FormatHost(targetHost)}:{targetPort}: \"{reply.StatusCode} {reply.ReasonPhrase}\".");

    /// <summary>Whether the proxy keeps the connection open after this answer, so the sign-in can carry on over it.</summary>
    internal static bool KeepsConnection(CapturedResponse reply)
    {
        var connection = (ContentDecoder.FindHeader(reply.Headers, "Proxy-Connection") ?? string.Empty) + "," +
                         (ContentDecoder.FindHeader(reply.Headers, "Connection") ?? string.Empty);
        if (connection.Split(',').Any(token => string.Equals(token.Trim(), "close", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !Http1MessageReader.IsCloseDelimited(reply.Headers) &&
               !string.Equals(reply.HttpVersion, "HTTP/1.0", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}
