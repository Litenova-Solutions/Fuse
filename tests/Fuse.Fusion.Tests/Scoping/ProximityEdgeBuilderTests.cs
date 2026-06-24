using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class ProximityEdgeBuilderTests
{
    [Fact]
    public void Build_LinksTestToImplementationAcrossDirectories()
    {
        var edges = ProximityEdgeBuilder.Build([
            "src/Services/OrderService.cs",
            "tests/Services/OrderServiceTests.cs",
            "src/Other.cs",
        ]);

        Assert.True(edges.ContainsKey("src/Services/OrderService.cs"));
        Assert.Contains("tests/Services/OrderServiceTests.cs", edges["src/Services/OrderService.cs"]);
        Assert.Contains("src/Services/OrderService.cs", edges["tests/Services/OrderServiceTests.cs"]);
        Assert.False(edges.ContainsKey("src/Other.cs")); // no sibling, no edge
    }

    [Fact]
    public void Build_DropsGenericStemSharedByManyFiles()
    {
        // Five files sharing the stem "program" are treated as generic and produce no edges.
        var edges = ProximityEdgeBuilder.Build([
            "a/Program.cs", "b/Program.cs", "c/Program.cs", "d/Program.cs", "e/Program.cs",
        ]);

        Assert.Empty(edges);
    }

    [Fact]
    public void Build_IgnoresVeryShortStem()
    {
        // Stem "io" is below the minimum length, so IO.cs and IOTests.cs are not linked.
        var edges = ProximityEdgeBuilder.Build(["IO.cs", "IOTests.cs"]);

        Assert.Empty(edges);
    }

    [Fact]
    public void Build_LinksSameStemSiblings()
    {
        // Implementation and a non-test sibling sharing the stem link too.
        var edges = ProximityEdgeBuilder.Build([
            "Order.cs",
            "OrderTests.cs",
        ]);

        Assert.Contains("OrderTests.cs", edges["Order.cs"]);
    }
}
