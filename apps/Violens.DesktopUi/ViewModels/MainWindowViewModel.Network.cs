using Avalonia.Threading;
using Violens.AppCore.Certificates;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Proxy.AutoConfig;
using Violens.ProxyCore.SystemProxy;

namespace Violens.DesktopUi.ViewModels;

/// <summary>
/// Tools &gt; Connections: forwarding through the network's own proxy
/// (<see cref="UpstreamProxy"/>, or the network's automatic configuration
/// script via <see cref="ProxyAutoConfig"/>), and letting phones and other
/// computers use VioLens as their proxy.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const string DirectSetting = "none";

    private bool _showConnectionsRow;
    private string _upstreamProxySetting = string.Empty;
    private bool _allowRemoteClients;
    private bool _http2Enabled;
    private string _skipDecryptionHostsText = string.Empty;
    private List<ClientCertificateChoice> _clientCertificateChoices = [];
    private string _clientCertificateThumbprint = string.Empty;
    private string _clientCertificateHostsText = string.Empty;
    private string _gatewayStatus = "Upstream proxy: decided when capture starts.";
    private UpstreamProxyDetection? _detectedUpstream;
    private string _proxyUserName = string.Empty;
    private string _proxyPassword = string.Empty;

    /// <summary>Whether the Connections row shows (Tools &gt; Connections). Purely a visibility switch.</summary>
    public bool ShowConnectionsRow
    {
        get => _showConnectionsRow;
        set
        {
            if (SetField(ref _showConnectionsRow, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.ShowConnectionsRow, value);
            }
        }
    }

    /// <summary>
    /// The upstream proxy: empty for automatic (whatever the system proxy
    /// was before VioLens took over), "none" to connect directly, or
    /// "host:port". Applies immediately while capturing.
    /// </summary>
    public string UpstreamProxySetting
    {
        get => _upstreamProxySetting;
        set
        {
            if (SetField(ref _upstreamProxySetting, value ?? string.Empty))
            {
                _preferences.SetStringPref(PreferenceKeys.UpstreamProxy, _upstreamProxySetting);
                if (_proxy is not null)
                {
                    ApplyGateway(_proxy);
                }
            }
        }
    }

    /// <summary>
    /// The user name for an upstream proxy that asks for a sign-in. Empty
    /// means sign in as the current Windows account (Negotiate or NTLM),
    /// which is what most company proxies want. Saved; see
    /// <see cref="ProxyPassword"/> for the password.
    /// </summary>
    public string ProxyUserName
    {
        get => _proxyUserName;
        set
        {
            if (SetField(ref _proxyUserName, value ?? string.Empty))
            {
                _preferences.SetStringPref(PreferenceKeys.UpstreamProxyUserName, _proxyUserName);
                ApplyProxyCredential();
            }
        }
    }

    /// <summary>
    /// The password that goes with <see cref="ProxyUserName"/>. Kept in
    /// memory only, never written to disk, so it's asked for again after a
    /// restart.
    /// </summary>
    public string ProxyPassword
    {
        get => _proxyPassword;
        set
        {
            if (SetField(ref _proxyPassword, value ?? string.Empty))
            {
                ApplyProxyCredential();
            }
        }
    }

    private void ApplyProxyCredential()
    {
        if (_proxy is not null)
        {
            _proxy.ProxyCredential = UpstreamProxy.CredentialFor(ProxyUserName, ProxyPassword);
        }
    }

    /// <summary>
    /// Fiddler Classic's "Allow remote computers to connect": listen on every
    /// network interface, not just this machine, so a phone can use
    /// VioLens as its proxy. Takes effect the next time capture starts.
    /// </summary>
    public bool AllowRemoteClients
    {
        get => _allowRemoteClients;
        set
        {
            if (SetField(ref _allowRemoteClients, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.AllowRemoteClients, value);
                RaisePropertyChanged(nameof(RemoteClientsHint));
            }
        }
    }

    /// <summary>
    /// Lets browsers use HTTP/2 for HTTPS sites, and VioLens use it with the
    /// sites that support it. Applies to connections opened from now on,
    /// even while capturing.
    /// </summary>
    public bool Http2Enabled
    {
        get => _http2Enabled;
        set
        {
            if (SetField(ref _http2Enabled, value))
            {
                _preferences.SetBoolPref(PreferenceKeys.Http2, value);
                if (_proxy is not null)
                {
                    _proxy.Http2Enabled = value;
                }
            }
        }
    }

    /// <summary>
    /// Hosts to tunnel without decrypting (see
    /// InterceptingProxyListener.SkipDecryptionHosts), as typed: separated by
    /// semicolons, commas or spaces. Applies to connections opened from now
    /// on, even while capturing.
    /// </summary>
    public string SkipDecryptionHostsText
    {
        get => _skipDecryptionHostsText;
        set
        {
            if (SetField(ref _skipDecryptionHostsText, value ?? string.Empty))
            {
                var hosts = SkipDecryptionHosts;
                _preferences.SetListPref(PreferenceKeys.SkipDecryptionHosts, hosts);
                if (_proxy is not null)
                {
                    _proxy.SkipDecryptionHosts = hosts;
                }
            }
        }
    }

    private IReadOnlyList<string> SkipDecryptionHosts => SplitHostList(_skipDecryptionHostsText);

    private static IReadOnlyList<string> SplitHostList(string text) =>
        text.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The client certificate drop-down: "None", then each usable certificate (see ClientCertificateStore).</summary>
    public IReadOnlyList<string> ClientCertificateLabels =>
        ["None", .. _clientCertificateChoices.Select(c => c.Description)];

    /// <summary>
    /// Which client certificate is sent to <see cref="ClientCertificateHostsText"/>'s
    /// hosts when they ask for one (mutual TLS); 0 is none. Applies straight away.
    /// </summary>
    public int ClientCertificateIndex
    {
        get => _clientCertificateChoices.FindIndex(c => c.Thumbprint == _clientCertificateThumbprint) + 1;
        set
        {
            var thumbprint = value >= 1 && value <= _clientCertificateChoices.Count ? _clientCertificateChoices[value - 1].Thumbprint : string.Empty;
            if (thumbprint != _clientCertificateThumbprint)
            {
                _clientCertificateThumbprint = thumbprint;
                _preferences.SetStringPref(PreferenceKeys.ClientCertificate, thumbprint);
                ApplyClientCertificate();
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// The hosts the client certificate is sent to, as typed (* for every
    /// server that asks). Empty sends it to none.
    /// </summary>
    public string ClientCertificateHostsText
    {
        get => _clientCertificateHostsText;
        set
        {
            if (SetField(ref _clientCertificateHostsText, value ?? string.Empty))
            {
                _preferences.SetListPref(PreferenceKeys.ClientCertificateHosts, SplitHostList(_clientCertificateHostsText));
                ApplyClientCertificate();
            }
        }
    }

    /// <summary>Hands the chosen certificate and its hosts to the listener, loading the certificate from the store.</summary>
    private void ApplyClientCertificate()
    {
        if (_proxy is null)
        {
            return;
        }

        _proxy.ClientCertificate = ClientCertificateStore.Load(_clientCertificateThumbprint);
        _proxy.ClientCertificateHosts = SplitHostList(_clientCertificateHostsText);
    }

    /// <summary>Which upstream proxy is in use, for the Connections row.</summary>
    public string GatewayStatus
    {
        get => _gatewayStatus;
        private set => SetField(ref _gatewayStatus, value);
    }

    /// <summary>What to set on a phone, shown next to the remote-computers checkbox.</summary>
    public string RemoteClientsHint
    {
        get
        {
            if (!AllowRemoteClients)
            {
                return string.Empty;
            }

            if (_proxy is not { AllowsRemoteClients: true })
            {
                return "Takes effect when capture starts.";
            }

            var addresses = LocalNetwork.GetIPv4Addresses();
            var where = addresses.Count == 0
                ? $"this computer's address, port {_proxy.Port}"
                : string.Join(" or ", addresses.Select(a => $"{a}:{_proxy.Port}"));
            return $"On the other device, set the proxy to {where}, then open http://{ProxyHomePage.HostName}/ to install the certificate.";
        }
    }

    private void LoadNetworkPreferences()
    {
        _showConnectionsRow = _preferences.GetBoolPref(PreferenceKeys.ShowConnectionsRow, false);
        _upstreamProxySetting = _preferences.GetStringPref(PreferenceKeys.UpstreamProxy, string.Empty);
        _proxyUserName = _preferences.GetStringPref(PreferenceKeys.UpstreamProxyUserName, string.Empty);
        _allowRemoteClients = _preferences.GetBoolPref(PreferenceKeys.AllowRemoteClients, false);
        _http2Enabled = _preferences.GetBoolPref(PreferenceKeys.Http2, false);
        _skipDecryptionHostsText = string.Join("; ", _preferences.GetListPref(PreferenceKeys.SkipDecryptionHosts, []));
        _clientCertificateChoices = [.. ClientCertificateStore.FindUsable()];
        _clientCertificateThumbprint = _preferences.GetStringPref(PreferenceKeys.ClientCertificate, string.Empty);
        _clientCertificateHostsText = string.Join("; ", _preferences.GetListPref(PreferenceKeys.ClientCertificateHosts, []));
    }

    /// <summary>
    /// Called by Start once the listener is running and before VioLens
    /// registers as the system proxy (so the system setting read is still the
    /// network's own). Returns a phrase for the status line.
    /// </summary>
    private string StartGateway(InterceptingProxyListener listener, UpstreamProxyDetection detected)
    {
        _detectedUpstream = detected;
        ApplyProxyCredential();
        if (_detectedUpstream.AutoConfig is { } autoConfig)
        {
            // The script loads in the background; show what happened.
            autoConfig.DescriptionChanged += OnAutoConfigDescriptionChanged;
        }

        var status = ApplyGateway(listener);
        RaisePropertyChanged(nameof(RemoteClientsHint));
        return status;
    }

    /// <summary>Sets the listener's gateway from <see cref="UpstreamProxySetting"/> and returns what it chose.</summary>
    private string ApplyGateway(InterceptingProxyListener listener)
    {
        var setting = UpstreamProxySetting.Trim();
        string description;
        listener.AutoConfig = null;
        if (setting.Length == 0)
        {
            var detected = _detectedUpstream ?? UpstreamProxyDetection.None;
            listener.Gateway = detected.Proxy;
            listener.AutoConfig = detected.AutoConfig;
            description = detected.AutoConfig is { } autoConfig
                ? DescribeAutoConfig(autoConfig.Description)
                : detected.Description;
        }
        else if (string.Equals(setting, DirectSetting, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(setting, "direct", StringComparison.OrdinalIgnoreCase))
        {
            listener.Gateway = null;
            description = "connecting directly (upstream proxy set to none)";
        }
        else if (UpstreamProxy.Parse(setting) is { } manual)
        {
            if (manual.IsSelf(listener.Port))
            {
                listener.Gateway = null;
                description = $"not forwarding through {manual}, which is VioLens itself; connecting directly";
            }
            else
            {
                listener.Gateway = manual;
                description = $"forwarding through {manual}";
            }
        }
        else
        {
            listener.Gateway = null;
            description = $"couldn't read the upstream proxy \"{setting}\" (use host:port, or none), so connecting directly";
        }

        GatewayStatus = "Upstream: " + description + ".";
        return description;
    }

    private string DescribeAutoConfig(string description) =>
        _detectedUpstream?.Source is { } source ? $"{description} ({source})" : description;

    private void OnAutoConfigDescriptionChanged(string description) => Dispatcher.UIThread.Post(() =>
    {
        // Only while it's still the one in use (not replaced by a manual
        // setting, or stopped).
        if (_proxy?.AutoConfig is { } current && ReferenceEquals(current, _detectedUpstream?.AutoConfig))
        {
            GatewayStatus = "Upstream: " + DescribeAutoConfig(description) + ".";
        }
    });

    private void StopGateway()
    {
        if (_detectedUpstream?.AutoConfig is { } autoConfig)
        {
            autoConfig.DescriptionChanged -= OnAutoConfigDescriptionChanged;
            autoConfig.Dispose();
        }

        _detectedUpstream = null;
        GatewayStatus = "Upstream proxy: decided when capture starts.";
        RaisePropertyChanged(nameof(RemoteClientsHint));
    }
}
