using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Violens.ProxyCore.Certificates;

/// <summary>
/// Trusts and removes the root on macOS through the <c>security</c> command
/// (see the Interception Certificate Design).
/// </summary>
internal static class MacOSCertificateTrust
{
    /// <summary>
    /// Runs <c>security add-trusted-cert</c> on a temporary copy of the root.
    /// There's no OS prompt, so callers ask the user first. Throws
    /// <see cref="CertificateTrustException"/> on failure.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void Install(X509Certificate2 rootCertificate)
    {
        var tempCertPath = Path.Combine(Path.GetTempPath(), $"violens-root-{Guid.NewGuid():N}.cer");

        try
        {
            File.WriteAllBytes(tempCertPath, rootCertificate.Export(X509ContentType.Cert));
            RunSecurityCommand(BuildAddTrustedCertArguments(tempCertPath));
        }
        finally
        {
            try
            {
                File.Delete(tempCertPath);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    /// <summary>
    /// Runs <c>security delete-certificate -Z</c>, matching by SHA-1 so old
    /// roots with the same name aren't confused. Throws on failure, including
    /// when the certificate isn't there.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void Uninstall(X509Certificate2 rootCertificate) =>
        RunSecurityCommand(BuildDeleteCertificateArguments(rootCertificate.Thumbprint));

    /// <summary>
    /// No <c>-k</c>, so the user's default keychain; no <c>-d</c>, so per-user
    /// without elevation; <c>-r trustRoot</c> to trust it as a root for every
    /// policy. Internal for tests.
    /// </summary>
    internal static string[] BuildAddTrustedCertArguments(string certificateFilePath) =>
        ["add-trusted-cert", "-r", "trustRoot", certificateFilePath];

    /// <summary><see cref="X509Certificate2.Thumbprint"/> is already the hex <c>-Z</c> expects.</summary>
    internal static string[] BuildDeleteCertificateArguments(string sha1Thumbprint) =>
        ["delete-certificate", "-Z", sha1Thumbprint];

    [SupportedOSPlatform("macos")]
    private static void RunSecurityCommand(string[] arguments)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath.Find("security")!)
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
            ?? throw new CertificateTrustException("Failed to start the 'security' process.", exitCode: -1, standardError: string.Empty);

        // Read the output before waiting, so a full pipe can't deadlock.
        var standardError = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new CertificateTrustException(
                $"'security {string.Join(' ', arguments)}' exited with code {process.ExitCode}.",
                process.ExitCode,
                standardError);
        }
    }
}
