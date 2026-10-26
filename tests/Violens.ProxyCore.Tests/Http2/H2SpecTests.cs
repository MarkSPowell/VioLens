using System.Diagnostics;
using System.Text;
using Violens.ProxyCore.Http2;
using Xunit;
using Xunit.Abstractions;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary>
/// Runs h2spec (github.com/summerwind/h2spec), the HTTP/2 conformance suite,
/// against <see cref="Http2Connection"/>'s server side. Only when
/// <c>VIOLENS_H2SPEC</c> names the h2spec executable; otherwise it passes
/// without doing anything.
/// </summary>
public class H2SpecTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PassesH2Spec()
    {
        var h2spec = Environment.GetEnvironmentVariable("VIOLENS_H2SPEC");
        if (string.IsNullOrEmpty(h2spec))
        {
            return;
        }

        await using var server = new Http2TestServer(AnswerAsync);
        var start = new ProcessStartInfo(h2spec)
        {
            ArgumentList = { "-h", "127.0.0.1", "-p", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "5", "-j", Path.Combine(Path.GetTempPath(), "violens-h2spec.xml") },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));

        output.WriteLine(await stdout);
        output.WriteLine(await stderr);
        Assert.True(process.ExitCode == 0, "h2spec reported failures; see the test output.");
    }

    /// <summary>Reads the whole request, then answers with a 1 KB body, which h2spec's flow-control cases need.</summary>
    private static async Task AnswerAsync(Http2Stream stream)
    {
        await stream.ReadHeadersAsync(CancellationToken.None);
        await stream.ReadAllDataAsync(CancellationToken.None);
        var body = Encoding.ASCII.GetBytes(new string('x', 1024));
        await stream.WriteHeadersAsync([(":status", "200"), ("content-type", "text/plain"), ("content-length", "1024")], endStream: false, CancellationToken.None);
        await stream.WriteDataAsync(body, endStream: true, CancellationToken.None);
    }
}
