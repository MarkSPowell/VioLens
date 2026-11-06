using System.Collections.Concurrent;

namespace Violens.ProxyCore.Proxy.AutoConfig;

/// <summary>
/// Chooses the upstream proxy per request from the network's automatic
/// proxy configuration: a PAC script at a configured URL, or one found by
/// automatic detection (<see cref="WpadDiscovery"/>), as Windows' and
/// macOS' "Use setup script" and "Automatically detect settings" do.
///
/// The script is fetched in the background as soon as this is created;
/// requests that arrive first wait for it. If there's no script, or it
/// can't be fetched or run, the <c>fallback</c> proxy (the system's manual
/// proxy setting, if any) is used, or else a direct connection -- also what
/// Windows does. <see cref="Description"/> says which, and
/// <see cref="DescriptionChanged"/> fires when it changes.
/// </summary>
public sealed class ProxyAutoConfig : IDisposable
{
    private const int MaxCachedAnswers = 2000;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest script VioLens will load. Real ones are a few hundred KB
    /// at most; without a limit, a server could send one without end.
    /// </summary>
    internal const int MaxScriptBytes = 4 * 1024 * 1024;

    private readonly string? _scriptUrl;
    private readonly bool _autoDetect;
    private readonly UpstreamProxy? _fallback;
    private readonly IPacHost _host;
    private readonly CancellationTokenSource _disposed = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<UpstreamProxy?>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task<PacScript?> _script;
    private string _description;

    private ProxyAutoConfig(string? scriptUrl, bool autoDetect, UpstreamProxy? fallback, IPacHost? host, Func<CancellationToken, Task<string>>? download)
    {
        _scriptUrl = scriptUrl;
        _autoDetect = autoDetect;
        _fallback = fallback;
        _host = host ?? SystemPacHost.Instance;
        _description = autoDetect
            ? "looking for automatic proxy settings"
            : $"loading the automatic proxy configuration script {scriptUrl}";
        _script = Task.Run(() => LoadAsync(download, _disposed.Token));
    }

    /// <summary>
    /// The system's automatic proxy configuration. With
    /// <paramref name="autoDetect"/>, the network is searched first (WPAD),
    /// then <paramref name="scriptUrl"/> is used if that finds nothing.
    /// </summary>
    public static ProxyAutoConfig Create(bool autoDetect, string? scriptUrl, UpstreamProxy? fallback, IPacHost? host = null) =>
        new(string.IsNullOrWhiteSpace(scriptUrl) ? null : scriptUrl.Trim(), autoDetect, fallback, host, download: null);

    /// <summary>A script given directly, as if downloaded from <paramref name="scriptUrl"/>. For tests.</summary>
    public static ProxyAutoConfig FromScript(string script, IPacHost? host = null, UpstreamProxy? fallback = null, string scriptUrl = "http://pac.test/proxy.pac") =>
        new(scriptUrl, autoDetect: false, fallback, host, _ => Task.FromResult(script));

    /// <summary>What's in use, for the status line, e.g. "using the automatic proxy configuration script http://wpad/wpad.dat".</summary>
    public string Description => Volatile.Read(ref _description);

    /// <summary>Fires (on a background thread) when <see cref="Description"/> changes.</summary>
    public event Action<string>? DescriptionChanged;

    /// <summary>Completes when the script has loaded, or failed to.</summary>
    public Task Loaded => _script;

    /// <summary>
    /// The routes to try for a request, in order: a proxy, or null for a
    /// direct connection. <paramref name="url"/> is what the script is
    /// given; for HTTPS, browsers pass only the scheme, host and port
    /// ("https://example.com/"), and so should callers.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamProxy?>> GetRoutesAsync(string url, string host, CancellationToken cancellationToken)
    {
        var script = await _script.WaitAsync(cancellationToken);
        if (script is null)
        {
            return FallbackRoutes(host);
        }

        if (_answers.TryGetValue(url, out var cached))
        {
            return cached;
        }

        IReadOnlyList<UpstreamProxy?> routes;
        try
        {
            var answer = await Task.Run(() => script.FindProxyForUrl(url, host), cancellationToken);
            routes = PacResult.Parse(answer, out var unsupported);
            if (unsupported.Count > 0)
            {
                _host.Log($"{url}: skipped {string.Join("; ", unsupported)} (SOCKS and HTTPS proxies aren't supported yet)");
            }
        }
        catch (PacScriptException ex)
        {
            _host.Log(ex.Message);
            return FallbackRoutes(host);
        }

        if (_answers.Count >= MaxCachedAnswers)
        {
            _answers.Clear();
        }

        _answers[url] = routes;
        return routes;
    }

