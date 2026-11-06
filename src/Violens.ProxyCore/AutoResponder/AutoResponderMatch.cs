using System.Text.RegularExpressions;

namespace Violens.ProxyCore.AutoResponder;

/// <summary>
/// Parses an AutoResponder match pattern, following Fiddler Classic's
/// documented syntax:
/// <list type="bullet">
/// <item><description><c>METHOD:&lt;verb&gt; </c> prefix: only requests with that method.</description></item>
/// <item><description><c>NOT:&lt;pattern&gt;</c>: inverts the pattern.</description></item>
/// <item><description><c>EXACT:&lt;url&gt;</c>: case-sensitive match of the whole URL.</description></item>
/// <item><description><c>regex:&lt;pattern&gt;</c>: a .NET regex against the whole URL. Inline
/// options such as <c>(?insx)</c> work as-is.</description></item>
/// <item><description>Anything else: case-insensitive substring match, with <c>*</c> as a
/// wildcard.</description></item>
/// </list>
/// A <c>regex:</c> pattern gets <see cref="RegexTimeout"/> per URL: one that
/// takes longer (catastrophic backtracking) doesn't match, and is logged.
/// Wildcards are matched without a regex, so they can't take long.
/// </summary>
public sealed class AutoResponderMatch
{
    /// <summary>How long a <c>regex:</c> pattern may take on one URL.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly string? _method;
    private readonly bool _invert;
    private readonly Func<string, bool> _innerMatches;

    private AutoResponderMatch(string? method, bool invert, Func<string, bool> innerMatches)
    {
        _method = method;
        _invert = invert;
        _innerMatches = innerMatches;
    }

    private const int MaxCachedPatterns = 1000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AutoResponderMatch> s_cache = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="Parse"/>, remembered: rules are matched against every
    /// request, and building a regex each time costs far more than matching.
    /// </summary>
    internal static AutoResponderMatch ParseCached(string pattern)
    {
        if (s_cache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        var parsed = Parse(pattern);
        if (s_cache.Count >= MaxCachedPatterns)
        {
            s_cache.Clear();
        }

        s_cache[pattern] = parsed;
        return parsed;
    }

    public static AutoResponderMatch Parse(string pattern)
    {
        var remainder = pattern.Trim();

        string? method = null;
        if (remainder.StartsWith("METHOD:", StringComparison.OrdinalIgnoreCase))
        {
            var afterKeyword = remainder["METHOD:".Length..];
            var spaceIndex = afterKeyword.IndexOf(' ');
            if (spaceIndex >= 0)
            {
                method = afterKeyword[..spaceIndex];
                remainder = afterKeyword[(spaceIndex + 1)..].TrimStart();
            }
            else
            {
                // "METHOD:GET" alone matches any URL with that method.
                method = afterKeyword;
                remainder = string.Empty;
            }
        }

        var invert = false;
        if (remainder.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase))
        {
            invert = true;
            remainder = remainder["NOT:".Length..];
        }

        var innerMatches = ParseInner(remainder);
        return new AutoResponderMatch(method, invert, innerMatches);
    }

    public bool Matches(string method, string url)
    {
        if (_method is not null && !string.Equals(_method, method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var matched = _innerMatches(url);
        return _invert ? !matched : matched;
    }

    private static Func<string, bool> ParseInner(string pattern)
    {
        if (pattern.StartsWith("EXACT:", StringComparison.OrdinalIgnoreCase))
        {
            var exact = pattern["EXACT:".Length..];
            return url => string.Equals(url, exact, StringComparison.Ordinal);
        }

        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            // A malformed regex throws, so a broken rule is noticed rather than
            // silently never matching.
            var regex = new Regex(pattern["regex:".Length..], RegexOptions.None, RegexTimeout);
            return url =>
            {
                try
                {
                    return regex.IsMatch(url);
                }
                catch (RegexMatchTimeoutException)
                {
                    ProxyEngine.Log("autoresponder", $"The rule {pattern} took too long on a URL, so it didn't match it.");
                    return false;
                }
            };
        }

        if (pattern.Contains('*'))
        {
            return url => Wildcard.Contains(url, pattern, questionMarkMatchesOne: false);
        }

        return url => url.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
