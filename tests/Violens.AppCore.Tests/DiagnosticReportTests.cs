using Violens.AppCore.Diagnostics;
using Violens.ProxyCore.Certificates;
using Xunit;

namespace Violens.AppCore.Tests;

public class DiagnosticReportTests
{
    [Fact]
    public void ReportsCaptureStateAndSettings()
    {
        var text = DiagnosticReport.Format(
            new DiagnosticSnapshot
            {
                Version = "0.3.0-preview.1",
                Port = 8888,
                RegisteredAsSystemProxy = true,
                Upstream = "Upstream: connecting directly.",
                Http2Enabled = true,
                SessionCount = 12,
                LastStatus = "Listening on 127.0.0.1:8888",
            },
            ["Wi-Fi (Wireless80211)"],
            "none set");

        Assert.Contains("VioLens: 0.3.0-preview.1", text);
        Assert.Contains("Capturing: yes, on 127.0.0.1, port 8888", text);
        Assert.Contains("System proxy: registered", text);
        Assert.Contains("Upstream: connecting directly.", text);
        Assert.Contains("HTTP/2: on", text);
        Assert.Contains("Root certificate: not created yet", text);
        Assert.Contains("Sessions: 12", text);
        Assert.Contains("  Wi-Fi (Wireless80211)", text);
        Assert.Contains("Last status: Listening on 127.0.0.1:8888", text);
    }

    [Fact]
    public void LeavesOutTheMachineName()
    {
        // The root's subject carries the machine name; only its thumbprint is reported.
        using var root = CertificateAuthority.GenerateRoot(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1), CertificateAuthority.SubjectPrefix);

        var text = DiagnosticReport.Format(
            new DiagnosticSnapshot { Version = "1.0.0", RootCertificate = root }, [], "none set");

        Assert.Contains("Capturing: no", text);
        Assert.Contains(root.Thumbprint, text);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUntrustedRootSaysSo()
    {
        using var root = CertificateAuthority.GenerateRoot(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1), "DO_NOT_TRUST_ViolensTestRoot");

        Assert.StartsWith("NOT trusted", DiagnosticReport.DescribeRoot(root));
    }
}
