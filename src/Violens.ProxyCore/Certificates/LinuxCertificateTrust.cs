using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Violens.ProxyCore.Certificates;

/// <summary>
/// Trusts and removes the root on Linux (see the Interception Certificate
/// Design). Linux has no one trust store that every app reads, so the root
/// goes, for this account only and without a password, into:
/// <list type="bullet">
/// <item>the NSS database in <c>~/.pki/nssdb</c>, which Chrome, Chromium,
/// Edge, Brave and other NSS apps read (made if it isn't there yet), and
/// the Chromium snap's copy of it;</item>
/// <item>each Firefox profile, including the snap's and the Flatpak's;</item>
/// <item>.NET's own per-user root store, for .NET apps.</item>
/// </list>
/// NSS databases are changed with <c>certutil</c>, from the distribution's
/// NSS tools package. The system store that curl, wget and most other
/// command-line tools read needs root access, so VioLens leaves it alone;
/// the User Guide gives the commands.
/// </summary>
internal static class LinuxCertificateTrust
{
    /// <summary>NSS's trust flags for "a CA trusted to issue server certificates".</summary>
    private const string ServerCaTrust = "C,,";

    private static readonly TimeSpan CertutilTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Trusts the root everywhere listed above, and returns where it went.
    /// Throws <see cref="CertificateTrustException"/> if <c>certutil</c>
    /// isn't installed, or no NSS database took it.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static IReadOnlyList<string> Install(X509Certificate2 rootCertificate, Action<string>? log = null)
    {
        TryAddToDotNetRootStore(rootCertificate, log);

        var tempCertPath = Path.Combine(Path.GetTempPath(), $"violens-root-{Guid.NewGuid():N}.cer");
        var trusted = new List<string>();
        var failures = new List<string>();
        try
        {
            File.WriteAllBytes(tempCertPath, rootCertificate.Export(X509ContentType.Cert));
            var nickname = Nickname(rootCertificate);
            foreach (var database in FindDatabases(HomeFolder(), createDefault: true))
            {
                try
                {
                    if (database.Create)
                    {
                        Directory.CreateDirectory(database.Path);
                        RunCertutil(BuildCreateDatabaseArguments(database.Path));
                    }

                    // Adding a nickname that's already there is an error, so
                    // an earlier copy of this root goes first.
                    if (ListNicknames(database.Path).Contains(nickname))
                    {
                        RunCertutil(BuildDeleteArguments(database.Path, nickname));
                    }

                    RunCertutil(BuildAddArguments(database.Path, nickname, tempCertPath));
                    trusted.Add(database.Description);
                }
                catch (CertificateTrustException ex) when (ex.ExitCode != CertutilMissing)
                {
                    failures.Add($"{database.Description}: {ex.Message} {ex.StandardError}".Trim());
                }
            }
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

        foreach (var failure in failures)
        {
            log?.Invoke($"Couldn't trust the root in {failure}");
        }

        if (trusted.Count == 0)
        {
            throw new CertificateTrustException(
                "Couldn't add the root certificate to any browser's certificate database." +
                (failures.Count > 0 ? " " + string.Join("; ", failures) : string.Empty),
                exitCode: -1,
                standardError: string.Empty);
        }

        log?.Invoke($"Trusted the root certificate in {string.Join(", ", trusted)}.");
        return trusted;
    }

    /// <summary>
    /// Removes every root whose nickname starts with
    /// <paramref name="subjectPrefix"/> (or, given a thumbprint, just that
    /// one) from every NSS database and .NET's root store. Returns how many
    /// databases had one. Doesn't throw for a database it can't change.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static int Remove(string subjectPrefix, string? thumbprint, Action<string>? log = null)
    {
        TryRemoveFromDotNetRootStore(subjectPrefix, thumbprint, log);

        var changed = 0;
        foreach (var database in FindDatabases(HomeFolder(), createDefault: false))
        {
            try
            {
                var removedHere = false;
                foreach (var nickname in ListNicknames(database.Path))
                {
                    if (IsOurs(nickname, subjectPrefix, thumbprint))
                    {
                        RunCertutil(BuildDeleteArguments(database.Path, nickname));
                        removedHere = true;
                    }
                }

                if (removedHere)
                {
                    changed++;
                }
            }
            catch (CertificateTrustException ex)
            {
                log?.Invoke($"Couldn't remove the root certificate from {database.Description}: {ex.Message}");
                if (ex.ExitCode == CertutilMissing)
                {
                    break;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// The name a root is stored under in NSS: its name prefix and SHA-1
    /// thumbprint, so roots from earlier installs can be found and one root
    /// told from another.
    /// </summary>
    internal static string Nickname(X509Certificate2 rootCertificate) =>
        $"{FirstNamePart(rootCertificate)} {rootCertificate.Thumbprint}";

    internal static bool IsOurs(string nickname, string subjectPrefix, string? thumbprint) =>
        nickname.StartsWith(subjectPrefix, StringComparison.Ordinal) &&
        (thumbprint is null || nickname.EndsWith(" " + thumbprint, StringComparison.OrdinalIgnoreCase));

    private static string FirstNamePart(X509Certificate2 certificate)
    {
        // "DO_NOT_TRUST_ViolensRoot (host, generated 2026-10-30)" -> the prefix.
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var space = name.IndexOf(' ');
        return space > 0 ? name[..space] : name;
    }

    /// <summary>An NSS database, a description for messages, and whether it has to be made first.</summary>
    internal readonly record struct NssDatabase(string Path, string Description, bool Create);

    /// <summary>
    /// The NSS databases under <paramref name="home"/>: the shared one (to
    /// be made when <paramref name="createDefault"/> and it's missing), the
    /// Chromium snap's, and every Firefox profile with a <c>cert9.db</c>.
    /// Internal for tests.
    /// </summary>
    internal static List<NssDatabase> FindDatabases(string home, bool createDefault)
    {
        var databases = new List<NssDatabase>();
        var shared = Path.Combine(home, ".pki", "nssdb");
        if (File.Exists(Path.Combine(shared, "cert9.db")))
        {
            databases.Add(new NssDatabase(shared, "Chrome, Chromium and other NSS apps", Create: false));
        }
        else if (createDefault)
        {
            databases.Add(new NssDatabase(shared, "Chrome, Chromium and other NSS apps", Create: true));
        }

        var chromiumSnap = Path.Combine(home, "snap", "chromium", "current", ".pki", "nssdb");
        if (File.Exists(Path.Combine(chromiumSnap, "cert9.db")))
        {
            databases.Add(new NssDatabase(chromiumSnap, "Chromium (snap)", Create: false));
        }

        foreach (var (folder, label) in new[]
                 {
                     (Path.Combine(home, ".mozilla", "firefox"), "Firefox"),
                     (Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"), "Firefox (snap)"),
                     (Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"), "Firefox (Flatpak)"),
                 })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var profile in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(profile, "cert9.db")))
                {
                    databases.Add(new NssDatabase(profile, $"{label} profile {Path.GetFileName(profile)}", Create: false));
                }
            }
        }

        return databases;
    }

    internal static string[] BuildCreateDatabaseArguments(string database) =>
        ["-N", "-d", "sql:" + database, "--empty-password"];

    internal static string[] BuildAddArguments(string database, string nickname, string certificatePath) =>
        ["-A", "-d", "sql:" + database, "-n", nickname, "-t", ServerCaTrust, "-i", certificatePath];

    internal static string[] BuildDeleteArguments(string database, string nickname) =>
        ["-D", "-d", "sql:" + database, "-n", nickname];

    internal static string[] BuildListArguments(string database) => ["-L", "-d", "sql:" + database];

    /// <summary>
    /// Parses <c>certutil -L</c>: a header, then one certificate per line,
    /// its nickname followed by its trust flags (such as <c>C,,</c> or
    /// <c>CT,C,C</c>) after a run of spaces. Internal for tests.
    /// </summary>
    internal static List<string> ParseNicknames(string output)
    {
        var nicknames = new List<string>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            var lastSpace = line.LastIndexOf(' ');
            if (lastSpace <= 0)
            {
                continue;
            }

            var flags = line[(lastSpace + 1)..];
            if (flags.Count(c => c == ',') != 2 || flags.Any(c => c != ',' && !char.IsAsciiLetter(c)))
            {
                continue;
            }

            var nickname = line[..lastSpace].TrimEnd();
            if (nickname.Length > 0 && nickname != "SSL,S/MIME,JAR/XPI")
            {
                nicknames.Add(nickname);
            }
        }

        return nicknames;
    }

    [SupportedOSPlatform("linux")]
    private static List<string> ListNicknames(string database) =>
        ParseNicknames(RunCertutil(BuildListArguments(database)));

    private static string HomeFolder() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>For .NET apps, which read their own per-user root store rather than NSS. Best effort.</summary>
    [SupportedOSPlatform("linux")]
    private static void TryAddToDotNetRootStore(X509Certificate2 rootCertificate, Action<string>? log)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            using var publicOnly = X509CertificateLoader.LoadCertificate(rootCertificate.Export(X509ContentType.Cert));
            store.Add(publicOnly);
        }
        catch (CryptographicException ex)
        {
            log?.Invoke($"Couldn't add the root certificate to .NET's root store: {ex.Message}");
        }
    }

