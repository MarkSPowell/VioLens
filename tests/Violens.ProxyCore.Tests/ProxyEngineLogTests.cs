using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>What <see cref="ProxyEngine.Log"/> does to the text it's given.</summary>
public class ProxyEngineLogTests
{
    [Fact]
    public void ControlCharactersAreWrittenOut()
    {
        // A URL carrying a line break and a terminal escape.
        var escaped = ProxyEngine.EscapeControlCharacters("GET /a\r\n[proxy] fake line\u001b[2J\u0085\u2028");

        Assert.Equal("GET /a\\x0d\\x0a[proxy] fake line\\x1b[2J\\x85\\u2028", escaped);
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        const string text = "GET https://example.test/ünïcödé\twith a tab";

        Assert.Same(text, ProxyEngine.EscapeControlCharacters(text));
    }
}
