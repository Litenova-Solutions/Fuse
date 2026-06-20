using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class FocusSeedResolverExpandTests
{
    private static readonly FocusSeedResolver Resolver =
        new(new CapabilityRegistry<ITypeNameLocator>([]));

    // a.cs declares A and references B; b.cs declares B and references C; c.cs declares C.
    // d.cs declares D and references A (a dependent of a.cs).
    private static DependencyGraph BuildGraph()
    {
        var fileReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.cs"] = ["B"],
            ["b.cs"] = ["C"],
            ["c.cs"] = [],
            ["d.cs"] = ["A"],
        };
        var typeIndex = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["A"] = ["a.cs"],
            ["B"] = ["b.cs"],
            ["C"] = ["c.cs"],
            ["D"] = ["d.cs"],
        };
        var declaredTypes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.cs"] = ["A"],
            ["b.cs"] = ["B"],
            ["c.cs"] = ["C"],
            ["d.cs"] = ["D"],
        };
        var typeReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["A"] = ["d.cs"],
            ["B"] = ["a.cs"],
            ["C"] = ["b.cs"],
        };
        return new DependencyGraph(fileReferences, typeIndex, declaredTypes, typeReferences);
    }

    [Fact]
    public void Expand_ForwardOnly_PullsDependencies()
    {
        var graph = BuildGraph();
        var result = Resolver.Expand(
            graph,
            new Dictionary<string, double> { ["a.cs"] = 1.0 },
            new ExpansionOptions(Depth: 2, FollowReferences: true, FollowDependents: false));

        Assert.Contains("a.cs", result.IncludedPaths);
        Assert.Contains("b.cs", result.IncludedPaths);
        Assert.Contains("c.cs", result.IncludedPaths);
        Assert.DoesNotContain("d.cs", result.IncludedPaths); // d depends on a, not the reverse
    }

    [Fact]
    public void Expand_WithDependents_PullsReverseEdges()
    {
        var graph = BuildGraph();
        var result = Resolver.Expand(
            graph,
            new Dictionary<string, double> { ["a.cs"] = 1.0 },
            new ExpansionOptions(Depth: 1, FollowReferences: true, FollowDependents: true));

        Assert.Contains("a.cs", result.IncludedPaths);
        Assert.Contains("b.cs", result.IncludedPaths); // forward dependency
        Assert.Contains("d.cs", result.IncludedPaths); // reverse dependent
    }

    [Fact]
    public void Expand_ScoresDecayWithHops()
    {
        var graph = BuildGraph();
        var result = Resolver.Expand(
            graph,
            new Dictionary<string, double> { ["a.cs"] = 1.0 },
            new ExpansionOptions(Depth: 2, FollowReferences: true, FollowDependents: false, HopDecay: 0.5));

        Assert.Equal(1.0, result.Scores["a.cs"]);
        Assert.Equal(0.5, result.Scores["b.cs"]);
        Assert.Equal(0.25, result.Scores["c.cs"]);
    }

    [Fact]
    public void Expand_BudgetGate_AdmitsSeedsAndStopsAtBudget()
    {
        var graph = BuildGraph();
        var costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.cs"] = 100,
            ["b.cs"] = 100,
            ["c.cs"] = 100,
        };

        var result = Resolver.Expand(
            graph,
            new Dictionary<string, double> { ["a.cs"] = 1.0 },
            new ExpansionOptions(
                Depth: 2,
                FollowReferences: true,
                FollowDependents: false,
                TokenBudget: 250,
                TokenCosts: costs));

        // Seed (100) plus the first neighbour (100) fit within 250; the second neighbour (300) does not.
        Assert.Contains("a.cs", result.IncludedPaths);
        Assert.Contains("b.cs", result.IncludedPaths);
        Assert.DoesNotContain("c.cs", result.IncludedPaths);
    }

    [Fact]
    public void Expand_SeedAlwaysAdmittedOverBudget()
    {
        var graph = BuildGraph();
        var costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a.cs"] = 9999 };

        var result = Resolver.Expand(
            graph,
            new Dictionary<string, double> { ["a.cs"] = 1.0 },
            new ExpansionOptions(Depth: 1, TokenBudget: 10, TokenCosts: costs));

        Assert.Contains("a.cs", result.IncludedPaths);
    }
}
