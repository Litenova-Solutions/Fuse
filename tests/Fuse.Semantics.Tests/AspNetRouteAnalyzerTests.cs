using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// P4.6: route-to-handler edges over the OrderingApp fixture.
public sealed class AspNetRouteAnalyzerTests
{
    [Fact]
    public void EmitsRouteHandlesEdgeToActionMethod()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "route_handles"
            && e.FromNodeId == "route:POST:/api/orders/{id}"
            && e.ToNodeId == "method:OrderingApp.Api.OrdersController.Create"
            && e.Weight == 1.00);
    }

    [Fact]
    public void RecordsRouteWithResolvedHandlerSymbol()
    {
        var result = Analyze();

        var route = Assert.Single(result.Routes, r => r.RoutePattern == "/api/orders/{id}");
        Assert.Equal("POST", route.HttpMethod);
        Assert.Equal("mvc", route.SourceKind);
        Assert.NotNull(route.HandlerSymbolId);
        Assert.StartsWith("symbol:", route.HandlerSymbolId);
    }

    [Fact]
    public void EmitsRouteAndMethodNodes()
    {
        var result = Analyze();

        Assert.Contains(result.Nodes, n => n.NodeId == "route:POST:/api/orders/{id}" && n.Kind == "route");
        Assert.Contains(result.Nodes, n => n.NodeId == "method:OrderingApp.Api.OrdersController.Create");
    }

    private static SemanticAnalyzerResult Analyze()
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return new AspNetRouteAnalyzer().Analyze(context, CancellationToken.None);
    }
}
