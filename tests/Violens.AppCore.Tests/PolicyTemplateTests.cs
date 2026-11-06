using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Violens.AppCore.Preferences;
using Violens.ProxyCore.Preferences;
using Xunit;

namespace Violens.AppCore.Tests;

/// <summary>
/// The Group Policy template (installer/policy): it writes where
/// <see cref="PolicySettings"/> reads, names real preferences, and stays
/// importable into Intune, which refuses dependencies it hasn't been given
/// and combo boxes.
/// </summary>
public class PolicyTemplateTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static readonly string LockedKey = @"Software\Policies\VioLens";

    private static readonly string DefaultsKey = LockedKey + @"\" + PolicySettings.RegistryDefaultsSubkey;

    private static string PolicyFolder([CallerFilePath] string callerPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", "installer", "policy"));

    private static XDocument Admx() => XDocument.Load(Path.Combine(PolicyFolder(), "VioLens.admx"));

    private static XDocument Adml() => XDocument.Load(Path.Combine(PolicyFolder(), "en-US", "VioLens.adml"));

    private static IEnumerable<XElement> Policies() => Admx().Descendants(Ns + "policy");

    [Fact]
    public void WritesWhereVioLensReads()
    {
        Assert.Equal(PolicySettings.RegistryKeyPath, LockedKey, ignoreCase: true);

        foreach (var key in Admx().Descendants().Attributes("key").Select(a => a.Value))
        {
            Assert.Contains(key, new[] { LockedKey, DefaultsKey });
        }

        Assert.All(Policies(), p => Assert.Equal("Machine", (string?)p.Attribute("class")));
    }

    [Fact]
    public void NamesOnlyValidPreferences()
    {
        var names = Admx().Descendants().Attributes("valueName").Select(a => a.Value).ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name =>
        {
            Assert.True(PreferenceStore.IsValidName(name), name);
            Assert.StartsWith("violens.", name, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EveryLockedSettingHasADefaultTwin()
    {
        static string Shape(XElement policy) => string.Join(
            "|",
            policy.Descendants().Attributes("valueName").Select(a => a.Value)
                .Concat(policy.Attributes("valueName").Select(a => a.Value))
                .Order(StringComparer.Ordinal));

        var locked = Policies().Where(p => (string?)p.Attribute("key") == LockedKey).ToList();
        var defaults = Policies().Where(p => (string?)p.Attribute("key") == DefaultsKey)
            .ToDictionary(p => (string)p.Attribute("name")!);

        Assert.Equal(locked.Count, defaults.Count);
        foreach (var policy in locked)
        {
            var twin = defaults[(string)policy.Attribute("name")! + "_Default"];
            Assert.Equal(Shape(policy), Shape(twin));
        }
    }

    [Fact]
    public void IntuneCanImportIt()
    {
        var admx = Admx();

        // Self-contained: a "using" namespace must be imported into Intune first.
        Assert.Empty(admx.Descendants(Ns + "using"));
        Assert.DoesNotContain(admx.Descendants().Attributes("ref"), a => a.Value.Contains(':'));
        Assert.Empty(Adml().Descendants(Ns + "comboBox"));
    }

    [Fact]
    public void EveryStringAndPresentationResolves()
    {
        var adml = Adml();
        var strings = adml.Descendants(Ns + "string").Select(s => (string)s.Attribute("id")!).ToHashSet();
        var presentations = adml.Descendants(Ns + "presentation").ToDictionary(p => (string)p.Attribute("id")!);

        foreach (Match reference in Regex.Matches(Admx().ToString(), @"\$\((string|presentation)\.([^)]+)\)"))
        {
            var id = reference.Groups[2].Value;
            Assert.True(
                reference.Groups[1].Value == "string" ? strings.Contains(id) : presentations.ContainsKey(id),
                $"{reference.Value} isn't in VioLens.adml");
        }

        // Each control in a policy's presentation points at one of that policy's elements.
        foreach (var policy in Policies().Where(p => p.Attribute("presentation") is not null))
        {
            var elementIds = policy.Elements(Ns + "elements").Elements().Select(e => (string)e.Attribute("id")!).ToHashSet();
            var presentationId = Regex.Match((string)policy.Attribute("presentation")!, @"presentation\.([^)]+)").Groups[1].Value;
            var refIds = presentations[presentationId].Elements().Select(e => (string)e.Attribute("refId")!).ToList();

            Assert.Equal(elementIds.Order(), refIds.Order());
        }
    }
}
