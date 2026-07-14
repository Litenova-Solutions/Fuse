using Fuse.Retrieval;
using Fuse.Scoping;
using Xunit;

namespace Fuse.Retrieval.Tests;

public sealed class GraphCentralityTests
{
    [Fact]
    public void NormalizedPageRank_RanksCentralNodeHighest()
    {
        var outEdges = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = new[] { "Core.cs" },
            ["B.cs"] = new[] { "Core.cs" },
            ["C.cs"] = new[] { "Core.cs" },
            ["Core.cs"] = Array.Empty<string>(),
            ["Leaf.cs"] = Array.Empty<string>(),
        };

        var centrality = GraphCentrality.NormalizedPageRank(outEdges);

        Assert.True(centrality.TryGetValue("Core.cs", out var core));
        Assert.Equal(1.0, core);
        Assert.True(centrality.TryGetValue("Leaf.cs", out var leaf));
        Assert.True(leaf < core);
    }

    [Fact]
    public void NormalizedDegree_RanksHubAboveLeaf()
    {
        var degree = GraphCentrality.NormalizedDegree(
        [
            ("type:ConsumerA", "type:Hub"),
            ("type:ConsumerB", "type:Hub"),
        ]);

        Assert.Equal(1.0, degree["type:Hub"]);
        Assert.True(degree["type:ConsumerA"] < degree["type:Hub"]);
    }

    [Fact]
    public void BlendRankScore_ZeroWeight_ReturnsTraversalScore()
    {
        var centrality = new Dictionary<string, double> { ["a.cs"] = 1.0 };
        Assert.Equal(0.5, GraphCentrality.BlendRankScore(0.5, "a.cs", centrality, 0.0));
    }

    [Fact]
    public void ApplyRetrievalPrior_IsCappedAtOne()
    {
        Assert.Equal(1.0, GraphCentrality.ApplyRetrievalPrior(0.95, 1.0));
    }

    [Fact]
    public void ContextPlanPacker_DropsOptionalFilesPastBudget()
    {
        var warnings = new List<string>();
        var items = new List<ContextPlanItem>
        {
            new("a.cs", null, "exact-seed", RenderTier.Reduced, 1.0, 60, true, [], []),
            new("b.cs", null, "dependency", RenderTier.Skeleton, 0.5, 50, false, [], []),
        };

        var packed = ContextPlanPacker.Pack(items, 80, warnings);

        Assert.Single(packed);
        Assert.Contains("dropped", warnings[0], StringComparison.OrdinalIgnoreCase);
    }
}
