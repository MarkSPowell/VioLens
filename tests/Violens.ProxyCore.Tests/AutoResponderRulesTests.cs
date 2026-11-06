using Violens.ProxyCore.AutoResponder;
using Xunit;

namespace Violens.ProxyCore.Tests;

public class AutoResponderRulesTests
{
    private const string Url = "https://api.example.com/widgets/42?verbose=1";

    [Fact]
    public void Evaluate_DisabledGlobally_AlwaysPassesThrough()
    {
        var rules = new AutoResponderRules { IsEnabled = false };
        rules.Rules.Add(Rule("*", "*drop"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Null(outcome.FinalActionKind);
    }

    [Fact]
    public void Evaluate_ADisabledRule_IsSkippedAsIfAbsent()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*drop", isEnabled: false));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Null(outcome.FinalActionKind);
    }

    [Theory]
    [InlineData("widgets")] // plain substring, case-insensitive
    [InlineData("WIDGETS")]
    [InlineData("api.example.com/*")] // wildcard prefix
    [InlineData("EXACT:https://api.example.com/widgets/42?verbose=1")] // exact, case-sensitive
    [InlineData("regex:^https://api\\.example\\.com/widgets/\\d+")]
    public void Evaluate_MatchingPatterns_FireTheRule(string pattern)
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule(pattern, "*drop"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Equal(AutoResponderActionKind.Drop, outcome.FinalActionKind);
    }

    [Fact]
    public void Evaluate_ExactIsCaseSensitiveUnlikeThePlainLiteralForm()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("EXACT:HTTPS://API.EXAMPLE.COM/WIDGETS/42?VERBOSE=1", "*drop"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Null(outcome.FinalActionKind);
    }

    [Fact]
    public void Evaluate_NotInvertsTheInnerMatch()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("NOT:gadgets", "*drop"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Equal(AutoResponderActionKind.Drop, outcome.FinalActionKind);
    }

    [Fact]
    public void Evaluate_MethodPrefixRestrictsToThatVerb()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("METHOD:POST widgets", "*drop"));

