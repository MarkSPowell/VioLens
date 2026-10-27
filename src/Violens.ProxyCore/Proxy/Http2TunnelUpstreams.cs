using Violens.ProxyCore.Http;
using Violens.ProxyCore.Http2;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// The server side of one HTTP/2 client connection's tunnel. The first
/// exchange that needs the server connects, offering HTTP/2 and HTTP/1.1.
/// If the server picks HTTP/2, every exchange is a stream on that one
/// connection (a new one is opened if it closes or the server sends
/// GOAWAY). If it picks HTTP/1.1, exchanges share a small pool of HTTP/1.1
/// connections instead. Exchanges that arrive while the first is still
/// connecting wait for it, rather than all connecting at once.
/// </summary>
internal sealed class Http2TunnelUpstreams(string targetHost, int targetPort, Func<bool, Http1Upstream> create) : IAsyncDisposable
{
    private readonly Http1UpstreamPool _pool = new(create);
    private readonly SemaphoreSlim _negotiating = new(1, 1);
    private readonly object _sync = new();
    private readonly List<(Http2Connection Connection, UpstreamConnection Opened)> _http2Connections = [];
    private Http2Connection? _http2;
    private UpstreamConnection? _http2Opened;
    private volatile bool _serverChoseHttp11;

    /// <summary>The upstream for one exchange; it connects (or joins the shared connection) only if the exchange needs the server.</summary>
    public NegotiatingUpstream CreateUpstream() => new(this, targetHost, targetPort);

    /// <summary>
    /// Ends an exchange's use of the server: an HTTP/1.1 connection goes back
    /// to the pool (closed unless <paramref name="reusable"/>); an HTTP/2
    /// stream left unfinished is reset.
    /// </summary>
    public void Release(NegotiatingUpstream upstream, bool reusable)
    {
        switch (upstream.Inner)
        {
            case Http1Upstream http1:
                _pool.Return(http1, reusable);
                break;
            case Http2Upstream http2:
                http2.Dispose();
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<(Http2Connection Connection, UpstreamConnection Opened)> connections;
        lock (_sync)
        {
            connections = [.. _http2Connections];
            _http2Connections.Clear();
        }

        foreach (var (connection, opened) in connections)
        {
            await connection.DisposeAsync();
            opened.Client.Dispose();
        }

        _pool.Dispose();
        _negotiating.Dispose();
    }

    /// <summary>Finds or opens the server connection for <paramref name="exchange"/> and sets its <see cref="NegotiatingUpstream.Inner"/>.</summary>
    internal async Task AttachAsync(NegotiatingUpstream exchange, string target, CancellationToken cancellationToken)
    {
        if (TryJoinHttp2(exchange) || await TryUseHttp11Async(exchange, target, cancellationToken))
        {
            return;
        }

        await _negotiating.WaitAsync(cancellationToken);
        try
        {
            // Another exchange may have finished negotiating while this one waited.
            if (TryJoinHttp2(exchange) || await TryUseHttp11Async(exchange, target, cancellationToken))
            {
                return;
            }

            var upstream = await _pool.RentAsync(cancellationToken, offerHttp2: true);
            upstream.Timing = exchange.Timing;
            exchange.Inner = upstream;
            await upstream.ConnectAsync(target, cancellationToken);
            if (!upstream.NegotiatedHttp2)
            {
                _serverChoseHttp11 = true;
                return;
            }

            var opened = upstream.DetachConnection();
            exchange.Inner = null;
            _pool.Return(upstream, reusable: false);
            Http2Connection connection;
            try
            {
                connection = await Http2Connection.ConnectAsync(opened.Stream, new Http2Settings(), cancellationToken);
            }
            catch
            {
                await opened.Stream.DisposeAsync();
                opened.Client.Dispose();
                throw;
            }

            lock (_sync)
            {
                _http2 = connection;
                _http2Opened = opened;
                _http2Connections.Add((connection, opened));
            }

            exchange.Inner = new Http2Upstream(connection, opened, targetHost, targetPort, openedForThisExchange: true) { Timing = exchange.Timing };
        }
        finally
        {
            _negotiating.Release();
        }
    }

    private bool TryJoinHttp2(NegotiatingUpstream exchange)
    {
        lock (_sync)
        {
            if (_http2 is null)
            {
                return false;
            }

            if (!_http2.CanOpenStreams)
            {
                // Closed, or the server sent GOAWAY: the next exchange opens a new one.
                _http2 = null;
                _http2Opened = null;
                return false;
            }

            exchange.Inner = new Http2Upstream(_http2, _http2Opened!, targetHost, targetPort, openedForThisExchange: false) { Timing = exchange.Timing };
            return true;
        }
    }

    private async Task<bool> TryUseHttp11Async(NegotiatingUpstream exchange, string target, CancellationToken cancellationToken)
    {
        if (!_serverChoseHttp11)
        {
            return false;
        }

        var upstream = await _pool.RentAsync(cancellationToken);
        upstream.Timing = exchange.Timing;
        exchange.Inner = upstream;
        await upstream.ConnectAsync(target, cancellationToken);
        return true;
    }
}

/// <summary>
/// The <see cref="IUpstream"/> an exchange in an HTTP/2 tunnel starts with:
/// until <see cref="ConnectAsync"/>, which protocol the server speaks isn't
/// known; from then on everything goes to the HTTP/1.1 connection or HTTP/2
/// stream <see cref="Http2TunnelUpstreams"/> picked.
/// </summary>
internal sealed class NegotiatingUpstream(Http2TunnelUpstreams tunnel, string targetHost, int targetPort) : IUpstream
{
    private SessionTimingRecorder? _timing;

