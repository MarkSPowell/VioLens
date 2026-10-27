using System.Security.Cryptography.X509Certificates;
using System.Text;
using Violens.ProxyCore.AutoResponder;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>The Composer and Replay: sending a request through VioLens's own listener.</summary>
public class ComposerClientTests
{
    private static CapturedRequest Typed(string target, params (string Name, string Value)[] headers) =>
        new("GET", target, "HTTP/1.1", headers, []);

    [Fact]
    public void AnAbsoluteUrlBecomesAPathAndAHostHeader()
    {
        Assert.True(ComposerClient.TryResolve(Typed("https://api.example.test:8443/items?x=1", ("Accept", "*/*")), out var url, out var toSend, out var error));

        Assert.Null(error);
        Assert.Equal("https://api.example.test:8443/items?x=1", url!.ToString());
        Assert.Equal("/items?x=1", toSend!.Target);
        Assert.Equal(("Host", "api.example.test:8443"), toSend.Headers[0]);
        Assert.Contains(("Accept", "*/*"), toSend.Headers);
    }

    [Fact]
    public void APathUsesTheHostHeaderWhichIsKeptInPlace()
    {
        Assert.True(ComposerClient.TryResolve(Typed("/a", ("Accept", "*/*"), ("Host", "example.test")), out var url, out var toSend, out _));

        Assert.Equal("https://example.test/a", url!.ToString());
        Assert.Equal(new[] { ("Accept", "*/*"), ("Host", "example.test") }, toSend!.Headers.ToArray());
    }

    [Fact]
    public void AStaleHostHeaderIsCorrectedToMatchTheUrl()
    {
        Assert.True(ComposerClient.TryResolve(Typed("https://new.example.test/", ("Host", "old.example.test")), out _, out var toSend, out _));

        Assert.Equal(("Host", "new.example.test"), Assert.Single(toSend!.Headers));
    }

    [Theory]
    [InlineData("https://example.test/", "HTTP/2", "HTTP/2")]
    [InlineData("https://example.test/", "HTTP/2.0", "HTTP/2")]
    [InlineData("http://example.test/", "HTTP/2", "HTTP/1.1")]
    [InlineData("https://example.test/", "HTTP/3", "HTTP/1.1")]
    public void HttpTwoIsKeptForHttpsOnly(string target, string version, string expected)
    {
        Assert.True(ComposerClient.TryResolve(Typed(target) with { HttpVersion = version }, out _, out var toSend, out _));

        Assert.Equal(expected, toSend!.HttpVersion);
    }

    [Fact]
    public void AcceptsPlainHttpUrls()
    {
        Assert.True(ComposerClient.TryResolve(Typed("http://example.test:8080/a"), out var url, out var toSend, out var error));

        Assert.Null(error);
        Assert.Equal("http", url!.Scheme);
        Assert.Equal("/a", toSend!.Target);
        Assert.Equal(("Host", "example.test:8080"), Assert.Single(toSend.Headers));
    }

    [Fact]
    public async Task SendsPlainHttpThroughTheListener()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var store = new SessionStore();
        var recorded = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SessionAdded += session => recorded.TrySetResult(session);

        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(new AutoResponderRule { MatchPattern = "composer.invalid", Action = "*redir:http://elsewhere.test/" });

        var leaves = new LeafCertificateProvider(root);
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, leaves, store, autoResponderRules: rules);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.True(ComposerClient.TryResolve(Typed("http://composer.invalid/page"), out var url, out var toSend, out _));

            var response = await ComposerClient.SendAsync(listener.Port, leaves.IssuerCertificateDer, url!, toSend!, timeout.Token);

            Assert.Equal(302, response.StatusCode);
            var session = await recorded.Task.WaitAsync(timeout.Token);
            Assert.Equal("http", session.Scheme);
            Assert.Equal("/page", session.Request.Target);
            Assert.Equal("composer.invalid", ContentDecoder.FindHeader(session.Request.Headers, "Host"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("/no-host", "Host header")]
    [InlineData("ftp://example.test/", "only sends HTTP and HTTPS")]
    [InlineData("not a url", "isn't a URL")]
    public void ExplainsWhatItCantSend(string target, string expected)
    {
        Assert.False(ComposerClient.TryResolve(Typed(target), out _, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task SendsThroughTheListenerWhichRecordsTheSession()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var store = new SessionStore();
        var recorded = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SessionAdded += session => recorded.TrySetResult(session);

        // Answered by the AutoResponder, so nothing leaves this machine; the
        // .invalid host couldn't be reached anyway.
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(new AutoResponderRule { MatchPattern = "composer.invalid", Action = "*redir:https://elsewhere.test/" });

        var leaves = new LeafCertificateProvider(root);
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, leaves, store, autoResponderRules: rules);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var typed = new CapturedRequest(
                "POST", "https://composer.invalid/submit", "HTTP/1.1", [("Content-Type", "text/plain")], Encoding.UTF8.GetBytes("hi"));
            Assert.True(ComposerClient.TryResolve(typed, out var url, out var toSend, out _));

            var response = await ComposerClient.SendAsync(listener.Port, leaves.IssuerCertificateDer, url!, toSend!, timeout.Token);

            Assert.Equal(302, response.StatusCode);
            Assert.Equal("https://elsewhere.test/", ContentDecoder.FindHeader(response.Headers, "Location"));

            var session = await recorded.Task.WaitAsync(timeout.Token);
            Assert.Equal("POST", session.Request.Method);
            Assert.Equal("/submit", session.Request.Target);
            Assert.Equal("hi", Encoding.UTF8.GetString(session.Request.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RefusesAListenerWithADifferentRoot()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        using var otherRoot = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        try
        {
            Assert.True(ComposerClient.TryResolve(Typed("https://x.invalid/"), out var url, out var toSend, out _));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                ComposerClient.SendAsync(listener.Port, otherRoot.Export(X509ContentType.Cert), url!, toSend!, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }
}
