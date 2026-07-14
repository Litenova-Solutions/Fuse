using Fuse.Context;
using Fuse.Retrieval;
using Fuse.Scoping;
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
    public void ManifestIncludesClaimsSectionWhenProvided()
    {
        // U2: the graded-claims block rides the manifest, emitted after the api-delta and ahead of the seeds so the
        // evidence trail is read before the source. The block is a rendered string the tool computes.
        var plan = SamplePlan();
        var claims = ClaimLedger.Render([Claim.FromCompiler("2 changed file(s) are seeded as must-keep", "git diff origin/main")]);

        var manifest = SemanticManifestBuilder.Build(plan, root: "/repo", changedSince: "origin/main", claimsSection: claims);

        Assert.Contains("claims (1", manifest);
        Assert.Contains("[verified] 2 changed file(s) are seeded as must-keep", manifest);
        // Ordering: claims precede the seeds section.
        Assert.True(manifest.IndexOf("claims (1", StringComparison.Ordinal) < manifest.IndexOf("seeds:", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestOmitsClaimsSectionWhenNull()
    {
        var manifest = SemanticManifestBuilder.Build(SamplePlan(), claimsSection: null);

        Assert.DoesNotContain("claims (", manifest);
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
