using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Http2;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// Sends a request the way Fiddler Classic's Composer and "Reissue" did:
/// through VioLens's own listener, exactly as a browser would, so it's
/// captured as a new session and goes through everything live traffic does
/// (AutoResponder, breakpoints, FiddlerScript, extensions, the upstream
/// proxy).
///
/// http:// and https:// URLs. An HTTPS connection trusts VioLens's root
/// certificate specifically, so it works whether or not that root is in the
/// system's trust store. An HTTPS request whose request line says HTTP/2 is
/// sent over HTTP/2 when the listener offers it, and over HTTP/1.1 when it
/// doesn't; the response's version says which.
/// </summary>
public static class ComposerClient
{
    /// <summary>
    /// Works out where a composed request goes. The target may be an absolute
    /// URL, or a path with a Host header (sent as HTTPS). Returns the URL and a
    /// request with a path-only target, or false with an
    /// <paramref name="error"/>.
    /// </summary>
    public static bool TryResolve(CapturedRequest request, out Uri? url, out CapturedRequest? toSend, out string? error)
    {
        url = null;
        toSend = null;
        error = null;

        var target = request.Target.Trim();
        if (target.StartsWith('/'))
        {
            var host = ContentDecoder.FindHeader(request.Headers, "Host");
            if (string.IsNullOrWhiteSpace(host))
            {
                error = "The request needs a full http:// or https:// URL, or a Host header to go with its path.";
                return false;
            }

            target = $"https://{host.Trim()}{target}";
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var parsed))
        {
            error = $"\"{request.Target}\" isn't a URL. Start the first line with the method and an https:// URL, e.g. GET https://example.com/ HTTP/1.1";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{ProxyEngine.Identity.ProductName} only sends HTTP and HTTPS requests, and this one is {parsed.Scheme}://.";
            return false;
        }

        var authority = parsed.IsDefaultPort ? parsed.Host : $"{parsed.Host}:{parsed.Port}";
        var headers = new List<(string Name, string Value)>(request.Headers.Count + 1);
        var hostSet = false;
        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                if (!hostSet)
                {
                    headers.Add((name, authority));
                    hostSet = true;
                }

                continue;
            }

            headers.Add((name, value));
        }

        if (!hostSet)
        {
            headers.Insert(0, ("Host", authority));
        }

        // HTTP/2 only over TLS: nothing uses it unencrypted (h2c).
        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var version = isHttps && HttpVersions.IsHttp2(request.HttpVersion) ? HttpVersions.Http2
            : string.IsNullOrWhiteSpace(request.HttpVersion) || !request.HttpVersion.StartsWith("HTTP/1", StringComparison.OrdinalIgnoreCase) ? HttpVersions.Http11
            : request.HttpVersion;

        url = parsed;
        toSend = request with { Target = parsed.PathAndQuery, HttpVersion = version, Headers = headers };
        return true;
    }

    /// <summary>
    /// Sends <paramref name="request"/> (as returned by <see cref="TryResolve"/>)
    /// to <paramref name="url"/> through the listener on
    /// <paramref name="listenerPort"/>, and returns the response. The
    /// listener records the exchange as a session as it goes.
    /// </summary>
    public static async Task<CapturedResponse> SendAsync(
        int listenerPort,
        byte[] rootCertificateDer,
        Uri url,
        CapturedRequest request,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, listenerPort, cancellationToken);
        var stream = client.GetStream();
        var isHead = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            // Plain HTTP: an ordinary proxy request, the full URL in the
            // request line, as a browser sends it.
            var authority = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
            var absolute = request.Target.StartsWith('/') ? request with { Target = $"http://{authority}{request.Target}" } : request;
            await HttpMessageWriter.WriteRequestAsync(stream, absolute, cancellationToken);
            return await Http1MessageReader.ReadResponseAsync(stream, isHead, cancellationToken)
                ?? throw new IOException("The connection closed before a response arrived.");
        }

        using var root = X509CertificateLoader.LoadCertificate(rootCertificateDer);
        await UpstreamProxy.EstablishTunnelAsync(stream, url.Host, url.Port, "the proxy's own listener", cancellationToken);

        var wantsHttp2 = HttpVersions.IsHttp2(request.HttpVersion);
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        await using var tlsOwner = tls;
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = url.Host,
                EnabledSslProtocols = SslProtocols.None,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => IsIssuedBy(certificate, root),
                ApplicationProtocols = wantsHttp2 ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11] : null,
            },
            cancellationToken);

        if (tls.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
        {
            return await SendHttp2Async(tls, url, request, cancellationToken);
        }

        if (wantsHttp2)
        {
            // The listener's HTTP/2 setting is off.
            request = request with { HttpVersion = HttpVersions.Http11 };
        }

        await HttpMessageWriter.WriteRequestAsync(tls, request, cancellationToken);
        return await Http1MessageReader.ReadResponseAsync(tls, isHead, cancellationToken)
            ?? throw new IOException("The connection closed before a response arrived.");
    }

    private static async Task<CapturedResponse> SendHttp2Async(
        Stream tls, Uri url, CapturedRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await Http2Connection.ConnectAsync(tls, null, cancellationToken);
        var authority = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        var hasBody = request.Body.Length > 0;
        var stream = await connection.OpenStreamAsync(
            Http2Messages.ToRequestBlock(request, authority, hasBody ? request.Body.Length : null), endStream: !hasBody, cancellationToken);
        if (hasBody)
        {
            await stream.WriteDataAsync(request.Body, endStream: true, cancellationToken);
        }

        // Interim (1xx) responses first, then the real one.
        CapturedResponse response;
        do
        {
            response = Http2Messages.ToResponse(await stream.ReadHeadersAsync(cancellationToken));
        }
        while (response.StatusCode is >= 100 and < 200 && !stream.HeadersEndedStream);

        var body = stream.HeadersEndedStream ? [] : await stream.ReadAllDataAsync(cancellationToken);
        return response with { Body = body, Trailers = stream.Trailers };
    }

    /// <summary>True when <paramref name="certificate"/> chains to <paramref name="root"/> (and only that root).</summary>
    private static bool IsIssuedBy(X509Certificate? certificate, X509Certificate2 root)
    {
        if (certificate is null)
        {
            return false;
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }
}