    /// <summary>The HTTP/1.1 connection or HTTP/2 stream in use; null until connected.</summary>
    internal IUpstream? Inner { get; set; }

    public string TargetHost => targetHost;

    public int TargetPort => targetPort;

    public SessionTimingRecorder? Timing
    {
        get => _timing;
        set
        {
            _timing = value;
            if (Inner is not null)
            {
                Inner.Timing = value;
            }
        }
    }

    public bool ClientCertificateRequested => Inner?.ClientCertificateRequested == true;

    public Stream Stream => Connected.Stream;

    public IReadOnlyList<(string Name, string Value)>? ResponseTrailers => Inner?.ResponseTrailers;

    // HTTPS only: the tunnel's server never changes.
    public void RouteFrom(CapturedRequest request)
    {
    }

    public async Task ConnectAsync(string target, CancellationToken cancellationToken)
    {
        if (Inner is not null)
        {
            await Inner.ConnectAsync(target, cancellationToken);
            return;
        }

        await tunnel.AttachAsync(this, target, cancellationToken);
        await Inner!.ConnectAsync(target, cancellationToken);
    }

    public Task SendRequestAsync(CapturedRequest request, CancellationToken cancellationToken) =>
        Connected.SendRequestAsync(request, cancellationToken);

    public Task<Http1MessageReader.RelayedBody> SendRequestRelayingBodyAsync(
        CapturedRequest preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken) =>
        Connected.SendRequestRelayingBodyAsync(preamble, client, maxCapturedBodyBytes, cancellationToken);

    public Task<CapturedResponse?> ReadResponsePreambleAsync(CancellationToken cancellationToken) =>
        Connected.ReadResponsePreambleAsync(cancellationToken);

    public Task<CapturedResponse?> SignInIfAskedAsync(CapturedRequest request, CapturedResponse response, CancellationToken cancellationToken) =>
        Connected.SignInIfAskedAsync(request, response, cancellationToken);

    public Task<byte[]> ReadResponseBodyAsync(CapturedResponse preamble, CancellationToken cancellationToken) =>
        Connected.ReadResponseBodyAsync(preamble, cancellationToken);

    public Task<Http1MessageReader.RelayedBody> RelayResponseBodyAsync(
        CapturedResponse preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken) =>
        Connected.RelayResponseBodyAsync(preamble, client, maxCapturedBodyBytes, cancellationToken);

    // Released through Http2TunnelUpstreams.Release, which knows where the connection goes back to.
    public void Dispose()
    {
    }

    private IUpstream Connected => Inner ?? throw new InvalidOperationException("The exchange hasn't connected to the server.");
}
