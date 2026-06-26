using Xunit;

namespace Fuse.Indexing.Tests;

// Phase 0 foundation smoke test: confirms the project is discovered and executed by the
// test runner. Replaced by real schema/store tests in Phase 1 (P1.4).
public sealed class PlaceholderTests
{
    [Fact]
    public void ProjectIsWiredIntoTheTestRun()
    {
        Assert.True(true);
    }
}
