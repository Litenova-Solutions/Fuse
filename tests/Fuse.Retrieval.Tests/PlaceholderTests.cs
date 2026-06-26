using Xunit;

namespace Fuse.Retrieval.Tests;

// Phase 0 foundation smoke test: confirms the project is discovered and executed by the
// test runner. Replaced by candidate/expansion/packing tests in Phase 5.
public sealed class PlaceholderTests
{
    [Fact]
    public void ProjectIsWiredIntoTheTestRun()
    {
        Assert.True(true);
    }
}
