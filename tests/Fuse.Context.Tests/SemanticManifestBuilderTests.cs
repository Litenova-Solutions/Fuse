using Fuse.Context;
using Fuse.Retrieval;
using Xunit;

namespace Fuse.Context.Tests;

// P7.2: semantic manifest preamble and per-file provenance.
public sealed class SemanticManifestBuilderTests
{
    [Fact]
    public void ManifestListsSeedsAndImpactWithProvenance()
    {
        var plan = SamplePlan();

        var manifest = SemanticManifestBuilder.Build(plan, root: "/repo", changedSince: "origin/main");

        Assert.Contains("fuse:semantic-context", manifest);
        Assert.Contains("mode: review", manifest);
        Assert.Contains("changedSince: origin/main", manifest);
        Assert.Contains("seeds:", manifest);
        Assert.Contains("src/OrderService.cs (changed)", manifest);
        Assert.Contains("semantic impact:", manifest);
        Assert.Contains("src/IOrderService.cs [di-implementation]", manifest);
        Assert.Contains("di_resolves_to", manifest);
    }

    [Fact]
    public void ManifestIncludesNotesWhenWarningsPresent()
    {
        var plan = SamplePlan() with { Warnings = ["3 files dropped to fit the budget."] };

        var manifest = SemanticManifestBuilder.Build(plan);

        Assert.Contains("notes:", manifest);
        Assert.Contains("dropped to fit the budget", manifest);
    }

    [Fact]
    public void ProvenanceFormatProducesBlock()
    {
        var block = ProvenanceFormatter.Format(["<- di_resolves_to (hop 1)", "<- di_injects (hop 2)"]);

        Assert.Contains("included via:", block);
        Assert.Contains("di_resolves_to", block);
        Assert.Contains("di_injects", block);
    }

    [Fact]
    public void ProvenanceSummaryIsSeedWhenNoEdges()
    {
        Assert.Equal("seed", ProvenanceFormatter.Summarize(["changed file"]));
    }

    private static ContextPlan SamplePlan()
    {
        var items = new[]
        {
            new ContextPlanItem("src/OrderService.cs", "type:App.OrderService", "changed", RenderTier.Reduced, 1.0, 40, true,
                ["changed symbol"], ["changed symbol"]),
            new ContextPlanItem("src/IOrderService.cs", "type:App.IOrderService", "di-implementation", RenderTier.Reduced, 0.6, 12, false,
                ["<- di_resolves_to (hop 1)"], ["<- di_resolves_to (hop 1)"]),
        };

        return new ContextPlan("review", items, [], 52, []);
    }
}
