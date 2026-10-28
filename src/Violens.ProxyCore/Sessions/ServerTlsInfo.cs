namespace Violens.ProxyCore.Sessions;

/// <summary>
/// The TLS connection VioLens made to the server for a session: what was
/// agreed, the server's certificate, and whether the server asked for a
/// client certificate (mutual TLS). VioLens makes its own connection to
/// the server, so a server's request for a client certificate reaches
/// VioLens, not the browser; this is where it shows, along with the
/// certificate VioLens sent (see
/// <see cref="Proxy.InterceptingProxyListener.ClientCertificate"/>).
/// </summary>
public sealed record ServerTlsInfo
{
    /// <summary>The TLS version, such as "TLS 1.3"; null if the handshake didn't finish.</summary>
    public string? Protocol { get; init; }

    /// <summary>The cipher suite, such as "TLS_AES_128_GCM_SHA256"; null if the handshake didn't finish.</summary>
    public string? CipherSuite { get; init; }

    /// <summary>The server certificate's subject, such as "CN=example.com".</summary>
    public string? CertificateSubject { get; init; }

    public string? CertificateIssuer { get; init; }

    public DateTimeOffset? CertificateNotAfter { get; init; }

    /// <summary>True when the server asked for a client certificate.</summary>
    public bool ClientCertificateRequested { get; init; }

    /// <summary>
    /// The certificate authorities the server said it accepts client
    /// certificates from (distinguished names). Empty when it asked without
    /// naming any, or didn't ask.
    /// </summary>
    public IReadOnlyList<string> AcceptableIssuers { get; init; } = [];

    /// <summary>
    /// The session flag set on sessions whose server asked for a client
    /// certificate; its value lists the accepted issuers. Shows in the Notes tab.
    /// </summary>
    public const string ClientCertificateRequestedFlag = "x-client-certificate-requested";

    /// <summary>The subject of the client certificate VioLens sent; null when it sent none.</summary>
    public string? ClientCertificateSent { get; init; }

    /// <summary>The session flag naming the client certificate sent (<see cref="ClientCertificateSent"/>). Shows in the Notes tab.</summary>
    public const string ClientCertificateSentFlag = "x-client-certificate-sent";

    /// <summary>The value of <see cref="ClientCertificateRequestedFlag"/>.</summary>
    public string DescribeAcceptableIssuers() =>
        AcceptableIssuers.Count == 0 ? "any issuer" : string.Join("; ", AcceptableIssuers);
}
