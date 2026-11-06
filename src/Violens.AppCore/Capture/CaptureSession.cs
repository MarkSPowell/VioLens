using System.Security.Cryptography.X509Certificates;
using Violens.ProxyCore;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Violens.ProxyCore.SystemProxy;

namespace Violens.AppCore.Capture;

/// <summary>
/// What VioLens Capture (apps/Violens.Capture, roadmap item 13) does, kept
/// out of its window: start capturing as the system proxy, stop and put the
/// proxy settings back, save a SAZ, and take its certificate away again.
/// No breakpoints, rules, scripts or extensions: a capture records traffic
/// and changes nothing.
///
/// With <c>decryptHttps</c> off, nothing is trusted or stored: HTTPS is
/// tunnelled (one CONNECT session per connection) through a throwaway
/// in-memory root that's never used. With it on, VioLens's own root is
/// created and trusted as the main app does, and shared with it.
/// Not thread-safe: call from one thread (the UI's).
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private readonly Action<string> _log;
    private InterceptingProxyListener? _listener;
    private UpstreamProxyDetection? _upstream;
    private bool _trustedRoot;

    public CaptureSession(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public SessionStore Sessions { get; } = new();

    public bool IsCapturing => _listener is not null;

    /// <summary>Whether this run trusted VioLens's root, so there's a certificate to remove.</summary>
    public bool TrustedRoot => _trustedRoot;

    /// <summary>The port while capturing, otherwise null.</summary>
    public int? Port => _listener?.Port;

    /// <summary>
    /// Starts capturing and makes this the system proxy. Returns a sentence
    /// for the window to show; throws if the listener can't start. Failing
    /// to become the system proxy isn't an error: the capture runs, and the
    /// message says to point the browser at it.
    /// </summary>
    public string Start(bool decryptHttps)
    {
        if (_listener is not null)
        {
            return "Already capturing.";
        }

        SystemProxyController.RecoverFromCrash();

        X509Certificate2 root;
        if (decryptHttps)
        {
            root = new CertificateAuthority().RootCertificate;
            _trustedRoot = true;
        }
        else
        {
            root = CertificateAuthority.GenerateRoot(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), Sessions);
        if (!decryptHttps)
        {
            listener.SkipDecryptionHosts = ["*"];
        }

        // The network's own proxy, read before this one replaces it.
        _upstream = SystemProxyController.DetectUpstream(listener.Port);
        listener.Gateway = _upstream.Proxy;
        listener.AutoConfig = _upstream.AutoConfig;
        _listener = listener;

        try
        {
            SystemProxyController.Enable(listener.Port);
            return "Capturing. Now do what shows the problem, then click Stop Capture.";
        }
        catch (Exception ex)
        {
            _log($"Couldn't become the system proxy: {ex.Message}");
            return $"Capturing, but the computer's proxy setting couldn't be changed ({ex.Message}). " +
                   $"Set your browser's proxy to 127.0.0.1 port {listener.Port} by hand.";
        }
    }

    /// <summary>Stops capturing and puts the computer's proxy setting back. Does nothing when not capturing.</summary>
    public void Stop()
    {
        if (_listener is null)
        {
            return;
        }

        _listener.Stop();
        _listener = null;
        try
        {
            SystemProxyController.Disable();
        }
        catch (Exception ex)
        {
            _log($"Couldn't put the proxy setting back: {ex.Message}");
        }

        _upstream?.AutoConfig?.Dispose();
        _upstream = null;
    }

    /// <summary>Writes every session so far to a .saz file.</summary>
    public int Save(string path)
    {
        var sessions = Sessions.Snapshot();
        SazWriter.Write(path, sessions);
        return sessions.Count;
    }

    /// <summary>Removes VioLens's root certificates (this run's, and any left by VioLens itself) and returns how many went.</summary>
    public int RemoveCertificate()
    {
        Stop();
        var removed = CertificateAuthority.RemoveRoots(ProxyEngine.Identity.RootCertificateName, _log);
        _trustedRoot = false;
        return removed;
    }

    /// <summary>
    /// "128 requests to 14 sites", for the window: what's been captured,
    /// in words a non-technical person can read back to support.
    /// </summary>
    public static string Describe(IReadOnlyList<Session> sessions)
    {
        if (sessions.Count == 0)
        {
            return "Nothing captured yet.";
        }

        var sites = sessions.Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var requests = sessions.Count == 1 ? "1 request" : $"{sessions.Count:N0} requests";
        return $"{requests} to {(sites == 1 ? "1 site" : $"{sites:N0} sites")}.";
    }

    public void Dispose() => Stop();
}
