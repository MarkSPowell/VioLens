using System.Net;
using System.Text;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// The proxy's own page, like Fiddler Classic's echo page, where a phone or
/// another computer downloads the root certificate (<c>http://violens/</c>
/// through the proxy, or this computer's address and port).
/// </summary>
internal static class ProxyHomePage
{
    /// <summary>The host name that always means "the proxy itself" when requested through it.</summary>
    public static string HostName => ProxyEngine.Identity.ProxyHostName;

    /// <summary>Where the root certificate is served (DER-encoded).</summary>
    public static string CertificatePath => "/" + ProxyEngine.Identity.RootCertificateFileName;

    /// <summary>
    /// True when a plain-HTTP request's target is addressed to VioLens
    /// itself rather than to a website: an origin-form target ("/..."), sent
    /// straight to the listener; the host name <see cref="HostName"/>; or
    /// this machine's own address on <paramref name="ownPort"/>.
    /// </summary>
    public static bool IsRequestForProxy(string? target, int ownPort)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (target.StartsWith('/'))
        {
            return true;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.Host, HostName, StringComparison.OrdinalIgnoreCase) ||
               (uri.Port == ownPort && LocalNetwork.IsThisMachine(uri.Host));
    }

    /// <summary>The response for a request to the proxy itself: the certificate, or the page that links to it.</summary>
    public static CapturedResponse Build(string target, byte[] rootCertificateDer)
    {
        var identity = ProxyEngine.Identity;
        var path = PathOf(target);
        if (string.Equals(path, CertificatePath, StringComparison.OrdinalIgnoreCase))
        {
            return new CapturedResponse(
                "HTTP/1.1",
                200,
                "OK",
                [
                    ("Content-Type", "application/x-x509-ca-cert"),
                    ("Content-Disposition", $"attachment; filename=\"{identity.RootCertificateFileName}\""),
                    ("Content-Length", rootCertificateDer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("Cache-Control", "no-store"),
                    ("Connection", "close"),
                ],
                rootCertificateDer);
        }

        if (path is not ("/" or ""))
        {
            return Text(404, "Not Found", $"{identity.ProductName} has nothing at {path}. The root certificate is at {CertificatePath}.");
        }

        var html = Encoding.UTF8.GetBytes(HomePageHtml(WebUtility.HtmlEncode(identity.ProductName), CertificatePath));
        return new CapturedResponse(
            "HTTP/1.1",
            200,
            "OK",
            [
                ("Content-Type", "text/html; charset=utf-8"),
                ("Content-Length", html.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("Cache-Control", "no-store"),
                ("Connection", "close"),
            ],
            html);
    }

    /// <summary>
    /// The answer to a request the proxy can't forward: one sent without a
    /// CONNECT tunnel whose URL isn't http:// (an ftp:// URL, say).
    /// </summary>
    public static CapturedResponse NotForwarded(string method, string target) =>
        Text(
            501,
            "Not Implemented",
            $"{ProxyEngine.Identity.ProductName} forwards http:// and https:// requests only, so it didn't forward this one ({method} {target}). " +
            $"To install its certificate on this device, browse to http://{HostName}/ with the proxy set.");

    /// <summary>
    /// The answer to another computer asking for this computer's own
    /// loopback address (see InterceptingProxyListener.IsThisComputerAsync).
    /// </summary>
    internal static CapturedResponse NotForThisComputer(string target) =>
        Text(
            403,
            "Forbidden",
            $"{ProxyEngine.Identity.ProductName} doesn't let other devices reach the computer it runs on through localhost ({target}): " +
            "that would expose services meant only for that computer.");

    private static CapturedResponse Text(int status, string reason, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        return new CapturedResponse(
            "HTTP/1.1",
            status,
            reason,
            [
                ("Content-Type", "text/plain; charset=utf-8"),
                ("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("Connection", "close"),
            ],
            body);
    }

    private static string PathOf(string target)
    {
        var path = target;
        if (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        var query = path.IndexOfAny(['?', '#']);
        return query < 0 ? path : path[..query];
    }

    private static string HomePageHtml(string name, string certificatePath) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{name}}</title>
        <style>
          body { font-family: system-ui, sans-serif; max-width: 40em; margin: 2em auto; padding: 0 1em; line-height: 1.5; }
          a.button { display: inline-block; padding: .6em 1.2em; background: #2f6fdf; color: #fff; border-radius: .4em; text-decoration: none; }
          .warn { background: #fff4d6; padding: .8em 1em; border-radius: .4em; }
        </style>
        </head>
        <body>
        <h1>{{name}}</h1>
        <p>This device is using {{name}} as its proxy. To let {{name}} show this device's HTTPS traffic,
        install and trust {{name}}'s root certificate.</p>
        <p><a class="button" href="{{certificatePath}}">Download the {{name}} root certificate</a></p>
        <h2>iPhone and iPad</h2>
        <ol>
          <li>Open this page in Safari and tap the link above, then <b>Allow</b>.</li>
          <li>Settings &gt; General &gt; VPN &amp; Device Management: tap the {{name}} profile and <b>Install</b>.</li>
          <li>Settings &gt; General &gt; About &gt; Certificate Trust Settings: turn on full trust for the {{name}} certificate.</li>
        </ol>
        <h2>Android</h2>
        <ol>
          <li>Tap the link above to save the certificate.</li>
          <li>Settings &gt; Security (or Security &amp; privacy) &gt; Encryption &amp; credentials &gt; Install a certificate &gt; CA certificate, and pick the file.</li>
          <li>Many apps only trust certificates that came with the system, so their traffic may still fail. Browsers generally work.</li>
        </ol>
        <p class="warn">Anyone with this certificate's private key could read this device's HTTPS traffic. The key stays on the
        computer running {{name}}. Remove the certificate from this device when you've finished.</p>
        </body>
        </html>
        """;
}
