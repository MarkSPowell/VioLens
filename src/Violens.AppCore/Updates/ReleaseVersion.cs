using System.Globalization;
using System.Text.RegularExpressions;

namespace Violens.AppCore.Updates;

/// <summary>
/// A release's version, as its git tag names it (<c>v0.3.0-preview.1</c>),
/// ordered by Semantic Versioning's rules: a pre-release comes before its
/// release, and pre-release parts compare as numbers when they're numeric.
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreRelease) : IComparable<ReleaseVersion>
{
    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>"1.2.3" or "v1.2.3-preview.4", ignoring any "+metadata"; null for anything else.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        var match = text is null ? null : VersionPattern().Match(text.Trim());
        if (match is not { Success: true } ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }

        var preRelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        return new ReleaseVersion(major, minor, patch, preRelease);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        // A release outranks any of its pre-releases.
        if (IsPreRelease != other.IsPreRelease)
        {
            return IsPreRelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var part = ComparePart(PreRelease[i], other.PreRelease[i]);
            if (part != 0)
            {
                return part;
            }
        }

        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    // Numeric parts compare as numbers and come before text parts.
    private static int ComparePart(string left, string right)
    {
        var leftIsNumber = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightIsNumber = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
        return (leftIsNumber, rightIsNumber) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? "-" + string.Join('.', PreRelease) : string.Empty);

    // Records compare their list by reference; compare the parts instead.
    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]*)?$")]
    private static partial Regex VersionPattern();
}
