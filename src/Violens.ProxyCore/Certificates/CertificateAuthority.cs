using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Violens.ProxyCore.Certificates;

/// <summary>
/// The per-user VioLens root: created once, its key stored non-exportable,
/// and trusted in the user's root store (see the Interception Certificate
/// Design).
/// </summary>
public sealed class CertificateAuthority
{
    /// <summary>
    /// VioLens's root name prefix, after Fiddler's <c>DO_NOT_TRUST_FiddlerRoot</c>.
    /// Hosts with their own <see cref="ProxyHostIdentity"/> use their own name.
    /// </summary>
    public const string SubjectPrefix = "DO_NOT_TRUST_ViolensRoot";

    private static readonly TimeSpan RootValidity = TimeSpan.FromDays(365 * 5);

    public X509Certificate2 RootCertificate { get; }

    /// <summary>The root's name prefix, from <see cref="ProxyEngine.Identity"/> at creation.</summary>
    public string RootCertificateName { get; }

    /// <param name="installToTrustStore">
    /// Trust the root (on Windows, the OS asks the user the first time). Tests
    /// pass false to leave the trust store alone.
    /// </param>
    public CertificateAuthority(bool installToTrustStore = true)
    {
        RootCertificateName = ProxyEngine.Identity.RootCertificateName;
        RootCertificate = LoadOrCreateRoot(RootCertificateName);

        if (installToTrustStore)
        {
            EnsureTrusted(RootCertificate);
        }
    }

