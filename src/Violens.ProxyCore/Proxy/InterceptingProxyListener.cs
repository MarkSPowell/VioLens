using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Violens.ProxyCore.AutoResponder;
using Violens.ProxyCore.Breakpoints;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Extensions;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Http2;
using Violens.ProxyCore.Proxy.AutoConfig;
using Violens.ProxyCore.Scripting;
using Violens.ProxyCore.Sessions;
using Violens.ProxyCore.WebSockets;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// The intercepting proxy. Accepts CONNECT tunnels, terminating TLS with a
/// leaf certificate, and plain http:// proxy requests, and records each
/// request/response pair in the <see cref="SessionStore"/>. Bodies are
/// buffered only when a breakpoint, AutoResponder, FiddlerScript or an
/// extension will act on them; otherwise they're relayed as they arrive.
/// One request at a time per connection; no pipelining.
/// </summary>
public sealed class InterceptingProxyListener
{
    /// <summary>
    /// For AutoResponder rules that serve another URL. Never goes through the
    /// system proxy, which while capturing is VioLens itself.
    /// </summary>
    private readonly UpstreamWebProxy _proxyUrl;

    private readonly LeafCertificateProvider _leafProvider;
    private readonly SessionStore _sessionStore;
    private readonly BreakpointManager _breakpointManager;
    private readonly AutoResponderRules _autoResponderRules;
    private readonly IScriptRunner? _scriptRunner;
    private readonly IExtensionAutoTamperHost? _extensionHost;
    private readonly IExtensionSessionHost? _sessionHost;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;
    private UpstreamProxy? _gateway;
    private ProxyAutoConfig? _autoConfig;

    // Numbers the connections VioLens opens to servers and upstream
    // proxies, so the waterfall can show which requests shared one.
    private static int s_connectionCounter;
    private NetworkCredential? _proxyCredential;

    public InterceptingProxyListener(
        int port,
        LeafCertificateProvider leafProvider,
        SessionStore sessionStore,
        BreakpointManager? breakpointManager = null,
        AutoResponderRules? autoResponderRules = null,
        IScriptRunner? scriptRunner = null,
        IExtensionAutoTamperHost? extensionHost = null,
        IExtensionSessionHost? sessionHost = null,
        bool allowRemoteClients = false)
    {
        _leafProvider = leafProvider;
        _sessionStore = sessionStore;
        _proxyUrl = new UpstreamWebProxy(this);
        // With nothing configured, each check is a single boolean.
        _breakpointManager = breakpointManager ?? new BreakpointManager();
        _autoResponderRules = autoResponderRules ?? new AutoResponderRules();
        // Null means no script loaded.
        _scriptRunner = scriptRunner;
        // Null means no extensions loaded.
        _extensionHost = extensionHost;
        // Ported Fiddler-shaped extensions (see IExtensionSessionHost). Null,
        // or IsActive false, means none are loaded.
        _sessionHost = sessionHost;
        AllowsRemoteClients = allowRemoteClients;
        _listener = new TcpListener(allowRemoteClients ? IPAddress.Any : IPAddress.Loopback, port);
    }

    /// <summary>The port being listened on, which may differ from the one requested. Valid after <see cref="Start"/>.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Whether this listener accepts connections from other computers
    /// (phones, other machines on the network) as well as this one: Fiddler
    /// Classic's "Allow remote computers to connect". Fixed when constructed.
    /// </summary>
    public bool AllowsRemoteClients { get; }

    /// <summary>
    /// The proxy VioLens forwards its own outgoing connections through, or
    /// null to connect directly. Read when each new client connection opens
    /// its connection to the server, so it can change while running. See
    /// <see cref="UpstreamProxy"/>.
    /// </summary>
    public UpstreamProxy? Gateway
    {
        get => Volatile.Read(ref _gateway);
        set => Volatile.Write(ref _gateway, value);
    }

    /// <summary>
    /// Automatic proxy configuration (a PAC script): when set and
    /// <see cref="Gateway"/> isn't, each server connection asks it which
    /// proxy to use, trying the next one it lists when one can't be reached.
    /// Read when each server connection is opened, like <see cref="Gateway"/>.
    /// The listener doesn't own it; whoever sets it disposes it.
    /// </summary>
    public ProxyAutoConfig? AutoConfig
    {
        get => Volatile.Read(ref _autoConfig);
        set => Volatile.Write(ref _autoConfig, value);
    }

    /// <summary>
    /// The user name and password for an upstream proxy that asks for a
    /// sign-in (see <see cref="ProxyAuthenticator"/>), or null to sign in as
    /// the signed-in account (Windows sign-in: Negotiate or NTLM). Read
    /// each time a sign-in is needed.
    /// </summary>
    public NetworkCredential? ProxyCredential
    {
        get => Volatile.Read(ref _proxyCredential);
        set => Volatile.Write(ref _proxyCredential, value);
    }

    private long _maxCapturedBodyBytes = Http1MessageReader.DefaultMaxCapturedBodyBytes;

