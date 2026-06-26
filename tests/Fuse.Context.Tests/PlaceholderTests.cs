using Xunit;

namespace Fuse.Context.Tests;

// Phase 0 foundation smoke test: confirms the project is discovered and executed by the
// test runner. Replaced by rendering/manifest/provenance tests in Phase 7.
public sealed class PlaceholderTests
{
    [Fact]
    public void ProjectIsWiredIntoTheTestRun()
    {
        Assert.True(true);
    }
}
