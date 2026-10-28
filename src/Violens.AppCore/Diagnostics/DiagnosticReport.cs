using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Violens.AppCore.Diagnostics;

/// <summary>What the app knows about itself when Help &gt; Copy Diagnostic Info is chosen.</summary>
public sealed record DiagnosticSnapshot
{
    public required string Version { get; init; }

    /// <summary>Null when not capturing.</summary>
    public int? Port { get; init; }

    public bool ListensOnAllInterfaces { get; init; }

    /// <summary>Null when not capturing.</summary>
    public bool? RegisteredAsSystemProxy { get; init; }

    /// <summary>The Connections row's upstream description.</summary>
    public string Upstream { get; init; } = string.Empty;

    public bool Http2Enabled { get; init; }

    /// <summary>Null until capture has started once (the root is created then).</summary>
    public X509Certificate2? RootCertificate { get; init; }

    public int SessionCount { get; init; }

    public int ExtensionCount { get; init; }

    public bool FiddlerScriptLoaded { get; init; }

    /// <summary>The status line, which carries the last start-up error.</summary>
    public string LastStatus { get; init; } = string.Empty;
}

/// <summary>
/// Help &gt; Copy Diagnostic Info: the text a bug report asks for (see
/// <c>.github/ISSUE_TEMPLATE/bug_report.yml</c>), so users don't have to dig
/// it out. Leaves out anything identifying: no machine or user name (the
/// root's subject carries the machine name, so only its thumbprint is used),
/// and proxy environment variables are reported as set or not, never their
/// values, which can hold passwords.
/// </summary>
public static class DiagnosticReport
{
    /// <summary>
    /// The informational version without "+metadata", so it matches the git
    /// tag. The numeric versions can't carry a "-preview.1" suffix.
    /// </summary>
    public static string DisplayVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown";

    public static string Format(DiagnosticSnapshot snapshot) =>
        Format(snapshot, DescribeNetworkInterfaces(), DescribeProxyEnvironment());

    /// <summary>The same, with the machine-dependent parts passed in for tests.</summary>
    public static string Format(DiagnosticSnapshot snapshot, IReadOnlyList<string> networkInterfaces, string proxyEnvironment)
    {
        var text = new StringBuilder();
        text.AppendLine($"VioLens: {snapshot.Version}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        text.AppendLine();

        if (snapshot.Port is { } port)
        {
            var where = snapshot.ListensOnAllInterfaces ? "all network interfaces" : "127.0.0.1";
            text.AppendLine($"Capturing: yes, on {where}, port {port}");
            text.AppendLine($"System proxy: {(snapshot.RegisteredAsSystemProxy == true ? "registered" : "not registered")}");
        }
        else
        {
            text.AppendLine("Capturing: no");
        }

        if (snapshot.Upstream.Length > 0)
        {
            text.AppendLine(snapshot.Upstream);
        }

        text.AppendLine($"Proxy environment variables: {proxyEnvironment}");
        text.AppendLine($"HTTP/2: {(snapshot.Http2Enabled ? "on" : "off")}");
        text.AppendLine($"Root certificate: {DescribeRoot(snapshot.RootCertificate)}");
        text.AppendLine();

        text.AppendLine($"Sessions: {snapshot.SessionCount}");
        text.AppendLine($"Extensions loaded: {snapshot.ExtensionCount}");
        text.AppendLine($"FiddlerScript loaded: {(snapshot.FiddlerScriptLoaded ? "yes" : "no")}");
        text.AppendLine();

        text.AppendLine("Network interfaces (up):");
        if (networkInterfaces.Count == 0)
        {
            text.AppendLine("  none found");
        }

        foreach (var networkInterface in networkInterfaces)
        {
            text.AppendLine($"  {networkInterface}");
        }

        if (snapshot.LastStatus.Length > 0)
        {
            text.AppendLine();
            text.AppendLine($"Last status: {snapshot.LastStatus}");
        }

        return text.ToString();
    }

    /// <summary>Whether the root is trusted, by building its chain with the OS's own trust settings.</summary>
    public static string DescribeRoot(X509Certificate2? root)
    {
        if (root is null)
        {
            return "not created yet (capture hasn't started)";
        }

        string trust;
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            trust = chain.Build(root) ? "trusted" : "NOT trusted";
        }
        catch (CryptographicException ex)
        {
            trust = $"couldn't check trust ({ex.Message})";
        }

        return $"{trust}, thumbprint {root.Thumbprint}, expires {root.NotAfter.ToUniversalTime():yyyy-MM-dd}";
    }

    /// <summary>
    /// Names and types, which show a VPN, Docker or WSL adapter. Only
    /// adapters with an address beyond link-local: Windows also reports
    /// each adapter's filter drivers ("Wi-Fi-QoS Packet Scheduler-0000"),
    /// and idle Wi-Fi Direct adapters, as interfaces that are up. Loopback
    /// is left out. Addresses aren't listed.
    /// </summary>
    public static IReadOnlyList<string> DescribeNetworkInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            HasUsableAddress(n.GetIPProperties().UnicastAddresses.Select(a => a.Address)))
                .Select(n => $"{n.Name} ({n.NetworkInterfaceType})")
                .ToList();
        }
        catch (NetworkInformationException ex)
        {
            return [$"couldn't list them ({ex.Message})"];
        }
    }

    /// <summary>Whether any address is more than loopback or link-local (169.254.x.x, fe80::).</summary>
    internal static bool HasUsableAddress(IEnumerable<IPAddress> addresses) =>
        addresses.Any(a => !IPAddress.IsLoopback(a) &&
                           !a.IsIPv6LinkLocal &&
                           !(a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..]));

    /// <summary>Which of the usual proxy variables are set. Never their values.</summary>
    public static string DescribeProxyEnvironment()
    {
        string[] names = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];
        var set = names
            .Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) ||
                           !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name.ToLowerInvariant())))
            .ToList();
        return set.Count == 0 ? "none set" : string.Join(", ", set) + " set";
    }
}
