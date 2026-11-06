using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Violens.ProxyCore.SystemProxy;

/// <summary>
/// The system proxy on Linux, which is a desktop setting rather than an OS
/// one (see the Interception Certificate Design):
/// <list type="bullet">
/// <item>GNOME, and desktops built on its settings (Cinnamon, Budgie,
/// Pantheon, Unity): <c>org.gnome.system.proxy</c>, through
/// <c>gsettings</c>;</item>
/// <item>KDE Plasma: <c>kioslaverc</c>'s Proxy Settings, through
/// <c>kwriteconfig6</c> or <c>kwriteconfig5</c>.</item>
/// </list>
/// Chrome, Firefox (set to use the system proxy, its default) and most
/// desktop apps follow these. Both are set when both are there, since a
/// computer can have more than one desktop installed. Like
/// <see cref="MacOSSystemProxy"/>, <see cref="Enable"/> backs up the
/// previous settings first, <see cref="Disable"/> restores them, and
/// <see cref="RecoverFromCrash"/> restores a backup left by a crash.
/// Programs that only read <c>http_proxy</c> from their environment can't
/// be changed while they run, so they're not covered.
/// </summary>
internal static class LinuxSystemProxy
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private const string GnomeSchema = "org.gnome.system.proxy";
    private const string KdeFile = "kioslaverc";
    private const string KdeGroup = "Proxy Settings";

    /// <summary>The GNOME settings VioLens changes, as schema and key.</summary>
    internal static readonly (string Schema, string Key)[] GnomeKeys =
    [
        (GnomeSchema, "mode"),
        (GnomeSchema, "autoconfig-url"),
        (GnomeSchema + ".http", "host"),
        (GnomeSchema + ".http", "port"),
        (GnomeSchema + ".https", "host"),
        (GnomeSchema + ".https", "port"),
    ];

    /// <summary>The KDE settings VioLens changes.</summary>
    internal static readonly string[] KdeKeys = ["ProxyType", "httpProxy", "httpsProxy", "Proxy Config Script"];

    private static string BackupFilePath =>
        Path.Combine(ProxyEngine.Identity.ResolveDataFolder(), "system-proxy-backup-linux.json");

    /// <summary>
    /// Points GNOME's and KDE's proxy settings, whichever are there, at
    /// <c>127.0.0.1:port</c>. Throws <see cref="SystemProxyCommandException"/>
    /// when neither is. Calling it twice doesn't overwrite the first backup.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static void Enable(int port)
    {
        var gnome = GnomeAvailable();
        var kde = KdeTools();
        if (!gnome && kde is null)
        {
            throw new SystemProxyCommandException(
                "this desktop's proxy settings aren't GNOME's or KDE's, which are the ones VioLens can change",
                exitCode: -1,
                standardError: string.Empty);
        }

        if (!File.Exists(BackupFilePath))
        {
            WriteBackup(new SystemProxyBackup
            {
                ManagedPort = port,
                Gnome = gnome ? GnomeKeys.ToDictionary(k => k.Schema + " " + k.Key, k => Gsettings("get", k.Schema, k.Key).Trim()) : null,
                Kde = kde is { } tools ? KdeKeys.ToDictionary(k => k, k => KdeRead(tools.Read, k)) : null,
            });
        }

        if (gnome)
        {
            foreach (var (schema, key, value) in BuildGnomeEnableSettings(port))
            {
                Gsettings("set", schema, key, value);
            }
        }

        if (kde is { } kdeTools)
        {
            foreach (var (key, value) in BuildKdeEnableSettings(port))
            {
                Run(kdeTools.Write, BuildKdeWriteArguments(key, value));
            }

            NotifyKde();
        }
    }

    /// <summary>
    /// The proxy the desktop is set to use now, to forward through. Call
    /// before <see cref="Enable"/>. A setting pointing at VioLens itself is
    /// ignored. Without a desktop setting, <c>https_proxy</c> or
    /// <c>http_proxy</c> in VioLens's own environment is used.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static UpstreamProxyDetection DetectUpstream(int ownPort)
    {
        DesktopProxy? found = null;
        if (KdeTools() is { } kde && IsKdeSession())
        {
            found = FromKde(KdeKeys.ToDictionary(k => k, k => KdeRead(kde.Read, k)), ownPort);
        }
        else if (GnomeAvailable())
        {
            found = FromGnome(GnomeKeys.ToDictionary(k => k.Schema + " " + k.Key, k => Gsettings("get", k.Schema, k.Key).Trim()), ownPort);
        }

        found ??= FromEnvironment(Environment.GetEnvironmentVariable, ownPort);
        if (found is null)
        {
            return UpstreamProxyDetection.None;
        }

        return found.Proxy is { } proxy
            ? new UpstreamProxyDetection(proxy, $"forwarding through {proxy} ({found.Source})")
            : UpstreamProxyDetection.Automatic(
                Proxy.AutoConfig.ProxyAutoConfig.Create(found.AutoDetect, found.ScriptUrl, fallback: null),
                found.Source);
    }

    /// <summary>
    /// A proxy setting read from the desktop or environment: a proxy, or
    /// automatic configuration (a script, or discovery), and where it came from.
    /// </summary>
    internal sealed record DesktopProxy(Proxy.UpstreamProxy? Proxy, bool AutoDetect, string? ScriptUrl, string Source);

    /// <summary>Restores the backed-up settings and deletes the backup; without one, turns the proxy off.</summary>
    [SupportedOSPlatform("linux")]
    public static void Disable()
    {
        var backup = ReadBackup();
        var gnome = GnomeAvailable();
        var kde = KdeTools();

        if (gnome)
        {
            if (backup?.Gnome is { } gnomeBackup)
            {
                foreach (var (schema, key) in GnomeKeys)
                {
                    if (gnomeBackup.TryGetValue(schema + " " + key, out var value) && value.Length > 0)
                    {
                        Gsettings("set", schema, key, value);
                    }
                }
            }
            else if (backup is null)
            {
                Gsettings("set", GnomeSchema, "mode", "'none'");
            }
        }

        if (kde is { } kdeTools)
        {
            if (backup?.Kde is { } kdeBackup)
            {
                foreach (var key in KdeKeys)
                {
                    Run(kdeTools.Write, kdeBackup.TryGetValue(key, out var value) && value.Length > 0
                        ? BuildKdeWriteArguments(key, value)
                        : BuildKdeDeleteArguments(key));
                }
            }
            else if (backup is null)
            {
                Run(kdeTools.Write, BuildKdeWriteArguments("ProxyType", "0"));
            }

            NotifyKde();
        }

        DeleteBackupFile();
    }

    /// <summary>Call once at launch: restores a backup left by a run that didn't stop cleanly.</summary>
    [SupportedOSPlatform("linux")]
    public static void RecoverFromCrash()
    {
        if (File.Exists(BackupFilePath))
        {
            Disable();
        }
    }

    /// <summary>The GNOME settings for capturing, as GVariant text; mode last, so the address is in place first.</summary>
    internal static IReadOnlyList<(string Schema, string Key, string Value)> BuildGnomeEnableSettings(int port)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        return
        [
            (GnomeSchema + ".http", "host", "'127.0.0.1'"),
            (GnomeSchema + ".http", "port", portText),
            (GnomeSchema + ".https", "host", "'127.0.0.1'"),
            (GnomeSchema + ".https", "port", portText),
            (GnomeSchema, "mode", "'manual'"),
        ];
    }

    /// <summary>KDE's settings for capturing: ProxyType 1 is "use manually specified proxy configuration".</summary>
    internal static IReadOnlyList<(string Key, string Value)> BuildKdeEnableSettings(int port)
    {
        var address = "http://127.0.0.1 " + port.ToString(CultureInfo.InvariantCulture);
        return [("httpProxy", address), ("httpsProxy", address), ("ProxyType", "1")];
    }

    internal static string[] BuildKdeWriteArguments(string key, string value) =>
        ["--file", KdeFile, "--group", KdeGroup, "--key", key, value];

    internal static string[] BuildKdeDeleteArguments(string key) =>
        ["--file", KdeFile, "--group", KdeGroup, "--key", key, "--delete"];

    internal static string[] BuildKdeReadArguments(string key) =>
        ["--file", KdeFile, "--group", KdeGroup, "--key", key];

    /// <summary>
    /// GNOME's settings (keyed "schema key", values as <c>gsettings get</c>
    /// prints them) as an upstream proxy: "manual" gives its HTTPS proxy, or
    /// else its HTTP one; "auto" a PAC script, or discovery when no script
    /// address is set. Null for "none". Internal for tests.
    /// </summary>
    internal static DesktopProxy? FromGnome(IReadOnlyDictionary<string, string> settings, int ownPort)
    {
        string Get(string schema, string key) =>
            settings.TryGetValue(schema + " " + key, out var value) ? UnquoteGVariant(value) : string.Empty;

        switch (Get(GnomeSchema, "mode"))
        {
            case "auto":
            {
                var url = Get(GnomeSchema, "autoconfig-url");
                return new DesktopProxy(null, AutoDetect: url.Length == 0, url.Length > 0 ? url : null, "GNOME proxy settings");
            }

            case "manual":
                foreach (var schema in new[] { GnomeSchema + ".https", GnomeSchema + ".http" })
                {
                    var host = Get(schema, "host");
                    if (host.Length > 0 && int.TryParse(Get(schema, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port > 0)
                    {
                        var proxy = new Proxy.UpstreamProxy(host, port);
                        if (!proxy.IsSelf(ownPort))
                        {
                            return new DesktopProxy(proxy, false, null, "GNOME proxy settings");
                        }
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// KDE's settings as an upstream proxy: ProxyType 1 is a manual proxy, 2
    /// a PAC script, 3 discovery, 4 the environment variables. Internal for tests.
    /// </summary>
    internal static DesktopProxy? FromKde(IReadOnlyDictionary<string, string> settings, int ownPort)
    {
        string Get(string key) => settings.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

        switch (Get("ProxyType"))
        {
            case "1":
                foreach (var key in new[] { "httpsProxy", "httpProxy" })
                {
                    if (ParseKdeProxy(Get(key)) is { } proxy && !proxy.IsSelf(ownPort))
                    {
                        return new DesktopProxy(proxy, false, null, "KDE proxy settings");
                    }
                }

                return null;

            case "2" when Get("Proxy Config Script").Length > 0:
                return new DesktopProxy(null, AutoDetect: false, Get("Proxy Config Script"), "KDE proxy settings");

            case "3":
                return new DesktopProxy(null, AutoDetect: true, ScriptUrl: null, "KDE proxy settings");

            default:
                return null;
        }
    }

    /// <summary><c>https_proxy</c>, then <c>http_proxy</c> (either case), as an upstream proxy. Internal for tests.</summary>
    internal static DesktopProxy? FromEnvironment(Func<string, string?> getVariable, int ownPort)
    {
        foreach (var name in new[] { "https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY" })
        {
            if (getVariable(name) is { Length: > 0 } value &&
                Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttp && uri.Host.Length > 0)
            {
                var proxy = new Proxy.UpstreamProxy(uri.DnsSafeHost, uri.Port);
                if (!proxy.IsSelf(ownPort))
                {
                    return new DesktopProxy(proxy, false, null, name);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A KDE proxy setting: "http://host port" as KDE writes it, or
    /// "http://host:port" or "host:port". Null when empty or unreadable.
    /// </summary>
    internal static Proxy.UpstreamProxy? ParseKdeProxy(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var space = value.LastIndexOf(' ');
        if (space > 0 && int.TryParse(value[(space + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var spacedPort))
        {
            value = value[..space].TrimEnd() + ":" + spacedPort.ToString(CultureInfo.InvariantCulture);
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        // DnsSafeHost: an IPv6 address without its brackets.
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.Port > 0
            ? new Proxy.UpstreamProxy(uri.DnsSafeHost, uri.Port)
            : null;
    }

    /// <summary>A GVariant string as <c>gsettings get</c> prints it (<c>'manual'</c>), unquoted; a number as is.</summary>
    internal static string UnquoteGVariant(string value)
    {
        value = value.Trim();
        if (value.StartsWith("uint32 ", StringComparison.Ordinal))
        {
            value = value["uint32 ".Length..];
        }

        return value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0]
            ? value[1..^1].Replace("\\'", "'", StringComparison.Ordinal)
            : value;
    }

    private static bool IsKdeSession() =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
        .Split(':').Any(part => part.Equals("KDE", StringComparison.OrdinalIgnoreCase));

    [SupportedOSPlatform("linux")]
    private static bool GnomeAvailable()
    {
        try
        {
            Gsettings("get", GnomeSchema, "mode");
            return true;
        }
        catch (SystemProxyCommandException)
        {
            return false;
        }
    }

    /// <summary>
    /// KDE's config tools (6, else 5), when this is a KDE session or KDE
    /// proxy settings already exist; null otherwise.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static (string Read, string Write)? KdeTools()
    {
        var kioslaverc = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            KdeFile);
        if (!IsKdeSession() && !File.Exists(kioslaverc))
        {
            return null;
        }

        foreach (var version in new[] { "6", "5" })
        {
            if (OnPath("kwriteconfig" + version) && OnPath("kreadconfig" + version))
            {
                return ("kreadconfig" + version, "kwriteconfig" + version);
            }
        }

        return null;
    }

    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Any(folder => File.Exists(Path.Combine(folder, program)));

    [SupportedOSPlatform("linux")]
    private static string KdeRead(string tool, string key) => Run(tool, BuildKdeReadArguments(key)).TrimEnd('\n', '\r');

    /// <summary>Tells running KDE apps to reread their proxy settings. Best effort: apps started later read them anyway.</summary>
    [SupportedOSPlatform("linux")]
    private static void NotifyKde()
    {
        try
        {
            Run("dbus-send", ["--type=signal", "/KIO/Scheduler", "org.kde.KIO.Scheduler.reparseSlaveConfiguration", "string:"]);
        }
        catch (SystemProxyCommandException ex)
        {
            ProxyEngine.Log("system-proxy", ex.Message);
        }
    }

    [SupportedOSPlatform("linux")]
    private static string Gsettings(string verb, string schema, string key, string? value = null) =>
        Run("gsettings", value is null ? [verb, schema, key] : [verb, schema, key, value]);

    [SupportedOSPlatform("linux")]
    private static string Run(string program, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            process = null;
        }

        if (process is null)
        {
            throw new SystemProxyCommandException($"'{program}' isn't installed.", exitCode: -1, standardError: string.Empty);
        }

        using (process)
        {
            var standardOutputRead = process.StandardOutput.ReadToEndAsync();
            var standardErrorRead = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeout))
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
                    $"'{program} {string.Join(' ', arguments)}' didn't finish within {CommandTimeout.TotalSeconds:0} seconds.",
                    exitCode: -1,
                    standardError: string.Empty);
            }

            var standardOutput = standardOutputRead.GetAwaiter().GetResult();
            var standardError = standardErrorRead.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new SystemProxyCommandException(
                    $"'{program} {string.Join(' ', arguments)}' exited with code {process.ExitCode}.",
                    process.ExitCode,
                    standardError);
            }

            return standardOutput;
        }
    }

    private static SystemProxyBackup? ReadBackup()
    {
        try
        {
            return File.Exists(BackupFilePath)
                ? JsonSerializer.Deserialize<SystemProxyBackup>(File.ReadAllText(BackupFilePath))
                : null;
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

    /// <summary>
    /// The settings before VioLens changed them, exactly as the tools print
    /// them, so they're written back as they were. Null for a desktop that
    /// wasn't there; an empty KDE value means the key wasn't set.
    /// </summary>
    internal sealed class SystemProxyBackup
    {
        public int ManagedPort { get; set; }

        public Dictionary<string, string>? Gnome { get; set; }

        public Dictionary<string, string>? Kde { get; set; }
    }
}
