using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Violens.ProxyCore.Certificates;

/// <summary>
/// Signs and caches short-lived leaf certificates per host (see the
/// Interception Certificate Design).
/// </summary>
public sealed class LeafCertificateProvider
{
    private static readonly TimeSpan LeafValidity = TimeSpan.FromDays(7);
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    private readonly X509Certificate2 _issuer;

    // Lazy values make signing single-flight: browsers open several
    // connections to a new host at once, and signing in parallel caused
    // intermittent Schannel "unknown error" handshake failures.
    private readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Hosts in the order their leaves were made, oldest first, so the cache
    // can drop the oldest once it's full.
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _maxCached;

    /// <summary>
    /// The most leaves kept. Each holds a key, so a client naming a new host
    /// on every connection (any SNI it likes) mustn't grow the cache
    /// without end. Far more hosts than a browsing session visits.
    /// </summary>
    internal const int MaxCachedLeaves = 2000;

    public LeafCertificateProvider(X509Certificate2 issuer)
        : this(issuer, MaxCachedLeaves)
    {
    }

    /// <summary>With a smaller cache. Internal for tests.</summary>
    internal LeafCertificateProvider(X509Certificate2 issuer, int maxCached)
    {
        _maxCached = maxCached;
        if (!issuer.HasPrivateKey)
        {
            throw new ArgumentException(
                "The issuer certificate must carry its private key to sign leaf certificates.",
                nameof(issuer));
        }

        _issuer = issuer;
    }

    /// <summary>
    /// The root, without its key (DER). What other devices install to trust
    /// VioLens, and what the Composer trusts.
    /// </summary>
    public byte[] IssuerCertificateDer => _issuer.Export(X509ContentType.Cert);

    /// <summary>A cached leaf for <paramref name="host"/>, or a new one if fewer than five minutes are left.</summary>
    public X509Certificate2 GetCertificateFor(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        while (true)
        {
            var lazyLeaf = _cache.GetOrAdd(host, CreateLazyLeaf);
            Trim();

            try
            {
                var certificate = lazyLeaf.Value;
                if (certificate.NotAfter > DateTime.UtcNow.AddMinutes(5))
                {
                    return certificate;
                }

                // Nearly expired: remove it and loop to make a new one.
                _cache.TryRemove(new KeyValuePair<string, Lazy<X509Certificate2>>(host, lazyLeaf));
            }
            catch
            {
                // A faulted Lazy rethrows forever, so drop it and let the next caller
                // try again.
                _cache.TryRemove(new KeyValuePair<string, Lazy<X509Certificate2>>(host, lazyLeaf));
                throw;
            }
        }
    }

    /// <summary>How many leaves are cached. Internal for tests.</summary>
    internal int CachedCount => _cache.Count;

    // Evicted leaves aren't disposed: a handshake may still be using one.
    // Once nothing refers to it, its key is released with it.
    private void Trim()
    {
        while (_cache.Count > _maxCached && _order.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }

    private Lazy<X509Certificate2> CreateLazyLeaf(string host)
    {
        _order.Enqueue(host);
        return NewLazyLeaf(host);
    }

    private Lazy<X509Certificate2> NewLazyLeaf(string host) => new(
        () =>
        {
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.Add(LeafValidity);

            // A leaf can't be valid outside its issuer's validity.
            var issuerNotBefore = new DateTimeOffset(_issuer.NotBefore.ToUniversalTime(), TimeSpan.Zero);
            var issuerNotAfter = new DateTimeOffset(_issuer.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (notBefore < issuerNotBefore)
            {
                notBefore = issuerNotBefore;
            }

            if (notAfter > issuerNotAfter)
            {
                notAfter = issuerNotAfter;
            }

            return CreateLeaf(_issuer, host, notBefore, notAfter);
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Signs one leaf, without caching or touching any store.</summary>
    public static X509Certificate2 CreateLeaf(
        X509Certificate2 issuer,
        string host,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        var subjectBuilder = new X500DistinguishedNameBuilder();
        subjectBuilder.AddCommonName(host);
        var subject = subjectBuilder.Build();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address))
        {
            sanBuilder.AddIpAddress(address);
        }
        else
        {
            sanBuilder.AddDnsName(host);
        }
        request.CertificateExtensions.Add(sanBuilder.Build());

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));

        // ECDSA leaves only need DigitalSignature; KeyEncipherment is an
        // RSA-specific usage and doesn't apply to an EC key.
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(ServerAuthenticationOid) },
                critical: false));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // Strict validators expect the authority key identifier even in a
        // two-level chain.
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                issuer,
                includeKeyIdentifier: true,
                includeIssuerAndSerial: false));

        var serialNumber = RandomNumberGenerator.GetBytes(16);
        using var signed = request.Create(issuer, notBefore, notAfter, serialNumber);
        using var signedWithEphemeralKey = signed.CopyWithPrivateKey(key);

        return PersistKey(signedWithEphemeralKey);
    }

    private static X509Certificate2 PersistKey(X509Certificate2 certWithEphemeralKey)
    {
        // Schannel won't use a key that exists only in memory ("ephemeral
        // keys"), so round-trip through a PFX to give it a key container.
        // No PersistKeySet, so the container is deleted when the certificate is
        // released instead of piling up in the profile.
        // A fresh container isn't always ready straight away, which showed up as
        // occasional handshake failures; WarmUpPrivateKey uses the key once here,
        // where a failure can be retried.
        const int maxAttempts = 2;
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var password = Guid.NewGuid().ToString("N");
            var pfxBytes = certWithEphemeralKey.Export(X509ContentType.Pfx, password);
            X509Certificate2? persisted = null;

            try
            {
                persisted = X509CertificateLoader.LoadPkcs12(
                    pfxBytes,
                    password,
                    X509KeyStorageFlags.UserKeySet);

                WarmUpPrivateKey(persisted);
                return persisted;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                persisted?.Dispose();
            }
            finally
            {
                Array.Clear(pfxBytes);
            }
        }

        throw new CryptographicException(
            $"Failed to obtain a usable persisted private key after {maxAttempts} attempts.", lastFailure);
    }

    private static void WarmUpPrivateKey(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPrivateKey()
            ?? throw new CryptographicException("Persisted leaf certificate has no usable ECDSA private key.");
        _ = ecdsa.SignData("violens-key-warmup"u8.ToArray(), HashAlgorithmName.SHA256);
    }
}