    /// <summary>
    /// How much of each relayed request or response body is kept in its
    /// session (64 MB unless changed). Every byte is still relayed; a body
    /// larger than this is captured only up to here, and the session gets
    /// <see cref="Session.RequestBodyTruncatedFlag"/> or
    /// <see cref="Session.ResponseBodyTruncatedFlag"/>. Read when each
    /// client connection opens. A body that's buffered so a breakpoint,
    /// FiddlerScript or an extension can change it is always kept whole.
    /// </summary>
    public long MaxCapturedBodyBytes
    {
        get => Interlocked.Read(ref _maxCapturedBodyBytes);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Interlocked.Exchange(ref _maxCapturedBodyBytes, value);
        }
    }

    /// <summary>The default for <see cref="ClientIdleTimeout"/>.</summary>
    public static readonly TimeSpan DefaultClientIdleTimeout = TimeSpan.FromMinutes(2);

    private long _clientIdleTimeoutTicks = DefaultClientIdleTimeout.Ticks;

    /// <summary>
    /// How long a client connection may take to send each request's start
    /// line and headers (two minutes unless changed), or
    /// <see cref="Timeout.InfiniteTimeSpan"/> for no limit. Covers the
    /// first request, the gap between requests on a kept-alive connection,
    /// and the TLS handshake after CONNECT; a connection that runs out of
    /// time is closed quietly, the same way a client ending a keep-alive
    /// connection is. Without it, a client that connects and then says
    /// nothing (or stops halfway through its headers) holds a connection
    /// and its task for ever -- a nuisance on loopback, and a way to tie
    /// the listener up once <see cref="AllowsRemoteClients"/> opens it to
    /// the network.
    ///
    /// Deliberately only the preamble: once a request has started, nothing
    /// here times it out. A request or response body streamed slowly (a
    /// chat reply, a long upload), a session paused at a breakpoint while
    /// someone edits it, and a WebSocket or other upgraded connection after
    /// its 101 can all legitimately sit quiet for far longer than this.
    /// Read when each client connection opens, like
    /// <see cref="MaxCapturedBodyBytes"/>.
    /// </summary>
    public TimeSpan ClientIdleTimeout
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _clientIdleTimeoutTicks));
        set
        {
            if (value != Timeout.InfiniteTimeSpan)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            }

            Interlocked.Exchange(ref _clientIdleTimeoutTicks, value.Ticks);
        }
    }

    private int _http2Enabled;

    /// <summary>
    /// Whether clients may use HTTP/2 for HTTPS traffic: offered in the TLS
    /// handshake (ALPN) alongside HTTP/1.1, and used when the client picks
    /// it. Their requests then go on to the server over HTTP/2 too, when the
    /// server supports it, and HTTP/1.1 otherwise; HTTP/1.1 clients are
    /// unaffected. Off unless set; read
    /// when each client connection's TLS handshake starts.
    /// </summary>
    public bool Http2Enabled
    {
        get => Volatile.Read(ref _http2Enabled) == 1;
        set => Volatile.Write(ref _http2Enabled, value ? 1 : 0);
    }

    private IReadOnlyList<string> _skipDecryptionHosts = [];

    /// <summary>
    /// Hosts whose HTTPS traffic passes through without being decrypted:
    /// Fiddler Classic's "Skip decryption for the following hosts", for apps
    /// that pin their server's certificate and so refuse any decrypting
    /// proxy. Entries are in <see cref="UpstreamProxy.BypassList"/>'s format:
    /// exact names and <c>*</c> wildcards (<c>*.example.com</c>, which
    /// doesn't match <c>example.com</c> itself). Such a connection is
    /// recorded as one <c>CONNECT</c> session with no requests inside it,
    /// flagged <see cref="NoDecryptFlag"/>. Read when each CONNECT arrives,
    /// so it can change while running.
    /// </summary>
    public IReadOnlyList<string> SkipDecryptionHosts
    {
        get => Volatile.Read(ref _skipDecryptionHosts);
        set => Volatile.Write(ref _skipDecryptionHosts, [.. value ?? throw new ArgumentNullException(nameof(value))]);
    }

    private X509Certificate2? _clientCertificate;
    private IReadOnlyList<string> _clientCertificateHosts = [];

    /// <summary>
    /// The certificate VioLens sends when a server asks for one (mutual
    /// TLS), limited to <see cref="ClientCertificateHosts"/> when that's set. It
    /// needs its private key (one from the user's personal certificate
    /// store, say). Null, the default, sends none. The session records what
    /// was sent (<see cref="ServerTlsInfo.ClientCertificateSent"/>). Read
    /// when each server connection's handshake asks, so it can change while
    /// running.
    /// </summary>
    public X509Certificate2? ClientCertificate
    {
        get => Volatile.Read(ref _clientCertificate);
        set
        {
            if (value is { HasPrivateKey: false })
            {
                throw new ArgumentException("A client certificate needs its private key.", nameof(value));
            }

            Volatile.Write(ref _clientCertificate, value);
        }
    }

    /// <summary>
    /// The hosts <see cref="ClientCertificate"/> is sent to, in
    /// <see cref="UpstreamProxy.BypassList"/>'s format. Empty, the default,
    /// sends it to every server that asks, as a browser offers its
    /// certificate; list hosts to keep it from servers it isn't meant for.
    /// </summary>
    public IReadOnlyList<string> ClientCertificateHosts
    {
        get => Volatile.Read(ref _clientCertificateHosts);
        set => Volatile.Write(ref _clientCertificateHosts, [.. value ?? throw new ArgumentNullException(nameof(value))]);
    }

    private X509Certificate2? ChooseClientCertificate(string host) =>
        ClientCertificate is { } certificate &&
        (ClientCertificateHosts.Count == 0 || UpstreamProxy.MatchesHostList(host, ClientCertificateHosts))
            ? certificate
            : null;

    /// <summary>Fiddler's flag for a tunnel left encrypted; on the CONNECT session of a host in <see cref="SkipDecryptionHosts"/>.</summary>
    public const string NoDecryptFlag = "x-no-decrypt";

    /// <summary>Replaces the normal check of servers' certificates. Tests only, for origins with test certificates.</summary>
    internal RemoteCertificateValidationCallback? UpstreamCertificateValidation { get; set; }

    private static readonly IReadOnlyList<UpstreamProxy?> DirectOnly = [null];

    /// <summary>
    /// The routes to try for a server connection, in order: a proxy, or
    /// null for direct. This machine is always reached directly.
    /// </summary>
    private async Task<IReadOnlyList<UpstreamProxy?>> ChooseRoutesAsync(
        bool isTls, string host, int port, string target, CancellationToken cancellationToken)
    {
        if (UpstreamProxy.IsLoopback(host))
        {
            return DirectOnly;
        }

        if (Gateway is { } gateway)
        {
            return gateway.ShouldBypass(host) ? DirectOnly : [gateway];
        }

        if (AutoConfig is not { } autoConfig)
        {
            return DirectOnly;
        }

        // Browsers give a PAC script only the scheme, host and port of an
        // HTTPS request (the path is private), and the whole URL for HTTP.
        var authority = (host.Contains(':') ? $"[{host}]" : host) + (port == (isTls ? 443 : 80) ? string.Empty : $":{port}");
        var url = isTls ? $"https://{authority}/" : $"http://{authority}{target}";
        return await autoConfig.GetRoutesAsync(url, host, cancellationToken);
    }

    /// <summary>
    /// Starts on <paramref name="preferredPort"/>, or on any free port if it's
    /// taken (usually by another VioLens). Read the result from <see cref="Port"/>.
    /// </summary>
    public static InterceptingProxyListener StartOnAvailablePort(
        int preferredPort,
        LeafCertificateProvider leafProvider,
        SessionStore sessionStore,
        BreakpointManager? breakpointManager = null,
        AutoResponderRules? autoResponderRules = null,
        IScriptRunner? scriptRunner = null,
        IExtensionAutoTamperHost? extensionHost = null,
        IExtensionSessionHost? sessionHost = null,
        bool allowRemoteClients = false)
    {
        var preferred = new InterceptingProxyListener(preferredPort, leafProvider, sessionStore, breakpointManager, autoResponderRules, scriptRunner, extensionHost, sessionHost, allowRemoteClients);
        try
        {
            preferred.Start();
            return preferred;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // Port 0: the OS picks a free port.
            var fallback = new InterceptingProxyListener(0, leafProvider, sessionStore, breakpointManager, autoResponderRules, scriptRunner, extensionHost, sessionHost, allowRemoteClients);
            fallback.Start();
            return fallback;
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
    }

    /// <summary>
    /// The routes <see cref="ChooseRoutesAsync"/> picks, as an
    /// <see cref="IWebProxy"/> for <see cref="HttpClient"/>: the first one
    /// (the gateway, the PAC script's first choice, or direct). Credentials
    /// are the proxy sign-in (<see cref="ProxyCredential"/>), or the
    /// signed-in account. <see cref="Client"/> is the <see cref="HttpClient"/>
    /// that goes out this way.
    ///
    /// <see cref="IWebProxy"/> is synchronous, and HttpClient asks it for
    /// every request (twice: <see cref="IsBypassed"/>, then
    /// <see cref="GetProxy"/>), while a PAC script's answer is
    /// asynchronous -- the script may still be downloading, and running it
    /// happens off-thread (see <see cref="ProxyAutoConfig.GetRoutesAsync"/>).
    /// So a PAC answer is kept here per scheme, host and port for
    /// <see cref="RouteLifetime"/>, and <see cref="PrepareRouteAsync"/> fills
    /// that in asynchronously ahead of a request, leaving the synchronous
    /// question nothing to wait for. Only a question that arrives with
    /// nothing prepared (HttpClient following a redirect to another host,
    /// say) still blocks, once per host. Keyed by host rather than the whole
    /// URL, so a script that routes plain-HTTP requests by path gets its
    /// answer for the first path asked about -- the same per-host shortcut
    /// browsers' own PAC caches take, and the only way the cache helps at all
    /// for a host whose paths are all different. A gateway, or no proxy
    /// configuration at all, is decided without waiting, so isn't cached.
    /// </summary>
    internal sealed class UpstreamWebProxy : IWebProxy
    {
        /// <summary>How long a PAC script's answer for one host is reused.</summary>
        public static readonly TimeSpan RouteLifetime = TimeSpan.FromSeconds(30);

        private const int MaxCachedRoutes = 256;

        private readonly InterceptingProxyListener _listener;
        private readonly ConcurrentDictionary<string, CachedRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

        public UpstreamWebProxy(InterceptingProxyListener listener)
        {
            _listener = listener;
            Client = new HttpClient(new SocketsHttpHandler
            {
                Proxy = this,
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.None,
            });
        }

        public HttpClient Client { get; }

        public ICredentials? Credentials
        {
            get => (ICredentials?)_listener.ProxyCredential ?? CredentialCache.DefaultCredentials;
            set { }
        }

        public Uri? GetProxy(Uri destination) =>
            Route(destination) is { } proxy ? new Uri($"http://{proxy}/") : destination;

        public bool IsBypassed(Uri host) => Route(host) is null;

        /// <summary>
        /// Works out the route for <paramref name="destination"/> without
        /// blocking, so the synchronous <see cref="GetProxy"/>/
        /// <see cref="IsBypassed"/> that follow find it ready.
        /// </summary>
        public async Task PrepareRouteAsync(Uri destination, CancellationToken cancellationToken)
        {
            if (UsesAutoConfig(destination) is not { } autoConfig || TryGetCached(destination, autoConfig, out _))
            {
                return;
            }

            var routes = await ChooseAsync(destination, cancellationToken);
            Remember(destination, autoConfig, routes);
        }

        private UpstreamProxy? Route(Uri destination)
        {
            if (UsesAutoConfig(destination) is not { } autoConfig)
            {
                // Loopback, a gateway, or no configuration: ChooseRoutesAsync
                // decides those before its first await, so this doesn't block.
                return First(ChooseAsync(destination, CancellationToken.None).GetAwaiter().GetResult());
            }

            if (TryGetCached(destination, autoConfig, out var cached))
            {
                return cached;
            }

            // Nothing prepared: wait, once for this host. ProxyAutoConfig's
            // own answer cache often has it anyway, and then this task has
            // already finished.
            var routes = ChooseAsync(destination, CancellationToken.None).GetAwaiter().GetResult();
            return Remember(destination, autoConfig, routes);
        }

        /// <summary>The PAC script <paramref name="destination"/> would be routed by, or null when none is consulted.</summary>
        private ProxyAutoConfig? UsesAutoConfig(Uri destination) =>
            _listener.Gateway is null && _listener.AutoConfig is { } autoConfig && !UpstreamProxy.IsLoopback(destination.IdnHost)
                ? autoConfig
                : null;

        private Task<IReadOnlyList<UpstreamProxy?>> ChooseAsync(Uri destination, CancellationToken cancellationToken)
        {
            var isTls = string.Equals(destination.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            return _listener.ChooseRoutesAsync(isTls, destination.IdnHost, destination.Port, destination.PathAndQuery, cancellationToken);
        }

        private bool TryGetCached(Uri destination, ProxyAutoConfig autoConfig, out UpstreamProxy? route)
        {
            // An entry from a script that's since been replaced doesn't count.
            if (_routes.TryGetValue(Key(destination), out var cached) &&
                ReferenceEquals(cached.AutoConfig, autoConfig) && Environment.TickCount64 < cached.ExpiresAt)
            {
                route = cached.Route;
                return true;
            }

            route = null;
            return false;
        }

        private UpstreamProxy? Remember(Uri destination, ProxyAutoConfig autoConfig, IReadOnlyList<UpstreamProxy?> routes)
        {
            if (_routes.Count >= MaxCachedRoutes)
            {
                _routes.Clear();
            }

            var route = First(routes);
            _routes[Key(destination)] = new CachedRoute(autoConfig, route, Environment.TickCount64 + (long)RouteLifetime.TotalMilliseconds);
            return route;
        }

        private static string Key(Uri destination) => $"{destination.Scheme}://{destination.IdnHost}:{destination.Port}";

        private static UpstreamProxy? First(IReadOnlyList<UpstreamProxy?> routes) => routes.Count > 0 ? routes[0] : null;

        private sealed record CachedRoute(ProxyAutoConfig AutoConfig, UpstreamProxy? Route, long ExpiresAt);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        client.NoDelay = true;

        try
        {
            // Read-buffered (see ReadBufferedStream), and the TLS handshake after
            // CONNECT reads through it too, so nothing read ahead is lost.
            using var clientStream = new ReadBufferedStream(client.GetStream());
            var idleTimeout = ClientIdleTimeout;

            // Only possible with AllowsRemoteClients. Such a client mustn't
            // reach this computer's loopback-only services through VioLens.
            var isRemoteClient = client.Client.RemoteEndPoint is IPEndPoint remote && !IPAddress.IsLoopback(remote.Address);
            var first = await ReadRequestPreambleWithinAsync(clientStream, idleTimeout, cancellationToken);
            if (first is null)
            {
                return;
            }

            if (!string.Equals(first.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                // Plain HTTP. A request addressed to VioLens itself gets its
                // home page and root certificate (how a phone installs it);
                // an http:// request is captured and forwarded like HTTPS
                // traffic; anything else (an ftp:// URL, say) is answered
                // rather than silently dropped.
                if (ProxyHomePage.IsRequestForProxy(first.Target, Port))
                {
                    await AnswerAsync(clientStream, first, ProxyHomePage.Build(first.Target, _leafProvider.IssuerCertificateDer), cancellationToken);
                    return;
                }

                if (!first.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    await AnswerAsync(clientStream, first, ProxyHomePage.NotForwarded(first.Method, first.Target), cancellationToken);
                    return;
                }

                await PumpSessionsAsync(
                    isTls: false, string.Empty, PlainHttpRequest.DefaultPort, client, clientStream, first, _sessionStore,
                    _breakpointManager, _autoResponderRules, _scriptRunner, _extensionHost, _sessionHost, ChooseRoutesAsync, () => ProxyCredential, _proxyUrl, Port,
                    () => _leafProvider.IssuerCertificateDer, MaxCapturedBodyBytes, idleTimeout, UpstreamCertificateValidation, isRemoteClient,
                    ChooseClientCertificate, cancellationToken);
                return;
            }

            var (targetHost, targetPort) = ParseConnectTarget(first.Target);
            if (isRemoteClient && await IsThisComputerAsync(targetHost, cancellationToken))
            {
                await AnswerAsync(clientStream, first, ProxyHomePage.NotForThisComputer(first.Target), cancellationToken);
                return;
            }

            if (UpstreamProxy.MatchesHostList(targetHost, SkipDecryptionHosts))
            {
                await TunnelWithoutDecryptingAsync(clientStream, first, targetHost, targetPort, cancellationToken);
                return;
            }

            var established = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
            await clientStream.WriteAsync(established, cancellationToken);

            using var clientTls = new SslStream(clientStream, leaveInnerStreamOpen: false);
            var serverOptions = new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, sniHostName) =>
                    _leafProvider.GetCertificateFor(string.IsNullOrEmpty(sniHostName) ? targetHost : sniHostName),
                // Let the OS choose TLS 1.2 or 1.3.
                EnabledSslProtocols = SslProtocols.None,
                ApplicationProtocols = Http2Enabled ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11] : null,
            };
            // The handshake falls under the idle timeout too: a client that
            // sends CONNECT and then nothing else would otherwise hold the
            // connection just as surely as one that never sends a request.
            using (var handshakeTimer = StartIdleTimer(idleTimeout, cancellationToken))
            {
                try
                {
                    await clientTls.AuthenticateAsServerAsync(serverOptions, handshakeTimer.Token);
                }
                catch (Exception) when (handshakeTimer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    ProxyEngine.Log("proxy", $"{targetHost}: the client didn't finish its TLS handshake in time; closing the connection.");
                    return;
                }
            }

            if (clientTls.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
            {
                await ServeHttp2Async(new ReadBufferedStream(clientTls), targetHost, targetPort, idleTimeout, cancellationToken);
                return;
            }

            // The server connection is opened later, only when needed.
            await PumpSessionsAsync(
                isTls: true, targetHost, targetPort, client, new ReadBufferedStream(clientTls), firstRequest: null, _sessionStore, _breakpointManager,
                _autoResponderRules, _scriptRunner, _extensionHost, _sessionHost, ChooseRoutesAsync, () => ProxyCredential, _proxyUrl, Port,
                () => _leafProvider.IssuerCertificateDer, MaxCapturedBodyBytes, idleTimeout, UpstreamCertificateValidation, isRemoteClient: false,
                ChooseClientCertificate, cancellationToken);
        }
        catch (BreakpointAbortedException ex)
        {
            ProxyEngine.Log("proxy", ex.Message);
        }
        catch (Exception ex)
        {
            ProxyEngine.Log("proxy", $"Connection ended: {DescribeException(ex)}");
        }
    }

    /// <summary>
    /// A CONNECT for a host in <see cref="SkipDecryptionHosts"/>: connects to
    /// the server (through the same routes as any other HTTPS connection),
    /// then passes the client's bytes through untouched, TLS handshake and
    /// all, so the client sees the server's own certificate.
    /// </summary>
    private async Task TunnelWithoutDecryptingAsync(
        Stream clientStream, CapturedRequest connect, string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NoDecryptFlag] = "the host is in the skip-decryption list",
        };

        TcpClient server;
        try
        {
            server = await OpenServerTunnelAsync(targetHost, targetPort, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            var failed = BadGateway(targetHost, targetPort, ex);
            await HttpMessageWriter.WriteResponseAsync(clientStream, failed, cancellationToken);
            _sessionStore.Add(targetHost, startedAt, connect, failed, flags, "http");
            return;
        }

        using (server)
        {
            // Written by hand: a 2xx answer to CONNECT has no Content-Length.
            await clientStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancellationToken);
            var session = _sessionStore.Add(
                targetHost, startedAt, connect, new CapturedResponse("HTTP/1.1", 200, "Connection Established", [], []), flags, "http");
            ProxyEngine.Log("session", $"#{session.Id} CONNECT {connect.Target} -- not decrypted (skip-decryption list)");

            // clientStream may already hold the start of the client's TLS
            // handshake, read ahead with the CONNECT; it's relayed first.
            await RelayRawBytesUntilClosedAsync(clientStream, server.GetStream(), cancellationToken);
        }
    }

    /// <summary>A plain TCP connection to the server: direct, or a CONNECT tunnel through the chosen proxy.</summary>
    private async Task<TcpClient> OpenServerTunnelAsync(string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        var routes = await ChooseRoutesAsync(isTls: true, targetHost, targetPort, "/", cancellationToken);
        for (var i = 0; ; i++)
        {
            var route = routes[i];
            try
            {
                if (route is not null)
                {
                    return await route.ConnectTunnelAsync(targetHost, targetPort, ProxyCredential, cancellationToken);
                }

                var direct = new TcpClient { NoDelay = true };
                try
                {
                    await direct.ConnectAsync(targetHost, targetPort, cancellationToken);
                    return direct;
                }
                catch
                {
                    direct.Dispose();
                    throw;
                }
            }
            catch (SocketException ex) when (i + 1 < routes.Count)
            {
                ProxyEngine.Log("proxy", $"{targetHost}: couldn't reach {DescribeRoute(route)} ({ex.Message}); trying {DescribeRoute(routes[i + 1])}.");
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="host"/> is this computer through a loopback
    /// address: <c>localhost</c>, <c>*.localhost</c>, a loopback or
    /// unspecified address (<c>0.0.0.0</c> reaches this computer on macOS),
    /// or a name that resolves to one. A name that can't be resolved isn't;
    /// connecting to it fails anyway. The answer could change between here
    /// and the connection (DNS rebinding), a narrow gap that remains.
    /// </summary>
    internal static async Task<bool> IsThisComputerAsync(string host, CancellationToken cancellationToken)
    {
        if (UpstreamProxy.IsLoopback(host) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            return IsLoopbackOrUnspecified(literal);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses.Any(IsLoopbackOrUnspecified);
        }
        catch (SocketException)
        {
            return false;
        }

        static bool IsLoopbackOrUnspecified(IPAddress address) =>
            IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            (address.IsIPv4MappedToIPv6 && IsLoopbackOrUnspecified(address.MapToIPv4()));
    }

    /// <summary>Drains a plain request's body, sends VioLens's own answer, and logs it.</summary>
    private static async Task AnswerAsync(Stream clientStream, CapturedRequest request, CapturedResponse answer, CancellationToken cancellationToken)
    {
        await Http1MessageReader.ReadBodyAsync(clientStream, request.Headers, cancellationToken);
        await HttpMessageWriter.WriteResponseAsync(clientStream, answer, cancellationToken);
        ProxyEngine.Log("proxy", $"{request.Method} {request.Target} -> {answer.StatusCode} (answered by {ProxyEngine.Identity.ProductName})");
    }

    private static string DescribeException(Exception ex)
    {
        // The useful reason is in the inner exceptions.
        var description = $"{ex.GetType().Name}: {ex.Message}";
        var inner = ex.InnerException;
        while (inner is not null)
        {
            description += $" ---> {inner.GetType().Name}: {inner.Message}";
            inner = inner.InnerException;
        }

        return description;
    }

    /// <summary>
    /// Handles the HTTP/1.1 requests on one connection, one after another:
    /// reads each request's headers, runs it through an
    /// <see cref="ExchangePipeline"/>, then decides whether the connection
    /// can carry another. The server connection is opened only when a
    /// request actually needs it, so AutoResponder can answer every request
    /// with no route to the server at all.
    /// </summary>
    private static async Task PumpSessionsAsync(
        bool isTls,
        string targetHost,
        int targetPort,
        TcpClient clientConnection,
        Stream clientTls,
        CapturedRequest? firstRequest,
        SessionStore sessionStore,
        BreakpointManager breakpointManager,
        AutoResponderRules autoResponderRules,
        IScriptRunner? scriptRunner,
        IExtensionAutoTamperHost? extensionHost,
        IExtensionSessionHost? sessionHost,
        Func<bool, string, int, string, CancellationToken, Task<IReadOnlyList<UpstreamProxy?>>> chooseRoutes,
        Func<NetworkCredential?> proxyCredential,
        UpstreamWebProxy proxyUrl,
        int ownPort,
        Func<byte[]> rootCertificateDer,
        long maxCapturedBodyBytes,
        TimeSpan idleTimeout,
        RemoteCertificateValidationCallback? validateServer,
        bool isRemoteClient,
        Func<string, X509Certificate2?> chooseClientCertificate,
        CancellationToken cancellationToken)
    {
        // isRemoteClient: a plain connection from another computer, whose
        // requests mustn't reach this computer's own loopback services (a
        // CONNECT tunnel's one target is checked before it gets here).
        //
        // clientTls is the TLS stream for a CONNECT tunnel, or the plain
        // connection for http:// requests. A plain connection can carry
        // requests for different servers, so the target is set per request.
        var scheme = isTls ? "https" : "http";
        var pipeline = new ExchangePipeline(new ExchangeServices(
            sessionStore, breakpointManager, autoResponderRules, scriptRunner, extensionHost, sessionHost, proxyUrl, maxCapturedBodyBytes));
        var client = new Http1ClientExchange(clientConnection, clientTls);
        using var upstream = new Http1Upstream(
            isTls, targetHost, targetPort, chooseRoutes, proxyCredential, validateServer, chooseClientCertificate: chooseClientCertificate);
        var pendingRequest = firstRequest;

        while (true)
        {
            var requestPreamble = pendingRequest ?? await ReadRequestPreambleWithinAsync(clientTls, idleTimeout, cancellationToken);
            pendingRequest = null;
            if (requestPreamble is null)
            {
                // The client closed the connection between requests, or
                // left it idle past ClientIdleTimeout.
                return;
            }

            // Timed from the request's arrival, not keep-alive idle time.
            var startedAt = DateTimeOffset.Now;
            var timing = new SessionTimingRecorder();
            upstream.Timing = timing;

            if (!isTls)
            {
                // Plain HTTP is stored like HTTPS: a path-only target plus a Host header
                // (see PlainHttpRequest).
                if (ProxyHomePage.IsRequestForProxy(requestPreamble.Target, ownPort) && !requestPreamble.Target.StartsWith('/'))
                {
                    await AnswerAsync(clientTls, requestPreamble, ProxyHomePage.Build(requestPreamble.Target, rootCertificateDer()), cancellationToken);
                    return;
                }

                if (!PlainHttpRequest.TryNormalize(requestPreamble, out var normalized, out var plainHost, out var plainPort))
                {
                    await AnswerAsync(clientTls, requestPreamble, ProxyHomePage.NotForwarded(requestPreamble.Method, requestPreamble.Target), cancellationToken);
                    return;
                }

                if (isRemoteClient && await IsThisComputerAsync(plainHost, cancellationToken))
                {
                    await AnswerAsync(clientTls, requestPreamble, ProxyHomePage.NotForThisComputer(requestPreamble.Target), cancellationToken);
                    return;
                }

                requestPreamble = normalized;
                upstream.SetTarget(plainHost, plainPort);
            }

            var url = isTls
                ? $"https://{upstream.TargetHost}{requestPreamble.Target}"
                : $"http://{ContentDecoder.FindHeader(requestPreamble.Headers, "Host") ?? upstream.TargetHost}{requestPreamble.Target}";

            var result = await pipeline.RunAsync(client, upstream, requestPreamble, url, scheme, startedAt, timing, cancellationToken);
            if (result.End == ExchangeEnd.CloseConnection)
            {
                return;
            }

            if (result.End == ExchangeEnd.AnsweredLocally)
            {
                continue;
            }

            var request = result.Request!;
            var responsePreamble = result.ResponsePreamble!;
            if (result.Response!.StatusCode == 101)
            {
                // 101 Switching Protocols: no longer HTTP/1.1, so relay both directions
                // raw for the rest of the connection, reading WebSocket messages on the
                // way through.
                if (result.WebSocket is { } webSocket)
                {
                    await WebSocketRelay.RelayAsync(
                        clientTls, upstream.Stream, webSocket, WebSocketCompression.FromHeader(webSocket.Extensions), cancellationToken);
                }
                else
                {
                    await RelayRawBytesUntilClosedAsync(clientTls, upstream.Stream, cancellationToken);
                }

                return;
            }

            // A response whose body ran until the server closed the
            // connection, or either side asking to close, ends this
            // connection too: the client can't tell where a further
            // response would start.
            if ((!result.ResponseHasNoBody && Http1MessageReader.IsCloseDelimited(responsePreamble.Headers)) ||
                AsksToClose(request.HttpVersion, request.Headers) || AsksToClose(responsePreamble.HttpVersion, responsePreamble.Headers))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Handles an HTTP/2 client connection inside a CONNECT tunnel: each
    /// stream is an exchange, run through the <see cref="ExchangePipeline"/>
    /// alongside the others. They go on to the server over HTTP/2 when the
    /// server offers it, as streams on one connection, or otherwise over
    /// HTTP/1.1 on a small pool of connections (see <see cref="Http2TunnelUpstreams"/>).
    /// The connection closes once it's been idle
    /// (no streams in progress) for <paramref name="idleTimeout"/>.
    /// </summary>
    private async Task ServeHttp2Async(Stream clientTls, string targetHost, int targetPort, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await using var connection = await Http2Connection.AcceptAsync(clientTls, new Http2Settings(), cancellationToken);
        var pipeline = new ExchangePipeline(new ExchangeServices(
            _sessionStore, _breakpointManager, _autoResponderRules, _scriptRunner, _extensionHost, _sessionHost, _proxyUrl, MaxCapturedBodyBytes));
        var validateServer = UpstreamCertificateValidation;
        await using var tunnel = new Http2TunnelUpstreams(
            targetHost,
            targetPort,
            offerHttp2 => new Http1Upstream(
                isTls: true, targetHost, targetPort, ChooseRoutesAsync, () => ProxyCredential, validateServer, offerHttp2, ChooseClientCertificate));

        var exchanges = new List<Task>();
        var inProgress = 0;
        var lastActivity = Environment.TickCount64;
        while (true)
        {
            Http2Stream? stream;
            using (var timer = StartIdleTimer(idleTimeout, cancellationToken))
            {
                try
                {
                    stream = await connection.AcceptStreamAsync(timer.Token);
                }
                catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref inProgress) == 0 &&
                        Environment.TickCount64 - Volatile.Read(ref lastActivity) >= (long)idleTimeout.TotalMilliseconds)
                    {
                        await connection.GoAwayAsync(cancellationToken);
                        break;
                    }

                    continue;
                }
            }

            if (stream is null)
            {
                break;
            }

            Interlocked.Increment(ref inProgress);
            exchanges.RemoveAll(task => task.IsCompleted);
            exchanges.Add(Task.Run(async () =>
            {
                try
                {
                    await RunHttp2ExchangeAsync(stream, pipeline, tunnel, targetHost, cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref lastActivity, Environment.TickCount64);
                    Interlocked.Decrement(ref inProgress);
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(exchanges);
    }

    /// <summary>One HTTP/2 stream's exchange. A stream left without a complete response is reset.</summary>
    private static async Task RunHttp2ExchangeAsync(
        Http2Stream stream, ExchangePipeline pipeline, Http2TunnelUpstreams tunnel, string targetHost, CancellationToken cancellationToken)
    {
        Http2ClientExchange? client = null;
        try
        {
            var headers = await stream.ReadHeadersAsync(cancellationToken);
            var startedAt = DateTimeOffset.Now;
            var timing = new SessionTimingRecorder();
            if (!Http2Messages.TryToRequest(headers, out var request))
            {
                await stream.ResetAsync(Http2ErrorCode.RefusedStream);
                return;
            }

            client = new Http2ClientExchange(stream, request);
            var upstream = tunnel.CreateUpstream();
            var reusable = false;
            try
            {
                upstream.Timing = timing;
                var result = await pipeline.RunAsync(
                    client, upstream, request, $"https://{targetHost}{request.Target}", "https", startedAt, timing, cancellationToken);
                reusable = result.End switch
                {
                    ExchangeEnd.AnsweredLocally => true,
                    ExchangeEnd.Forwarded => result.Response!.StatusCode != 101 &&
                        !(!result.ResponseHasNoBody && Http1MessageReader.IsCloseDelimited(result.ResponsePreamble!.Headers)) &&
                        !AsksToClose(result.ResponsePreamble!.HttpVersion, result.ResponsePreamble.Headers),
                    _ => false,
                };
            }
            finally
            {
                tunnel.Release(upstream, reusable);
            }
        }
        catch (Http2StreamClosedException)
        {
            // The client reset the stream, or the connection ended.
        }
        catch (BreakpointAbortedException ex)
        {
            ProxyEngine.Log("proxy", ex.Message);
        }
        catch (Exception ex)
        {
            ProxyEngine.Log("proxy", $"Stream {stream.Id} to {targetHost} ended: {DescribeException(ex)}");
        }

        if (client is null || !client.IsFinished)
        {
            await stream.ResetAsync(Http2ErrorCode.InternalError);
        }
    }

    /// <summary>
    /// Records a connection AutoResponder ended with <c>*reset</c> or
    /// <c>*drop</c>, so it still shows in the list. Status 0 means no response
    /// was sent.
    /// </summary>
    internal static void RecordAutoRespondedSession(
        SessionStore sessionStore, string targetHost, string scheme, DateTimeOffset startedAt, CapturedRequest request, string reason,
        SessionTimings timings)
    {
        var response = new CapturedResponse(HttpVersions.IsHttp2(request.HttpVersion) ? HttpVersions.Http2 : HttpVersions.Http11, 0, reason, [], []);
        var session = sessionStore.Add(targetHost, startedAt, request, response, flags: null, scheme, timings);
        ProxyEngine.Log("session", $"#{session.Id} {request.Method} {scheme}://{targetHost}{request.Target} -- {reason}");
    }

    /// <summary>The next number for a connection VioLens opens to a server or upstream proxy.</summary>
    internal static int NextConnectionId() => Interlocked.Increment(ref s_connectionCounter);

    internal static string DescribeRoute(UpstreamProxy? route) =>
        route is null ? "a direct connection" : $"the proxy {route}";

    /// <summary>
    /// <paramref name="flags"/> plus, when the server asked for a client
    /// certificate, <see cref="ServerTlsInfo.ClientCertificateRequestedFlag"/>
    /// (which the Notes tab shows).
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? WithTlsFlags(IReadOnlyDictionary<string, string>? flags, SessionTimings? timings)
    {
        if (timings?.Tls is not { ClientCertificateRequested: true } tls)
        {
            return flags;
        }

        var combined = flags is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(flags, StringComparer.OrdinalIgnoreCase);
        combined[ServerTlsInfo.ClientCertificateRequestedFlag] = tls.DescribeAcceptableIssuers();
        if (tls.ClientCertificateSent is { } sent)
        {
            combined[ServerTlsInfo.ClientCertificateSentFlag] = sent;
        }
        return combined;
    }

    /// <summary>
    /// <paramref name="flags"/> plus <see cref="Session.RequestBodyTruncatedFlag"/>/
    /// <see cref="Session.ResponseBodyTruncatedFlag"/> for a relayed body
    /// that was only partly kept.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? WithTruncationFlags(
        IReadOnlyDictionary<string, string>? flags, Http1MessageReader.RelayedBody? requestBody, Http1MessageReader.RelayedBody? responseBody)
    {
        if (requestBody is not { Truncated: true } && responseBody is not { Truncated: true })
        {
            return flags;
        }

        var combined = flags is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(flags, StringComparer.OrdinalIgnoreCase);
        if (requestBody is { Truncated: true } request)
        {
            combined[Session.RequestBodyTruncatedFlag] = DescribeTruncation(request);
        }

        if (responseBody is { Truncated: true } response)
        {
            combined[Session.ResponseBodyTruncatedFlag] = DescribeTruncation(response);
        }

        return combined;
    }

    private static string DescribeTruncation(Http1MessageReader.RelayedBody body) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"kept the first {body.Captured.Length:N0} of {body.Length:N0} bytes");

    /// <summary>The response sent when VioLens can't reach the server, with an optional explanation.</summary>
    internal static CapturedResponse BadGateway(string host, int port, Exception ex, string? explanation = null)
    {
        var body = Encoding.UTF8.GetBytes(
            $"{ProxyEngine.Identity.ProductName} couldn't connect to {host}:{port}: {ex.Message}" + (explanation is null ? string.Empty : $"\r\n\r\n{explanation}"));
        return new CapturedResponse(
            "HTTP/1.1",
            502,
            "Bad Gateway",
            [("Content-Type", "text/plain; charset=utf-8"), ("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Connection", "close")],
            body);
    }

    /// <summary>
    /// True when a message ends its connection: its Connection header
    /// includes "close", or it's HTTP/1.0 without "keep-alive" (RFC 9112
    /// section 9.3).
    /// </summary>
    internal static bool AsksToClose(string httpVersion, IReadOnlyList<(string Name, string Value)> headers)
    {
        var keepAlive = false;
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in value.Split(','))
            {
                if (string.Equals(token.Trim(), "close", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                keepAlive |= string.Equals(token.Trim(), "keep-alive", StringComparison.OrdinalIgnoreCase);
            }
        }

        return string.Equals(httpVersion, "HTTP/1.0", StringComparison.OrdinalIgnoreCase) && !keepAlive;
    }

    /// <summary>Applies AutoResponder <c>*header:</c> overrides: replace by name, or append.</summary>
    internal static IReadOnlyList<(string Name, string Value)> MergeHeaders(
        IReadOnlyList<(string Name, string Value)> original, IReadOnlyList<(string Name, string Value)> overrides)
    {
        var merged = new List<(string Name, string Value)>(original);
        foreach (var (name, value) in overrides)
        {
            var index = merged.FindIndex(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                merged[index] = (name, value);
            }
            else
            {
                merged.Add((name, value));
            }
        }

        return merged;
    }

    internal static Task<CapturedResponse> BuildMockResponseAsync(
        AutoResponderOutcome outcome, CapturedRequest request, UpstreamWebProxy proxyUrl, CancellationToken cancellationToken) => outcome.FinalActionKind switch
    {
        AutoResponderActionKind.ServeFile => BuildServeFileResponseAsync(outcome.Text!, cancellationToken),
        AutoResponderActionKind.ProxyUrl => FetchAndBuildResponseAsync(proxyUrl, outcome.Text!, cancellationToken),
        AutoResponderActionKind.Redirect => Task.FromResult(BuildRedirectResponse(outcome.Text!)),
        AutoResponderActionKind.CorsPreflightAllow => Task.FromResult(BuildCorsPreflightResponse(request)),
        _ => throw new InvalidOperationException($"Unexpected AutoResponder final action: {outcome.FinalActionKind}."),
    };

    private static async Task<CapturedResponse> BuildServeFileResponseAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            var missingBody = Encoding.UTF8.GetBytes($"{ProxyEngine.Identity.ProductName} AutoResponder: file not found -- {path}");
            return new CapturedResponse(
                "HTTP/1.1", 404, "Not Found", [("Content-Type", "text/plain; charset=utf-8")], missingBody);
        }

        var body = await File.ReadAllBytesAsync(path, cancellationToken);
        return new CapturedResponse("HTTP/1.1", 200, "OK", [("Content-Type", GuessContentType(path))], body);
    }

    /// <summary>
    /// Common file types only; anything else is application/octet-stream. A
    /// non-final <c>*header:Content-Type=</c> rule can override it.
    /// </summary>
    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".xml" => "application/xml",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// AutoResponder URL proxying: fetch another URL and serve its response
    /// as-is, without decompressing it.
    /// </summary>
    private static async Task<CapturedResponse> FetchAndBuildResponseAsync(UpstreamWebProxy proxyUrl, string url, CancellationToken cancellationToken)
    {
        try
        {
            // Asks for the route first, asynchronously, so HttpClient's own
            // (synchronous) question a moment later is answered from the
            // cache rather than by blocking on the PAC script.
            await proxyUrl.PrepareRouteAsync(new Uri(url), cancellationToken);
            using var upstreamResponse = await proxyUrl.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);

            var headers = new List<(string Name, string Value)>();
            foreach (var header in upstreamResponse.Headers)
            {
                headers.AddRange(header.Value.Select(value => (header.Key, value)));
            }

            foreach (var header in upstreamResponse.Content.Headers)
            {
                headers.AddRange(header.Value.Select(value => (header.Key, value)));
            }

            return new CapturedResponse(
                "HTTP/1.1", (int)upstreamResponse.StatusCode, upstreamResponse.ReasonPhrase ?? string.Empty, headers, body);
        }
        catch (Exception ex)
        {
            var body = Encoding.UTF8.GetBytes($"{ProxyEngine.Identity.ProductName} AutoResponder: fetching '{url}' failed -- {ex.Message}");
            return new CapturedResponse("HTTP/1.1", 502, "Bad Gateway", [("Content-Type", "text/plain; charset=utf-8")], body);
        }
    }

    private static CapturedResponse BuildRedirectResponse(string url) =>
        new("HTTP/1.1", 302, "Found", [("Location", url)], []);

    /// <summary>
    /// Answers an OPTIONS preflight with CORS headers echoing what the browser
    /// asked for. Fiddler doesn't document its exact response, so this is our
    /// own reading of it.
    /// </summary>
    private static CapturedResponse BuildCorsPreflightResponse(CapturedRequest request)
    {
        var origin = FindHeaderValue(request.Headers, "Origin") ?? "*";
        var requestedMethod = FindHeaderValue(request.Headers, "Access-Control-Request-Method");
        var requestedHeaders = FindHeaderValue(request.Headers, "Access-Control-Request-Headers");

        var headers = new List<(string Name, string Value)>
        {
            ("Access-Control-Allow-Origin", origin),
            ("Access-Control-Allow-Methods", requestedMethod ?? "GET, POST, PUT, PATCH, DELETE, OPTIONS"),
            ("Access-Control-Allow-Headers", requestedHeaders ?? "*"),
            ("Access-Control-Allow-Credentials", "true"),
            ("Access-Control-Max-Age", "86400"),
        };

        return new CapturedResponse("HTTP/1.1", 204, "No Content", headers, []);
    }

    private static string? FindHeaderValue(IReadOnlyList<(string Name, string Value)> headers, string name)
    {
        foreach (var (headerName, value) in headers)
        {
            if (string.Equals(headerName, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Whether a 101 response switched to WebSocket (Upgrade: websocket).</summary>
    internal static bool IsWebSocketUpgrade(IReadOnlyList<(string Name, string Value)> headers) =>
        ContentDecoder.FindHeader(headers, "Upgrade")?.Split(',').Any(p => string.Equals(p.Trim(), "websocket", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Passes an upgraded (101, not WebSocket) connection's bytes through in
    /// both directions until it's done. Once one side closes, the other gets
    /// <see cref="WebSocketRelay.CloseGrace"/> to finish too -- the same
    /// rule <see cref="WebSocketRelay"/> uses -- and is then cut off: just
    /// waiting for both directions would leave a connection open for ever
    /// whenever one side goes away and the other never notices (a server
    /// that closes while the client sits waiting to send, say), holding
    /// both connections and this task with it.
    /// </summary>
    private static async Task RelayRawBytesUntilClosedAsync(
        Stream clientTls, Stream upstreamTls, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientToUpstream = clientTls.CopyToAsync(upstreamTls, stop.Token);
        var upstreamToClient = upstreamTls.CopyToAsync(clientTls, stop.Token);

        try
        {
            var first = await Task.WhenAny(clientToUpstream, upstreamToClient);
            var other = first == clientToUpstream ? upstreamToClient : clientToUpstream;
            if (await Task.WhenAny(other, Task.Delay(WebSocketRelay.CloseGrace, cancellationToken)) != other)
            {
                await stop.CancelAsync();
            }

            await Task.WhenAll(clientToUpstream, upstreamToClient);
        }
        catch (Exception)
        {
            // A reset or a one-sided close is the normal way an upgraded
            // connection ends here; nothing to act on.
        }
    }

    /// <summary>
    /// A token for one wait under <see cref="ClientIdleTimeout"/>: cancelled
    /// when <paramref name="cancellationToken"/> is, or once
    /// <paramref name="idleTimeout"/> has passed (never, for
    /// <see cref="Timeout.InfiniteTimeSpan"/>).
    /// </summary>
    private static CancellationTokenSource StartIdleTimer(TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(idleTimeout);
        return timer;
    }

    /// <summary>
    /// <see cref="Http1MessageReader.ReadRequestPreambleAsync"/>, given
    /// <paramref name="idleTimeout"/> to arrive in full. Null, as for a
    /// client that closed, when it doesn't: a client that went quiet before
    /// starting a request and one that stalled halfway through its headers
    /// both just lose the connection (see <see cref="ClientIdleTimeout"/>).
    /// </summary>
    private static async Task<CapturedRequest?> ReadRequestPreambleWithinAsync(
        Stream client, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        using var timer = StartIdleTimer(idleTimeout, cancellationToken);
        try
        {
            return await Http1MessageReader.ReadRequestPreambleAsync(client, timer.Token);
        }
        catch (Exception) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A cancelled socket read surfaces as OperationCanceledException,
            // or as an IOException from a stream wrapped around it.
            return null;
        }
    }

    /// <summary>
    /// The host and port a CONNECT request names: <c>host:port</c>, with
    /// 443 when the port is missing or unreadable. An IPv6 address comes in
    /// brackets (<c>[::1]:443</c>, the authority form RFC 9110 requires),
    /// which are removed here -- from now on the host is used as a bare
    /// address (DNS, the leaf certificate's IP SAN, TLS), and whatever writes
    /// it back into a URL or authority (<see cref="UpstreamProxy"/>'s CONNECT,
    /// a PAC script's URL) adds them again. An IPv6 address without brackets
    /// has no room for a port -- every colon belongs to the address -- so
    /// it's taken whole, on 443.
    /// </summary>
    internal static (string Host, int Port) ParseConnectTarget(string target)
    {
        string host;
        string? portText;
        if (target.StartsWith('[') && target.IndexOf(']') is var close and > 0)
        {
            host = target[1..close];
            var rest = target[(close + 1)..];
            portText = rest.StartsWith(':') ? rest[1..] : null;
        }
        else if (target.IndexOf(':') is var separator and >= 0 && separator == target.LastIndexOf(':'))
        {
            host = target[..separator];
            portText = target[(separator + 1)..];
        }
        else
        {
            // No colon at all, or more than one: a bare IPv6 address.
            host = target;
            portText = null;
        }

        return int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) &&
               port is > 0 and <= IPEndPoint.MaxPort
            ? (host, port)
            : (host, 443);
    }
}
