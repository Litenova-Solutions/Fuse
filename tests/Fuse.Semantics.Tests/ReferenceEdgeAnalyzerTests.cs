using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// R5: type-level references edges over the OrderingApp fixture. The analyzer emits one deduped edge per
// (referencing type, referenced source type) pair, never a self-loop, at the references weight, with both
// endpoints materialized as nodes so the edge can be stored.
public sealed class ReferenceEdgeAnalyzerTests
{
    [Fact]
    public void Emits_references_edges_with_the_reference_contract()
    {
        var result = Analyze();
        var references = result.Edges.Where(e => e.EdgeType == "references").ToList();

        Assert.NotEmpty(references);
        // No type references itself.
        Assert.DoesNotContain(references, e => e.FromNodeId == e.ToNodeId);
        // Deduped: one edge per (from, to) pair.
        Assert.Equal(references.Count, references.Select(e => (e.FromNodeId, e.ToNodeId)).Distinct().Count());
        // The weakest structural weight, and both endpoints exist as nodes so the edge can be stored.
        Assert.All(references, e =>
        {
            Assert.Equal(0.15, e.Weight);
            Assert.Contains(result.Nodes, n => n.NodeId == e.FromNodeId);
            Assert.Contains(result.Nodes, n => n.NodeId == e.ToNodeId);
        });
    }

    [Fact]
    public void OrderService_references_another_source_type()
    {
        var result = Analyze();
        // OrderService is a wired service in the fixture; it must reference at least one other source type
        // (its dependencies, commands, or entities), giving fuse_impact an incoming edge to those types.
        Assert.Contains(result.Edges, e =>
            e.EdgeType == "references" && e.FromNodeId == "type:OrderingApp.Ordering.OrderService");
    }

    private static SemanticAnalyzerResult Analyze()
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return new ReferenceEdgeAnalyzer().Analyze(context, CancellationToken.None);
    }
}
