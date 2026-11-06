using Violens.AppCore.Capture;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.AppCore.Tests;

/// <summary>
/// VioLens Capture's logic. Start isn't tested here: it changes the
/// computer's proxy setting and trusts a certificate.
/// </summary>
public class CaptureSessionTests
{
    private static void Add(SessionStore store, string host) =>
        store.Add(host, DateTimeOffset.UtcNow,
            new CapturedRequest("GET", "/", "HTTP/1.1", [("Host", host)], []),
            new CapturedResponse("HTTP/1.1", 200, "OK", [], []));

    [Fact]
    public void DescribesWhatWasCaptured()
    {
        using var capture = new CaptureSession();
        Assert.Equal("Nothing captured yet.", CaptureSession.Describe(capture.Sessions.Snapshot()));

        Add(capture.Sessions, "example.test");
        Assert.Equal("1 request to 1 site.", CaptureSession.Describe(capture.Sessions.Snapshot()));

        Add(capture.Sessions, "EXAMPLE.test");
        Add(capture.Sessions, "cdn.example.test");
        Assert.Equal("3 requests to 2 sites.", CaptureSession.Describe(capture.Sessions.Snapshot()));
    }

    [Fact]
    public async Task SavesASazTheMainAppCanOpen()
    {
        using var capture = new CaptureSession();
        Add(capture.Sessions, "example.test");
        Add(capture.Sessions, "api.example.test");
        var path = Path.Combine(Path.GetTempPath(), $"violens-capture-{Guid.NewGuid():N}.saz");
        try
        {
            Assert.Equal(2, capture.Save(path));

            var reopened = new SessionStore();
            var result = await SazReader.ImportAsync(path, reopened);
            Assert.Equal(2, result.Imported);
            Assert.Equal(["example.test", "api.example.test"], reopened.Snapshot().Select(s => s.Host));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StopWithoutStartDoesNothing()
    {
        using var capture = new CaptureSession();

        capture.Stop();

        Assert.False(capture.IsCapturing);
        Assert.Null(capture.Port);
        Assert.False(capture.TrustedRoot);
    }
}
