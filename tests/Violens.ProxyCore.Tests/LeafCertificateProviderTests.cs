using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Violens.ProxyCore.Certificates;
using Xunit;

namespace Violens.ProxyCore.Tests;

public class LeafCertificateProviderTests
{
    [Fact]
    public void CreateLeaf_IsSignedByTheIssuerAndCarriesTheHostAsASubjectAlternativeName()
    {
        var rootNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(rootNotBefore, rootNotBefore.AddYears(5));

        var leafNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var leaf = LeafCertificateProvider.CreateLeaf(
            root, "example.test", leafNotBefore, leafNotBefore.AddDays(7));

        Assert.True(leaf.HasPrivateKey);
        Assert.Equal(root.Subject, leaf.Issuer);
        Assert.Equal("CN=example.test", leaf.Subject);

        var san = GetSubjectAlternativeNames(leaf);
        Assert.Contains("example.test", san.EnumerateDnsNames());

        var eku = GetEnhancedKeyUsage(leaf);
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void CreateLeaf_UsesAnIpAddressSanForAnIpHost()
    {
        var rootNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(rootNotBefore, rootNotBefore.AddYears(5));

        var leafNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var leaf = LeafCertificateProvider.CreateLeaf(
            root, "127.0.0.1", leafNotBefore, leafNotBefore.AddDays(7));

        var san = GetSubjectAlternativeNames(leaf);
        Assert.Contains(san.EnumerateIPAddresses(), ip => ip.ToString() == "127.0.0.1");
    }

    [Fact]
    public void CreateLeaf_DoesNotLeaveAKeyContainerBehindOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(rootNotBefore, rootNotBefore.AddYears(5));

        var leafNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        var leaf = LeafCertificateProvider.CreateLeaf(
            root, "example.test", leafNotBefore, leafNotBefore.AddDays(7));

        string keyName;
        CngProvider provider;
        using (var ecdsa = (ECDsaCng)leaf.GetECDsaPrivateKey()!)
        {
            keyName = ecdsa.Key.KeyName!;
            provider = ecdsa.Key.Provider!;
        }

        // The key is a real, named container while the certificate lives
        // (which Schannel needs), and gone once it's released.
        Assert.True(CngKey.Exists(keyName, provider, CngKeyOpenOptions.UserKey));
        leaf.Dispose();
        Assert.False(CngKey.Exists(keyName, provider, CngKeyOpenOptions.UserKey));
    }

    [Fact]
    public void CreateLeaf_ReturnedCertificateHasAnImmediatelyUsablePrivateKey()
    {
        // Regression guard for PersistKey's warm-up step: the certificate
        // this returns must actually be able to sign with its private key
        // right away, not just report HasPrivateKey == true.
        var rootNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(rootNotBefore, rootNotBefore.AddYears(5));

        var leafNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var leaf = LeafCertificateProvider.CreateLeaf(
            root, "example.test", leafNotBefore, leafNotBefore.AddDays(7));

        using var ecdsa = leaf.GetECDsaPrivateKey();
        Assert.NotNull(ecdsa);
        var signature = ecdsa!.SignData("probe"u8.ToArray(), HashAlgorithmName.SHA256);
        Assert.NotEmpty(signature);
    }

    [Fact]
    public async Task GetCertificateFor_ConcurrentCallsForANewHostShareOneSigningOperation()
    {
        // A browser's burst of parallel connections to a new host. Without
        // single-flight signing each caller gets its own certificate, which is
        // what caused the intermittent handshake failures.
        var rootNotBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var root = CertificateAuthority.GenerateRoot(rootNotBefore, rootNotBefore.AddYears(5));
        var provider = new LeafCertificateProvider(root);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => provider.GetCertificateFor("example.test")))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, cert => Assert.Same(results[0], cert));
    }

    [Fact]
    public void GetCertificateFor_CachesByHost()
    {
        // Leaves are backdated five minutes, so the root needs a wider margin
        // than the other tests' one minute.
        var rootNotBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var root = CertificateAuthority.GenerateRoot(rootNotBefore, rootNotBefore.AddYears(5));

        var provider = new LeafCertificateProvider(root);
        var first = provider.GetCertificateFor("example.test");
        var second = provider.GetCertificateFor("example.test");

        Assert.Same(first, second);
    }

    private static X509SubjectAlternativeNameExtension GetSubjectAlternativeNames(X509Certificate2 cert)
    {
        var raw = cert.Extensions["2.5.29.17"]
            ?? throw new InvalidOperationException("Subject Alternative Name extension is missing.");
        return new X509SubjectAlternativeNameExtension(raw.RawData, raw.Critical);
    }

    private static X509EnhancedKeyUsageExtension GetEnhancedKeyUsage(X509Certificate2 cert)
    {
        var raw = cert.Extensions["2.5.29.37"]
            ?? throw new InvalidOperationException("Enhanced Key Usage extension is missing.");
        return new X509EnhancedKeyUsageExtension(raw, raw.Critical);
    }

    [Fact]
    public void TheCacheDropsTheOldestLeavesOnceItsFull()
    {
        var rootNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(rootNotBefore, rootNotBefore.AddYears(5));
        var provider = new LeafCertificateProvider(root, maxCached: 5);

        // A client naming a new host on every connection.
        var first = provider.GetCertificateFor("host0.test");
        for (var i = 1; i < 12; i++)
        {
            provider.GetCertificateFor($"host{i}.test");
        }

        Assert.Equal(5, provider.CachedCount);
        var latest = provider.GetCertificateFor("host11.test");
        Assert.Same(latest, provider.GetCertificateFor("host11.test"));
        Assert.NotSame(first, provider.GetCertificateFor("host0.test"));
    }

    [Fact]
    public void TheDefaultCacheIsLargeEnoughForBrowsing() =>
        Assert.InRange(LeafCertificateProvider.MaxCachedLeaves, 500, 10_000);
}