        Assert.Null(rules.Evaluate("GET", Url).FinalActionKind);
        Assert.Equal(AutoResponderActionKind.Drop, rules.Evaluate("POST", Url).FinalActionKind);
    }

    [Fact]
    public void Evaluate_UnprefixedAction_ServesALocalFile()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", @"C:\mocks\widget.json"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Equal(AutoResponderActionKind.ServeFile, outcome.FinalActionKind);
        Assert.Equal(@"C:\mocks\widget.json", outcome.Text);
    }

    [Theory]
    [InlineData("http://localhost:9000/fixtures/widget.json")]
    [InlineData("https://localhost:9000/fixtures/widget.json")]
    public void Evaluate_UrlAction_IsProxyUrlNotServeFile(string action)
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", action));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Equal(AutoResponderActionKind.ProxyUrl, outcome.FinalActionKind);
        Assert.Equal(action, outcome.Text);
    }

    [Fact]
    public void Evaluate_RedirAction_CarriesTheRedirectTarget()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*redir:https://mocks.example.com/widgets"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Equal(AutoResponderActionKind.Redirect, outcome.FinalActionKind);
        Assert.Equal("https://mocks.example.com/widgets", outcome.Text);
    }

    [Theory]
    [InlineData("*reset", AutoResponderActionKind.Reset)]
    [InlineData("*drop", AutoResponderActionKind.Drop)]
    [InlineData("*CORSPreflightAllow", AutoResponderActionKind.CorsPreflightAllow)]
    public void Evaluate_ConnectionAndCorsActions_MapToTheirKind(string action, AutoResponderActionKind expected)
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", action));

        Assert.Equal(expected, rules.Evaluate("GET", Url).FinalActionKind);
    }

    [Fact]
    public void Evaluate_Bpu_ForcesBreakBeforeRequestWithoutAnsweringItself()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*bpu"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.True(outcome.ForceBreakpointBeforeRequest);
        Assert.False(outcome.ForceBreakpointAfterResponse);
    }

    [Fact]
    public void Evaluate_Bpafter_ForcesBreakAfterResponse()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*bpafter"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.True(outcome.ForceBreakpointAfterResponse);
        Assert.False(outcome.ForceBreakpointBeforeRequest);
    }

    [Fact]
    public void Evaluate_NonFinalActionsAccumulateAndEvaluationContinues()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*delay:100"));
        rules.Rules.Add(Rule("widgets", "*header:X-Mock=1"));
        rules.Rules.Add(Rule("widgets", "*delay:250"));
        // No final action anywhere -- passthrough, but carrying everything
        // accumulated along the way.

        var outcome = rules.Evaluate("GET", Url);

        Assert.Null(outcome.FinalActionKind);
        Assert.Equal(350, outcome.DelayMilliseconds);
        var header = Assert.Single(outcome.HeadersToSet);
        Assert.Equal(("X-Mock", "1"), header);
    }

    [Fact]
    public void Evaluate_NonFinalEffectsBeforeAFinalActionAreStillCarried()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*delay:500"));
        rules.Rules.Add(Rule("widgets", "*flag:x-test=slow"));
        rules.Rules.Add(Rule("widgets", "*drop"));
        rules.Rules.Add(Rule("widgets", "*reset")); // never reached -- Drop above is final

        var outcome = rules.Evaluate("GET", Url);

        Assert.Equal(AutoResponderActionKind.Drop, outcome.FinalActionKind);
        Assert.Equal(500, outcome.DelayMilliseconds);
        Assert.Equal(("x-test", "slow"), Assert.Single(outcome.FlagsToSet));
    }

    [Fact]
    public void Evaluate_Exit_StopsEvaluationWithoutProducingAResponseOfItsOwn()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*header:X-Mock=1"));
        rules.Rules.Add(Rule("widgets", "*exit"));
        rules.Rules.Add(Rule("widgets", "*drop")); // never reached

        var outcome = rules.Evaluate("GET", Url);

        Assert.Null(outcome.FinalActionKind);
        Assert.Equal(("X-Mock", "1"), Assert.Single(outcome.HeadersToSet));
    }

    [Fact]
    public void Evaluate_FirstMatchingFinalRuleWins_LaterRulesAreIgnored()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("widgets", "*redir:https://first.example.com"));
        rules.Rules.Add(Rule("widgets", "*drop"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Equal(AutoResponderActionKind.Redirect, outcome.FinalActionKind);
        Assert.Equal("https://first.example.com", outcome.Text);
    }

    [Fact]
    public void Evaluate_NoRuleMatches_PassesThroughWithNoAccumulatedEffects()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(Rule("gadgets", "*drop"));

        var outcome = rules.Evaluate("GET", Url);

        Assert.Same(AutoResponderOutcome.PassThrough, outcome);
    }

    [Theory]
    [InlineData(false, 0, false)] // disabled globally
    [InlineData(true, 0, false)]  // enabled, but no rules
    [InlineData(true, 1, true)]   // enabled, has a rule
    public void AnyActive_ReflectsGlobalEnableAndRuleCount(bool isEnabled, int ruleCount, bool expected)
    {
        var rules = new AutoResponderRules { IsEnabled = isEnabled };
        for (var i = 0; i < ruleCount; i++)
        {
            rules.Rules.Add(Rule("widgets", "*drop"));
        }

        Assert.Equal(expected, rules.AnyActive);
    }

    private static AutoResponderRule Rule(string matchPattern, string action, bool isEnabled = true) =>
        new() { MatchPattern = matchPattern, Action = action, IsEnabled = isEnabled };

    [Fact]
    public void ARegexRuleThatTakesTooLongDoesntMatch()
    {
        // Catastrophic backtracking: without a timeout this runs for ages.
        var match = AutoResponderMatch.Parse("regex:^(a+)+$");
        var url = "https://example.test/" + new string('a', 40) + "!";
        var timer = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(match.Matches("GET", url));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
    }

    [Fact]
    public void AWildcardRuleWithManyStarsStaysFast()
    {
        var match = AutoResponderMatch.Parse("*a*a*a*a*a*a*a*a*a*a*a*a*a*a*b");
        var url = "https://example.test/" + new string('a', 20_000);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(match.Matches("GET", url));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    [Theory]
    [InlineData("*/widgets/*", true)]
    [InlineData("API.EXAMPLE.com/*/42", true)]
    [InlineData("*/gadgets/*", false)]
    [InlineData("widgets*verbose", true)]
    [InlineData("verbose*widgets", false)]
    public void WildcardRulesStillMatchAnywhereInTheUrl(string pattern, bool matches) =>
        Assert.Equal(matches, AutoResponderMatch.Parse(pattern).Matches("GET", Url));

    [Fact]
    public void ParseCachedGivesTheSameMatchForTheSamePattern() =>
        Assert.Same(AutoResponderMatch.ParseCached("regex:widgets/\\d+"), AutoResponderMatch.ParseCached("regex:widgets/\\d+"));
}
