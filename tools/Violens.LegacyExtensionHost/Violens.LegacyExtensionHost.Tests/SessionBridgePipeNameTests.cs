using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Violens.LegacyExtensionHost.Bridge;
using Xunit;

namespace Violens.LegacyExtensionHost.Tests;

/// <summary>The session bridge's pipe name, which another account mustn't be able to take first.</summary>
public sealed class SessionBridgePipeNameTests
{
    [Fact]
    public void TheNameIsntTheFixedOneAnotherAccountCouldTakeFirst()
    {
        var name = SessionBridgeProtocol.PipeName;

        Assert.NotEqual(SessionBridgeProtocol.BasePipeName, name);
        var prefix = SessionBridgeProtocol.BasePipeName + "." + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture) + ".";
        Assert.StartsWith(prefix, name);

        var secret = name.Substring(prefix.Length);
        Assert.Equal(32, secret.Length);
        Assert.True(secret.All(System.Uri.IsHexDigit));
    }

    [Fact]
    public void BothEndsReadTheSameSecretAndADamagedOneIsReplaced()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "violens-bridge-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            // The first end to ask makes it; the other reads the same one.
            var first = SessionBridgeProtocol.AccountSecret(folder);
            Assert.Equal(first, SessionBridgeProtocol.AccountSecret(folder));

            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "legacy-host-bridge.id"), "damaged");
            var replaced = SessionBridgeProtocol.AccountSecret(folder);
            Assert.Equal(32, replaced.Length);
            Assert.Equal(replaced, SessionBridgeProtocol.AccountSecret(folder));
        }
        finally
        {
            System.IO.Directory.Delete(folder, recursive: true);
        }
    }
}
