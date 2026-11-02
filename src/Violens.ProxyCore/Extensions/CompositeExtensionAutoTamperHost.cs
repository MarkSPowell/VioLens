using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Extensions;

/// <summary>
/// Runs several hosts in order as one, each seeing the previous one's
/// edits, so the listener needn't know about more than one extension source.
/// </summary>
internal sealed class CompositeExtensionAutoTamperHost : IExtensionAutoTamperHost
{
    private readonly IReadOnlyList<IExtensionAutoTamperHost> _hosts;

    public CompositeExtensionAutoTamperHost(IReadOnlyList<IExtensionAutoTamperHost> hosts)
    {
        _hosts = hosts;
    }

    /// <inheritdoc/>
    public bool HasAnyRequestBeforeHandlers => _hosts.Any(h => h.HasAnyRequestBeforeHandlers);

    /// <inheritdoc/>
    public bool HasAnyResponseBeforeHandlers => _hosts.Any(h => h.HasAnyResponseBeforeHandlers);

    /// <inheritdoc/>
    public CapturedRequest RunRequestBefore(int sessionOrdinal, string hostname, CapturedRequest request)
    {
        var current = request;
        foreach (var host in _hosts)
        {
            current = host.RunRequestBefore(sessionOrdinal, hostname, current);
        }

        return current;
    }

    /// <inheritdoc/>
    public void RunRequestAfter(int sessionOrdinal, string hostname, CapturedRequest request)
    {
        foreach (var host in _hosts)
        {
            host.RunRequestAfter(sessionOrdinal, hostname, request);
        }
    }

    /// <inheritdoc/>
    public CapturedResponse RunResponseBefore(int sessionOrdinal, string hostname, CapturedRequest request, CapturedResponse response)
    {
        var current = response;
        foreach (var host in _hosts)
        {
            current = host.RunResponseBefore(sessionOrdinal, hostname, request, current);
        }

        return current;
    }

    /// <inheritdoc/>
    public void RunResponseAfter(int sessionOrdinal, string hostname, CapturedRequest request, CapturedResponse response)
    {
        foreach (var host in _hosts)
        {
            host.RunResponseAfter(sessionOrdinal, hostname, request, response);
        }
    }
}
