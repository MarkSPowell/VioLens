using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;

namespace Violens.ProxyCore.Proxy.AutoConfig;

/// <summary>
/// A proxy auto-config (PAC) script: the JavaScript file, usually
/// <c>proxy.pac</c> or <c>wpad.dat</c>, whose <c>FindProxyForURL(url, host)</c>
/// tells a browser which proxy to use for each request. Run with Jint, with
/// the standard helper functions every browser provides
/// (<c>isPlainHostName</c>, <c>dnsDomainIs</c>, <c>shExpMatch</c>,
/// <c>isInNet</c>, <c>dnsResolve</c>, <c>myIpAddress</c>,
/// <c>weekdayRange</c>, <c>dateRange</c>, <c>timeRange</c> and the rest),
/// plus Microsoft's IPv6 additions (<c>FindProxyForURLEx</c>,
/// <c>dnsResolveEx</c>, <c>isInNetEx</c> and so on), which Windows uses in
/// preference when a script defines them.
///
/// The script comes from the network, so it's treated as untrusted: no
/// access to .NET types, and limits on time, recursion and memory on every
/// call, so a hostile automatic-detection server can't hang or exhaust
/// VioLens.
/// </summary>
internal sealed class PacScript
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Far more than any real PAC script allocates.</summary>
    private const long MemoryLimitBytes = 64 * 1024 * 1024;

    private readonly Engine _engine;
    private readonly IPacHost _host;
    private readonly bool _hasEx;
    private readonly Lock _gate = new();

    /// <summary>Compiles <paramref name="source"/>. Throws <see cref="PacScriptException"/> if it doesn't define FindProxyForURL.</summary>
    public PacScript(string source, IPacHost? host = null)
        : this(source, host, CallTimeout)
    {
    }

    /// <summary>As above, with a different time limit per call (for tests).</summary>
    internal PacScript(string source, IPacHost? host, TimeSpan callTimeout)
    {
        _host = host ?? SystemPacHost.Instance;
        _engine = new Engine(options => options
            .TimeoutInterval(callTimeout)
            .LimitRecursion(256)
            .LimitMemory(MemoryLimitBytes));

        _engine.SetValue("__violensResolve", new Func<string, string?>(ResolveIPv4));
        _engine.SetValue("__violensResolveEx", new Func<string, string>(ResolveAll));
        _engine.SetValue("__violensMyIp", new Func<string>(MyIPv4));
        _engine.SetValue("__violensMyIpEx", new Func<string>(MyAddresses));
        _engine.SetValue("__violensIsInNet", new Func<string, string, string, bool>(IsInNet));
        _engine.SetValue("__violensIsInNetEx", new Func<string, string, bool>(IsInNetEx));
        _engine.SetValue("__violensShExpMatch", new Func<string, string, bool>(ShExpMatch));
        _engine.SetValue("__violensLog", new Action<string>(message => _host.Log(message)));

        try
        {
            _engine.Execute(Library);
            _engine.Execute(source);
        }
        catch (Exception ex)
        {
            // Jint reports bad syntax and runtime errors with different
            // exception types; either way the script can't be used.
            throw new PacScriptException($"the script couldn't be run: {ex.Message}", ex);
        }

        _hasEx = IsFunction("FindProxyForURLEx");
        if (!_hasEx && !IsFunction("FindProxyForURL"))
        {
            throw new PacScriptException("the script doesn't define FindProxyForURL");
        }
    }

    /// <summary>
    /// Runs <c>FindProxyForURL</c> (or <c>FindProxyForURLEx</c>) and returns
    /// what it says, e.g. "PROXY proxy.corp:8080; DIRECT". Throws
    /// <see cref="PacScriptException"/> if the script fails.
    /// </summary>
    public string FindProxyForUrl(string url, string host)
    {
        lock (_gate)
        {
            try
            {
                var result = _engine.Invoke(_hasEx ? "FindProxyForURLEx" : "FindProxyForURL", url, host);
                return result.IsNull() || result.IsUndefined() ? string.Empty : result.ToString();
            }
            catch (Exception ex)
            {
                throw new PacScriptException($"FindProxyForURL failed for {url}: {ex.Message}", ex);
            }
        }
    }

    private bool IsFunction(string name) =>
        _engine.Evaluate($"typeof {name} === 'function'").AsBoolean();

    private string? ResolveIPv4(string host) =>
        _host.Resolve(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();

    private string ResolveAll(string host) =>
        string.Join(";", _host.Resolve(host).Select(a => a.ToString()));

    private string MyIPv4() =>
        _host.LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";

    private string MyAddresses() =>
        string.Join(";", _host.LocalAddresses().Select(a => a.ToString()));

    /// <summary>
    /// <c>isInNet(host, pattern, mask)</c>: whether <paramref name="host"/>
    /// (resolved if it's a name) is in the IPv4 network
    /// <paramref name="pattern"/>/<paramref name="mask"/>.
    /// </summary>
    internal bool IsInNet(string host, string pattern, string mask)
    {
        var address = IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork
            ? literal
            : _host.Resolve(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (address is null ||
            !IPAddress.TryParse(pattern, out var network) || network.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(mask, out var netmask) || netmask.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var m = netmask.GetAddressBytes();
        for (var i = 0; i < 4; i++)
        {
            if ((a[i] & m[i]) != (n[i] & m[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>isInNetEx(address, "prefix/bits")</c>, for IPv4 or IPv6.</summary>
    internal static bool IsInNetEx(string address, string prefix)
    {
        var slash = prefix.IndexOf('/');
        if (slash < 0 ||
            !IPAddress.TryParse(address, out var ip) ||
            !IPAddress.TryParse(prefix[..slash], out var network) ||
            ip.AddressFamily != network.AddressFamily ||
            !int.TryParse(prefix[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
        {
            return false;
        }

        var a = ip.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (bits > a.Length * 8)
        {
            return false;
        }

        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>shExpMatch(str, pattern)</c>: a shell-style match, where <c>*</c> is anything and <c>?</c> is one character.</summary>
    internal static bool ShExpMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The standard PAC functions. The ones that touch the network or need
    /// care call into C# (the <c>__violens</c> functions); each converts
    /// its arguments to strings first, as browsers do.
    /// </summary>
    private const string Library = """
        function alert(message) { __violensLog(String(message)); }

        function isPlainHostName(host) { return String(host).indexOf('.') < 0; }

        function dnsDomainIs(host, domain) {
          host = String(host).toLowerCase(); domain = String(domain).toLowerCase();
          return host.length >= domain.length && host.substring(host.length - domain.length) === domain;
        }

        function localHostOrDomainIs(host, hostdom) {
          host = String(host).toLowerCase(); hostdom = String(hostdom).toLowerCase();
          return host === hostdom || (host.indexOf('.') < 0 && hostdom.lastIndexOf(host + '.', 0) === 0);
        }

        function dnsDomainLevels(host) { return String(host).split('.').length - 1; }

        function dnsResolve(host) { return __violensResolve(String(host)); }
        function isResolvable(host) { return dnsResolve(host) !== null; }
        function myIpAddress() { return __violensMyIp(); }
        function isInNet(host, pattern, mask) { return __violensIsInNet(String(host), String(pattern), String(mask)); }
        function shExpMatch(str, shexp) { return __violensShExpMatch(String(str), String(shexp)); }

        function dnsResolveEx(host) { return __violensResolveEx(String(host)); }
        function isResolvableEx(host) { return dnsResolveEx(host) !== ''; }
        function myIpAddressEx() { return __violensMyIpEx(); }
        function isInNetEx(ipAddress, ipPrefix) { return __violensIsInNetEx(String(ipAddress), String(ipPrefix)); }
        function sortIpAddressList(list) { return String(list); }
        function getClientVersion() { return '1.0'; }

        // Some old scripts call this Netscape-internal helper directly.
        function convert_addr(ipaddr) {
          var bytes = String(ipaddr).split('.');
          return ((bytes[0] & 0xff) << 24 | (bytes[1] & 0xff) << 16 | (bytes[2] & 0xff) << 8 | (bytes[3] & 0xff)) >>> 0;
        }

        var __violensDays = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];
        var __violensMonths = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];

        function __violensIsGmt(args) {
          return args.length > 0 && String(args[args.length - 1]).toUpperCase() === 'GMT';
        }

        // Inclusive; a range whose start is after its end wraps around
        // (FRI to MON, NOV to FEB, 22 to 6).
        function __violensInRange(start, end, value) {
          return start <= end ? (value >= start && value <= end) : (value >= start || value <= end);
        }

        function weekdayRange() {
          var gmt = __violensIsGmt(arguments), count = arguments.length - (gmt ? 1 : 0);
          if (count < 1 || count > 2) return false;
          var now = new Date(), today = gmt ? now.getUTCDay() : now.getDay();
          var first = __violensDays.indexOf(String(arguments[0]).toUpperCase());
          if (first < 0) return false;
          if (count === 1) return today === first;
          var last = __violensDays.indexOf(String(arguments[1]).toUpperCase());
          return last >= 0 && __violensInRange(first, last, today);
        }

        // dateRange(day | month | year), or a range of the same fields:
        // (d1, d2), (m1, m2), (y1, y2), (d1, m1, d2, m2), (m1, y1, m2, y2),
        // (d1, m1, y1, d2, m2, y2). Numbers up to 31 are days, larger ones years.
        function dateRange() {
          var gmt = __violensIsGmt(arguments), count = arguments.length - (gmt ? 1 : 0);
          if (count !== 1 && count !== 2 && count !== 4 && count !== 6) return false;
          var now = new Date();
          var current = {
            day: gmt ? now.getUTCDate() : now.getDate(),
            month: gmt ? now.getUTCMonth() : now.getMonth(),
            year: gmt ? now.getUTCFullYear() : now.getFullYear()
          };
          var parts = [];
          for (var i = 0; i < count; i++) {
            var value = arguments[i];
            if (typeof value === 'number' || /^\d+$/.test(String(value))) {
              var number = Number(value);
              parts.push(number > 31 ? { kind: 'year', value: number } : { kind: 'day', value: number });
            } else {
              var month = __violensMonths.indexOf(String(value).toUpperCase());
              if (month < 0) return false;
              parts.push({ kind: 'month', value: month });
            }
          }
          if (count === 1) return current[parts[0].kind] === parts[0].value;
          var weight = { year: 10000, month: 100, day: 1 };
          var half = count / 2, start = 0, end = 0, today = 0;
          for (var j = 0; j < half; j++) {
            var kind = parts[j].kind;
            if (parts[j + half].kind !== kind) return false;
            start += parts[j].value * weight[kind];
            end += parts[j + half].value * weight[kind];
            today += current[kind] * weight[kind];
          }
          return __violensInRange(start, end, today);
        }

        // timeRange(hour), (h1, h2), (h1, m1, h2, m2) or (h1, m1, s1, h2, m2, s2).
        // An end hour or minute includes the whole of that hour or minute.
        function timeRange() {
          var gmt = __violensIsGmt(arguments), count = arguments.length - (gmt ? 1 : 0);
          var now = new Date();
          var hour = gmt ? now.getUTCHours() : now.getHours();
          var current = hour * 3600 + (gmt ? now.getUTCMinutes() : now.getMinutes()) * 60 + (gmt ? now.getUTCSeconds() : now.getSeconds());
          var a = [];
          for (var i = 0; i < count; i++) a.push(Number(arguments[i]));
          if (count === 1) return hour === a[0];
          var start, end;
          if (count === 2) { start = a[0] * 3600; end = a[1] * 3600 + 3599; }
          else if (count === 4) { start = a[0] * 3600 + a[1] * 60; end = a[2] * 3600 + a[3] * 60 + 59; }
          else if (count === 6) { start = a[0] * 3600 + a[1] * 60 + a[2]; end = a[3] * 3600 + a[4] * 60 + a[5]; }
          else return false;
          return __violensInRange(start, end, current);
        }
        """;
}

/// <summary>A PAC script that can't be loaded or run.</summary>
public sealed class PacScriptException(string message, Exception? inner = null) : Exception(message, inner);
