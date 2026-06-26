using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// P4.5: MediatR request/handler edges over the OrderingApp fixture.
public sealed class MediatRAnalyzerTests
{
    [Fact]
    public void EmitsHandlesEdgeFromCommandToHandler()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "mediatr_handles"
            && e.FromNodeId == "type:OrderingApp.Ordering.CreateOrderCommand"
            && e.ToNodeId == "type:OrderingApp.Ordering.CreateOrderHandler"
            && e.Weight == 0.95);
    }

    [Fact]
    public void EmitsSendsRequestEdgeFromController()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "sends_request"
            && e.FromNodeId == "type:OrderingApp.Api.OrdersController"
            && e.ToNodeId == "type:OrderingApp.Ordering.CreateOrderCommand"
            && e.Weight == 0.70);
    }

    private static SemanticAnalyzerResult Analyze()
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return new MediatRAnalyzer().Analyze(context, CancellationToken.None);
    }
}
