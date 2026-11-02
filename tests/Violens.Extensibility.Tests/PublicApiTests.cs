using Violens.Extensibility.Inspection;
using Violens.Tests.Shared;
using Xunit;

namespace Violens.Extensibility.Tests;

/// <summary>The inspector contract extensions build against, frozen at 1.0 (see tests/Shared/PublicApi.cs).</summary>
public class PublicApiTests
{
    [Fact]
    public void PublicApiIsUnchanged() => PublicApi.Verify(typeof(IInspector).Assembly);
}