    /// <summary>Creates a self-signed root without touching any store.</summary>
    public static X509Certificate2 GenerateRoot(DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        GenerateRoot(notBefore, notAfter, ProxyEngine.Identity.RootCertificateName);

    /// <summary>The same, with a given name prefix.</summary>
    public static X509Certificate2 GenerateRoot(DateTimeOffset notBefore, DateTimeOffset notAfter, string rootCertificateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootCertificateName);

        // The builder, because the common name contains a comma, which the
        // string form would read as a separator.
        var subjectBuilder = new X500DistinguishedNameBuilder();
        subjectBuilder.AddCommonName(
            $"{rootCertificateName} ({Environment.MachineName}, generated {DateTime.UtcNow:yyyy-MM-dd})");
        var subject = subjectBuilder.Build();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: true,
                pathLengthConstraint: 0,
                critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                critical: true));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 LoadOrCreateRoot(string rootCertificateName)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectName, rootCertificateName, validOnly: false)
            .Cast<X509Certificate2>()
            .Where(c => c.NotAfter > DateTime.UtcNow && c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault();

        if (existing is not null)
        {
            return existing;
        }

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.Add(RootValidity);

        using var generated = GenerateRoot(notBefore, notAfter, rootCertificateName);
        return PersistNonExportable(store, generated);
    }

    private static X509Certificate2 PersistNonExportable(X509Store store, X509Certificate2 certWithKey)
    {
        // Storage flags can only be set on import, so round-trip through an
        // in-memory PFX to persist the key without Exportable.
        var password = Guid.NewGuid().ToString("N");
        var pfxBytes = certWithKey.Export(X509ContentType.Pfx, password);

        try
        {
            var persisted = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

            store.Add(persisted);
            return persisted;
        }
        finally
        {
            Array.Clear(pfxBytes);
        }
    }

    private static void EnsureTrusted(X509Certificate2 rootCert)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureTrustedWindows(rootCert);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // security add-trusted-cert is idempotent, so no "already trusted" check.
            MacOSCertificateTrust.Install(rootCert);
        }
        else if (OperatingSystem.IsLinux())
        {
            // certutil -A replaces any earlier copy, so no "already trusted" check either.
            LinuxCertificateTrust.Install(rootCert, message => ProxyEngine.Log("certificates", message));
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Certificate trust-store installation is only implemented for Windows, macOS and Linux " +
                "-- see the Interception Certificate Design doc's 'Platform status' section.");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureTrustedWindows(X509Certificate2 rootCert)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var alreadyTrusted = store.Certificates
            .Find(X509FindType.FindByThumbprint, rootCert.Thumbprint, validOnly: false)
            .Count > 0;

        if (!alreadyTrusted)
        {
            // On Windows this shows the OS's "install this certificate?" prompt.
            // macOS has none, so the app asks first.
            store.Add(rootCert);
        }
    }

    /// <summary>
    /// Removes every root whose name contains <paramref name="subjectPrefix"/>,
    /// including old ones, from the trusted-root and personal stores, with
    /// their keys. On Windows the user confirms each removal; a "No" keeps that
    /// root's key. Never throws.
    /// </summary>
    public static int RemoveRoots(string subjectPrefix, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectPrefix);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                using var myStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                return RemoveRootsFromStores(rootStore, myStore, subjectPrefix, log);
            }

            if (OperatingSystem.IsMacOS())
            {
                using var myStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                myStore.Open(OpenFlags.ReadWrite);
                var removed = 0;
                foreach (var root in FindRoots(myStore, subjectPrefix))
                {
                    try
                    {
                        // Deleting it from the keychain removes its trust setting too.
                        MacOSCertificateTrust.Uninstall(root);
                        removed++;
                    }
                    catch (CertificateTrustException ex)
                    {
                        log?.Invoke($"Couldn't remove the root certificate {root.Thumbprint}: {ex.Message}");
                    }
                }

                return removed;
            }

            if (OperatingSystem.IsLinux())
            {
                var databases = LinuxCertificateTrust.Remove(subjectPrefix, thumbprint: null, log);
                using var myStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                myStore.Open(OpenFlags.ReadWrite);
                var removed = 0;
                foreach (var root in FindRoots(myStore, subjectPrefix))
                {
                    myStore.Remove(root);
                    removed++;
                }

                if (databases > 0 || removed > 0)
                {
                    log?.Invoke($"Removed {removed} root certificate(s) named {subjectPrefix}, trusted in {databases} certificate database(s).");
                }

                // Count trusted copies too, for a root whose key was already gone.
                return Math.Max(removed, databases > 0 ? 1 : 0);
            }

            log?.Invoke("Removing root certificates is only implemented for Windows, macOS and Linux.");
        }
        catch (CryptographicException ex)
        {
            log?.Invoke($"Couldn't check for root certificates to remove: {ex.Message}");
        }

        return 0;
    }

    /// <summary>The Windows half of <see cref="RemoveRoots"/>, with stores passed in for tests.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static int RemoveRootsFromStores(X509Store trustStore, X509Store keyStore, string subjectPrefix, Action<string>? log)
    {
        trustStore.Open(OpenFlags.ReadWrite);
        keyStore.Open(OpenFlags.ReadWrite);

        var removed = 0;
        foreach (var root in FindRoots(trustStore, subjectPrefix))
        {
            try
            {
                trustStore.Remove(root);
                removed++;
            }
            catch (CryptographicException ex)
            {
                log?.Invoke($"The root certificate {root.Thumbprint} is still trusted: {ex.Message}");
            }
        }

        foreach (var root in FindRoots(keyStore, subjectPrefix))
        {
            try
            {
                // Still trusted means the user said No above; keep the key
                // so a later "Yes" leaves nothing behind.
                if (trustStore.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, validOnly: false).Count > 0)
                {
                    continue;
                }

                keyStore.Remove(root);

                // Removing a certificate from a store leaves its persisted
                // key container on disk; delete that too.
                using var key = root.GetECDsaPrivateKey();
                (key as ECDsaCng)?.Key.Delete();
            }
            catch (CryptographicException ex)
            {
                log?.Invoke($"Couldn't remove the root certificate {root.Thumbprint}: {ex.Message}");
            }
        }

        if (removed > 0)
        {
            log?.Invoke($"Removed {removed} root certificate(s) named {subjectPrefix}.");
        }

        return removed;
    }

    private static List<X509Certificate2> FindRoots(X509Store store, string subjectPrefix) =>
        store.Certificates
            .Find(X509FindType.FindBySubjectName, subjectPrefix, validOnly: false)
            .Cast<X509Certificate2>()
            .ToList();

    /// <summary>
    /// Removes this root from the personal and trusted-root stores.
    /// <see cref="RemoveRoots"/> also catches roots from earlier installs.
    /// </summary>
    public void Uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            rootStore.Open(OpenFlags.ReadWrite);
            rootStore.Remove(RootCertificate);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacOSCertificateTrust.Uninstall(RootCertificate);
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxCertificateTrust.Remove(RootCertificateName, RootCertificate.Thumbprint, message => ProxyEngine.Log("certificates", message));
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Certificate trust-store removal is only implemented for Windows, macOS and Linux " +
                "-- see the Interception Certificate Design doc's 'Platform status' section.");
        }

        // X509Store's personal store works on every platform.
        using var myStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        myStore.Open(OpenFlags.ReadWrite);
        myStore.Remove(RootCertificate);
    }
}
