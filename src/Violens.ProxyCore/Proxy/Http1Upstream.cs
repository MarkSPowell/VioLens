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
    Func<NetworkCredential?> proxyCredential,
    RemoteCertificateValidationCallback? validateServer = null,
    bool offerHttp2 = false) : IUpstream
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

    /// <summary>
    /// Whether the server picked HTTP/2 in the TLS handshake (only possible
    /// when this was created to offer it). The connection is then for
    /// <see cref="DetachConnection"/>, not for HTTP/1.1.
    /// </summary>
    public bool NegotiatedHttp2 { get; private set; }

    public Stream Stream => _stream!;

    // The HTTP/1.1 reader drops trailers.
    public IReadOnlyList<(string Name, string Value)>? ResponseTrailers => null;

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

            var options = _tlsProbe.CreateOptions(TargetHost, validateServer);
            if (offerHttp2)
            {
                options.ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11];
            }

            await tls.AuthenticateAsClientAsync(options, cancellationToken);
            Timing?.TlsDone();
            NegotiatedHttp2 = tls.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2;

            // Settled on HTTP/1.1: a reconnect later mustn't offer HTTP/2 on a
            // connection that's in use as HTTP/1.1.
            offerHttp2 &= NegotiatedHttp2;
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
        CapturedRequest preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken)
    {
        if (client is Http1ClientExchange http1)
        {
            // HTTP/1.1 both sides: the body's own framing passes through unchanged.
            await HttpMessageWriter.WriteRequestPreambleAsync(_stream!, ForUpstream(preamble), cancellationToken);
            return await Http1MessageReader.RelayBodyAsync(
                http1.Stream, _stream!, preamble.Headers, cancellationToken, maxCapturedBodyBytes);
        }

        return await RelayFramingAsync(preamble, (IClientBodyStream)client, maxCapturedBodyBytes, cancellationToken);
    }

    /// <summary>
    /// Relays a body that arrives without HTTP/1.1 framing (from an HTTP/2
    /// stream): as is with its Content-Length, or chunked when it has none.
    /// </summary>
    private async Task<Http1MessageReader.RelayedBody> RelayFramingAsync(
        CapturedRequest preamble, IClientBodyStream client, long maxCapturedBodyBytes, CancellationToken cancellationToken)
    {
        var chunked = client.RequestHasBody && ContentDecoder.FindHeader(preamble.Headers, "Content-Length") is null;
        var outgoing = ForUpstream(preamble);
        if (chunked)
        {
            outgoing = outgoing with { Headers = [.. outgoing.Headers, ("Transfer-Encoding", "chunked")] };
        }

        await HttpMessageWriter.WriteRequestPreambleAsync(_stream!, outgoing, cancellationToken);
        var captured = new MemoryStream();
        long length = 0;
        if (client.RequestHasBody)
        {
            var buffer = new byte[16384];
            int read;
            while ((read = await client.ReadRequestBodyAsync(buffer, cancellationToken)) > 0)
            {
                if (chunked)
                {
                    await _stream!.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"{read:x}\r\n"), cancellationToken);
                }

                await _stream!.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (chunked)
                {
                    await _stream!.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
                }

                var room = maxCapturedBodyBytes - captured.Length;
                if (room > 0)
                {
                    captured.Write(buffer, 0, (int)Math.Min(room, read));
                }

                length += read;
            }
        }

        if (chunked)
        {
            await _stream!.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
        }

        await _stream!.FlushAsync(cancellationToken);
        return new Http1MessageReader.RelayedBody(captured.ToArray(), length);
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
        CapturedResponse preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken) =>
        client is Http1ClientExchange http1
            ? Http1MessageReader.RelayResponseBodyAsync(_stream!, http1.Stream, preamble.Headers, cancellationToken, maxCapturedBodyBytes)
            : Http1MessageReader.RelayDecodedResponseBodyAsync(
                _stream!, preamble.Headers, ((IClientBodyStream)client).WriteResponseBodyAsync, cancellationToken, maxCapturedBodyBytes);

    /// <summary>
    /// Hands the open connection over (for HTTP/2, once the server picked
    /// it), leaving this upstream without one.
    /// </summary>
    public UpstreamConnection DetachConnection()
    {
        var connection = new UpstreamConnection(_client!, _stream!, _route, _connectionId, _tlsProbe);
        _client = null;
        _stream = null;
        _key = null;
        NegotiatedHttp2 = false;
        return connection;
    }

    /// <summary>
    /// Before an idle connection is used again: if the server has closed it
    /// (or sent anything unasked, which is the same as far as reuse goes),
    /// it's dropped, and the next request connects afresh.
    /// </summary>
    public void DropIfServerClosed()
    {
        try
        {
            if (_client?.Client is not { } socket || !socket.Poll(0, SelectMode.SelectRead))
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
        }

        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }

    // What actually goes to the server (or the network's proxy) for a
    // request: unchanged for HTTPS; for plain HTTP, without the
    // proxy-only headers, and with the full URL when going via a proxy.
    // A request that arrived over HTTP/2 goes on as HTTP/1.1.
    private CapturedRequest ForUpstream(CapturedRequest outgoing)
    {
        var request = isTls ? outgoing : PlainHttpRequest.ForUpstream(outgoing, _sendAbsoluteForm);
        return request.HttpVersion.StartsWith("HTTP/1", StringComparison.OrdinalIgnoreCase)
            ? request
            : request with { HttpVersion = "HTTP/1.1" };
    }
}

/// <summary>An open connection to a server: the socket, the stream to use (TLS), how it was reached, and its number for the waterfall.</summary>
internal sealed record UpstreamConnection(
    TcpClient Client, Stream Stream, UpstreamProxy? Route, int ConnectionId, UpstreamTlsProbe? TlsProbe);
