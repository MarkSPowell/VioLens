using System.Collections.Concurrent;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// HTTP/1.1 connections to one server, shared by the streams of one HTTP/2
/// client connection. HTTP/1.1 carries one exchange at a time, so each
/// stream rents a connection for its exchange; at most
/// <paramref name="maxConnections"/> are open at once (browsers use six per
/// server), and further streams wait for one to come back.
/// </summary>
internal sealed class Http1UpstreamPool(Func<bool, Http1Upstream> create, int maxConnections = 6) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(maxConnections, maxConnections);
    private readonly ConcurrentBag<Http1Upstream> _idle = [];

    /// <summary>
    /// An idle connection, or a new unconnected one. <paramref name="offerHttp2"/>
    /// asks for a new one that offers HTTP/2 when it connects.
    /// </summary>
    public async Task<Http1Upstream> RentAsync(CancellationToken cancellationToken, bool offerHttp2 = false)
    {
        await _slots.WaitAsync(cancellationToken);
        if (!offerHttp2 && _idle.TryTake(out var upstream))
        {
            upstream.DropIfServerClosed();
            return upstream;
        }

        return create(offerHttp2);
    }

    /// <summary>
    /// Hands a connection back: kept for the next stream when it can carry
    /// another exchange, closed otherwise.
    /// </summary>
    public void Return(Http1Upstream upstream, bool reusable)
    {
        if (reusable)
        {
            upstream.Timing = null;
            _idle.Add(upstream);
        }
        else
        {
            upstream.Dispose();
        }

        _slots.Release();
    }

    public void Dispose()
    {
        while (_idle.TryTake(out var upstream))
        {
            upstream.Dispose();
        }
    }
}
