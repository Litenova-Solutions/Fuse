using Fuse.Semantics.Analyzers;
using Xunit;

namespace Fuse.Semantics.Tests;

// P4.4: constructor-injection edges (di_injects, di_depends_on_impl) over the OrderingApp fixture.
public sealed class ConstructorInjectionAnalyzerTests
{
    [Fact]
    public void EmitsInjectsEdgeForControllerDependency()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "di_injects"
            && e.FromNodeId == "type:OrderingApp.Api.OrdersController"
            && e.ToNodeId == "type:OrderingApp.Ordering.IOrderService"
            && e.Weight == 0.75);
    }

    [Fact]
    public void EmitsDependsOnImplWhenServiceIsRegistered()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "di_depends_on_impl"
            && e.FromNodeId == "type:OrderingApp.Api.OrdersController"
            && e.ToNodeId == "type:OrderingApp.Ordering.OrderService"
            && e.Weight == 0.85);
    }

    [Fact]
    public void HandlerInjectsOrderService()
    {
        var result = Analyze();

        Assert.Contains(result.Edges, e =>
            e.EdgeType == "di_injects"
            && e.FromNodeId == "type:OrderingApp.Ordering.CreateOrderHandler"
            && e.ToNodeId == "type:OrderingApp.Ordering.IOrderService");
    }

    private static SemanticAnalyzerResult Analyze()
    {
        var project = OrderingAppFixture.Load();
        var context = new SemanticAnalysisContext(project, OrderingAppFixture.RootDirectory);
        return new ConstructorInjectionAnalyzer(new DiRegistrationAnalyzer()).Analyze(context, CancellationToken.None);
    }
}
