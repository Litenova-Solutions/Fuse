using Xunit;

namespace Fuse.Semantics.Tests;

// Phase 0 foundation smoke test: confirms the project is discovered and executed by the
// test runner. Replaced by discovery/analyzer fixture tests in Phases 3-4.
public sealed class PlaceholderTests
{
    [Fact]
    public void ProjectIsWiredIntoTheTestRun()
    {
        Assert.True(true);
    }
}
