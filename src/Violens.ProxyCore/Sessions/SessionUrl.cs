using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Sessions;

/// <summary>A session's full request URL.</summary>
public static class SessionUrl
{
    /// <summary>
    /// The request's full URL: the session's scheme, then the Host header
    /// (which carries any non-default port) or else the session's host, then
    /// the target. An absolute target is returned as is, and so is a
    /// CONNECT's <c>host:port</c> (a tunnel that wasn't decrypted), as
    /// Fiddler shows it.
    /// </summary>
    public static string Of(Session session)
    {
        var target = session.Request.Target;
        if (string.Equals(session.Request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var authority = ContentDecoder.FindHeader(session.Request.Headers, "Host") ?? session.Host;
        var scheme = string.IsNullOrEmpty(session.Scheme) ? "https" : session.Scheme;
        return $"{scheme}://{authority}{(target.StartsWith('/') ? target : "/" + target)}";
    }
}
