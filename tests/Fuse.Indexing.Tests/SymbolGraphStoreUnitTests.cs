using Fuse.Indexing;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class SymbolGraphStoreUnitTests
{
    [Fact]
    public void BuildEdgeId_is_stable_for_same_components()
    {
        var first = SymbolGraphStore.BuildEdgeId("from", "to", "calls", 42);
        var second = SymbolGraphStore.BuildEdgeId("from", "to", "calls", 42);

        Assert.Equal(first, second);
        Assert.StartsWith("edge:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEdgeId_differs_when_evidence_file_changes()
    {
        var withFile = SymbolGraphStore.BuildEdgeId("from", "to", "calls", 1);
        var withoutFile = SymbolGraphStore.BuildEdgeId("from", "to", "calls", null);

        Assert.NotEqual(withFile, withoutFile);
    }
}
