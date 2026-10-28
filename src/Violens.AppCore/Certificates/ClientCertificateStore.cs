using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Violens.ProxyCore.Certificates;

namespace Violens.AppCore.Certificates;

/// <summary>A certificate that can be sent to a server that asks for one, as the Connections row lists it.</summary>
public sealed record ClientCertificateChoice(string Thumbprint, string Description);

/// <summary>
/// The client certificates (mutual TLS) VioLens can send: the user's own
/// personal certificates with a private key, still valid, and allowed for
/// client authentication. On macOS the personal store is the login keychain.
/// </summary>
public static class ClientCertificateStore
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>Every usable certificate, soonest-expiring last. Never throws: an unreadable store gives an empty list.</summary>
    public static IReadOnlyList<ClientCertificateChoice> FindUsable()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates
                .Where(IsUsable)
                .OrderByDescending(c => c.NotAfter)
                .Select(c => new ClientCertificateChoice(c.Thumbprint, Describe(c)))
                .ToList();
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    /// <summary>The certificate with this thumbprint, with its private key, or null if it's gone or unusable.</summary>
    public static X509Certificate2? Load(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates
                .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                .FirstOrDefault(IsUsable);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Has a private key, is in date, allows client authentication (or
    /// doesn't restrict its use), and isn't a certificate authority: that
    /// leaves out VioLens's own root, and other debugging proxies' roots
    /// (such as Fiddler Classic's), which sit in the same store.
    /// </summary>
    internal static bool IsUsable(X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        if (!certificate.HasPrivateKey || certificate.NotBefore > now || certificate.NotAfter < now ||
            certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(c => c.CertificateAuthority) ||
            certificate.Subject.Contains(CertificateAuthority.SubjectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usages is null || usages.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthenticationOid);
    }

    /// <summary>"Ana Lima (issued by Corp CA, expires 2027-03-01)".</summary>
    internal static string Describe(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        return $"{(string.IsNullOrEmpty(name) ? certificate.Subject : name)} (issued by {issuer}, expires {certificate.NotAfter:yyyy-MM-dd})";
    }
}
