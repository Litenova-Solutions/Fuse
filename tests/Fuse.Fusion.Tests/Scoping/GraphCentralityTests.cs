using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class GraphCentralityTests
{
    // Builds a graph where Core.cs declares a type referenced by three other files (central), while Leaf.cs
    // declares a type no one references (peripheral).
    private static DependencyGraph BuildGraph()
    {
        var fileReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = ["CoreType"],
            ["B.cs"] = ["CoreType"],
            ["C.cs"] = ["CoreType"],
            ["Core.cs"] = [],
            ["Leaf.cs"] = [],
        };
        var declaredTypes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Core.cs"] = ["CoreType"],
            ["Leaf.cs"] = ["LeafType"],
            ["A.cs"] = ["A"],
            ["B.cs"] = ["B"],
            ["C.cs"] = ["C"],
        };
        var typeIndex = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CoreType"] = ["Core.cs"],
            ["LeafType"] = ["Leaf.cs"],
        };
        var typeReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CoreType"] = ["A.cs", "B.cs", "C.cs"],
        };
        return new DependencyGraph(fileReferences, typeIndex, declaredTypes, typeReferences);
    }

    [Fact]
    public void Compute_RanksCentralFileHighest()
    {
        var centrality = GraphCentrality.Compute(BuildGraph());

        Assert.True(centrality.TryGetValue("Core.cs", out var core));
        Assert.Equal(1.0, core); // most depended-upon -> normalized to 1
        // PageRank gives every node a floor score, so the peripheral file is present but ranks well below the
        // central one (in-degree centrality omitted it entirely).
        Assert.True(centrality.TryGetValue("Leaf.cs", out var leaf));
        Assert.True(leaf < core);
    }

    [Fact]
    public void Compute_PageRankRewardsBeingReferencedByCentralFiles()
    {
        // Hub is referenced by Core (itself heavily referenced) and by nobody-else; Lonely is referenced only
        // by an otherwise-peripheral file. PageRank, unlike raw in-degree (both have one referrer), ranks Hub
        // above Lonely because the importance flows from the central Core.
        var fileReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.cs"] = ["CoreType"],
            ["B.cs"] = ["CoreType"],
            ["C.cs"] = ["CoreType"],
            ["Core.cs"] = ["HubType"],
            ["Edge.cs"] = ["LonelyType"],
        };
        var declaredTypes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Core.cs"] = ["CoreType"],
            ["Hub.cs"] = ["HubType"],
            ["Lonely.cs"] = ["LonelyType"],
            ["A.cs"] = ["A"],
            ["B.cs"] = ["B"],
            ["C.cs"] = ["C"],
            ["Edge.cs"] = ["Edge"],
        };
        var typeIndex = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var typeReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CoreType"] = ["A.cs", "B.cs", "C.cs"],
            ["HubType"] = ["Core.cs"],
            ["LonelyType"] = ["Edge.cs"],
        };

        var centrality = GraphCentrality.Compute(
            new DependencyGraph(fileReferences, typeIndex, declaredTypes, typeReferences));

        Assert.True(centrality["Hub.cs"] > centrality["Lonely.cs"]);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var graph = BuildGraph();
        var first = GraphCentrality.Compute(graph);
        var second = GraphCentrality.Compute(graph);

        Assert.Equal(first.Count, second.Count);
        foreach (var (key, value) in first)
            Assert.Equal(value, second[key]);
    }

    [Fact]
    public void Compute_EmptyGraph_ReturnsEmpty()
    {
        var empty = new DependencyGraph(
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, IReadOnlyList<string>>());

        Assert.Empty(GraphCentrality.Compute(empty));
    }

    [Fact]
    public void Expand_WithCentralityWeight_RanksCentralFileAboveEqualRelevancePeer()
    {
        var graph = BuildGraph();
        var resolver = new FocusSeedResolver(new Plugins.Abstractions.CapabilityRegistry<Plugins.Abstractions.Dependencies.ITypeNameLocator>([]));
        var centrality = GraphCentrality.Compute(graph);

        // Two seeds at identical relevance: the central one must score higher with the prior on.
        var seeds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Core.cs"] = 1.0,
            ["Leaf.cs"] = 1.0,
        };

        var withPrior = resolver.Expand(graph, seeds,
            new ExpansionOptions(Depth: 0, Centrality: centrality, CentralityWeight: 0.5));
        Assert.True(withPrior.Scores["Core.cs"] > withPrior.Scores["Leaf.cs"]);

        // Weight 0 reproduces equal scores (prior ordering unchanged).
        var noPrior = resolver.Expand(graph, seeds,
            new ExpansionOptions(Depth: 0, Centrality: centrality, CentralityWeight: 0.0));
        Assert.Equal(noPrior.Scores["Core.cs"], noPrior.Scores["Leaf.cs"]);
    }
}
