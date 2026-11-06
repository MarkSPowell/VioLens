using Violens.Tests.Shared;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>ProxyCore's public API, frozen at 1.0 (see tests/Shared/PublicApi.cs).</summary>
public class PublicApiTests
{
    [Fact]
    public void PublicApiIsUnchanged() => PublicApi.Verify(typeof(ProxyEngine).Assembly);
}
