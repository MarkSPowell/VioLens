using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Violens.ProxyCore.SystemProxy;

/// <summary>
/// The system proxy on macOS, set per network service with
/// <c>networksetup</c> (see the Interception Certificate Design). Like
/// <see cref="WinInetSystemProxy"/>, <see cref="Enable"/> backs up the
/// previous settings first, <see cref="Disable"/> restores them, and
/// <see cref="RecoverFromCrash"/> restores a backup left by a crash.
/// </summary>
internal static class MacOSSystemProxy
{
    /// <summary>How long one <c>networksetup</c> run may take: long enough to answer a password prompt.</summary>
    private static readonly TimeSpan NetworkSetupTimeout = TimeSpan.FromMinutes(2);

    private static string BackupFilePath =>
        Path.Combine(ProxyEngine.Identity.ResolveDataFolder(), "system-proxy-backup-macos.json");

    /// <summary>
    /// Points every enabled service's HTTP and HTTPS proxy at
    /// <c>127.0.0.1:port</c>. Calling it twice doesn't overwrite the first
    /// backup.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void Enable(int port)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var serviceNames = ListEnabledNetworkServices();

        if (!File.Exists(BackupFilePath))
        {
            WriteBackup(new SystemProxyBackup
            {
                ManagedPort = port,
                Services = serviceNames.Select(CaptureServiceBackup).ToList(),
            });
        }

