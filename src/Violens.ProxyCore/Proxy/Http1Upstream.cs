using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// One client connection's HTTP/1.1 connection to the server: opened the
/// first time a request has to reach it, then reused. Directly, through the
/// network's proxy, or as its PAC script says. A plain HTTP client can move
/// on to another server, which replaces the connection.
/// </summary>
internal sealed class Http1Upstream(
    bool isTls,
    string targetHost,
    int targetPort,
    Func<bool, string, int, string, CancellationToken, Task<IReadOnlyList<UpstreamProxy?>>> chooseRoutes,
    Func<NetworkCredential?> proxyCredential) : IUpstream
{
    private TcpClient? _client;
    private Stream? _stream;
    private string? _key;
    private bool _sendAbsoluteForm;
    private UpstreamProxy? _route;
    private int _connectionId;
    private UpstreamTlsProbe? _tlsProbe;

    public string TargetHost { get; private set; } = targetHost;

    public int TargetPort { get; private set; } = targetPort;

    public SessionTimingRecorder? Timing { get; set; }

    public bool ClientCertificateRequested => _tlsProbe?.ClientCertificateRequested == true;

    public Stream Stream => _stream!;

    /// <summary>The server a plain HTTP request names.</summary>
    public void SetTarget(string host, int port)
    {
        TargetHost = host;
        TargetPort = port;
    }

    public void RouteFrom(CapturedRequest request)
    {
        if (!isTls && PlainHttpRequest.TryGetRoute(request.Headers, out var host, out var port))
        {
            SetTarget(host, port);
        }
    }

    public async Task ConnectAsync(string target, CancellationToken cancellationToken)
    {
        var key = $"{TargetHost}:{TargetPort}";
        if (_stream is not null && _key != key)
        {
            // A plain HTTP client moved on to a different server.
            _stream.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
        }

        if (_stream is not null && Timing is not null)
        {
            Timing.ConnectionReused = true;
            Timing.ConnectionId = _connectionId;
            Timing.Proxy = _route?.ToString();
            Timing.TlsProbe = _tlsProbe;
        }

        if (_stream is not null)
        {
            return;
        }

        // Trying the next route when one can't be reached, as browsers do.
        _client?.Dispose(); // left over from a failed TLS handshake
        _client = null;
        _tlsProbe = null;
        var routes = await chooseRoutes(isTls, TargetHost, TargetPort, target, cancellationToken);
        Timing?.ConnectStarted();
        UpstreamProxy? route = null;
        for (var i = 0; _client is null; i++)
        {
            route = routes[i];
            try
            {
                _client = await OpenRouteAsync(route, cancellationToken);
            }
            catch (SocketException ex) when (i + 1 < routes.Count)
            {
                ProxyEngine.Log("proxy",
                    $"{TargetHost}: couldn't reach {InterceptingProxyListener.DescribeRoute(route)} ({ex.Message}); trying {InterceptingProxyListener.DescribeRoute(routes[i + 1])}.");
            }
        }

        Timing?.Connected();
        _connectionId = InterceptingProxyListener.NextConnectionId();
        if (Timing is not null)
        {
            Timing.ConnectionId = _connectionId;
            Timing.Proxy = route?.ToString();
        }

        if (isTls)
        {
            var tls = new SslStream(_client.GetStream(), leaveInnerStreamOpen: false);

            // The probe records a client-certificate request on the session.
            // Default certificate validation: a real upstream certificate problem
            // should show, not be hidden.
            _tlsProbe = new UpstreamTlsProbe();
            _tlsProbe.Attach(tls);
            if (Timing is not null)
            {
                Timing.TlsProbe = _tlsProbe;
            }

            await tls.AuthenticateAsClientAsync(_tlsProbe.CreateOptions(TargetHost), cancellationToken);
            Timing?.TlsDone();
            _stream = new ReadBufferedStream(tls);
            _sendAbsoluteForm = false;
        }
        else
        {
            // Plain HTTP goes to a proxy as an ordinary proxy
            // request (full URL in the request line).
            _stream = new ReadBufferedStream(_client.GetStream());
            _sendAbsoluteForm = route is not null;
        }

        _route = route;
        _key = key;
    }

    // One route: a CONNECT tunnel through a proxy for HTTPS, a plain
    // connection to the proxy for HTTP, or straight to the server.
    private async Task<TcpClient> OpenRouteAsync(UpstreamProxy? route, CancellationToken cancellationToken)
    {
        if (route is not null && isTls)
        {
            return await route.ConnectTunnelAsync(TargetHost, TargetPort, proxyCredential(), cancellationToken);
        }

        var client = new TcpClient();
        try
        {
            // The address is looked up first, on its own, so the
            // waterfall can show DNS time apart from connecting.
            var (host, port) = route is null ? (TargetHost, TargetPort) : (route.Host, route.Port);
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            Timing?.DnsDone();
            await client.ConnectAsync(addresses, port, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task SendRequestAsync(CapturedRequest request, CancellationToken cancellationToken) =>
        HttpMessageWriter.WriteRequestAsync(_stream!, ForUpstream(request), cancellationToken);

    public async Task<Http1MessageReader.RelayedBody> SendRequestRelayingBodyAsync(
        CapturedRequest preamble, Stream bodySource, long maxCapturedBodyBytes, CancellationToken cancellationToken)
    {
        await HttpMessageWriter.WriteRequestPreambleAsync(_stream!, ForUpstream(preamble), cancellationToken);
        return await Http1MessageReader.RelayBodyAsync(
            bodySource, _stream!, preamble.Headers, cancellationToken, maxCapturedBodyBytes);
    }

    public Task<CapturedResponse?> ReadResponsePreambleAsync(CancellationToken cancellationToken) =>
        Http1MessageReader.ReadResponsePreambleAsync(_stream!, cancellationToken);

    public async Task<CapturedResponse?> SignInIfAskedAsync(
        CapturedRequest request, CapturedResponse response, CancellationToken cancellationToken)
    {
        // For HTTPS the sign-in happened when the tunnel was opened.
        if (response.StatusCode != 407 || isTls || _route is null)
        {
            return response;
        }

        // Sign in (see ProxyAuthenticator) and send the request again, on
        // the same connection when the proxy keeps it open. The answer to
        // pass on is the real response, or the last 407 when signing in
        // didn't work.
        var proxy = _route;
        var credential = proxyCredential();
        using var authenticator = new ProxyAuthenticator(proxy.Host, credential);
        var reply = response;
        while (reply.StatusCode == 407)
        {
            var authorization = authenticator.Respond(reply.Headers);
            if (authorization is null)
            {
                ProxyEngine.Log("proxy", $"{proxy}: {UpstreamProxy.SignInFailedMessage(authenticator, credential)}");
                return reply;
            }

            if (UpstreamProxy.KeepsConnection(reply))
            {
                await Http1MessageReader.ReadBodyAsync(_stream!, reply.Headers, cancellationToken);
            }
            else
            {
                _stream?.Dispose();
                _client?.Dispose();
                _stream = null;
                _client = new TcpClient();
                await _client.ConnectAsync(proxy.Host, proxy.Port, cancellationToken);
                _stream = new ReadBufferedStream(_client.GetStream());
            }

            var stream = _stream!;
            var outgoing = ForUpstream(request);
            outgoing = outgoing with { Headers = [.. outgoing.Headers, ("Proxy-Authorization", authorization)] };
            await HttpMessageWriter.WriteRequestAsync(stream, outgoing, cancellationToken);
            var next = await Http1MessageReader.ReadResponsePreambleAsync(stream, cancellationToken);
            if (next is null)
            {
                return null;
            }

            reply = next;
        }

        return reply;
    }

    public Task<byte[]> ReadResponseBodyAsync(CapturedResponse preamble, CancellationToken cancellationToken) =>
        Http1MessageReader.ReadResponseBodyAsync(_stream!, preamble.Headers, cancellationToken);

    public Task<Http1MessageReader.RelayedBody> RelayResponseBodyAsync(
        CapturedResponse preamble, Stream destination, long maxCapturedBodyBytes, CancellationToken cancellationToken) =>
        Http1MessageReader.RelayResponseBodyAsync(_stream!, destination, preamble.Headers, cancellationToken, maxCapturedBodyBytes);

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }

    // What actually goes to the server (or the network's proxy) for a
    // request: unchanged for HTTPS; for plain HTTP, without the
    // proxy-only headers, and with the full URL when going via a proxy.
    private CapturedRequest ForUpstream(CapturedRequest outgoing) =>
        isTls ? outgoing : PlainHttpRequest.ForUpstream(outgoing, _sendAbsoluteForm);
}
