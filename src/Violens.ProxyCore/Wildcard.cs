namespace Violens.ProxyCore;

/// <summary>
/// Wildcard matching (<c>*</c> for any run of characters, and optionally
/// <c>?</c> for any one), case-insensitively. Done without a regex: a
/// pattern such as <c>*a*a*a*a*b</c> made a backtracking regex take
/// exponential time on a long name, and this takes at most the text's
/// length times the pattern's.
/// </summary>
internal static class Wildcard
{
    /// <summary>
    /// Whether all of <paramref name="text"/> matches <paramref name="pattern"/>.
    /// With <paramref name="questionMarkMatchesOne"/> false, <c>?</c> is an
    /// ordinary character.
    /// </summary>
    public static bool IsMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern, bool questionMarkMatchesOne)
    {
        var t = 0;
        var p = 0;
        var star = -1;
        var resume = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                // Try matching nothing first; come back here if that fails.
                star = p++;
                resume = t;
            }
            else if (p < pattern.Length && ((questionMarkMatchesOne && pattern[p] == '?') || SameLetter(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                // Let the last * take one more character.
                p = star + 1;
                t = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>Whether <paramref name="pattern"/> matches anywhere in <paramref name="text"/>.</summary>
    public static bool Contains(string text, string pattern, bool questionMarkMatchesOne) =>
        IsMatch(text, "*" + pattern + "*", questionMarkMatchesOne);

    private static bool SameLetter(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