    /// <summary>Stops a download or detection still in progress.</summary>
    public void Dispose() => _disposed.Cancel();

    private IReadOnlyList<UpstreamProxy?> FallbackRoutes(string host) =>
        _fallback is null || _fallback.ShouldBypass(host) ? [null] : [_fallback];

    private string FallbackPhrase => _fallback is null ? "connecting directly" : $"forwarding through {_fallback}";

    private async Task<PacScript?> LoadAsync(Func<CancellationToken, Task<string>>? download, CancellationToken cancellationToken)
    {
        var url = _scriptUrl;
        var discovered = false;
        try
        {
            if (_autoDetect)
            {
                var found = await WpadDiscovery.FindScriptUrlAsync(cancellationToken);
                if (found is not null)
                {
                    url = found;
                    discovered = true;
                }
                else if (url is null)
                {
                    SetDescription($"automatic proxy detection found no configuration script, so {FallbackPhrase}");
                    return null;
                }
            }

            if (url is null)
            {
                SetDescription(FallbackPhrase);
                return null;
            }

            var source = download is not null
                ? await download(cancellationToken)
                : await DownloadAsync(url, discovered, cancellationToken);
            var script = new PacScript(source, _host);
            SetDescription($"using the automatic proxy configuration script {url}");
            return script;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // An unreachable server, a 404, bad JavaScript: all mean the
            // same thing here -- carry on without the script, and say why.
            var reason = ex is PacScriptException ? ex.Message : ex.GetBaseException().Message;
            SetDescription($"couldn't use the automatic proxy configuration script {url} ({reason}), so {FallbackPhrase}");
            return null;
        }
    }

    /// <summary>
    /// Fetches the script directly, never through a proxy: while capturing,
    /// the system proxy is VioLens itself. file:// URLs are read from disk,
    /// but only when the user's own settings name them: a URL the network
    /// offered (<paramref name="discovered"/>, from DHCP or DNS) must be
    /// http or https, so the network can't point VioLens at a local file or
    /// at a file share, which Windows would open by signing in to it as the
    /// user. Internal for tests.
    /// </summary>
    internal static async Task<string> DownloadAsync(string url, bool discovered, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new PacScriptException($"\"{url}\" isn't a URL");
        }

        var isWeb = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        if (discovered && !isWeb)
        {
            throw new PacScriptException($"the network offered \"{url}\", and only http and https scripts are used from the network");
        }

        if (uri.IsFile)
        {
            if (new FileInfo(uri.LocalPath).Length > MaxScriptBytes)
            {
                throw TooLarge();
            }

            return await File.ReadAllTextAsync(uri.LocalPath, cancellationToken);
        }

        if (!isWeb)
        {
            throw new PacScriptException($"\"{url}\" isn't an http, https or file URL");
        }

        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = DownloadTimeout };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new PacScriptException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Content.Headers.ContentLength > MaxScriptBytes)
        {
            throw TooLarge();
        }

        // Read with a limit as it arrives: Content-Length may be missing,
        // or wrong.
        using var kept = new MemoryStream();
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (kept.Length + read > MaxScriptBytes)
                {
                    throw TooLarge();
                }

                kept.Write(buffer, 0, read);
            }
        }

        kept.Position = 0;
        using var reader = new StreamReader(kept, CharsetOf(response), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static System.Text.Encoding CharsetOf(HttpResponseMessage response)
    {
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        try
        {
            return string.IsNullOrEmpty(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return System.Text.Encoding.UTF8;
        }
    }

    private static PacScriptException TooLarge() =>
        new($"the script is larger than the {MaxScriptBytes / (1024 * 1024)} MB VioLens accepts");

    private void SetDescription(string description)
    {
        Volatile.Write(ref _description, description);
        DescriptionChanged?.Invoke(description);
    }
}
