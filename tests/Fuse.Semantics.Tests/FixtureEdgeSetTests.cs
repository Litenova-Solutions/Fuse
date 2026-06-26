using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// P4.8: the full worked-example edge set is present when all analyzers run over the OrderingApp fixture.
public sealed class FixtureEdgeSetTests
{
    [Theory]
    [InlineData("di_resolves_to", "type:OrderingApp.Ordering.IOrderService", "type:OrderingApp.Ordering.OrderService")]
    [InlineData("di_injects", "type:OrderingApp.Api.OrdersController", "type:OrderingApp.Ordering.IOrderService")]
    [InlineData("di_depends_on_impl", "type:OrderingApp.Api.OrdersController", "type:OrderingApp.Ordering.OrderService")]
    [InlineData("mediatr_handles", "type:OrderingApp.Ordering.CreateOrderCommand", "type:OrderingApp.Ordering.CreateOrderHandler")]
    [InlineData("route_handles", "route:POST:/api/orders/{id}", "method:OrderingApp.Api.OrdersController.Create")]
    [InlineData("options_binds", "config:Orders", "type:OrderingApp.Ordering.OrderOptions")]
    [InlineData("options_consumes", "type:OrderingApp.Ordering.OrderService", "type:OrderingApp.Ordering.OrderOptions")]
    [InlineData("implements", "type:OrderingApp.Ordering.OrderService", "type:OrderingApp.Ordering.IOrderService")]
    public void ExpectedEdgeIsPresent(string edgeType, string from, string to)
    {
        var result = RunAllAnalyzers();

        Assert.Contains(result.Edges, e => e.EdgeType == edgeType && e.FromNodeId == from && e.ToNodeId == to);
    }

    [Fact]
    public void EveryEdgeEndpointHasANode()
    {
        var result = RunAllAnalyzers();
        var nodeIds = result.Nodes.Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in result.Edges)
        {
            Assert.Contains(edge.FromNodeId, nodeIds);
            Assert.Contains(edge.ToNodeId, nodeIds);
        }
    }

    private static SemanticAnalyzerResult RunAllAnalyzers()
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return SemanticAnalysisRunner.CreateDefault().Run(context, CancellationToken.None);
    }
}
