using System.Net;
using System.Net.Security;
using System.Text;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// Signs in to an upstream proxy that answers 407 Proxy Authentication
/// Required. One instance per sign-in (per connection to the proxy): call
/// <see cref="Respond"/> with each 407's headers, and send what it returns
/// as <c>Proxy-Authorization</c> on the next attempt, until the proxy
/// stops asking or this returns null.
///
/// Windows sign-in (Negotiate, which is Kerberos or NTLM, and NTLM on its
/// own) is preferred, as browsers prefer it, using the signed-in account
/// unless a user name and password were given. Basic needs a user name and
/// password, and is used only when the proxy offers nothing else.
/// </summary>
internal sealed class ProxyAuthenticator : IDisposable
{
    // Enough for a three-leg handshake plus a retry; a proxy that keeps
    // asking after that isn't going to accept these credentials.
    private const int MaxLegs = 5;

    private readonly string _proxyHost;
    private readonly NetworkCredential? _credential;
    private NegotiateAuthentication? _negotiate;
    private string? _scheme;
    private int _legs;

    /// <param name="proxyHost">The proxy's host name, for Kerberos.</param>
    /// <param name="credential">A user name and password to sign in with, or null for the signed-in account.</param>
    public ProxyAuthenticator(string proxyHost, NetworkCredential? credential)
    {
        _proxyHost = proxyHost;
        _credential = credential;
    }

    /// <summary>The schemes the proxy offered in the last 407, for error messages.</summary>
    public IReadOnlyList<string> OfferedSchemes { get; private set; } = [];

    /// <summary>
    /// The <c>Proxy-Authorization</c> value to send next, given a 407's
    /// headers, or null when there's nothing more to try (no scheme this can
    /// use, the proxy rejected the sign-in, or too many rounds).
    /// </summary>
    public string? Respond(IReadOnlyList<(string Name, string Value)> challengeHeaders)
    {
        var challenges = ParseChallenges(challengeHeaders);
        OfferedSchemes = challenges.Select(c => c.Scheme).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (++_legs > MaxLegs)
        {
            return null;
        }

        if (_scheme is not null)
        {
            return Continue(challenges);
        }

        foreach (var scheme in new[] { "Negotiate", "NTLM", "Basic" })
        {
            if (!challenges.Any(c => string.Equals(c.Scheme, scheme, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var first = scheme == "Basic" ? StartBasic() : StartNegotiate(scheme);
            if (first is not null)
            {
                _scheme = scheme;
                return first;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="Respond"/>, except that a Basic answer, which carries the
    /// password readably, is sent only if <paramref name="confirmPlaintext"/>
    /// agrees (null: always). Basic has one round, so that's asked at most
    /// once per sign-in.
    /// </summary>
    public async Task<string?> RespondAsync(
        IReadOnlyList<(string Name, string Value)> challengeHeaders,
        Func<CancellationToken, Task<bool>>? confirmPlaintext,
        CancellationToken cancellationToken)
    {
        var answer = Respond(challengeHeaders);
        if (answer is not null && _scheme == "Basic" && confirmPlaintext is not null && !await confirmPlaintext(cancellationToken))
        {
            PlaintextDeclined = true;
            return null;
        }

        return answer;
    }

    /// <summary>Whether the user chose not to send the password in plain text (see <see cref="RespondAsync"/>).</summary>
    public bool PlaintextDeclined { get; private set; }

    public void Dispose() => _negotiate?.Dispose();

    /// <summary>
    /// The challenges in Proxy-Authenticate headers: a scheme name and, for
    /// Negotiate and NTLM, the token that follows. A header can carry more
    /// than one challenge, separated by commas ("Negotiate, NTLM, Basic
    /// realm=\"proxy\""); a part that's a name=value parameter belongs to
    /// the challenge before it.
    /// </summary>
    internal static List<(string Scheme, string? Token)> ParseChallenges(IReadOnlyList<(string Name, string Value)> headers)
    {
        var challenges = new List<(string Scheme, string? Token)>();
        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var part in SplitOutsideQuotes(value))
            {
                var space = part.IndexOf(' ');
                var firstWord = space < 0 ? part : part[..space];
                if (firstWord.Contains('='))
                {
                    continue; // a parameter of the previous challenge (realm=..., charset=...)
                }

                var rest = space < 0 ? string.Empty : part[(space + 1)..].Trim();
                var token = rest.Length > 0 && IsToken68(rest) ? rest : null;
                challenges.Add((firstWord, token));
            }
        }

        return challenges;
    }

    private static IEnumerable<string> SplitOutsideQuotes(string value)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"' && (i == 0 || value[i - 1] != '\\'))
            {
                quoted = !quoted;
            }
            else if (value[i] == ',' && !quoted)
            {
                if (value[start..i].Trim() is { Length: > 0 } part)
                {
                    yield return part;
                }

                start = i + 1;
            }
        }

        if (value[start..].Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }

    /// <summary>A base64-style token, as Negotiate and NTLM send.</summary>
    private static bool IsToken68(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '=')
        {
            end--;
        }

        return end > 0 && text[..end].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/');
    }

    private string? StartBasic()
    {
        if (_credential is null || string.IsNullOrEmpty(_credential.UserName))
        {
            return null;
        }

        var user = string.IsNullOrEmpty(_credential.Domain) ? _credential.UserName : $"{_credential.Domain}\\{_credential.UserName}";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{_credential.Password}"));
    }

    private string? StartNegotiate(string scheme)
    {
        try
        {
            var negotiate = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package = scheme,
                Credential = _credential ?? CredentialCache.DefaultNetworkCredentials,
                TargetName = $"HTTP/{_proxyHost}",
            });

            var token = negotiate.GetOutgoingBlob((string?)null, out var status);
            if (token is null || status is not (NegotiateAuthenticationStatusCode.ContinueNeeded or NegotiateAuthenticationStatusCode.Completed))
            {
                negotiate.Dispose();
                return null;
            }

            _negotiate = negotiate;
            return $"{scheme} {token}";
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            // No Kerberos/NTLM support on this machine (some macOS and
            // Linux setups); try the next scheme.
            return null;
        }
    }

    private string? Continue(List<(string Scheme, string? Token)> challenges)
    {
        if (_scheme == "Basic" || _negotiate is null)
        {
            return null; // Basic doesn't have a second round: the credentials were refused
        }

        var challenge = challenges.FirstOrDefault(c => string.Equals(c.Scheme, _scheme, StringComparison.OrdinalIgnoreCase));
        if (challenge.Token is null)
        {
            return null; // the proxy started over: it refused the sign-in
        }

        var token = _negotiate.GetOutgoingBlob(challenge.Token, out var status);
        return token is not null && status is NegotiateAuthenticationStatusCode.ContinueNeeded or NegotiateAuthenticationStatusCode.Completed
            ? $"{_scheme} {token}"
            : null;
    }
}
