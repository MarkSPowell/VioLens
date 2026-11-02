using Violens.ProxyHost.Client;
using Violens.Tests.Shared;
using Xunit;

namespace Violens.ProxyHost.Tests;

/// <summary>Violens.ProxyHost.Client's public API, frozen at 1.0 (see tests/Shared/PublicApi.cs).</summary>
public class PublicApiTests
{
    [Fact]
    public void PublicApiIsUnchanged() => PublicApi.Verify(typeof(ProxyHostClient).Assembly);
}