        foreach (var serviceName in serviceNames)
        {
            RunNetworkSetupCommand(BuildSetProxyArguments(serviceName, "127.0.0.1", port, secure: false));
            RunNetworkSetupCommand(BuildSetProxyStateArguments(serviceName, enabled: true, secure: false));
            RunNetworkSetupCommand(BuildSetProxyArguments(serviceName, "127.0.0.1", port, secure: true));
            RunNetworkSetupCommand(BuildSetProxyStateArguments(serviceName, enabled: true, secure: true));

            // An automatic proxy configuration script, or automatic proxy
            // discovery, would take priority over the proxy just set, so
            // turn both off for the capture; the backup puts them back.
            TryRunNetworkSetupCommand(BuildSetAutoProxyStateArguments(serviceName, enabled: false));
            TryRunNetworkSetupCommand(BuildSetProxyAutoDiscoveryArguments(serviceName, enabled: false));
        }
    }

    /// <summary>
    /// The proxy an enabled service uses now, to forward through. Call before
    /// <see cref="Enable"/>. A setting pointing at VioLens itself is ignored; an
    /// automatic configuration script or discovery gives a
    /// <see cref="Proxy.AutoConfig.ProxyAutoConfig"/>, with any manual proxy as
    /// the fallback.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static UpstreamProxyDetection DetectUpstream(int ownPort)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return UpstreamProxyDetection.None;
        }

        foreach (var serviceName in ListEnabledNetworkServices())
        {
            var settings = CaptureServiceBackup(serviceName);
            var proxy =
                (settings.SecureWebProxyEnabled && settings.SecureWebProxyServer is { Length: > 0 } secureServer && settings.SecureWebProxyPort is { } securePort
                    ? new Proxy.UpstreamProxy(secureServer, securePort)
                    : null) ??
                (settings.WebProxyEnabled && settings.WebProxyServer is { Length: > 0 } webServer && settings.WebProxyPort is { } webPort
                    ? new Proxy.UpstreamProxy(webServer, webPort)
                    : null);
            if (proxy is not null && proxy.IsSelf(ownPort))
            {
                proxy = null;
            }

            var scriptUrl = settings.AutoProxyEnabled == true ? settings.AutoProxyUrl : null;
            if (settings.AutoDiscoveryEnabled == true || !string.IsNullOrWhiteSpace(scriptUrl))
            {
                return UpstreamProxyDetection.Automatic(
                    Proxy.AutoConfig.ProxyAutoConfig.Create(settings.AutoDiscoveryEnabled == true, scriptUrl, proxy),
                    $"{serviceName} proxy settings");
            }

            if (proxy is not null)
            {
                return new UpstreamProxyDetection(proxy, $"forwarding through {proxy} ({serviceName} proxy settings)");
            }
        }

        return UpstreamProxyDetection.None;
    }

    /// <summary>
    /// Restores the backed-up settings and deletes the backup. Without a
    /// backup, turns each service's proxy off rather than leave it pointing at
    /// a dead port.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void Disable()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var backup = ReadBackup();

        if (backup is not null)
        {
            foreach (var service in backup.Services)
            {
                RestoreServiceBackup(service);
            }
        }
        else
        {
            foreach (var serviceName in ListEnabledNetworkServices())
            {
                RunNetworkSetupCommand(BuildSetProxyStateArguments(serviceName, enabled: false, secure: false));
                RunNetworkSetupCommand(BuildSetProxyStateArguments(serviceName, enabled: false, secure: true));
            }
        }

        DeleteBackupFile();
    }

    /// <summary>Call once at launch: restores a backup left by a run that didn't stop cleanly.</summary>
    [SupportedOSPlatform("macos")]
    public static void RecoverFromCrash()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(BackupFilePath))
        {
            return;
        }

        Disable();
    }

    [SupportedOSPlatform("macos")]
    private static List<string> ListEnabledNetworkServices()
    {
        var output = RunNetworkSetupCommand(BuildListAllNetworkServicesArguments());
        return ParseNetworkServiceNames(output);
    }

    [SupportedOSPlatform("macos")]
    private static NetworkServiceProxyBackup CaptureServiceBackup(string serviceName)
    {
        var web = ParseProxyState(RunNetworkSetupCommand(BuildGetProxyArguments(serviceName, secure: false)));
        var secureWeb = ParseProxyState(RunNetworkSetupCommand(BuildGetProxyArguments(serviceName, secure: true)));
        var autoProxy = TryRunNetworkSetupCommand(BuildGetAutoProxyUrlArguments(serviceName)) is { } autoProxyOutput
            ? ParseAutoProxyUrl(autoProxyOutput)
            : ((string? Url, bool Enabled)?)null;
        var autoDiscovery = TryRunNetworkSetupCommand(BuildGetProxyAutoDiscoveryArguments(serviceName)) is { } discoveryOutput
            ? ParseProxyAutoDiscovery(discoveryOutput)
            : null;

        return new NetworkServiceProxyBackup
        {
            AutoProxyUrl = autoProxy?.Url,
            AutoProxyEnabled = autoProxy?.Enabled,
            AutoDiscoveryEnabled = autoDiscovery,
            ServiceName = serviceName,
            WebProxyEnabled = web.Enabled,
            WebProxyServer = web.Server,
            WebProxyPort = web.Port,
            SecureWebProxyEnabled = secureWeb.Enabled,
            SecureWebProxyServer = secureWeb.Server,
            SecureWebProxyPort = secureWeb.Port,
        };
    }

    [SupportedOSPlatform("macos")]
    private static void RestoreServiceBackup(NetworkServiceProxyBackup service)
    {
        // Only restore a server if one was set before; the enabled state is
        // restored regardless.
        if (service.WebProxyServer is not null && service.WebProxyPort is not null)
        {
            RunNetworkSetupCommand(BuildSetProxyArguments(service.ServiceName, service.WebProxyServer, service.WebProxyPort.Value, secure: false));
        }

        RunNetworkSetupCommand(BuildSetProxyStateArguments(service.ServiceName, service.WebProxyEnabled, secure: false));

        if (service.SecureWebProxyServer is not null && service.SecureWebProxyPort is not null)
        {
            RunNetworkSetupCommand(BuildSetProxyArguments(service.ServiceName, service.SecureWebProxyServer, service.SecureWebProxyPort.Value, secure: true));
        }

        RunNetworkSetupCommand(BuildSetProxyStateArguments(service.ServiceName, service.SecureWebProxyEnabled, secure: true));

        // Null in a backup from an older version, which didn't change these.
        if (service.AutoProxyEnabled is { } autoProxyEnabled)
        {
            if (service.AutoProxyUrl is not null)
            {
                TryRunNetworkSetupCommand(BuildSetAutoProxyUrlArguments(service.ServiceName, service.AutoProxyUrl));
            }

            TryRunNetworkSetupCommand(BuildSetAutoProxyStateArguments(service.ServiceName, autoProxyEnabled));
        }

        if (service.AutoDiscoveryEnabled is { } autoDiscoveryEnabled)
        {
            TryRunNetworkSetupCommand(BuildSetProxyAutoDiscoveryArguments(service.ServiceName, autoDiscoveryEnabled));
        }
    }

    /// <summary>Internal so tests can check the arguments without running <c>networksetup</c>.</summary>
    internal static string[] BuildListAllNetworkServicesArguments() => ["-listallnetworkservices"];

    /// <summary>macOS keeps the HTTP and HTTPS proxies as separate settings.</summary>
    internal static string[] BuildGetProxyArguments(string serviceName, bool secure) =>
        [secure ? "-getsecurewebproxy" : "-getwebproxy", serviceName];

    internal static string[] BuildSetProxyArguments(string serviceName, string host, int port, bool secure) =>
        [secure ? "-setsecurewebproxy" : "-setwebproxy", serviceName, host, port.ToString(CultureInfo.InvariantCulture)];

    internal static string[] BuildSetProxyStateArguments(string serviceName, bool enabled, bool secure) =>
        [secure ? "-setsecurewebproxystate" : "-setwebproxystate", serviceName, enabled ? "on" : "off"];

    internal static string[] BuildGetAutoProxyUrlArguments(string serviceName) => ["-getautoproxyurl", serviceName];

    internal static string[] BuildSetAutoProxyUrlArguments(string serviceName, string url) => ["-setautoproxyurl", serviceName, url];

    internal static string[] BuildSetAutoProxyStateArguments(string serviceName, bool enabled) =>
        ["-setautoproxystate", serviceName, enabled ? "on" : "off"];

    internal static string[] BuildGetProxyAutoDiscoveryArguments(string serviceName) => ["-getproxyautodiscovery", serviceName];

    internal static string[] BuildSetProxyAutoDiscoveryArguments(string serviceName, bool enabled) =>
        ["-setproxyautodiscovery", serviceName, enabled ? "on" : "off"];

    /// <summary>
    /// Parses <c>networksetup -getautoproxyurl</c>'s output:
    /// <code>
    /// URL: http://wpad.example.com/proxy.pac
    /// Enabled: Yes
    /// </code>
    /// with "URL: (null)" when none is set.
    /// </summary>
    internal static (string? Url, bool Enabled) ParseAutoProxyUrl(string output)
    {
        string? url = null;
        var enabled = false;
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("URL:", StringComparison.Ordinal))
            {
                var value = line["URL:".Length..].Trim();
                url = value.Length == 0 || value == "(null)" ? null : value;
            }
            else if (line.StartsWith("Enabled:", StringComparison.Ordinal))
            {
                enabled = line["Enabled:".Length..].Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        return (url, enabled);
    }

    /// <summary>Parses <c>networksetup -getproxyautodiscovery</c>'s "Auto Proxy Discovery: On" (or Off).</summary>
    internal static bool? ParseProxyAutoDiscovery(string output)
    {
        var colon = output.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var value = output[(colon + 1)..].Trim();
        if (value.Equals("On", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Equals("Off", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    /// <summary>
    /// Parses <c>-listallnetworkservices</c>: a header line, then one service
    /// per line, disabled ones starred. Returns the enabled ones.
    /// </summary>
    internal static List<string> ParseNetworkServiceNames(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines
            .Skip(1)
            .Where(line => !line.StartsWith('*'))
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Parses <c>-getwebproxy</c>/<c>-getsecurewebproxy</c>:
    /// <code>
    /// Enabled: Yes
    /// Server: 127.0.0.1
    /// Port: 8888
    /// Authenticated Proxy Enabled: 0
    /// </code>
    /// An empty server gives null server and port, so "nothing set" is told
    /// apart from port 0.
    /// </summary>
    internal static ProxyState ParseProxyState(string output)
    {
        var enabled = false;
        string? server = null;
        int? port = null;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("Enabled:", StringComparison.Ordinal))
            {
                enabled = line["Enabled:".Length..].Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("Server:", StringComparison.Ordinal))
            {
                var value = line["Server:".Length..].Trim();
                server = value.Length > 0 ? value : null;
            }
            else if (line.StartsWith("Port:", StringComparison.Ordinal))
            {
                var value = line["Port:".Length..].Trim();
                port = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed != 0
                    ? parsed
                    : null;
            }
        }

        // A server without a port can't be restored; treat it as unset.
        if (server is not null && port is null)
        {
            server = null;
        }

        return new ProxyState(enabled, server, port);
    }

    /// <summary>
    /// For the automatic-proxy settings, which a service may not support:
    /// returns null instead of throwing when <c>networksetup</c> fails.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static string? TryRunNetworkSetupCommand(string[] arguments)
    {
        try
        {
            return RunNetworkSetupCommand(arguments);
        }
        catch (SystemProxyCommandException ex)
        {
            ProxyEngine.Log("system-proxy", ex.Message);
            return null;
        }
    }

    [SupportedOSPlatform("macos")]
    private static string RunNetworkSetupCommand(string[] arguments)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath.Find("networksetup")!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new SystemProxyCommandException("Failed to start the 'networksetup' process.", exitCode: -1, standardError: string.Empty);

        // Drain both streams at once so neither fills up, and time out in case
        // a password prompt is never answered.
        var standardOutputRead = process.StandardOutput.ReadToEndAsync();
        var standardErrorRead = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(NetworkSetupTimeout))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // It exited just now.
            }

            throw new SystemProxyCommandException(
                $"'networksetup {string.Join(' ', arguments)}' didn't finish within {NetworkSetupTimeout.TotalSeconds:0} seconds.",
                exitCode: -1,
                standardError: string.Empty);
        }

        var standardOutput = standardOutputRead.GetAwaiter().GetResult();
        var standardError = standardErrorRead.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new SystemProxyCommandException(
                $"'networksetup {string.Join(' ', arguments)}' exited with code {process.ExitCode}.",
                process.ExitCode,
                standardError);
        }

        return standardOutput;
    }

    private static SystemProxyBackup? ReadBackup()
    {
        try
        {
            if (!File.Exists(BackupFilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SystemProxyBackup>(File.ReadAllText(BackupFilePath));
        }
        catch
        {
            // A crash can leave a corrupt backup; treat it as none, so Disable
            // turns the proxy off instead of throwing.
            return null;
        }
    }

    private static void WriteBackup(SystemProxyBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupFilePath)!);
        File.WriteAllText(BackupFilePath, JsonSerializer.Serialize(backup));
    }

    private static void DeleteBackupFile()
    {
        try
        {
            File.Delete(BackupFilePath);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Settable properties, so System.Text.Json round-trips it simply.</summary>
    internal sealed class SystemProxyBackup
    {
        public int ManagedPort { get; set; }

        public List<NetworkServiceProxyBackup> Services { get; set; } = [];
    }

    internal sealed class NetworkServiceProxyBackup
    {
        public string ServiceName { get; set; } = string.Empty;

        public bool WebProxyEnabled { get; set; }

        public string? WebProxyServer { get; set; }

        public int? WebProxyPort { get; set; }

        public bool SecureWebProxyEnabled { get; set; }

        public string? SecureWebProxyServer { get; set; }

        public int? SecureWebProxyPort { get; set; }

        /// <summary>The automatic proxy configuration script's URL ("Automatic proxy configuration").</summary>
        public string? AutoProxyUrl { get; set; }

        /// <summary>Whether that script was on; null in a backup from an older version.</summary>
        public bool? AutoProxyEnabled { get; set; }

        /// <summary>"Auto proxy discovery"; null in a backup from an older version.</summary>
        public bool? AutoDiscoveryEnabled { get; set; }
    }

    /// <summary>One <c>-getwebproxy</c>/<c>-getsecurewebproxy</c> result.</summary>
    internal readonly record struct ProxyState(bool Enabled, string? Server, int? Port);
}
