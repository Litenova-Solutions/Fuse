using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P6.3: the review preamble explains every non-changed file.
public sealed class ReviewPreambleTests
{
    [Fact]
    public void PreambleListsChangedAndImpactedFiles()
    {
        var plan = SamplePlan();

        var preamble = ReviewPreambleBuilder.Build(plan, "origin/main");

        Assert.Contains("changedSince: origin/main", preamble);
        Assert.Contains("src/OrderService.cs", preamble);
        Assert.Contains("semantic impact:", preamble);
        Assert.Contains("src/OrdersController.cs", preamble);
    }

    [Fact]
    public void EveryNonChangedFileHasAnExplanation()
    {
        var plan = SamplePlan();

        var preamble = ReviewPreambleBuilder.Build(plan, "origin/main");

        // Each impacted (non-changed) file line carries its provenance edge chain.
        foreach (var item in plan.Items.Where(i => i.Role != "changed"))
        {
            Assert.NotEmpty(item.ProvenanceChain);
            var line = preamble.Split('\n').Single(l => l.Contains(item.Path, StringComparison.Ordinal) && l.Contains('[', StringComparison.Ordinal));
            Assert.Contains("hop", line, StringComparison.Ordinal);
        }
    }

    private static ContextPlan SamplePlan()
    {
        var items = new[]
        {
            new ContextPlanItem("src/OrderService.cs", "type:App.OrderService", "changed", RenderTier.Reduced, 1.0, 40, true,
                ["changed symbol"], ["changed symbol"]),
            new ContextPlanItem("src/IOrderService.cs", "type:App.IOrderService", "di-implementation", RenderTier.Reduced, 0.6, 12, false,
                ["<- di_resolves_to (hop 1)"], ["<- di_resolves_to (hop 1)"]),
            new ContextPlanItem("src/OrdersController.cs", "type:App.OrdersController", "consumer", RenderTier.Skeleton, 0.3, 50, false,
                ["<- di_resolves_to (hop 1)", "<- di_injects (hop 2)"], ["<- di_resolves_to (hop 1)", "<- di_injects (hop 2)"]),
        };

        return new ContextPlan("review", items, [], 102, []);
    }
}
