namespace Violens.ProxyCore.AutoResponder;

/// <summary>An ordered list of AutoResponder rules, evaluated top to bottom.</summary>
public sealed class AutoResponderRules
{
    /// <summary>
    /// The AutoResponder's on/off switch. Off by default: a rule left on by
    /// accident would silently change real traffic next time.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>In priority order.</summary>
    public List<AutoResponderRule> Rules { get; } = [];

    /// <summary>A cheap check before evaluating each request.</summary>
    public bool AnyActive => IsEnabled && Rules.Count > 0;

    /// <summary>
    /// Evaluates enabled rules in order. Non-final actions accumulate and
    /// evaluation continues; the first final action ends it.
    /// </summary>
    public AutoResponderOutcome Evaluate(string method, string url)
    {
        if (!IsEnabled)
        {
            return AutoResponderOutcome.PassThrough;
        }

        var delayMilliseconds = 0;
        var headersToSet = new List<(string Name, string Value)>();
        var flagsToSet = new List<(string Name, string Value)>();

        foreach (var rule in Rules)
        {
            if (!rule.IsEnabled)
            {
                continue;
            }

            var match = AutoResponderMatch.ParseCached(rule.MatchPattern);
            if (!match.Matches(method, url))
            {
                continue;
            }

            var action = AutoResponderAction.Parse(rule.Action);
            switch (action.Kind)
            {
                case AutoResponderActionKind.Delay:
                    delayMilliseconds += action.Milliseconds;
                    continue;

                case AutoResponderActionKind.SetHeader:
                    headersToSet.Add((action.Name!, action.Value!));
                    continue;

                case AutoResponderActionKind.SetFlag:
                    flagsToSet.Add((action.Name!, action.Value!));
                    continue;

                case AutoResponderActionKind.Exit:
                    // Final, but with no response of its own: pass through.
                    return AutoResponderOutcome.Passthrough(delayMilliseconds, headersToSet, flagsToSet);

                case AutoResponderActionKind.BreakBeforeRequest:
                    return AutoResponderOutcome.Breakpoint(beforeRequest: true, delayMilliseconds, headersToSet, flagsToSet);

                case AutoResponderActionKind.BreakAfterResponse:
                    return AutoResponderOutcome.Breakpoint(beforeRequest: false, delayMilliseconds, headersToSet, flagsToSet);

                default:
                    return AutoResponderOutcome.Final(action.Kind, action.Text, delayMilliseconds, headersToSet, flagsToSet);
            }
        }

        return AutoResponderOutcome.Passthrough(delayMilliseconds, headersToSet, flagsToSet);
    }
}
