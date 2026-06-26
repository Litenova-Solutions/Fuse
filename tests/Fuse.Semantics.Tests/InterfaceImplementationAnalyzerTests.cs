using Fuse.Indexing;
using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// P4.2: interface implementation and inheritance edges over the OrderingApp fixture.
public sealed class InterfaceImplementationAnalyzerTests
{
    [Fact]
    public void EmitsImplementsEdgeForOrderService()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "implements"
            && e.FromNodeId == "type:OrderingApp.Ordering.OrderService"
            && e.ToNodeId == "type:OrderingApp.Ordering.IOrderService");
    }

    [Fact]
    public void EmittedEdgesHaveImplementsWeightAndEndpointNodes()
    {
        var result = Analyze();

        var edge = result.Edges.Single(e =>
            e.FromNodeId == "type:OrderingApp.Ordering.OrderService"
            && e.ToNodeId == "type:OrderingApp.Ordering.IOrderService");
        Assert.Equal(0.90, edge.Weight);
        Assert.Equal(1.0, edge.Confidence);

        // Both endpoints exist as nodes so the edges can be stored.
        Assert.Contains(result.Nodes, n => n.NodeId == edge.FromNodeId);
        Assert.Contains(result.Nodes, n => n.NodeId == edge.ToNodeId);
    }

    [Fact]
    public void DoesNotEmitInheritsEdgeForSystemObject()
    {
        var result = Analyze();

        // OrderService's base is object (external), so no inherits edge should be produced for it.
        Assert.DoesNotContain(result.Edges, e =>
            e.EdgeType == "inherits" && e.FromNodeId == "type:OrderingApp.Ordering.OrderService");
    }

    private static SemanticAnalyzerResult Analyze()
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return new InterfaceImplementationAnalyzer().Analyze(context, CancellationToken.None);
    }
}
