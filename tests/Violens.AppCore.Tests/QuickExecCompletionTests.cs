using Violens.AppCore.QuickExec;
using Xunit;

namespace Violens.AppCore.Tests;

public class QuickExecCompletionTests
{
    private static IEnumerable<string> Texts(string typed) => QuickExecCompletion.Suggest(typed).Select(s => s.Text);

    [Fact]
    public void SuggestsCommandsStartingWithWhatsTyped()
    {
        Assert.Equal(["select ", "start", "stop", "save "], Texts("s"));
        Assert.Equal(["bpu ", "bpafter ", "bps ", "bpm ", "bpv "], Texts("bp"));
        Assert.Equal(["cls", "clear"], Texts("CL"));
    }

    [Fact]
    public void ACompleteWordIsntSuggestedAgain()
    {
        Assert.Empty(Texts("stop"));
        Assert.Equal(["bpafter "], Texts("bpa"));
    }

    [Fact]
    public void SymbolCommandsExplainThemselvesUntilTheTextStarts()
    {
        var suggestion = Assert.Single(QuickExecCompletion.Suggest("?"));
        Assert.Equal("?text", suggestion.Usage);
        Assert.Empty(Texts("?login"));
    }

    [Fact]
    public void SuggestsArgumentsForSelectAllbutAndKeep()
    {
        Assert.Equal(["select json", "select javascript"], Texts("select j"));
        Assert.Contains("allbut image", Texts("allbut "));
        Assert.Equal(["keep all"], Texts("keep a"));
        Assert.Empty(Texts("keep all"));
        Assert.Empty(Texts("bpu exa"));
    }

    [Fact]
    public void NothingForAnEmptyBoxOrAnUnknownWord()
    {
        Assert.Empty(Texts(""));
        Assert.Empty(Texts("   "));
        Assert.Empty(Texts("xyz"));
    }

    [Fact]
    public void AtMostEightSuggestions()
    {
        Assert.True(QuickExecCompletion.Suggest("select ").Count <= QuickExecCompletion.MaxSuggestions);
    }
}