    [SupportedOSPlatform("linux")]
    private static void TryRemoveFromDotNetRootStore(string subjectPrefix, string? thumbprint, Action<string>? log)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var root in store.Certificates.Find(X509FindType.FindBySubjectName, subjectPrefix, validOnly: false))
            {
                if (thumbprint is null || string.Equals(root.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    store.Remove(root);
                }
            }
        }
        catch (CryptographicException ex)
        {
            log?.Invoke($"Couldn't remove the root certificate from .NET's root store: {ex.Message}");
        }
    }

    /// <summary>The exit code reported when <c>certutil</c> isn't installed.</summary>
    internal const int CertutilMissing = -2;

    [SupportedOSPlatform("linux")]
    private static string RunCertutil(string[] arguments)
    {
        var startInfo = new ProcessStartInfo("certutil")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
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
            throw new CertificateTrustException(
                "VioLens needs certutil to trust its certificate in your browsers. Install your distribution's NSS tools " +
                "(sudo apt install libnss3-tools on Debian and Ubuntu, sudo dnf install nss-tools on Fedora, " +
                "sudo pacman -S nss on Arch), then click Start again.",
                CertutilMissing,
                standardError: string.Empty);
        }

        using (process)
        {
            // Nothing to type: a database with a password would otherwise wait for one.
            process.StandardInput.Close();
            var standardOutputRead = process.StandardOutput.ReadToEndAsync();
            var standardErrorRead = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CertutilTimeout))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // It exited just now.
                }

                throw new CertificateTrustException(
                    $"'certutil {string.Join(' ', arguments)}' didn't finish within {CertutilTimeout.TotalSeconds:0} seconds.",
                    exitCode: -1,
                    standardError: string.Empty);
            }

            var standardOutput = standardOutputRead.GetAwaiter().GetResult();
            var standardError = standardErrorRead.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new CertificateTrustException(
                    $"'certutil {string.Join(' ', arguments)}' exited with code {process.ExitCode}.",
                    process.ExitCode,
                    standardError);
            }

            return standardOutput;
        }
    }
}
