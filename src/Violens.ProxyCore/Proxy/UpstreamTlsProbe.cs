using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Violens.ProxyCore.Sessions;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// Watches the TLS handshake (and any later renegotiation) VioLens makes
/// with a server, noticing when the server asks for a client certificate,
/// and describes the connection for the sessions that use it
/// (<see cref="ServerTlsInfo"/>).
///
/// .NET asks for a client certificate through
/// <see cref="SslClientAuthenticationOptions.LocalCertificateSelectionCallback"/>.
/// On some platforms that's also called once before the handshake starts,
/// with no server certificate yet; only a call made after the server's
/// certificate has arrived is the server actually asking. The certificate
/// sent is whatever <c>chooseClientCertificate</c> picks for the host (see
/// <see cref="InterceptingProxyListener.ClientCertificate"/>); with none, a
/// server that insists refuses the connection.
/// </summary>
internal sealed class UpstreamTlsProbe(Func<string, X509Certificate2?>? chooseClientCertificate = null)
{
    private readonly object _gate = new();
    private SslStream? _stream;
    private bool _requested;
    private string[] _issuers = [];
    private string? _sent;

    /// <summary>True once the server has asked for a client certificate.</summary>
    public bool ClientCertificateRequested
    {
        get
        {
            lock (_gate)
            {
                return _requested;
            }
        }
    }

    /// <summary>
    /// Options for connecting to <paramref name="targetHost"/> that report
    /// to this probe. <paramref name="validateServer"/> replaces the normal
    /// certificate check (tests only); null keeps the platform's own.
    /// </summary>
    public SslClientAuthenticationOptions CreateOptions(string targetHost, RemoteCertificateValidationCallback? validateServer = null) => new()
    {
        TargetHost = targetHost,
        EnabledSslProtocols = SslProtocols.None,
        RemoteCertificateValidationCallback = validateServer,
        LocalCertificateSelectionCallback = (_, _, _, remoteCertificate, acceptableIssuers) =>
        {
            if (remoteCertificate is null)
            {
                return null!; // not the server asking yet
            }

            var chosen = chooseClientCertificate?.Invoke(targetHost);
            lock (_gate)
            {
                _requested = true;
                _issuers = acceptableIssuers;
                _sent = chosen?.Subject;
            }

            return chosen!; // null: no client certificate
        },
    };

    /// <summary>The stream the handshake runs on, read for <see cref="Snapshot"/>.</summary>
    public void Attach(SslStream stream) => _stream = stream;

    /// <summary>The connection as it stands now.</summary>
    public ServerTlsInfo Snapshot()
    {
        string? protocol = null, cipherSuite = null, subject = null, issuer = null;
        DateTimeOffset? notAfter = null;
        if (_stream is { IsAuthenticated: true } stream)
        {
            try
            {
                protocol = DescribeProtocol(stream.SslProtocol);
                cipherSuite = stream.NegotiatedCipherSuite.ToString();
                if (stream.RemoteCertificate is { } certificate)
                {
                    subject = certificate.Subject;
                    issuer = certificate.Issuer;
                    if (certificate is X509Certificate2 certificate2)
                    {
                        notAfter = new DateTimeOffset(certificate2.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
            {
                // The connection closed under us; describe what's known.
            }
        }

        lock (_gate)
        {
            return new ServerTlsInfo
            {
                Protocol = protocol,
                CipherSuite = cipherSuite,
                CertificateSubject = subject,
                CertificateIssuer = issuer,
                CertificateNotAfter = notAfter,
                ClientCertificateRequested = _requested,
                ClientCertificateSent = _sent,
                AcceptableIssuers = _issuers.Where(i => !string.IsNullOrWhiteSpace(i)).ToArray(),
            };
        }
    }

    /// <summary>"TLS 1.3" for <see cref="SslProtocols.Tls13"/>, and so on.</summary>
    internal static string DescribeProtocol(SslProtocols protocol) => protocol switch
    {
#pragma warning disable SYSLIB0039 // older versions are only being named, not enabled
        SslProtocols.Tls => "TLS 1.0",
        SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore SYSLIB0039
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls13 => "TLS 1.3",
        _ => protocol.ToString(),
    };
}
