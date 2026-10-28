using System.Text.Json;
using Violens.AppCore.Updates;
using Xunit;

namespace Violens.AppCore.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.3.0-preview.1", "0.3.0-preview.2")]
    [InlineData("0.3.0-preview.2", "0.3.0-preview.10")]
    [InlineData("0.3.0-preview.9", "0.3.0")]
    [InlineData("0.3.0", "0.4.0-preview.1")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-beta")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    public void OrdersVersionsTheSemanticVersioningWay(string older, string newer)
    {
        var a = ReleaseVersion.Parse(older)!;
        var b = ReleaseVersion.Parse(newer)!;
        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
    }

    [Theory]
    [InlineData("v0.3.0-preview.1", "0.3.0-preview.1")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData(" 1.0.0 ", "1.0.0")]
    public void ParsesTagsAndInformationalVersions(string text, string expected) =>
        Assert.Equal(expected, ReleaseVersion.Parse(text)!.ToString());

    [Theory]
    [InlineData("dev")]
    [InlineData("1.2")]
    [InlineData("1.2.3-")]
    [InlineData(null)]
    public void RejectsWhatIsntAVersion(string? text) => Assert.Null(ReleaseVersion.Parse(text));

    [Theory]
    [InlineData("0.0.0-dev", false)]
    [InlineData("unknown", false)]
    [InlineData("0.3.0-preview.1", true)]
    public void LocalBuildsDontCheck(string version, bool checks) => Assert.Equal(checks, UpdateCheck.ShouldCheck(version));

    private const string Releases = """
        [
          { "tag_name": "v0.5.0", "draft": true, "prerelease": false },
          { "tag_name": "v0.4.0-preview.2", "draft": false, "prerelease": true },
          { "tag_name": "v0.3.1", "draft": false, "prerelease": false },
          { "tag_name": "v0.3.0", "draft": false, "prerelease": false },
          { "tag_name": "not-a-version", "draft": false, "prerelease": false },
          { "tag_name": "v0.3.0-preview.1", "draft": false, "prerelease": true }
        ]
        """;

    private static AvailableUpdate? Pick(string current)
    {
        using var document = JsonDocument.Parse(Releases);
        return UpdateCheck.PickNewer(current, document.RootElement);
    }

    [Fact]
    public void APreviewUserIsOfferedTheNewestReleaseOrPreview()
    {
        var update = Pick("0.3.0-preview.1")!;
        Assert.Equal("0.4.0-preview.2", update.Version.ToString());
        Assert.Equal("https://github.com/MarkSPowell/VioLens/releases/tag/v0.4.0-preview.2", update.Page.ToString());
    }

    [Fact]
    public void AReleaseUserIsOfferedReleasesOnlyAndNeverDrafts() =>
        Assert.Equal("0.3.1", Pick("0.3.0")!.Version.ToString());

    [Fact]
    public void NothingNewerMeansNoUpdate() => Assert.Null(Pick("0.4.0"));
}
