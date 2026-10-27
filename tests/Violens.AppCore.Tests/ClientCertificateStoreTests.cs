using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Violens.AppCore.Certificates;
using Violens.ProxyCore.Certificates;
using Xunit;

namespace Violens.AppCore.Tests;

public class ClientCertificateStoreTests
{
    private static X509Certificate2 Create(string subject, string? usageOid, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        if (usageOid is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usageOid)], critical: false));
        }

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    [Theory]
    [InlineData("1.3.6.1.5.5.7.3.2", true)] // client authentication
    [InlineData(null, true)] // no restriction
    [InlineData("1.3.6.1.5.5.7.3.1", false)] // server authentication only
    public void AcceptsCertificatesAllowedForClientAuthentication(string? usageOid, bool usable)
    {
        using var certificate = Create("CN=Ana Lima", usageOid, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Assert.Equal(usable, ClientCertificateStore.IsUsable(certificate));
    }

    [Fact]
    public void RejectsExpiredCertificatesAndRoots()
    {
        using var expired = Create("CN=Ana Lima", null, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        using var root = CertificateAuthority.GenerateRoot(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Another tool's root: no usage restriction, but a certificate authority.
        using var otherRoot = CertificateAuthority.GenerateRoot(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), "DO_NOT_TRUST_SomeOtherRoot");

        Assert.False(ClientCertificateStore.IsUsable(expired));
        Assert.False(ClientCertificateStore.IsUsable(root));
        Assert.False(ClientCertificateStore.IsUsable(otherRoot));
    }

    [Fact]
    public void DescribesByNameIssuerAndExpiry()
    {
        using var certificate = Create("CN=Ana Lima", null, DateTimeOffset.UtcNow.AddDays(-1), new DateTimeOffset(2027, 3, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.StartsWith("Ana Lima (issued by Ana Lima, expires 2027-03-0", ClientCertificateStore.Describe(certificate));
    }
}
