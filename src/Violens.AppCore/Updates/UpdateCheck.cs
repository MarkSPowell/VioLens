using System.Net.Http.Headers;
using System.Text.Json;

namespace Violens.AppCore.Updates;

/// <summary>A newer release than the one running, and its page on GitHub.</summary>
public sealed record AvailableUpdate(ReleaseVersion Version, Uri Page);

/// <summary>
/// Asks GitHub whether a newer VioLens release is out. One request to
/// GitHub's public releases API, sending nothing about the user beyond what
/// any web request does (the address it comes from); turned off with
/// <c>violens.update.check</c>. Previews are offered only to someone already
/// running a preview, and a local build (<c>0.0.0-dev</c>) never checks.
/// </summary>
public static class UpdateCheck
{
    public static readonly Uri ReleasesApi = new("https://api.github.com/repos/MarkSPowell/VioLens/releases?per_page=30");

    /// <summary>Release pages are built from the tag, never taken from the response, so only this site is ever opened.</summary>
    public const string ReleasePageBase = "https://github.com/MarkSPowell/VioLens/releases/tag/";

    /// <summary>
    /// The newest release after <paramref name="currentVersion"/>, or null
    /// when there's none, the version can't be read, or it's a local build.
    /// Throws <see cref="HttpRequestException"/> or <see cref="JsonException"/>
    /// (and cancellation) when GitHub can't be reached or answers oddly.
    /// </summary>
    public static async Task<AvailableUpdate?> CheckAsync(string currentVersion, HttpClient http, CancellationToken cancellationToken)
    {
        if (!ShouldCheck(currentVersion))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VioLens", ReleaseVersion.Parse(currentVersion)!.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var releases = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return PickNewer(currentVersion, releases.RootElement);
    }

    /// <summary>False for a local build (0.0.0, as in <c>0.0.0-dev</c>), or a version that can't be read.</summary>
    public static bool ShouldCheck(string currentVersion) =>
        ReleaseVersion.Parse(currentVersion) is { } current && (current.Major, current.Minor, current.Patch) != (0, 0, 0);

    /// <summary>The choice <see cref="CheckAsync"/> makes, from the API's array of releases.</summary>
    internal static AvailableUpdate? PickNewer(string currentVersion, JsonElement releases)
    {
        if (ReleaseVersion.Parse(currentVersion) is not { } current || releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        AvailableUpdate? newest = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.GetString() is not { } tag ||
                ReleaseVersion.Parse(tag) is not { } version)
            {
                continue;
            }

            // Previews only for someone already on one.
            var isPreRelease = version.IsPreRelease ||
                (release.TryGetProperty("prerelease", out var flag) && flag.ValueKind == JsonValueKind.True);
            if (isPreRelease && !current.IsPreRelease)
            {
                continue;
            }

            if (version.CompareTo(current) > 0 && (newest is null || version.CompareTo(newest.Version) > 0))
            {
                newest = new AvailableUpdate(version, new Uri(ReleasePageBase + Uri.EscapeDataString(tag)));
            }
        }

        return newest;
    }
}
